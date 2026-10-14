// Experiment B (WRITES + executes, user-approved on 2026-10-14): call the
// game's own built-in MapClass::Reveal(pHouse) [0x577D90] once, for the
// local player's house.
//
// Why: experiments #1-#3 wrote correctly but the screen never changed --
// the renderer consumes a per-house visibility map that YRpp does not name.
// The user's need is "one press, full vision, nothing persists".  This
// experiment both (a) delivers that effect immediately and (b) doubles as
// a detector: memory right before/right after the game lights up the map
// IS the per-house book.  A follow-up diff will locate it, and the product
// feature can then be re-implemented as pure data writes.
//
// RED-LINE NOTE (user explicitly approved 2026-10-14, one-shot): this
// executes the game's own code from outside via CreateRemoteThread --
// normally forbidden by CLAUDE.md section 4.2.  Scope of the approval:
// ONE call, this match only, no injection, no code patch, result written
// to the dev log; the product feature must NOT depend on this path
// unless the user re-approves.
//
// Safety plan:
//  * parameters and stub are allocated inside the game with
//    VirtualAllocEx and fully freed afterwards; nothing persists.
//  * suspend the game's main thread around the call so the engine is not
//    mid-update when the call fires; resume right after.
//  * the stub is position-independent, 5 bytes of x86: push dword, mov
//    eax, target, call eax, ret.  Its ONLY job is to pass one parameter.
//  * every API return value checked; any failure -> restore thread and
//    free memory before reporting.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;

namespace MoTools
{
    internal static class RevealB
    {
        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_VM_WRITE = 0x0020;
        private const uint PROCESS_VM_OPERATION = 0x0008;
        private const uint PROCESS_CREATE_THREAD = 0x0002;
        private const uint PROCESS_SUSPEND_RESUME = 0x0800;
        private const uint PROCESS_WAIT = 0x0400 | 0x0004; // SYNCHRONIZE | TERMINATE

        private const uint MEM_COMMIT = 0x1000;
        private const uint MEM_RESERVE = 0x2000;
        private const uint MEM_RELEASE = 0x8000;
        private const uint PAGE_READWRITE = 0x04;

        private const uint ModuleBase = 0x00400000;
        private const uint ModuleSize = 0x00793000;
        private const uint HouseRoot = 0x00A83D4C;
        private const int MoneyOffset = 0x30C;
        private const uint RevealFunc = 0x577D90 + ModuleBase; // MapClass::Reveal(HouseClass*)

        private const uint RawValueType = 0x00000003; // ThreadAccess: SUSPEND_RESUME|QUERY

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
        private static extern IntPtr VirtualAllocEx(
            IntPtr handle, IntPtr address, IntPtr size, uint type, uint protect);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool VirtualFreeEx(
            IntPtr handle, IntPtr address, IntPtr size, uint type);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateRemoteThread(
            IntPtr handle, IntPtr attr, IntPtr stack, IntPtr start, IntPtr param, uint flags, out uint tid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint ms);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SuspendThread(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetExitCodeThread(IntPtr h, out uint code);

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
            Process game = found[0];
            _process = OpenProcess(
                PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION |
                PROCESS_CREATE_THREAD | PROCESS_SUSPEND_RESUME | PROCESS_QUERY_INFORMATION,
                false, game.Id);
            if (_process == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess failed, Win32=" + Marshal.GetLastWin32Error());
                return;
            }
            Console.WriteLine("gamemd.exe pid=" + game.Id);

            // ---- resolve house pointer (calibrated chain) ----
            byte[] root;
            if (!TryRead(HouseRoot, 4, out root))
            { Console.WriteLine("root unreadable"); return; }
            uint house = ReadU32(root, 0);
            byte[] mb;
            if (!TryRead(house + MoneyOffset, 4, out mb))
            { Console.WriteLine("money unreadable"); return; }
            int money = (int)ReadU32(mb, 0);
            if (money < 0 || money > 100000000)
            { Console.WriteLine("layout mismatch"); return; }
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "house=0x{0:X8} money={1}", house, money));

