// One-off dev helper (WRITES to the game): flip every fully-black cell's
// Shroudedness from -2 (0xFE) to -1 (0xFF) -- i.e. reveal the whole map.
//
// Background: the shroud is NOT a table.  Each map cell is its own heap
// object (0x148 bytes) carrying a 1-byte Shroudedness at +0x120 (and
// Foggedness at +0x121).  Layout source: Ares team's YRpp headers
// (archived at <repo>\证据归档\2026-10-13-网络方向\YRpp\), verified on
// 2026-10-13 against two independent samples (Ares crash dump: 10527/10527
// index equation; spy-satellite before/after snapshots: 349 cells FE->FF
// in a disk of radius ~10).  See docs\07-全图视野交接说明.md section 10.
//
// Safety design (the user approved ONE reveal experiment on 2026-10-14,
// option A):
//   * writes ONLY the shroud byte of heap cell objects -- never code pages;
//     each target is re-checked with VirtualQueryEx immediately before
//     writing (committed, not guard, no execute flag, RW/WC).
//   * every cell must pass BOTH identity gates, or it is counted and
//     skipped: gate 1 vtable in module; gate 2 idx == (Y<<9)+X.
//   * only exact 0xFE cells are touched; visible and anim frames stay.
//   * a run of failing gates aborts (the map probably walked off).
//   * every write is verified by reading back; a mismatch is reported.
//   * no "write backwards / undo" pass: reverting would need a second
//     experiment the user did not approve; the map is per-match runtime
//     state and the match itself is the undo.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTools
{
    internal static class RevealOne
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_OPERATION = 0x0008;

        private const uint ModuleBase = 0x00400000;
        private const uint ModuleSize = 0x00793000;

        private const uint MapClassInstance = 0x0087F7E8;
        private const int MapRectOffset = 0xEC;
        private const int CellsItemsOffset = 0x13C;
        private const int CellsCapacityOffset = 0x140;
        private const int CellProbeBytes = 0x124;
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
                Console.WriteLine("  - black cleared across the whole map? -> reply 'cleared'");
                Console.WriteLine("  - cleared, then blackness came back?  -> reply 'refilled' (+ roughly when)");
                Console.WriteLine("  - nothing changed on screen?          -> reply 'no effect'");
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
            int left = (int)ReadU32(head, MapRectOffset);
            int top = (int)ReadU32(head, MapRectOffset + 4);
            int right = (int)ReadU32(head, MapRectOffset + 8);
            int bottom = (int)ReadU32(head, MapRectOffset + 12);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "MapRect=({0},{1})-({2},{3})  capacity=0x{4:X}  items=0x{5:X8}",
                left, top, right, bottom, capacity, items));
            if (items < 0x10000 || capacity != MaxCells)
            {
                Console.WriteLine("Cells chain looks wrong -- refusing to write anything.");
                return;
            }

            int cellCount = 0, black = 0, visible = 0, anim = 0;
            int failedEq = 0, failedVt = 0, unreadable = 0;
            int wrote = 0, skipPage = 0, skipWrite = 0, verifyBad = 0;
            int consecFails = 0;

            for (int idx = 0; idx < capacity; idx++)
            {
                if (_aborted) { break; }

                uint cell = 0;
                bool haveCell = false;
                byte[] slot;
                if (TryRead(items + (uint)(idx * 4), 4, out slot))
                {
                    cell = ReadU32(slot, 0);
                    haveCell = cell >= 0x10000;
                }
                else { unreadable++; continue; }   // the array itself should be solid; don't abort on one miss

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

                int s = (sbyte)body[0x120];
                if (s == -1) { visible++; continue; }
                if (s != -2) { anim++; continue; }   // 0..48 = fade animation, leave it alone
                black++;

                uint field = cell + 0x120;
                if (!PageWritable(field)) { skipPage++; continue; }
                byte[] one = { 0xFF };
                IntPtr written;
                if (!WriteProcessMemory(_process, (IntPtr)(ulong)field, one, (IntPtr)1, out written)
                    || (long)written != 1)
                {
                    skipWrite++;
                    continue;
                }

                byte[] back;
                if (TryRead(field, 1, out back) && back[0] == 0xFF) { wrote++; }
                else { verifyBad++; }
            }

            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "cells {0} | black {1} | visible {2} | anim {3} | vt-fail {4} | eq-fail {5} | unread {6}",
                cellCount, black, visible, anim, failedVt, failedEq, unreadable));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "WRITE: wrote {0} | page-not-writable {1} | write-failed {2} | verify-mismatch {3}",
                wrote, skipPage, skipWrite, verifyBad));
            if (_aborted) { Console.WriteLine("(( aborted early -- see ABORT line above ))"); }
        }

        private static void Abort()
        {
            _aborted = true;
            Console.WriteLine("ABORT: identity gates failing repeatedly -- stopping before any more writes.");
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
            if ((p & 0xF0) != 0) { return false; }          // any execute bit -> never touch
            return (p & 0x04) != 0 || (p & 0x08) != 0;      // READWRITE / WRITECOPY
        }

        private static bool TryRead(uint address, int length, out byte[] buffer)
        {
            buffer = new byte[length];
            IntPtr got;
            if (!ReadProcessMemory(_process, (IntPtr)(ulong)address, buffer, (IntPtr)length, out got)
                || (long)got != length)
            {
                return false;
            }
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
