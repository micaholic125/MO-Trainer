// ShroudLIVE (read-only): continuous differential watcher for the per-house
// shroud book.
//
// Method: sample the game's committed, non-executable, NON-screen-buffer
// memory every N seconds (small per-region digests: per-64KB-block bit and
// byte change counts vs the previous sample).  The user plays normally
// (explore, build, move).  When the shroud advances, the book region follows
// the user's steps; noise does not correlate with exploration runs.
//
// Output: after the run, print per-region stats: change-count, distinct
// change phases, and "correlation with quiet periods" so regions that ONLY
// move when the user explores stand out.  Also dump full digests to a side
// file for offline replay (snapseq-compatible).
//
// Read-only: ReadProcessMemory only.
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
    internal static class ShroudLIVE
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const uint ModuleBase = 0x00400000;
        private const uint ModuleSize = 0x00793000;

        // screen buffer band measured repeatedly on this machine
        private const uint NoiseLo0 = 0x0B500000, NoiseHi0 = 0x0B860000;
        private const uint NoiseLo1 = 0x0FDE0000, NoiseHi1 = 0x10000000;

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

        private static IntPtr _process = IntPtr.Zero;
        private static int _pid;
        private static byte[] tmpBuf;

        private static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try { RealMain(args); }
            catch (Exception e)
            {
                Console.WriteLine();
                Console.WriteLine("EXCEPTION: " + e.GetType().FullName);
                Console.WriteLine("message: " + e.Message);
                Console.WriteLine("stack: " + e.StackTrace);
                if (e.InnerException != null)
                {
                    Console.WriteLine("inner: " + e.InnerException.Message);
                }
            }
        }

        private static void RealMain(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            double seconds = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 240;
            double hz = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 0.5;

            Process[] found = Process.GetProcessesByName("gamemd");
            if (found.Length == 0)
            {
                Console.WriteLine("gamemd.exe is not running.");
                return;
            }
            _pid = found[0].Id;
            _process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, _pid);
            if (_process == IntPtr.Zero)
            { Console.WriteLine("OpenProcess failed"); return; }
            Console.WriteLine("gamemd.exe pid=" + _pid + "  watching " + seconds + "s @ " + hz + "Hz");

            // enumerate writable, non-noise regions once
            var regions = new List<Region>();
            ulong addr = 0x10000;
            ulong max = 0x7FFEFFFF;
            while (addr < max)
            {
                MEMORY_BASIC_INFORMATION64 info;
                if (VirtualQueryEx(_process, (IntPtr)addr, out info,
                    (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64))) == IntPtr.Zero)
                { break; }
                ulong size = info.RegionSize;
                if (size == 0) { break; }
                if (info.State == 0x1000 && (info.Protect & 0xF0) == 0
                    && ((info.Protect & 0x04) != 0 || (info.Protect & 0x08) != 0)
                    && (info.BaseAddress >= NoiseHi0 || info.BaseAddress + size <= NoiseLo0)
                    && (info.BaseAddress >= NoiseHi1 || info.BaseAddress + size <= NoiseLo1))
                {
                    if (info.BaseAddress < 0xFFFFFFFFul && size <= 0x2000000)
                    {
                        regions.Add(new Region { Base = (uint)info.BaseAddress, Size = (int)size });
                    }
                }
                addr = info.BaseAddress + size;
            }
            int totalMB = 0;
            foreach (Region r in regions) { totalMB += r.Size >> 20; }
            Console.WriteLine("watching " + regions.Count + " regions, ~" + totalMB + " MB per sweep");

            int samples = (int)(seconds * hz);
            int sleepMs = (int)(1000.0 / hz);
            string logPath = "shroudlive-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
            using (var log = new StreamWriter(logPath, false, Encoding.UTF8))
            {
                log.WriteLine("# t=seconds region_base changed_bytes changed_uniformbyte8");
                var prev = new Dictionary<uint, byte[]>();
                var stats = new Dictionary<uint, Stat>();
                // Reusable temp buffer: reading goes into `tmp`, then tmp and prev
                // swap by reference.  Peak memory = 2 x (largest single region),
                // NOT 2 x total -- the first version reallocated the whole set
                // every sample and exhausted the 32-bit address space.

                for (int s = 0; s < samples && !_kbHitStop; s++)
                {
                    double t = s / hz;
                    int changedRegions = 0;
                    foreach (Region r in regions)
                    {
                        byte[] buf;
                        if (tmpBuf == null || tmpBuf.Length < r.Size) { tmpBuf = new byte[r.Size]; }
                        IntPtr got;
                        if (!ReadProcessMemory(_process, (IntPtr)(ulong)r.Base, tmpBuf,
                            (IntPtr)r.Size, out got) || (long)got != r.Size)
                        { continue; }
                        buf = tmpBuf;

                        byte[] old;
                        Stat st;
                        if (!prev.TryGetValue(r.Base, out old))
                        {
                            prev[r.Base] = (byte[])buf.Clone();
                            continue;
                        }
                        if (!stats.TryGetValue(r.Base, out st))
                        {
                            st = new Stat { Base = r.Base, Size = r.Size };
                            stats[r.Base] = st;
                        }

                        int cbytes = 0;
                        for (int b = 0; b + 0x10000 <= r.Size; b += 0x10000)
                        {
                            int cb = 0;
                            for (int i = b; i < b + 0x10000; i++)
                            {
                                if (old[i] != buf[i]) { cb++; }
                            }
                            if (cb > 256) { cbytes += cb; }
                        }
                        if (cbytes > 0)
                        {
                            st.HotSamples++;
                            st.TotalChanged += cbytes;
                            st.MaxChanged = Math.Max(st.MaxChanged, cbytes);
                            log.WriteLine(string.Format(CultureInfo.InvariantCulture,
                                "t={0:F1} base=0x{1:X8} changed={2}", t, r.Base, cbytes));
                            changedRegions++;
                        }
                        // publish the new sample as the baseline for next round
                        old = prev[r.Base];
                        Buffer.BlockCopy(buf, 0, old, 0, r.Size);
                        prev[r.Base] = old;
                    }

                    if (s % 20 == 0)
                    {
                        Console.WriteLine("[t={0,6:0.0}s] hot regions this sample: {1}",
                            t, changedRegions);
                    }
                    bool stopNow = false;
                    try
                    {
                        if (Console.KeyAvailable)
                        {
                            ConsoleKeyInfo k;
                            k = Console.ReadKey(true);
                            stopNow = true;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // output redirected (or no console): no key detection, just keep running
                    }
                    if (stopNow) { _kbHitStop = true; }
                    Thread.Sleep(sleepMs);
                }

                // final ranking
                var list = new List<Stat>(stats.Values);
                list.Sort(delegate(Stat a, Stat b) { return b.HotSamples.CompareTo(a.HotSamples); });
                Console.WriteLine();
                Console.WriteLine("=== REGION RANKING (hot samples = samples with >256 changed bytes) ===");
                Console.WriteLine("base        size   hot  maxchange");
                for (int i = 0; i < Math.Min(list.Count, 30); i++)
                {
                    Stat st = list[i];
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8} {1,7:D} {2,5} {3,10}",
                        st.Base, st.Size, st.HotSamples, st.MaxChanged));
                }
            }
            Console.WriteLine("digest log: " + logPath);
        }

        private static bool _kbHitStop;

        private sealed class Region { public uint Base; public int Size; }
        private sealed class Stat
        {
            public uint Base;
            public int Size;
            public int HotSamples;
            public int TotalChanged;
            public int MaxChanged;
        }
    }
}
