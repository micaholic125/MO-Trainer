// Experiment #4F (read-only pre-pass for reveal experiment #4): find the
// per-house shroud bitmap.
//
// Reasoning: experiments #1-3 wrote the cell shroud byte, the cell Explored
// flag, and the house Visionary/MapIsClear bytes.  All landed; the screen
// never changed, and the engine actively rewrote the shroud byte.  So the
// data the game actually renders from must be a per-house visibility map.
// A house reveals a disk of cells when a unit moves; the ad-hoc spy-sat
// experiment (sat_a/sat_b snapshots) showed 349 cells going 0xFE->0xFF AT
// THE CELL OBJECTS -- so either the engine syncs per-house -> per-cell for
// the local human house only, or those cell bytes ARE the local player's
// map and the actual drawing ignores even them...  incomplete reasoning.
//
// What this tool does (read-only):
//  1. resolve the local house (static root 0x00A83D4C) and money check;
//  2. scan the house object's first 0x5800 bytes for pointers;
//  3. read the first 0x40 bytes at each pointer target and record:
//       - the fraction of bytes equal to 0xFF (fully-visible fill),
//       - the fraction equal to 0x00,
//       - any >=64-byte run of a single repeated byte;
//     candidates shaped like a 21k-byte bitmap (run-length census) stand out.
//  4. print a ranked candidate list.  THIS IS A CENSUS, NOT A CONCLUSION.
//
// Next step after census (experiment #4): compare two reads N seconds apart
// while the user explores; the per-house bitmap will change ONLY in explored
// cells, and the spy-sat write-test will then be re-run on the winner.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTools
{
    internal static class ShroudCensus
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        private const uint ModuleBase = 0x00400000;
        private const uint ModuleSize = 0x00793000;
        private const uint HouseRoot = 0x00A83D4C;
        private const int MoneyOffset = 0x30C;
        private const int HouseScanBytes = 0x5800;

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

        private static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Process[] found = Process.GetProcessesByName("gamemd");
            if (found.Length == 0)
            {
                Console.WriteLine("gamemd.exe is not running.");
                return;
            }
            _process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, found[0].Id);
            if (_process == IntPtr.Zero) { Console.WriteLine("OpenProcess failed"); return; }
            Console.WriteLine("gamemd.exe pid=" + found[0].Id);
            try { Run(); }
            finally { CloseHandle(_process); }
        }

        private static void Run()
        {
            byte[] root;
            if (!TryRead(HouseRoot, 4, out root)) { Console.WriteLine("root unreadable"); return; }
            uint house = ReadU32(root, 0);
            byte[] mb;
            if (!TryRead(house + MoneyOffset, 4, out mb)) { Console.WriteLine("money unreadable"); return; }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "house=0x{0:X8} money={1}", house, (int)ReadU32(mb, 0)));

            byte[] houseMem;
            if (!TryRead(house, HouseScanBytes, out houseMem))
            {
                Console.WriteLine("cannot read the whole house span; scanning what we got");
            }

            // slot census: (offsetInHouse, pointedToAddress), then inspect targets
            var hits = new List<uint>();
            for (int off = 0; off + 4 <= houseMem.Length; off += 4)
            {
                uint v = (uint)(houseMem[off] | (houseMem[off + 1] << 8)
                    | (houseMem[off + 2] << 16) | (houseMem[off + 3] << 24));
                if (v >= 0x00100000 && v < 0x7FFE0000 && (v & 3) == 0)
                {
                    hits.Add((uint)off);
                }
            }
            Console.WriteLine("plausible 4-aligned pointer slots in house span: " + hits.Count);

            var scored = new List<Candidate>();
            foreach (uint off in hits)
            {
                uint ptr = (uint)(houseMem[off] | (houseMem[off + 1] << 8)
                    | (houseMem[off + 2] << 16) | (houseMem[off + 3] << 24));
                byte[] probe;
                if (!TryRead(ptr, 0x100, out probe)) { continue; }

                int nFF = 0, n00 = 0, maxRun = 0, curRun = 1; byte runByte = probe[0];
                for (int i = 1; i < probe.Length; i++)
                {
                    if (probe[i] == runByte) { curRun++; if (curRun > maxRun) { maxRun = curRun; } }
                    else { runByte = probe[i]; curRun = 1; }
                }
                for (int i = 0; i < probe.Length; i++)
                { if (probe[i] == 0xFF) { nFF++; } else if (probe[i] == 0x00) { n00++; } }

                scored.Add(new Candidate
                {
                    HouseOffset = off,
                    Target = ptr,
                    PctFF = 100.0 * nFF / probe.Length,
                    Pct00 = 100.0 * n00 / probe.Length,
                    MaxRun = maxRun,
                });
            }

            scored.Sort(delegate(Candidate a, Candidate b)
            { return b.MaxRun.CompareTo(a.MaxRun); });

            Console.WriteLine();
            Console.WriteLine("top candidates by longest same-byte run (bitmap-shaped targets):");
            Console.WriteLine("   off      target      %FF   %00   maxrun");
            for (int i = 0; i < Math.Min(scored.Count, 25); i++)
            {
                Candidate c = scored[i];
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "+0x{0:X5}  0x{1:X8}  {2,5:F1} {3,5:F1}   {4}",
                    c.HouseOffset, c.Target, c.PctFF, c.Pct00, c.MaxRun));
            }
        }

        private sealed class Candidate
        {
            public uint HouseOffset;
            public uint Target;
            public double PctFF;
            public double Pct00;
            public int MaxRun;
        }

        private static bool TryRead(uint address, int length, out byte[] buffer)
        {
            buffer = new byte[length];
            IntPtr got;
            if (!ReadProcessMemory(_process, (IntPtr)(ulong)address, buffer, (IntPtr)length, out got)
                || (long)got != length)
            { return false; }
            return true;
        }

        private static uint ReadU32(byte[] b, int off)
        {
            return (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
        }
    }
}
