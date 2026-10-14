// Experiment #3 (WRITES, user-approved): set the player's HouseClass
// "Visionary" / "MapIsClear" bytes to 1.
//
// Why: experiment #1 (shroud byte) and #2 (cell Flags explored bit)
// both landed in memory but the screen did not change; the engine also
// rewrote the shroud byte back while KEEPING the explored flag.  YRpp
// HouseClass.h declares, right after PlanningPaths[0x0C]:
//     char Visionary;   // ??? exe says so
//     bool MapIsClear;
// and the user confirms the OBSERVER faction in-game sees the whole map.
// These two bytes are most likely exactly what "observer" means.
//
// Offset check: replaying the field chain from the declared anchor
// DWORD unknown_1E4 (= 0x1E4, int alignment re-aligning SelectedPathIndex
// to 0x20C after the 15-byte unknown_1FD hole) puts
//     Visionary  @ +0x240
//     MapIsClear @ +0x241
// SAFETY: only 2 bytes on the player house object are written; each write
// is verified by read-back; nothing else in the process is touched.
// The user's house pointer comes from the project-calibrated static root
// 0x00A83D4C, cross-checked with the money chain (+0x30C, known-good).
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTools
{
    internal static class RevealThree
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_OPERATION = 0x0008;

        private const uint ModuleBase = 0x00400000;
        private const uint ModuleSize = 0x00793000;

        private const uint HouseRoot = 0x00A83D4C;
        private const int MoneyOffset = 0x30C;
        private const int VisionaryOffset = 0x240;
        private const int MapIsClearOffset = 0x241;

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

        private static IntPtr _process = IntPtr.Zero;

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
                Console.WriteLine("  - whole map visible (mini-map too)? -> reply 'cleared'");
                Console.WriteLine("  - briefly then back?                -> reply 'refilled' (+ when)");
                Console.WriteLine("  - no change?                        -> reply 'no effect'");
                CloseHandle(_process);
            }
        }

        private static void Run()
        {
            byte[] root;
            if (!TryRead(HouseRoot, 4, out root))
            {
                Console.WriteLine("cannot read player static root 0x" + HouseRoot.ToString("X8"));
                return;
            }
            uint house = ReadU32(root, 0);
            // The house pointer is a private-heap address (0x2xxxxxxxx) -- a normal
            // YR heap block, NOT inside the module. The original sanity check was
            // wrong (it required the house itself to be inside the module); what
            // actually validates the chain is the money field reading a plausible
            // value, which is the calibration cross-check this project already uses.
            if (house < 0x10000 || house > 0x7FFEFFFF)
            {
                Console.WriteLine("static root value 0x" + house.ToString("X8")
                    + " is not a sane pointer -- not in a match?");
                return;
            }

            byte[] moneyB;
            if (!TryRead(house + MoneyOffset, 4, out moneyB))
            {
                Console.WriteLine("cannot read money field -- layout mismatch, refusing to write.");
                return;
            }
            int money = (int)ReadU32(moneyB, 0);
            if (money < 0 || money > 100000000)
            {
                Console.WriteLine("money field reads " + money
                    + " -- layout mismatch, refusing to write.");
                return;
            }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "house = 0x{0:X8}  money = {1} (layout confirmed)", house, money));

            byte[] before;
            TryRead(house + VisionaryOffset, 2, out before);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "before: Visionary(+0x240) = {0}  MapIsClear(+0x241) = {1}",
                (sbyte)before[0], before[1]));

            byte[] one = { (byte)1, (byte)1 };
            IntPtr w;
            uint addr = house + VisionaryOffset;
            if (!WriteProcessMemory(_process, (IntPtr)(ulong)addr, one, (IntPtr)2, out w)
                || (long)w != 2)
            {
                Console.WriteLine("write failed, Win32=" + Marshal.GetLastWin32Error());
                return;
            }

            byte[] after;
            bool ok = TryRead(house + VisionaryOffset, 2, out after)
                && after[0] == 1 && after[1] == 1;
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "after : Visionary = {0}  MapIsClear = {1}  (read-back {2})",
                after[0], after[1], ok ? "OK" : "MISMATCH"));

            // re-check after a moment: does the engine scrub these too?
            System.Threading.Thread.Sleep(3000);
            byte[] later;
            if (TryRead(house + VisionaryOffset, 2, out later))
            {
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "+3s   : Visionary = {0}  MapIsClear = {1}", later[0], later[1]));
            }
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
