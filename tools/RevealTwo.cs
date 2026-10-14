// Experiment #2 (WRITES, user-approved option A on 2026-10-14): set the
// "explored" bit in each map cell's Flags dword, on top of the shroud byte.
//
// Lesson from experiment #1 (same match, 16:0x): writing Shroudedness=0xFF
// succeeded on all 8127 black cells, but the engine rewrote the byte back
// within a tick and the screen did not change.  Reading YRpp again showed
// eCellFlags cf_Explored = 0x08 ("this means no shroud") -- the renderer
// consumes the FLAGS dword at CellClass+0x130, not the shroud byte.
//
// This run: for every cell that passes both identity gates,
//   Flags(+0x130) |= 0x08        (read-modify-write, other bits preserved)
//   Shroudedness(+0x120) = 0xFF  (keep experiment #1's write, harmless)
// Read-back verifies both.  Gates, skip counters, abort threshold,
// page-writability check -- same discipline as RevealOne.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTools
{
    internal static class RevealTwo
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_OPERATION = 0x0008;

        private const uint ModuleBase = 0x00400000;
        private const uint ModuleSize = 0x00793000;

        private const uint MapClassInstance = 0x0087F7E8;
        private const int CellsItemsOffset = 0x13C;
        private const int CellsCapacityOffset = 0x140;
        private const int CellProbeBytes = 0x134;   // covers +0x130 Flags dword
        private const int MaxCells = 0x40000;
        private const int MaxConsecutiveGateFails = 256;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(
            IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr written);

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
        private static bool _aborted;

        private static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            Process[] found = Process.GetProcessesByName("gamemd");
            if (found.Length == 0)
            {
                Console.WriteLine("gamemd.exe is not running. Nothing was touched.");
                return;
            }
            _process = OpenProcess(
                PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION,
                false, found[0].Id);
            if (_process == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess failed, Win32=" + Marshal.GetLastWin32Error());
                return;
            }
            Console.WriteLine("gamemd.exe pid=" + found[0].Id);
            try
            {
                Run();
            }
            finally
            {
                Console.WriteLine();
                Console.WriteLine("LOOK AT YOUR SCREEN NOW:");
                Console.WriteLine("  - black cleared map-wide?            -> reply 'cleared'");
                Console.WriteLine("  - cleared then black came back?      -> reply 'refilled' (+ when)");
                Console.WriteLine("  - no change on screen?               -> reply 'no effect'");
                CloseHandle(_process);
            }
        }

        private static void Run()
        {
            byte[] head;
            if (!TryRead(MapClassInstance, 0x160, out head))
            {
                Console.WriteLine("cannot read MapClass instance -- is the game in a match?");
                return;
            }
            uint items = ReadU32(head, CellsItemsOffset);
            int capacity = (int)ReadU32(head, CellsCapacityOffset);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "items=0x{0:X8} capacity=0x{1:X}", items, capacity));
            if (items < 0x10000 || capacity != MaxCells)
            {
                Console.WriteLine("Cells chain looks wrong -- refusing to write anything.");
                return;
            }

            int cellCount = 0, black = 0, visibleSh = 0, anim = 0;
            int alreadyExplored = 0, wroteFlags = 0, wroteShroud = 0;
            int skipPage = 0, skipWrite = 0, verifyBad = 0;
            int failedEq = 0, failedVt = 0;
            int consecFails = 0;

            for (int idx = 0; idx < capacity; idx++)
            {
                if (_aborted) { break; }

                byte[] slotB;                if (!TryRead(items + (uint)(idx * 4), 4, out slotB)) { continue; }
                uint cell = ReadU32(slotB, 0);
                if (cell < 0x10000) { continue; }

                byte[] body;
                if (!TryRead(cell, CellProbeBytes, out body)) { continue; }
                if (!InModule(ReadU32(body, 0)))
                {
                    failedVt++;
                    if (++consecFails >= MaxConsecutiveGateFails) { Abort(); return; }
                    continue;
                }

                int x = (short)(body[0x24] | (body[0x25] << 8));
                int y = (short)(body[0x26] | (body[0x27] << 8));
                if (((y << 9) + x) != idx)
                {
                    failedEq++;
                    if (++consecFails >= MaxConsecutiveGateFails) { Abort(); return; }
                    continue;
                }
                consecFails = 0;
                cellCount++;

                uint flags = ReadU32(body, 0x130);
                if ((flags & 0x08) != 0)
                {
                    alreadyExplored++;
                    // still make the shroud byte visible for consistency
                    if ((sbyte)body[0x120] == -2)
                    {
                        uint f = cell + 0x120;
                        if (PageWritable(f))
                        {
                            byte[] ff = { 0xFF };
                            IntPtr w;
                            if (WriteProcessMemory(_process, (IntPtr)(ulong)f, ff, (IntPtr)1, out w)
                                && (long)w == 1) { wroteShroud++; }
                        }
                    }
                    continue;
                }

                int sh = (sbyte)body[0x120];
                if (sh == -1) { visibleSh++; } else if (sh != -2) { anim++; } else { black++; }

                // ---- the write: Flags |= 0x08, then shroud byte -> 0xFF ----
                uint flagsAddr = cell + 0x130;
                if (!PageWritable(flagsAddr)) { skipPage++; continue; }
                byte[] four = new byte[4];
                four = BitConverter.GetBytes(flags | 0x08);
                IntPtr written;
                if (!WriteProcessMemory(_process, (IntPtr)(ulong)flagsAddr, four, (IntPtr)4, out written)
                    || (long)written != 4)
                { skipWrite++; continue; }

                uint field = cell + 0x120;
                if (PageWritable(field))
                {
                    byte[] ff = { 0xFF };
                    IntPtr w;
                    if (WriteProcessMemory(_process, (IntPtr)(ulong)field, ff, (IntPtr)1, out w)
                        && (long)w == 1) { wroteShroud++; }
                }

                // read-back verification of the flag word
                byte[] back;
                if (TryRead(flagsAddr, 4, out back)
                    && (ReadU32(back, 0) & 0x08) != 0)
                { wroteFlags++; }
                else { verifyBad++; }
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "cells {0} | already-explored {1} | black {2} | visible-sh {3} | anim {4} | vt-fail {5} | eq-fail {6}",
                cellCount, alreadyExplored, black, visibleSh, anim, failedVt, failedEq));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "WRITE: flags+0x08 ok {0} | shroud=FF ok {1} | page-skip {2} | write-fail {3} | verify {4}",
                wroteFlags, wroteShroud, skipPage, skipWrite, verifyBad));
            if (_aborted) { Console.WriteLine("(( aborted early ))"); }
        }

        private static void Abort()
        {
            _aborted = true;
            Console.WriteLine("ABORT: identity gates failing repeatedly -- stopping before further writes.");
        }

        private static bool PageWritable(uint address)
        {
            MEMORY_BASIC_INFORMATION64 info;
            IntPtr got = VirtualQueryEx(_process, (IntPtr)(ulong)address, out info,
                (IntPtr)Marshal.SizeOf(typeof(MEMORY_BASIC_INFORMATION64)));
            if (got == IntPtr.Zero) { return false; }
            if (info.State != 0x1000) { return false; }
            uint p = info.Protect;
            if ((p & 0x01) != 0 || (p & 0x100) != 0) { return false; }
            if ((p & 0xF0) != 0) { return false; }
            return (p & 0x04) != 0 || (p & 0x08) != 0;
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

        private static bool InModule(uint v)
        {
            return v >= ModuleBase && v < ModuleBase + ModuleSize;
        }
    }
}
