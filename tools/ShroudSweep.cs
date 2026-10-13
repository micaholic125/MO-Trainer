// One-off dev helper (READ-ONLY): hunt the shroud with a NEW criterion.
//
// Why the previous twelve rounds came up empty (2026-10-13 post-mortem):
//
//   * Every earlier sweep used MemSnap's DEFAULT mode, which collects only
//     WRITABLE regions and silently SKIPS executable-protected ones. Whole
//     areas of the process were never looked at even once.
//   * Every earlier criterion was "bytes that changed EXACTLY ONCE". But RA2's
//     shroud is a soft GRADIENT (0..255), not black-or-white -- so a cell being
//     revealed counts DOWN over dozens of frames. Those bytes change many times
//     and were thrown away by the first filter, every single round.
//
// So this tool looks for the signature a fading shroud MUST leave and scattered
// noise cannot fake: a LONG RUN of consecutive bytes that all move in the SAME
// DIRECTION at the same moment.
//
//   ShroudSweep <seconds> <hz> [--min-run N] [--top N]
//
// It never writes a single byte to the game.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MoTools
{
    internal static class ShroudSweep
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const int ChunkSize = 65536;
        private const int MinRegionSize = 4096;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(
            IntPtr handle, IntPtr address, out MEMORY_BASIC_INFORMATION64 info, IntPtr length);

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORY_BASIC_INFORMATION64
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint AllocationProtect;
            public uint Alignment1;
            public ulong RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
            public uint Alignment2;
        }

        private const uint MEM_COMMIT = 0x1000;

        private sealed class Region
        {
            public uint Base;
            public uint Size;
            public uint Protect;
            public uint Type;
            public byte[] Baseline;

            /// <summary>
            /// Set once the region proves to be a rendering surface (a large fraction
            /// of its bytes change every single sample). Skipping those is what buys
            /// the sampling rate: in the shakedown, 9.6M of 9.7M changed bytes per
            /// sample belonged to a handful of such regions -- i.e. the sweep was
            /// spending almost all of its time re-reading the screen. The shroud is a
            /// nearly-static buffer by comparison, so it can never be one of these.
            /// </summary>
            public bool Skipped;

            /// <summary>Proved to be a churning region. In --only-noisy mode these are
            /// the ONLY ones sampled, because they are exactly the blind spot.</summary>
            public bool Noisy;
            public int ChangedLast;
        }

        private sealed class RunStat
        {
            public uint Address;
            public int Appearances;
            public int MaxLength;
            public int LastLength;
            public int LastSample;
            public int FirstSample;
            public byte Before;
            public byte After;
            public int DownBytes;
            public int UpBytes;
        }

        private static IntPtr _process = IntPtr.Zero;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 2)
            {
                Console.WriteLine("usage: ShroudSweep <seconds> <hz> [--min-run N] [--top N]");
                return 2;
            }
            double seconds = double.Parse(args[0], CultureInfo.InvariantCulture);
            double hz = double.Parse(args[1], CultureInfo.InvariantCulture);
            int minRun = 16;
            if (Array.IndexOf(args, "--min-run") >= 0)
            {
                minRun = int.Parse(args[Array.IndexOf(args, "--min-run") + 1], CultureInfo.InvariantCulture);
            }
            int maxRun = 65536;
            if (Array.IndexOf(args, "--max-run") >= 0)
            {
                maxRun = int.Parse(args[Array.IndexOf(args, "--max-run") + 1], CultureInfo.InvariantCulture);
            }
            int top = 30;
            if (Array.IndexOf(args, "--top") >= 0)
            {
                top = int.Parse(args[Array.IndexOf(args, "--top") + 1], CultureInfo.InvariantCulture);
            }
            // --only-noisy turns the auto-skip inside out: instead of ignoring the
            // churning regions, sample ONLY them, and at a much higher rate (they are
            // a small fraction of memory, so the sweep gets far cheaper). This is the
            // one blind spot the normal mode never looks at: 34 MB of the process that
            // was being skipped for being "too active to be a map table".
            bool onlyNoisy = Array.IndexOf(args, "--only-noisy") >= 0;

            Process[] found = Process.GetProcessesByName("gamemd");
            if (found.Length == 0)
            {
                Console.WriteLine("gamemd.exe is not running.");
                return 3;
            }
            int pid = found[0].Id;
            _process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (_process == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess failed, Win32=" + Marshal.GetLastWin32Error());
                return 4;
            }
            Console.WriteLine("gamemd.exe pid=" + pid);

            try
            {
                List<Region> regions = EnumerateRegions();
                long total = 0;
                for (int i = 0; i < regions.Count; i++) { total += regions[i].Size; }
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "regions to sample: {0}   bytes: {1:N0} ({2:0.0} MB)   min-run={3}",
                    regions.Count, total, total / 1048576.0, minRun));

                int samples = (int)Math.Round(seconds * hz);
                int sleepMs = (int)Math.Round(1000.0 / hz);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "sampling {0} times @ {1} Hz  ({2}s)", samples, hz, seconds));
                Console.WriteLine();

                Dictionary<uint, RunStat> stats = new Dictionary<uint, RunStat>();
                Dictionary<uint, StrideHit> strideHits = new Dictionary<uint, StrideHit>();
                byte[] scratch = new byte[ChunkSize];
                Stopwatch clock = Stopwatch.StartNew();
                int skippedReported = 0;

                // seed the baseline
                for (int r = 0; r < regions.Count; r++) { ReadRegion(regions[r], null); }

                for (int s = 0; s < samples; s++)
                {
                    int runCount = 0;
                    long changed = 0;
                    long runBytes = 0;

                    int skippedNow = 0;
                    long skippedBytes = 0;
                    for (int r = 0; r < regions.Count; r++)
                    {
                        Region reg = regions[r];
                        bool sampleThis = onlyNoisy
                            ? (s < 6 || reg.Noisy)      // warm-up pass, then only the churners
                            : !reg.Skipped;
                        if (!sampleThis) { skippedNow++; skippedBytes += reg.Size; continue; }
                        int offset = 0;
                        int changedHere = 0;
                        List<int> chunkChanged = new List<int>();
                        int runStart = -1;
                        int runLen = 0;
                        int down = 0;
                        int up = 0;
                        byte runBefore = 0;
                        byte runAfter = 0;
                        // Range of the "before" and "after" values inside the current run.
                        // This is the decisive filter: a revealed strip of a shroud table is
                        // FLAT (every cell was 0xFF and is now 0x00, or every cell sits at the
                        // same point of the fade) -- a rendering surface being repainted is not.
                        int bMin = 0, bMax = 0, aMin = 0, aMax = 0;

                        while (offset < reg.Size)
                        {
                            int want = (int)(reg.Size - (uint)offset);
                            if (want > ChunkSize) { want = ChunkSize; }
                            IntPtr got;
                            if (!ReadProcessMemory(_process,
                                    (IntPtr)(ulong)(reg.Base + (uint)offset), scratch, (IntPtr)want, out got)
                                || (long)got != want)
                            {
                                // unreadable right now: close any open run, skip ahead
                                if (runLen >= minRun) { Record(stats, reg.Base + (uint)runStart, runLen, runBefore, runAfter, down, up, s, minRun, maxRun, bMin, bMax, aMin, aMax); runCount++; runBytes += runLen; }
                                runStart = -1; runLen = 0; down = 0; up = 0;
                                offset += want;
                                continue;
                            }

                            for (int i = 0; i < want; i++)
                            {
                                int abs = offset + i;
                                byte now = scratch[i];
                                byte was = reg.Baseline[abs];
                                if (now == was) { continue; }
                                changed++;
                                changedHere++;
                                if (chunkChanged.Count < 40000) { chunkChanged.Add(i); }
                                if (now > was) { up++; } else { down++; }
                                if (runLen == 0)
                                {
                                    runStart = abs;
                                    runBefore = was;
                                    bMin = was; bMax = was;
                                    aMin = now; aMax = now;
                                }
                                else
                                {
                                    if (was < bMin) { bMin = was; }
                                    if (was > bMax) { bMax = was; }
                                    if (now < aMin) { aMin = now; }
                                    if (now > aMax) { aMax = now; }
                                }
                                runLen++;
                                runAfter = now;
                                reg.Baseline[abs] = now;
                            }

                            // ---- the blind spot this run exists to close ----
                            //
                            // The contiguous-run detector above only sees a table whose
                            // cells sit right next to each other. If the shroud instead
                            // stores a small record per cell -- one byte of it being the
                            // state, the rest something else -- then the bytes that move
                            // are spaced evenly apart and can never form a run.
                            //
                            // So: ask whether the changed bytes form an ARITHMETIC
                            // PROGRESSION. For the right spacing almost every changed
                            // byte has a changed neighbour exactly that far away; for
                            // scattered noise the ratio collapses.
                            if (chunkChanged.Count >= 8 && chunkChanged.Count <= 40000)
                            {
                                StrideCheck(strideHits, reg.Base + (uint)offset, chunkChanged, s);
                            }
                            chunkChanged.Clear();

                            offset += want;
                        }

                        if (runLen >= minRun)
                        {
                            Record(stats, reg.Base + (uint)runStart, runLen, runBefore, runAfter, down, up, s, minRun, maxRun, bMin, bMax, aMin, aMax);
                            runCount++;
                            runBytes += runLen;
                        }

                        reg.ChangedLast = changedHere;
                        // More than 5% of the allocation rewritten within a single sample
                        // = actively churning memory (a drawing surface, a scratch buffer),
                        // not the nearly-static table we are after. The threshold started at
                        // 25% and is deliberately tight: a shroud buffer only ever moves the
                        // few cells the player just uncovered, so it is nowhere near 5%.
                        if (changedHere * 20 > reg.Size)
                        {
                            reg.Noisy = true;
                            reg.Skipped = true;
                            if (skippedReported < 12)
                            {
                                skippedReported++;
                                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                    "  (skip 0x{0:X8} size={1:N0} prot=0x{2:X} -- {3:N0} bytes changed in one sample)",
                                    reg.Base, reg.Size, reg.Protect, changedHere));
                            }
                        }
                    }

                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  [t={0,6:0.0}s] sample {1}/{2}  changed={3:N0}  long-runs={4}  bytes-in-long-runs={5:N0}  skipped={6} regions / {7:N0} bytes",
                        clock.Elapsed.TotalSeconds, s + 1, samples, changed, runCount, runBytes, skippedNow, skippedBytes));

                    Thread.Sleep(sleepMs);
                }

                // ---------------------------------------------------------- report
                Console.WriteLine();
                List<RunStat> ordered = new List<RunStat>(stats.Values);
                ordered.Sort(delegate(RunStat a, RunStat b)
                {
                    if (a.Appearances != b.Appearances) { return b.Appearances.CompareTo(a.Appearances); }
                    if (a.MaxLength != b.MaxLength) { return b.MaxLength.CompareTo(a.MaxLength); }
                    return a.Address.CompareTo(b.Address);
                });

                Console.WriteLine("=== runs of >= " + minRun + " consecutive bytes moving together ===");
                Console.WriteLine("(sorted by how many samples they showed up in -- a fading");
                Console.WriteLine(" shroud keeps moving for many frames; noise does not.)");
                Console.WriteLine();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0,-10} {1,-6} {2,-6} {3,-9} {4,-9} {5}", "address", "times", "len", "sample", "dir", "before->after"));

                int shown = 0;
                for (int i = 0; i < ordered.Count && shown < top; i++)
                {
                    RunStat st = ordered[i];
                    // Report EVERY site, even the ones seen only once.
                    // 2026-10-13 lesson: the first real run printed only sites seen twice or
                    // more, so the five single-appearance sites it did find were thrown away
                    // and could not be looked at afterwards. A negative result is still data.
                    if (st.Appearances < 1) { continue; }
                    string dir = st.DownBytes > st.UpBytes ? "down" : (st.UpBytes > st.DownBytes ? "up" : "mixed");
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8} {1,-6} {2,-6} {3,-9} {4,-9} 0x{5:X2} -> 0x{6:X2}",
                        st.Address, st.Appearances, st.MaxLength,
                        st.FirstSample + ".." + st.LastSample, dir, st.Before, st.After));
                    shown++;
                }
                if (shown == 0)
                {
                    Console.WriteLine("(none appeared in 2 or more samples)");
                    Console.WriteLine("total distinct long-run starts seen: " + ordered.Count);
                }
                Console.WriteLine();
                Console.WriteLine("distinct long-run sites: " + ordered.Count);

                // ---- the strided-table channel ----
                Console.WriteLine();
                Console.WriteLine("=== strided-table channel (changed bytes evenly spaced apart) ===");
                List<StrideHit> sh = new List<StrideHit>(strideHits.Values);
                sh.Sort(delegate(StrideHit a, StrideHit b)
                {
                    if (a.Appearances != b.Appearances) { return b.Appearances.CompareTo(a.Appearances); }
                    return b.Covered.CompareTo(a.Covered);
                });
                if (sh.Count == 0)
                {
                    Console.WriteLine("  (nothing in the whole process looked like an evenly spaced table)");
                }
                for (int i = 0; i < sh.Count && i < 25; i++)
                {
                    StrideHit h = sh[i];
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}  stride={1,-3} covered={2}/{3}  times={4}  samples {5}..{6}",
                        h.ChunkBase, h.Stride, h.Covered, h.Total, h.Appearances, h.FirstSample, h.LastSample));
                }
                Console.WriteLine("  strided-table sites: " + sh.Count);

                // Which pages were busiest? A shroud buffer is a compact area.
                Dictionary<uint, int> pageHits = new Dictionary<uint, int>();
                foreach (RunStat st in ordered)
                {
                    uint page = st.Address & 0xFFFFF000u;
                    int c;
                    pageHits.TryGetValue(page, out c);
                    pageHits[page] = c + st.Appearances;
                }
                List<KeyValuePair<uint, int>> pages = new List<KeyValuePair<uint, int>>(pageHits);
                pages.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b)
                {
                    return b.Value.CompareTo(a.Value);
                });
                Console.WriteLine();
                Console.WriteLine("=== busiest 4 KB pages (a compact buffer would stand out) ===");
                for (int i = 0; i < pages.Count && i < 25; i++)
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}  weight={1}", pages[i].Key, pages[i].Value));
                }
            }
            finally
            {
                CloseHandle(_process);
            }
            return 0;
        }

        /// <summary>One 64 KB chunk that behaved like a strided table.</summary>
        private sealed class StrideHit
        {
            public uint ChunkBase;
            public int Stride;
            public int Covered;      // changed bytes sitting on the progression
            public int Total;        // changed bytes in the chunk
            public int Appearances;
            public int FirstSample;
            public int LastSample;
            public byte Before;
            public byte After;
        }

        private static readonly int[] Strides = new int[]
        {
            1, 2, 3, 4, 6, 8, 10, 12, 16, 20, 24, 28, 32, 36, 40, 48, 56, 64
        };

        /// <summary>
        /// Does this chunk's changed-byte set look like an evenly spaced table?
        /// For each candidate spacing, count the changed bytes that have another
        /// changed byte exactly that far ahead. A real strided table scores ~1.0;
        /// scattered noise scores about the density of changes in the chunk.
        /// </summary>
        private static void StrideCheck(Dictionary<uint, StrideHit> hits, uint chunkBase,
            List<int> changed, int sample)
        {
            bool[] isChanged = new bool[ChunkSize];
            for (int i = 0; i < changed.Count; i++) { isChanged[changed[i]] = true; }

            int bestStride = 0;
            int bestCovered = 0;
            for (int si = 0; si < Strides.Length; si++)
            {
                int st = Strides[si];
                int covered = 0;
                for (int i = 0; i < changed.Count; i++)
                {
                    int o = changed[i];
                    if (o + st < ChunkSize && isChanged[o + st]) { covered++; }
                }
                if (covered > bestCovered) { bestCovered = covered; bestStride = st; }
            }
            if (bestStride == 0 || bestCovered < 8) { return; }
            // must cover most of the changed bytes, not just a handful
            if ((double)bestCovered / changed.Count < 0.75) { return; }

            uint key = chunkBase ^ (uint)(bestStride * 0x9E3779B1u);
            StrideHit hit;
            if (!hits.TryGetValue(key, out hit))
            {
                hit = new StrideHit();
                hit.ChunkBase = chunkBase;
                hit.Stride = bestStride;
                hit.FirstSample = sample;
                hits[key] = hit;
            }
            hit.Covered = bestCovered;
            hit.Total = changed.Count;
            hit.Appearances++;
            hit.LastSample = sample;
        }

        private static void Record(Dictionary<uint, RunStat> stats, uint key, int runLen,
            byte before, byte after, int down, int up, int sample, int minRun, int maxRun,
            int bMin, int bMax, int aMin, int aMax)
        {
            // The flatness gate, applied before anything is stored.
            const int FlatTolerance = 3;
            if (bMax - bMin > FlatTolerance || aMax - aMin > FlatTolerance) { return; }
            // A run longer than maxRun is a whole rendering surface being rewritten,
            // not a revealed strip of map. Dropping those is what makes the report readable:
            // in the shakedown run a handful of giant runs swallowed 9.6M of the 9.7M
            // changed bytes per sample.
            if (runLen < minRun || runLen > maxRun) { return; }
            RunStat st;
            if (!stats.TryGetValue(key, out st))
            {
                if (stats.Count > 400000) { return; }
                st = new RunStat();
                st.Address = key;
                st.Before = before;
                st.FirstSample = sample;
                stats[key] = st;
            }
            st.Appearances++;
            if (runLen > st.MaxLength) { st.MaxLength = runLen; }
            st.LastLength = runLen;
            st.LastSample = sample;
            st.After = after;
            st.DownBytes += down;
            st.UpBytes += up;
        }

        /// <summary>Reads a region into its baseline (or just checks readability when base is null).</summary>
        private static bool ReadRegion(Region reg, byte[] unused)
        {
            if (reg.Baseline == null) { reg.Baseline = new byte[reg.Size]; }
            int offset = 0;
            byte[] scratch = new byte[ChunkSize];
            bool any = false;
            while (offset < reg.Size)
            {
                int want = (int)(reg.Size - (uint)offset);
                if (want > ChunkSize) { want = ChunkSize; }
                IntPtr got;
                if (ReadProcessMemory(_process, (IntPtr)(ulong)(reg.Base + (uint)offset), scratch, (IntPtr)want, out got)
                    && (long)got == want)
                {
                    Buffer.BlockCopy(scratch, 0, reg.Baseline, offset, want);
                    any = true;
                }
                offset += want;
            }
            return any;
        }

        /// <summary>
        /// Every committed, readable region at least one page long -- INCLUDING
        /// executable ones and read-only ones. This is the gap that the earlier
        /// rounds never covered. Reading is harmless; nothing here writes.
        /// </summary>
        private static List<Region> EnumerateRegions()
        {
            List<Region> list = new List<Region>();
            ulong address = 0x10000;
            ulong limit = 0x80000000UL;
            MEMORY_BASIC_INFORMATION64 info = new MEMORY_BASIC_INFORMATION64();
            int guard = 0;
            while (address < limit && guard < 200000)
            {
                guard++;
                IntPtr written = VirtualQueryEx(_process, (IntPtr)(ulong)address, out info, (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64)));
                if (written == IntPtr.Zero) { break; }
                ulong size = info.RegionSize;
                if (size == 0) { break; }

                if (info.State == MEM_COMMIT && size >= MinRegionSize && (uint)size <= 0x08000000u)
                {
                    // readable? PAGE_NOACCESS(0x01) and PAGE_GUARD(0x100) say no
                    uint prot = info.Protect;
                    bool readable = prot != 0 && (prot & 0x01) == 0 && (prot & 0x100) == 0;
                    if (readable)
                    {
                        Region reg = new Region();
                        reg.Base = (uint)info.BaseAddress;
                        reg.Size = (uint)size;
                        reg.Protect = prot;
                        reg.Type = info.Type;
                        list.Add(reg);
                    }
                }
                address = info.BaseAddress + size;
            }
            return list;
        }
    }
}
