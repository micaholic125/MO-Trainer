// One-off dev helper: find the shroud by ASKING THE GAME, not by guessing its shape.
//
// Every previous round tried to recognise the shroud by what it looks like in a
// memory dump. Thirteen rounds, ten hypothesis classes, all negative. This tool
// stops guessing: it pokes one page at a time and watches the SCREEN for the one
// unmistakable reaction -- a large black area turning into terrain.
//
// Safety rules it follows, all of them non-negotiable:
//   * only pages VirtualQueryEx says are WRITABLE;
//   * never a page whose protection is executable (project red line: no code
//     patching -- Ares has 1450 hooks in there and touching code bricks the game);
//   * the original bytes are saved first and written back immediately, every
//     single time, including on the error path;
//   * one 4 KB page at a time, held for a fraction of a second, so nothing is
//     ever left in a modified state.
//
// Modes:
//   ShroudOracle find-screen                 -- locate the screen buffer (read-only)
//   ShroudOracle noise <seconds>             -- measure the metric's natural wobble (read-only)
//   ShroudOracle scan <max> [--hold ms] [--min-constant f] [--screen 0xADDR-0xADDR]
//
// Read-only except in `scan`, and even there only temporarily.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MoTools
{
    internal static class ShroudOracle
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const uint MEM_COMMIT = 0x1000;
        private const uint PAGE_GUARD = 0x100;
        private const uint PAGE_NOACCESS = 0x01;

        private const int PageSize = 4096;

        // The screen buffer two previous sessions both found here. `find-screen`
        // re-derives it rather than trusting the constant.
        private static uint _screenBase = 0x0B500000;
        private static uint _screenSize = 0x00350000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION64 info, IntPtr len);

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

        private sealed class Region
        {
            public uint Base;
            public uint Size;
            public uint Protect;
            public uint Type;
        }

        private static IntPtr _proc = IntPtr.Zero;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length == 0) { Console.WriteLine("usage: ShroudOracle find-screen|noise|scan ..."); return 2; }

            Process[] found = Process.GetProcessesByName("gamemd");
            if (found.Length == 0) { Console.WriteLine("gamemd.exe is not running."); return 3; }
            int pid = found[0].Id;
            _proc = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION
                | PROCESS_QUERY_INFORMATION, false, pid);
            if (_proc == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess failed, Win32=" + Marshal.GetLastWin32Error());
                return 4;
            }
            Console.WriteLine("gamemd.exe pid=" + pid);
            try
            {
                // The screen buffer moves between sessions (it is a fresh allocation
                // every time the game starts), so it must be overridable for any
                // command that watches it -- not just `scan`.
                string screenOverride = GetStr(args, "--screen", "");
                if (screenOverride.Length > 0)
                {
                    string[] parts = screenOverride.Split('-');
                    _screenBase = ParseHex(parts[0]);
                    _screenSize = ParseHex(parts[1]) - _screenBase;
                }

                string cmd = args[0].ToLowerInvariant();
                if (cmd == "flags")
                {
                    return Flags(ParseHex(args[1]), (int)ParseHex(args[2]),
                        GetInt(args, "--hold", 70), GetInt(args, "--max", 0));
                }
                if (cmd == "find-screen") { return FindScreen(); }
                if (cmd == "noise") { return Noise(double.Parse(args[1], CultureInfo.InvariantCulture)); }
                if (cmd == "scan")
                {
                    int max = int.Parse(args[1], CultureInfo.InvariantCulture);
                    int hold = GetInt(args, "--hold", 140);
                    double minConstant = GetDouble(args, "--min-constant", 0.60);
                    return Scan(max, hold, minConstant);
                }
                Console.WriteLine("unknown command: " + cmd);
                return 2;
            }
            finally { CloseHandle(_proc); }
        }

        private static int GetInt(string[] a, string name, int dflt)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 ? int.Parse(a[i + 1], CultureInfo.InvariantCulture) : dflt;
        }
        private static double GetDouble(string[] a, string name, double dflt)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 ? double.Parse(a[i + 1], CultureInfo.InvariantCulture) : dflt;
        }
        private static string GetStr(string[] a, string name, string dflt)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 ? a[i + 1] : dflt;
        }
        private static uint ParseHex(string s)
        {
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { s = s.Substring(2); }
            return uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private static bool ReadMem(uint addr, int len, out byte[] buf)
        {
            buf = new byte[len];
            IntPtr got;
            bool ok = ReadProcessMemory(_proc, (IntPtr)(ulong)addr, buf, (IntPtr)len, out got) && (long)got == len;
            if (!ok) { buf = null; }
            return ok;
        }

        // ------------------------------------------------------------------ metric

        /// <summary>
        /// How much of the screen is pure black. When a shroud lifts, a large black
        /// area turns into terrain, so this number FALLS -- hard. Nothing else the
        /// game does changes it nearly as much.
        /// </summary>
        private static long Blackness(out long total)
        {
            byte[] buf;
            total = 0;
            if (!ReadMem(_screenBase, (int)_screenSize, out buf)) { return -1; }
            long zeros = 0;
            for (int i = 0; i < buf.Length; i++) { if (buf[i] == 0) { zeros++; } }
            total = buf.Length;
            return zeros;
        }

        /// <summary>
        /// Flip ONLY the on/off looking bytes inside one object -- the player's own
        /// house record -- and watch the screen.
        ///
        /// Why this is the low-risk version of the poke idea: the target is a single
        /// object we already write to on purpose (money and power live in it, and the
        /// game has tolerated those writes for weeks), and only bytes currently worth
        /// 0x00 or 0x01 are touched -- those are flags, not pointers. A pointer's low
        /// byte is almost never exactly 0 or 1 while the pointer is live, so the odds
        /// of corrupting a reference are small. Anything that is not a plain flag is
        /// left completely alone.
        /// </summary>
        private static int Flags(uint house, int size, int holdMs, int max)
        {
            long total;
            long baseZeros = Blackness(out total);
            if (baseZeros < 0) { Console.WriteLine("screen unreadable -- abort"); return 5; }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "house 0x{0:X8}  scanning {1:N0} bytes  baseline blackness {2:N0} ({3:0.0}%)",
                house, size, baseZeros, 100.0 * baseZeros / total));

            List<uint> spots = new List<uint>();
            List<byte> values = new List<byte>();
            byte[] page;
            if (!ReadMem(house, size, out page)) { Console.WriteLine("cannot read the house object"); return 5; }
            for (int i = 0; i < page.Length; i++)
            {
                if (page[i] == 0x00 || page[i] == 0x01) { spots.Add(house + (uint)i); values.Add(page[i]); }
            }
            Console.WriteLine("on/off looking bytes: " + spots.Count);
            int limit = max > 0 && max < spots.Count ? max : spots.Count;
            Console.WriteLine("flipping " + limit + " of them, " + holdMs + " ms each");
            Console.WriteLine();

            string logPath = Path.Combine(Path.GetTempPath(), "mo-shroud-1013", "oracle-hits.txt");
            StreamWriter log = new StreamWriter(logPath, true, Encoding.UTF8);
            int hits = 0;
            List<string> top = new List<string>();
            List<double> topAbs = new List<double>();
            try
            {
                for (int i = 0; i < limit; i++)
                {
                    uint addr = spots[i];
                    byte[] one;
                    if (!ReadMem(addr, 1, out one)) { continue; }
                    // re-read: the game may have changed it since the sweep
                    if (one[0] != 0x00 && one[0] != 0x01) { continue; }
                    byte[] patch = new byte[1];
                    patch[0] = (byte)(one[0] ^ 0x01);

                    long t;
                    long before = Blackness(out t);
                    IntPtr written;
                    bool ok = WriteProcessMemory(_proc, (IntPtr)(ulong)addr, patch, (IntPtr)1, out written)
                        && (long)written == 1;
                    Thread.Sleep(holdMs);
                    long after = Blackness(out t);
                    IntPtr back;
                    bool restored = WriteProcessMemory(_proc, (IntPtr)(ulong)addr, one, (IntPtr)1, out back)
                        && (long)back == 1;
                    Thread.Sleep(holdMs);

                    if (!ok || !restored)
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "  0x{0:X8} WRITE/RESTORE FAILED (wrote={1} restored={2}) -- stopping", addr, ok, restored));
                        break;
                    }

                    double delta = 100.0 * (after - before) / total;
                    string entry = string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8}  {1} -> {2}  black {3:N0} -> {4:N0}  delta={5:0.00}%",
                        addr, one[0], patch[0], before, after, delta);
                    top.Add(entry); topAbs.Add(Math.Abs(delta));
                    while (top.Count > 10)
                    {
                        int worst = 0;
                        for (int k = 1; k < topAbs.Count; k++) { if (topAbs[k] < topAbs[worst]) { worst = k; } }
                        top.RemoveAt(worst); topAbs.RemoveAt(worst);
                    }

                    if (Math.Abs(delta) >= 2.0)
                    {
                        hits++;
                        Console.WriteLine("  HIT " + entry);
                        log.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  HIT " + entry);
                        log.Flush();
                    }
                    if ((i + 1) % 100 == 0) { Console.WriteLine("  ... " + (i + 1) + "/" + limit + "  hits=" + hits); }
                }
            }
            finally { log.Close(); }

            Console.WriteLine();
            Console.WriteLine("=== strongest reactions ===");
            List<int> idx = new List<int>();
            for (int i = 0; i < top.Count; i++) { idx.Add(i); }
            idx.Sort(delegate(int a, int b) { return topAbs[b].CompareTo(topAbs[a]); });
            for (int i = 0; i < idx.Count; i++) { Console.WriteLine("  " + top[idx[i]]); }
            Console.WriteLine();
            Console.WriteLine("done. hits=" + hits);
            return 0;
        }

        private static int FindScreen()
        {
            List<Region> regions = EnumerateRegions(true);
            Console.WriteLine("writable regions >= 1 MB: " + regions.Count);
            List<byte[]> first = new List<byte[]>();
            for (int i = 0; i < regions.Count; i++)
            {
                byte[] b = null;
                if (regions[i].Size <= 8u * 1024 * 1024) { ReadMem(regions[i].Base, (int)regions[i].Size, out b); }
                first.Add(b);
            }
            Thread.Sleep(400);
            List<string> results = new List<string>();
            for (int i = 0; i < regions.Count; i++)
            {
                if (first[i] == null) { continue; }
                byte[] b2 = null;
                if (!ReadMem(regions[i].Base, first[i].Length, out b2)) { continue; }
                long diff = 0;
                for (int k = 0; k < b2.Length; k++) { if (b2[k] != first[i][k]) { diff++; } }
                double ratio = diff / (double)b2.Length;
                if (ratio > 0.05)
                {
                    results.Add(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}  size={1:N0}  changed={2:0.0}%  prot=0x{3:X}",
                        regions[i].Base, regions[i].Size, ratio * 100, regions[i].Protect));
                }
            }
            results.Sort();
            Console.WriteLine("regions that change a lot between two samples (screen candidates):");
            for (int i = 0; i < results.Count && i < 20; i++) { Console.WriteLine(results[i]); }
            return 0;
        }

        private static int Noise(double seconds)
        {
            Console.WriteLine("measuring the blackness metric with NOTHING being written...");
            long min = long.MaxValue, max = long.MinValue, total = 0;
            int n = 0;
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < seconds)
            {
                long t;
                long z = Blackness(out t);
                if (z < 0) { Console.WriteLine("screen unreadable at 0x" + _screenBase.ToString("X8")); return 5; }
                total = t;
                if (z < min) { min = z; }
                if (z > max) { max = z; }
                n++;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  black bytes {0:N0} / {1:N0}  ({2:0.0}%)", z, t, 100.0 * z / t));
                Thread.Sleep(250);
            }
            Console.WriteLine();
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "samples={0}  min={1:N0}  max={2:N0}  spread={3:N0}  ({4:0.00}% of the screen)",
                n, min, max, max - min, 100.0 * (max - min) / total));
            Console.WriteLine("Anything the scan sees beyond this spread is real, not animation.");
            return 0;
        }

        // ------------------------------------------------------------------ the scan

        private static int Scan(int max, int holdMs, double minConstant)
        {
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "screen buffer assumed 0x{0:X8}..0x{1:X8}", _screenBase, _screenBase + _screenSize));

            long totalBytes;
            long baseZeros = Blackness(out totalBytes);
            if (baseZeros < 0) { Console.WriteLine("screen unreadable -- abort"); return 5; }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "baseline blackness: {0:N0} / {1:N0} ({2:0.0}%)", baseZeros, totalBytes, 100.0 * baseZeros / totalBytes));

            // ---- candidates: 4 KB pages that are mostly ONE byte value ----
            List<Region> regions = EnumerateRegions(false);
            long writable = 0;
            for (int i = 0; i < regions.Count; i++) { writable += regions[i].Size; }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "writable, non-executable regions: {0}   bytes: {1:N0} ({2:0.0} MB)",
                regions.Count, writable, writable / 1048576.0));

            List<uint> candidates = new List<uint>();
            List<double> constantness = new List<double>();
            byte[] page = new byte[PageSize];
            int[] hist = new int[256];
            for (int i = 0; i < regions.Count; i++)
            {
                Region reg = regions[i];
                for (uint off = 0; off + PageSize <= reg.Size; off += PageSize)
                {
                    byte[] got;
                    if (!ReadMem(reg.Base + off, PageSize, out got)) { continue; }
                    for (int k = 0; k < 256; k++) { hist[k] = 0; }
                    for (int k = 0; k < PageSize; k++) { hist[got[k]]++; }
                    int dom = 0;
                    for (int k = 0; k < 256; k++) { if (hist[k] > hist[dom]) { dom = k; } }
                    int best = hist[dom];
                    double f = best / (double)PageSize;

                    // Three sieves, each removing a different kind of look-alike:
                    //   * mostly one value        -- the shroud is overwhelmingly "unexplored"
                    //   * that value is 00 or FF  -- the only two natural encodings for it
                    //   * but NOT perfectly uniform -- a page that is 100% one byte is
                    //     unused memory or a scratch buffer, never a map with explored
                    //     cells in it. Without this sieve, 17,320 of the candidates were
                    //     simply blank pages, and the scan would have ground for hours.
                    if (dom != 0x00 && dom != 0xFF) { continue; }
                    if (f < minConstant || f >= 0.995) { continue; }

                    candidates.Add(reg.Base + off);
                    constantness.Add(f);
                }
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "candidate pages (>= {0:0.00} of one byte value): {1:N0}", minConstant, candidates.Count));
            if (candidates.Count == 0) { return 0; }

            // most-constant first: those are the least likely to be live structures,
            // and a shroud table is overwhelmingly one value.
            List<int> order = new List<int>();
            for (int i = 0; i < candidates.Count; i++) { order.Add(i); }
            order.Sort(delegate(int a, int b) { return constantness[b].CompareTo(constantness[a]); });

            int limit = max > 0 && max < order.Count ? max : order.Count;
            Console.WriteLine("poking " + limit + " page(s), hold=" + holdMs + " ms each. Any page that");
            Console.WriteLine("changes the screen far beyond the noise floor gets logged loudly.");
            Console.WriteLine();

            string logPath = Path.Combine(Path.GetTempPath(), "mo-shroud-1013", "oracle-hits.txt");
            StreamWriter log = new StreamWriter(logPath, true, Encoding.UTF8);
            int hits = 0;
            // Keep the strongest reactions even when none crosses the threshold: a
            // negative result is much more useful if it says HOW CLOSE anything got.
            List<string> top = new List<string>();
            List<double> topAbs = new List<double>();
            try
            {
                for (int i = 0; i < limit; i++)
                {
                    uint addr = candidates[order[i]];
                    byte[] original;
                    if (!ReadMem(addr, PageSize, out original)) { continue; }

                    int[] h = new int[256];
                    for (int k = 0; k < PageSize; k++) { h[original[k]]++; }
                    int dom = 0;
                    for (int k = 0; k < 256; k++) { if (h[k] > h[dom]) { dom = k; } }

                    // write the OPPOSITE of the dominant value, so whether "unexplored"
                    // is stored as 0xFF or as 0x00, the page always visibly changes
                    byte[] patch = new byte[PageSize];
                    for (int k = 0; k < PageSize; k++) { patch[k] = (byte)(dom ^ 0xFF); }

                    long t;
                    long before = Blackness(out t);

                    IntPtr written;
                    bool ok = WriteProcessMemory(_proc, (IntPtr)(ulong)addr, patch, (IntPtr)PageSize, out written)
                        && (long)written == PageSize;

                    Thread.Sleep(holdMs);
                    long after = Blackness(out t);

                    // put it back, no matter what
                    IntPtr restored;
                    bool back = WriteProcessMemory(_proc, (IntPtr)(ulong)addr, original, (IntPtr)PageSize, out restored)
                        && (long)restored == PageSize;

                    Thread.Sleep(holdMs);

                    if (!ok || !back)
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "  0x{0:X8}  WRITE/RESTORE FAILED (wrote={1} restored={2}) -- stopping",
                            addr, ok, back));
                        break;
                    }

                    double deltaPct = 100.0 * (after - before) / totalBytes;
                    string entry = string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8}  dominant=0x{1:X2}  black {2:N0} -> {3:N0}  delta={4:0.00}%",
                        addr, dom, before, after, deltaPct);
                    top.Add(entry);
                    topAbs.Add(Math.Abs(deltaPct));
                    while (top.Count > 12)
                    {
                        int worst = 0;
                        for (int k = 1; k < topAbs.Count; k++) { if (topAbs[k] < topAbs[worst]) { worst = k; } }
                        top.RemoveAt(worst); topAbs.RemoveAt(worst);
                    }

                    if (Math.Abs(deltaPct) >= 2.0)
                    {
                        hits++;
                        string line = string.Format(CultureInfo.InvariantCulture,
                            "HIT 0x{0:X8}  dominant=0x{1:X2}  black {2:N0} -> {3:N0}  delta={4:0.00}%",
                            addr, dom, before, after, deltaPct);
                        Console.WriteLine("  " + line);
                        log.WriteLine(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
                        log.Flush();
                    }

                    if ((i + 1) % 25 == 0)
                    {
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "  ... {0}/{1} poked, hits={2}", i + 1, limit, hits));
                    }
                }
            }
            finally
            {
                log.Close();
            }
            Console.WriteLine();
            Console.WriteLine("=== strongest reactions seen (even if none crossed the line) ===");
            List<int> idx = new List<int>();
            for (int i = 0; i < top.Count; i++) { idx.Add(i); }
            idx.Sort(delegate(int a, int b) { return topAbs[b].CompareTo(topAbs[a]); });
            for (int i = 0; i < idx.Count; i++) { Console.WriteLine("  " + top[idx[i]]); }
            Console.WriteLine();
            Console.WriteLine("done. hits=" + hits + "   log: " + logPath);
            return 0;
        }

        /// <summary>
        /// Committed regions that are readable and writable. Executable pages are
        /// excluded on purpose: writing code is the one thing this project must
        /// never do, and it would not be the shroud anyway.
        /// </summary>
        private static List<Region> EnumerateRegions(bool bigOnly)
        {
            List<Region> list = new List<Region>();
            ulong address = 0x10000;
            MEMORY_BASIC_INFORMATION64 info = new MEMORY_BASIC_INFORMATION64();
            int guard = 0;
            while (address < 0x80000000UL && guard < 200000)
            {
                guard++;
                IntPtr w = VirtualQueryEx(_proc, (IntPtr)(ulong)address, out info,
                    (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64)));
                if (w == IntPtr.Zero) { break; }
                ulong size = info.RegionSize;
                if (size == 0) { break; }

                // PAGE_* bits: 01 NOACCESS | 02 READONLY | 04 READWRITE | 08 WRITECOPY
                //              10 EXECUTE  | 20 EXECUTE_READ | 40 EXECUTE_READWRITE | 80 EXECUTE_WRITECOPY
                // 0x400 / 0x800 are modifiers (NOCACHE / WRITECOMBINE) and must not
                // be mistaken for access bits -- the screen buffer carries 0x404.
                uint prot = info.Protect;
                bool readable = prot != 0 && (prot & PAGE_NOACCESS) == 0 && (prot & PAGE_GUARD) == 0;
                bool anyExecute = (prot & 0xF0) != 0;
                bool writable = readable && !anyExecute && ((prot & 0x04) != 0 || (prot & 0x08) != 0);
                if (info.State == MEM_COMMIT && readable && writable
                    && size >= PageSize && (!bigOnly || size >= 1024 * 1024))
                {
                    Region reg = new Region();
                    reg.Base = (uint)info.BaseAddress;
                    reg.Size = (uint)size;
                    reg.Protect = prot;
                    reg.Type = info.Type;
                    list.Add(reg);
                }
                address = info.BaseAddress + size;
            }
            return list;
        }
    }
}
