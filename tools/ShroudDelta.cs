// ShroudDelta (read-only): capture -> user reveals -> capture, then report EVERY
// byte that changed, grouped by what it changed FROM and TO.
//
// Why this and not another shape-guess: the last two passes (ShroudPlane) tested
// "is there a flat plane of black/visible bytes somewhere" and came back empty in
// every encoding.  So stop guessing the shape and measure the event directly:
// when the shroud advances, something must be written.  Whatever is written will
// show up here as a specific (old -> new) byte transition in a specific place.
//
// The 2026-10-13 satellite experiment did a whole-memory diff too, but its
// criteria demanded ">= 12-byte continuous runs" -- which silently DISCARDS the
// single-byte flips a per-cell structure produces.  This tool keeps every byte
// and sorts by transition, so a per-cell write cannot hide.
//
// Read-only: OpenProcess(PROCESS_VM_READ) only, ReadProcessMemory only.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MoTools
{
    internal static class ShroudDelta
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr VirtualQueryEx(
            IntPtr handle, IntPtr address, out MEMORY_BASIC_INFORMATION64 info, IntPtr length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

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
            public int Size;
            public byte[] Prev;
        }

        private static IntPtr _process = IntPtr.Zero;
        private static long _bandChanged;
        private static int _bandRegions;

        private sealed class RegionChange
        {
            public uint Base;
            public int Size;
            public int Changed;
            public bool InBand;
        }

        private static readonly List<RegionChange> regionChanges = new List<RegionChange>();

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            double seconds = args.Length > 0 ? double.Parse(args[0], CultureInfo.InvariantCulture) : 20.0;
            try
            {
                return RealMain(seconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine("EXCEPTION: " + ex.Message);
                return 9;
            }
            finally
            {
                if (_process != IntPtr.Zero) { CloseHandle(_process); }
            }
        }

        private static int RealMain(double seconds)
        {
            System.Diagnostics.Process[] found =
                System.Diagnostics.Process.GetProcessesByName("gamemd");
            if (found.Length == 0)
            {
                Console.WriteLine("gamemd.exe is not running.");
                return 3;
            }
            _process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, found[0].Id);
            if (_process == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess failed, Win32=" + Marshal.GetLastWin32Error());
                return 4;
            }

            List<Region> regions = EnumerateRegions();
            long total = 0;
            foreach (Region r in regions) { total += r.Size; }
            Console.WriteLine("capturing " + regions.Count + " writable regions (~"
                + (total >> 20) + " MB)...");
            int captured = 0;
            for (int i = 0; i < regions.Count; i++)
            {
                byte[] buf = new byte[regions[i].Size];
                if (ReadWhole(regions[i].Base, buf))
                {
                    regions[i].Prev = buf;
                    captured++;
                }
            }
            Console.WriteLine("captured " + captured + " regions.  NOW: reveal map for "
                + seconds.ToString("F0", CultureInfo.InvariantCulture) + "s...");
            Console.Out.Flush();
            Thread.Sleep((int)(seconds * 1000.0));
            Console.WriteLine("second capture...");
            Console.Out.Flush();

            int changedRegions = 0;
            long totalChanged = 0;
            // Global transition histogram: key = (old << 8) | new
            Dictionary<int, long> globalHist = new Dictionary<int, long>();
            List<string> shroudLines = new List<string>();

            for (int i = 0; i < regions.Count; i++)
            {
                Region r = regions[i];
                if (r.Prev == null) { continue; }
                byte[] cur = new byte[r.Size];
                if (!ReadWhole(r.Base, cur)) { continue; }

                Dictionary<int, int> hist = new Dictionary<int, int>();
                List<int> shroudOffsets = new List<int>();
                int changed = 0;
                for (int k = 0; k < cur.Length; k++)
                {
                    byte a = r.Prev[k], b = cur[k];
                    if (a == b) { continue; }
                    changed++;
                    int key = (a << 8) | b;
                    int n;
                    hist[key] = hist.TryGetValue(key, out n) ? n + 1 : 1;
                    long g;
                    globalHist[key] = globalHist.TryGetValue(key, out g) ? g + 1 : 1;
                    // Anything involving 0xFE / 0xFF is a shroud-flip suspect.
                    if ((a == 0xFE || a == 0xFF || b == 0xFE || b == 0xFF) && shroudOffsets.Count < 64)
                    {
                        shroudOffsets.Add(k);
                    }
                }
                if (changed == 0) { continue; }
                changedRegions++;
                totalChanged += changed;

                // Rendering heartbeat: the two known screen-buffer bands.  If
                // these do NOT move, the game was not drawing at all during the
                // window (e.g. it lost focus behind a UAC prompt), and any
                // "quiet region" conclusion drawn from this run is worthless.
                bool inBand = (r.Base >= 0x0B500000 && r.Base < 0x0B860000)
                    || (r.Base >= 0x0FDE0000 && r.Base < 0x10000000);
                if (inBand) { _bandChanged += changed; _bandRegions++; }

                RegionChange rc = new RegionChange();
                rc.Base = r.Base;
                rc.Size = r.Size;
                rc.Changed = changed;
                rc.InBand = inBand;
                regionChanges.Add(rc);

                if (shroudOffsets.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("0x").Append(r.Base.ToString("X8"))
                      .Append("  size=").Append(r.Size)
                      .Append("  changed=").Append(changed)
                      .Append("  FE/FF-touching=").Append(shroudOffsets.Count >= 64 ? "64+" : shroudOffsets.Count.ToString())
                      .Append("  first offsets:");
                    for (int k = 0; k < shroudOffsets.Count && k < 24; k++)
                    {
                        sb.Append(" 0x").Append(shroudOffsets[k].ToString("X"));
                        sb.Append('(').Append(r.Prev[shroudOffsets[k]].ToString("X2"))
                          .Append("->").Append(cur[shroudOffsets[k]].ToString("X2")).Append(')');
                    }
                    shroudLines.Add(sb.ToString());
                }
            }

            Console.WriteLine();
            Console.WriteLine("=== SUMMARY ===");
            Console.WriteLine("regions with any change: " + changedRegions);
            Console.WriteLine("total changed bytes: " + totalChanged);
            Console.WriteLine();
            Console.WriteLine("=== RENDERING HEARTBEAT (screen-buffer bands) ===");
            Console.WriteLine("changed bytes in known render bands: " + _bandChanged
                + " across " + _bandRegions + " regions");
            Console.WriteLine(_bandChanged > 100000
                ? "VERDICT: the game WAS drawing during this window (recording is usable)."
                : "VERDICT: the game was NOT drawing (window unusable as an idle baseline!)");
            Console.WriteLine();
            Console.WriteLine("=== REGIONS BY CHANGED BYTES (render bands excluded) ===");
            List<RegionChange> ranked = new List<RegionChange>();
            for (int i = 0; i < regionChanges.Count; i++)
            {
                if (!regionChanges[i].InBand) { ranked.Add(regionChanges[i]); }
            }
            ranked.Sort(delegate(RegionChange a, RegionChange b) { return b.Changed.CompareTo(a.Changed); });
            for (int i = 0; i < ranked.Count && i < 40; i++)
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  0x{0:X8}  size={1,9}  changed={2,9}  ({3:F2}%)",
                    ranked[i].Base, ranked[i].Size, ranked[i].Changed,
                    100.0 * ranked[i].Changed / ranked[i].Size));
            }
            Console.WriteLine();
            Console.WriteLine("=== TOP TRANSITIONS (old -> new, whole memory) ===");
            List<KeyValuePair<int, long>> list = new List<KeyValuePair<int, long>>(globalHist);
            list.Sort(delegate(KeyValuePair<int, long> a, KeyValuePair<int, long> b)
            {
                return b.Value.CompareTo(a.Value);
            });
            for (int i = 0; i < list.Count && i < 30; i++)
            {
                int key = list[i].Key;
                Console.WriteLine("  " + ((key >> 8) & 0xFF).ToString("X2") + " -> "
                    + (key & 0xFF).ToString("X2") + "   " + list[i].Value);
            }

            Console.WriteLine();
            Console.WriteLine("=== REGIONS WITH 0xFE/0xFF-TRANSITIONS (the shroud-flip suspects) ===");
            if (shroudLines.Count == 0)
            {
                Console.WriteLine("(none -- nothing in memory flipped to or from FE/FF while you revealed)");
            }
            else
            {
                for (int i = 0; i < shroudLines.Count && i < 40; i++)
                {
                    Console.WriteLine(shroudLines[i]);
                }
                if (shroudLines.Count > 40)
                {
                    Console.WriteLine("... and " + (shroudLines.Count - 40) + " more regions");
                }
            }
            return 0;
        }

        private static List<Region> EnumerateRegions()
        {
            List<Region> regions = new List<Region>();
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
                bool writable = (info.State == 0x1000) && ((info.Protect & 0xF0) == 0)
                    && ((info.Protect & 0x04) != 0 || (info.Protect & 0x08) != 0);
                if (writable && info.BaseAddress < 0xFFFFFFFFul && size <= 0x2000000)
                {
                    Region r = new Region();
                    r.Base = (uint)info.BaseAddress;
                    r.Size = (int)size;
                    regions.Add(r);
                }
                addr = info.BaseAddress + size;
            }
            return regions;
        }

        private static bool ReadWhole(uint address, byte[] buffer)
        {
            int done = 0;
            while (done < buffer.Length)
            {
                int chunk = buffer.Length - done;
                if (chunk > 65536) { chunk = 65536; }
                IntPtr got;
                byte[] temp = new byte[chunk];
                if (!ReadProcessMemory(_process, (IntPtr)(ulong)(address + (uint)done),
                    temp, (IntPtr)chunk, out got) || (long)got != chunk)
                {
                    return false;
                }
                Buffer.BlockCopy(temp, 0, buffer, done, chunk);
                done += chunk;
            }
            return true;
        }
    }
}
