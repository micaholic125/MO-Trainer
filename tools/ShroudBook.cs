// ShroudBook (read-only): find the per-house visibility book by comparing
// the three houses in the match.
//
// Method: user's house is nearly fully explored (mid-match, most of the map
// revealed); AI houses see far less.  If a per-house book exists, each
// house points at its own copy.  So: scan the HOUSE SPANS (each 0x5800
// bytes) for pointer slots, and for each unique pointer target look for
// 21609-byte (map-size) or 2702-byte (bit-plane) arrays whose FF/FE mix
// DIFFERS between houses.  Only print targets that differ by >= 2x, and
// targets shaped like {0x00..0xFF} with a plausible byte distribution.
//
// Read-only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTools
{
    internal static class ShroudBook
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const uint HouseArrayItems = 0x1FFEA090; // runtime value read live below
        private const int MoneyOffset = 0x30C;
        private const int HouseScanBytes = 0x5800;
        private const int MapBytes = 0x5479;   // 21609
        private const int BitBytes = 0x0AF1;   // 2702 (21608/8)

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        private static IntPtr _process = IntPtr.Zero;

        private static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Process[] found = Process.GetProcessesByName("gamemd");
            if (found.Length == 0) { Console.WriteLine("game not running"); return; }
            _process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, found[0].Id);
            if (_process == IntPtr.Zero) { Console.WriteLine("OpenProcess failed"); return; }
            Console.WriteLine("gamemd.exe pid=" + found[0].Id);
            try { Run(); }
            finally { CloseHandle(_process); }
        }

        private static void Run()
        {
            // live-read the house array (its runtime items pointer varies)
            byte[] ab;
            if (!TryRead(0x00A80228, 32, out ab))
            { Console.WriteLine("cannot read house array"); return; }
            uint items = ReadU32(ab, 4);
            int count = (int)ReadU32(ab, 16);
            if (items < 0x10000 || count <= 0 || count > 16)
            { Console.WriteLine("house array looks wrong (items=0x" + items.ToString("X8")
                + " count=" + count + ")"); return; }

            var houses = new List<uint>();
            for (int i = 0; i < count; i++)
            {
                byte[] slot;
                if (!TryRead(items + (uint)(4 * i), 4, out slot)) { continue; }
                uint p = ReadU32(slot, 0);
                if (p >= 0x10000) { houses.Add(p); }
            }
            Console.WriteLine("houses: " + houses.Count);
            foreach (uint p in houses)
            {
                byte[] mb;
                int money = (TryRead(p + MoneyOffset, 4, out mb))
                    ? (int)ReadU32(mb, 0) : -1;
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "  0x{0:X8}  money {1}", p, money));
            }

            // per-house pointer census
            var perHouse = new Dictionary<uint, HashSet<uint>>();
            foreach (uint p in houses)
            {
                byte[] mem;
                var targets = new HashSet<uint>();
                if (TryRead(p, HouseScanBytes, out mem))
                {
                    for (int off = 0; off + 4 <= mem.Length; off += 4)
                    {
                        uint v = ReadU32(mem, off);
                        if (v >= 0x00100000 && v < 0x7FFE0000 && (v & 3) == 0)
                        { targets.Add(v); }
                    }
                }
                perHouse[p] = targets;
            }

            // consensus target = appears in user's house AND at least one AI house
            uint self = houses[0];
            var aiTargets = new HashSet<uint>();
            for (int i = 1; i < houses.Count; i++)
            { aiTargets.UnionWith(perHouse[houses[i]]); }
            var common = new List<uint>(perHouse[self]);
            common.RemoveAll(t => !aiTargets.Contains(t) || t < 0x00100000 || t >= 0x7FFE0000);
            Console.WriteLine("pointers common to self and AI houses: " + common.Count);

            // For each common target, sample the first 0x5479 bytes and
            // compare "FF-ratio" across the houses.  A per-house book pointed
            // to by all houses would be at DIFFERENT addresses (per-house
            // allocation), so instead compare the per-house SPAN for embedded
            // byte arrays directly: pointwise scan for MapBytes arrays
            // embedded in the house span near-shroud table.
            Console.WriteLine();
            Console.WriteLine("byte-array probes inside each house span (0x5479-byte windows, stride 4):");
            foreach (uint p in houses)
            {
                ProbeSpan(p, "house 0x" + p.ToString("X8"));
            }
        }

        private static void ProbeSpan(uint house, string label)
        {
            byte[] mem;
            if (!TryRead(house, HouseScanBytes, out mem)) { Console.WriteLine(label + ": unreadable"); return; }
            int found = 0;
            for (int off = 0; off + MapBytes <= mem.Length; off += 4)
            {
                // quick reject: first byte and last, then full histogram
                byte lo = mem[off], hi = mem[off];
                double ff = 0, fe = 0, other = 0;
                bool sane = true;
                for (int i = 0; i < MapBytes; i += 16)
                {
                    byte v = mem[off + i];
                    if (v == 0xFF) { ff++; }
                    else if (v == 0xFE) { fe++; }
                    else { other++; if (v > 0x35 && v != 0) { sane = false; break; } }
                }
                if (!sane) { continue; }
                int sampled = (MapBytes + 15) / 16;
                double ffRate = ff / sampled, feRate = fe / sampled;
                if (other <= 2 && (ffRate > 0.45 || feRate > 0.45) && (ffRate + feRate) > 0.9)
                {
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "{0}: +0x{1:X5} sampledFF={2,4:P1} sampledFE={3,4:P1}",
                        label, off, ffRate, feRate));
                    found++;
                    if (found >= 5) { break; }
                }
            }
            if (found == 0) { Console.WriteLine(label + ": no byte-plane found in span"); }
        }

        private static bool TryRead(uint address, int length, out byte[] buffer)
        {
            buffer = new byte[length];
            IntPtr got;
            if (!ReadProcessMemory(_process, (IntPtr)(ulong)address, buffer,
                (IntPtr)length, out got) || (long)got != length)
            { return false; }
            return true;
        }

        private static uint ReadU32(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }
    }
}