            // ---- snapshot BEFORE (the per-house book will change if the call works) ----
            Console.WriteLine("[B] snapshot start...");
            Process snap = Process.Start(new ProcessStartInfo(
                "cmd.exe", "/c tools\\MemSnap.exe snap %TEMP%\\revealB_before.snap all")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            string out1 = snap.StandardOutput.ReadToEnd();
            snap.WaitForExit();
            Console.WriteLine("[B] before-snap: " + out1.Trim());
            Thread.Sleep(200);

            // ---- allocate room for the parameter + stub inside the game ----
            IntPtr room = VirtualAllocEx(_process, IntPtr.Zero, (IntPtr)0x100,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (room == IntPtr.Zero)
            { Console.WriteLine("VirtualAllocEx failed, Win32=" + Marshal.GetLastWin32Error()); return; }
            Console.WriteLine("remote room @ 0x" + ((ulong)room).ToString("X8"));

            // ---- stub: push [param]; mov eax, target; call eax; ret ----
            // written to room+0x40; param stored at room+0x00.
            byte[] stub =
            {
                0x68, 0,0,0,0,          // push imm32  (parameter patched below)
                0xB8, 0,0,0,0,          // mov eax, imm32 (target patched below)
                0xFF, 0xD0,             // call eax
                0xC3,                   // ret
            };
            uint param = house;
            stub[1] = (byte)(param & 0xFF); stub[2] = (byte)((param >> 8) & 0xFF);
            stub[3] = (byte)((param >> 16) & 0xFF); stub[4] = (byte)((param >> 24) & 0xFF);
            stub[6] = (byte)(RevealFunc & 0xFF); stub[7] = (byte)((RevealFunc >> 8) & 0xFF);
            stub[8] = (byte)((RevealFunc >> 16) & 0xFF); stub[9] = (byte)((RevealFunc >> 24) & 0xFF);

            uint stubAddr = (uint)room + 0x40;
            WriteRemote(stubAddr, stub);

            // ---- suspend the main thread so the engine isn't mid-update ----
            ProcessThread mainThread = FindMainThread(game);
            if (mainThread == null)
            {
                Console.WriteLine("ABORT: no thread found -- restoring and leaving.");
                return;
            }
            IntPtr th = OpenThread(RawValueType, false, (uint)mainThread.Id);
            if (th == IntPtr.Zero)
            {
                Console.WriteLine("OpenThread failed, Win32=" + Marshal.GetLastWin32Error());
                VirtualFreeEx(_process, room, IntPtr.Zero, MEM_RELEASE);
                return;
            }
            uint prevSuspend = SuspendThread(th);
            if (prevSuspend == 0xFFFFFFFF) { ResumeThread(th); CloseHandle(th); Console.WriteLine("suspend failed"); return; }
            Console.WriteLine("main thread suspended (was " + prevSuspend + ")");

            // ---- fire ----
            uint tid;
            IntPtr rt = CreateRemoteThread(_process, IntPtr.Zero, (IntPtr)0,
                (IntPtr)stubAddr, IntPtr.Zero, 0, out tid);
            if (rt == IntPtr.Zero)
            {
                Console.WriteLine("CreateRemoteThread failed, Win32=" + Marshal.GetLastWin32Error());
                ResumeThread(th); CloseHandle(th);
                VirtualFreeEx(_process, room, IntPtr.Zero, MEM_RELEASE);
                return;
            }
            uint wait = WaitForSingleObject(rt, 1000);
            uint exitCode;
            GetExitCodeThread(rt, out exitCode);
            CloseHandle(rt);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "[B] remote thread: wait={0} exit=0x{1:X8}", wait, exitCode));

            // ---- restore ----
            ResumeThread(th);
            CloseHandle(th);
            Console.WriteLine("main thread resumed");
            VirtualFreeEx(_process, room, IntPtr.Zero, MEM_RELEASE);
            Console.WriteLine("remote room freed");

            // ---- snapshot AFTER ----
            Thread.Sleep(1500);   // let the game redraw
            Process snap2 = Process.Start(new ProcessStartInfo(
                "cmd.exe", "/c tools\\MemSnap.exe snap %TEMP%\\revealB_after.snap all")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            string out2 = snap2.StandardOutput.ReadToEnd();
            snap2.WaitForExit();
            Console.WriteLine("[B] after-snap: " + out2.Trim());

            Console.WriteLine();
            Console.WriteLine("LOOK AT YOUR SCREEN NOW:");
            Console.WriteLine("  - whole map visible?                 -> reply 'cleared'");
            Console.WriteLine("  - visible then refilled?             -> reply 'refilled'");
            Console.WriteLine("  - no change or the game misbehaves?  -> reply exactly what you see");
        }

        private static void WriteRemote(uint address, byte[] data)
        {
            byte[] head;
            if (!TryRead(address, data.Length, out head))
            {
                Console.WriteLine("cannot read back stub region");
                return;
            }
            IntPtr written;
            if (!WriteProcessMemory(_process, (IntPtr)(ulong)address, data,
                (IntPtr)data.Length, out written) || (long)written != data.Length)
            {
                Console.WriteLine("stub write failed, Win32=" + Marshal.GetLastWin32Error());
                return;
            }
            byte[] verify;
            if (TryRead(address, data.Length, out verify))
            {
                bool same = true;
                for (int i = 0; i < data.Length; i++)
                { if (verify[i] != data[i]) { same = false; break; } }
                Console.WriteLine("stub verified: " + (same ? "yes" : "NO"));
            }
        }

        private static ProcessThread FindMainThread(Process p)
        {
            ProcessThread best = null;
            foreach (ProcessThread t in p.Threads)
            {
                if (t.StartTime == null) { continue; }
                if (best == null || t.StartTime < best.StartTime) { best = t; }
            }
            return best;
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
