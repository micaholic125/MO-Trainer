// ShroudBook2 (read-only): examine the 20 pointer slots common to all three
// houses: if the same POINTER VALUE is stored in each house, but the target
// holds per-house data (e.g., an array of per-house planes), that's the book.
// For each shared target, read 0x8000 bytes and print the histogram, plus
// check for an "index by house" pattern: N sub-planes separated by ~0x5479
// or 0xAF1 bytes (byte/bit plane sizes).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTools
{
    internal static class ShroudBook2
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

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
            try { Run(); }
            finally { CloseHandle(_process); }
        }

        private static void Run()
        {
            byte[] ab;
            if (!TryRead(0x00A80228, 32, out ab)) { Console.WriteLine("no house array"); return; }
            uint items = ReadU32(ab, 4);
            int count = (int)ReadU32(ab, 16);
            if (items < 0x10000 || count <= 0 || count > 16)
            { Console.WriteLine("bad array"); return; }

            var houses = new List<uint>();
            for (int i = 0; i < count; i++)
            {
                byte[] s;
                if (!TryRead(items + (uint)(4 * i), 4, out s)) { continue; }
                uint p = ReadU32(s, 0);
                if (p >= 0x10000) { houses.Add(p); }
            }
            if (houses.Count < 2) { Console.WriteLine("fewer than 2 houses"); return; }

            // pointer slots keyed by offset, present in every house, with the SAME value
            var commonTargets = new SortedDictionary<uint, uint>();  // houseOffset -> pointerValue
            {
                byte[] m0;
                if (!TryRead(houses[0], 0x5800, out m0)) { return; }
                var othersMem = new List<byte[]>();
                bool allReadable = true;
                for (int h = 1; h < houses.Count; h++)
                {
                    byte[] m;
                    if (!TryRead(houses[h], 0x5800, out m)) { allReadable = false; break; }
                    othersMem.Add(m);
                }
                if (!allReadable) { Console.WriteLine("some house span unreadable"); return; }

                for (int off = 0; off + 4 <= m0.Length; off += 4)
                {
                    uint v0 = ReadU32(m0, off);
                    if (v0 < 0x00100000 || v0 >= 0x7FFE0000 || (v0 & 3) != 0) { continue; }
                    bool same = true;
                    foreach (byte[] m in othersMem)
                    {
                        if (ReadU32(m, off) != v0) { same = false; break; }
                    }
                    if (same) { commonTargets[(uint)off] = v0; }
                }
            }
            Console.WriteLine("identical pointer slots across all houses: " + commonTargets.Count);

            foreach (KeyValuePair<uint, uint> kv in commonTargets)
            {
                uint target = kv.Value;
                byte[] t;
                if (!TryRead(target, 0x8000, out t))
                {
                    Console.WriteLine("+0x{0:X4} -> 0x{1:X8}: <unreadable>", kv.Key, target);
                    continue;
                }
                // histogram
                Dictionary<byte, int> hist = new Dictionary<byte, int>();
                for (int i = 0; i < t.Length; i++)
                {
                    int c;
                    hist.TryGetValue(t[i], out c);
                    hist[t[i]] = c + 1;
                }
                // ff/fe share
                int nFF = 0, nFE = 0, n00 = 0;
                hist.TryGetValue(0xFF, out nFF);
                hist.TryGetValue(0xFE, out nFE);
                hist.TryGetValue(0x00, out n00);
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "+0x{0:X4} -> 0x{1:X8}  ff={2,6} fe={3,6} 00={4,6}  other={5}",
                    kv.Key, target, nFF, nFE, n00, t.Length - nFF - nFE - n00));
            }
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
