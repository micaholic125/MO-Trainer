// WriteSpy (32-bit): report WHICH INSTRUCTION writes a given memory address.
//
// Why this exists: Cheat Engine could not do it here.  Its "Windows debugger"
// refuses to attach to gamemd (error 87) and its VEH debugger attaches but never
// catches a memory write -- verified 2026-10-17: three watched shroud bytes all
// changed (0xFE -> 0xFF) while CE reported nothing.  This tool uses the plain
// Win32 debugging API instead, which is the same mechanism CE's Windows
// debugger uses, but with nothing else in the way.
//
// What it does, in full:
//   1. DebugActiveProcess(gamemd)
//   2. sets hardware breakpoint DR0 = <address>, DR7 = write, 1 byte, on EVERY
//      thread (and on any thread created later)
//   3. when a thread trips it, prints EIP, the CPU registers and the code bytes
//      around EIP, then detaches
// It does NOT inject anything, does NOT modify code, and does NOT write memory.
//
// MUST be compiled /platform:x86: the target is a 32-bit process and
// GetThreadContext needs the 32-bit CONTEXT layout.
//
// Usage: WriteSpy.exe <hexaddress> [maxHits] [seconds]
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTools
{
    internal static class WriteSpy
    {
        private const uint DBG_CONTINUE = 0x00010002;
        private const uint DBG_EXCEPTION_NOT_HANDLED = 0x80010001;

        private const uint CREATE_THREAD_DEBUG_EVENT = 2;
        private const uint EXIT_THREAD_DEBUG_EVENT = 4;
        private const uint EXIT_PROCESS_DEBUG_EVENT = 5;
        private const uint EXCEPTION_DEBUG_EVENT = 1;

        private const uint EXCEPTION_SINGLE_STEP = 0x80000004;
        private const uint EXCEPTION_BREAKPOINT = 0x80000003;

        private const uint TH32CS_SNAPTHREAD = 0x00000004;
        private const uint THREAD_GET_CONTEXT = 0x0008;
        private const uint THREAD_SET_CONTEXT = 0x0010;
        private const uint THREAD_SUSPEND_RESUME = 0x0002;
        private const uint THREAD_QUERY_INFORMATION = 0x0040;

        private const uint PROCESS_VM_READ = 0x0010;
        private const uint PROCESS_QUERY_INFORMATION = 0x0400;

        // 32-bit CONTEXT: ContextFlags plus the debug + integer + control areas.
        private const uint CONTEXT_FLAGS = 0x00010000 | 0x01 | 0x02 | 0x08;
        private const int CONTEXT_SIZE = 0x2CC;

        private const int OFF_FLAGS = 0x00;
        private const int OFF_DR0 = 0x04;
        private const int OFF_DR1 = 0x08;
        private const int OFF_DR2 = 0x0C;
        private const int OFF_DR3 = 0x10;
        private const int OFF_DR6 = 0x14;
        private const int OFF_DR7 = 0x18;
        private const int OFF_EDI = 0x9C;
        private const int OFF_ESI = 0xA0;
        private const int OFF_EBX = 0xA4;
        private const int OFF_EDX = 0xA8;
        private const int OFF_ECX = 0xAC;
        private const int OFF_EAX = 0xB0;
        private const int OFF_EBP = 0xB4;
        private const int OFF_EIP = 0xB8;
        private const int OFF_ESP = 0xC4;

        [StructLayout(LayoutKind.Sequential)]
        private struct THREADENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ThreadID;
            public uint th32OwnerProcessID;
            public int tpBasePri;
            public int tpDeltaPri;
            public uint dwFlags;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugActiveProcess(int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DebugActiveProcessStop(int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WaitForDebugEvent(IntPtr evt, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ContinueDebugEvent(int pid, int tid, uint status);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint access, bool inherit, uint tid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SuspendThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint ResumeThread(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadContext(IntPtr thread, IntPtr context);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadContext(IntPtr thread, IntPtr context);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(
            IntPtr handle, IntPtr address, byte[] buffer, IntPtr size, out IntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Thread32First(IntPtr snapshot, ref THREADENTRY32 entry);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool Thread32Next(IntPtr snapshot, ref THREADENTRY32 entry);

        private static IntPtr _process = IntPtr.Zero;
        private static uint _target;
        private static int _hits;
        private static int _maxHits = 3;

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args.Length < 1)
            {
                Console.WriteLine("usage: WriteSpy.exe <hexaddress> [maxHits] [seconds]");
                return 2;
            }
            _target = ParseHex(args[0]);
            if (args.Length > 1) { _maxHits = int.Parse(args[1], CultureInfo.InvariantCulture); }
            double seconds = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 20.0;

            System.Diagnostics.Process[] found = System.Diagnostics.Process.GetProcessesByName("gamemd");
            if (found.Length == 0) { Console.WriteLine("gamemd.exe is not running."); return 3; }
            int pid = found[0].Id;
            Console.WriteLine("target: gamemd.exe pid=" + pid
                + "  watching write to 0x" + _target.ToString("X8"));

            _process = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION, false, pid);
            if (_process == IntPtr.Zero)
            {
                Console.WriteLine("OpenProcess failed, Win32=" + Marshal.GetLastWin32Error());
                return 4;
            }

            if (!DebugActiveProcess(pid))
            {
                int err = Marshal.GetLastWin32Error();
                Console.WriteLine("DebugActiveProcess FAILED, Win32=" + err
                    + (err == 87 ? "  (ERROR_INVALID_PARAMETER -- the same wall Cheat Engine hit)"
                                 : ""));
                // Why can nobody attach?  Ask the kernel directly (read-only).
                DiagnoseDebugPort(pid);
                return 5;
            }
            Console.WriteLine("debugger attached.");

            // Breakpoints must be set on every existing thread; the shroud writer
            // may be any of them (render / logic / sound threads).
            int armed = ArmAllThreads(pid);

            IntPtr evt = Marshal.AllocHGlobal(256);
            int elapsedMs = 0;
            int budgetMs = (int)(seconds * 1000.0);
            while (elapsedMs < budgetMs && _hits < _maxHits)
            {
                for (int i = 0; i < 256; i++) { Marshal.WriteByte(evt, i, 0); }
                if (!WaitForDebugEvent(evt, 200))
                {
                    elapsedMs += 200;
                    continue;
                }
                elapsedMs += 200;
                uint code = (uint)Marshal.ReadInt32(evt, 0);
                int tid = Marshal.ReadInt32(evt, 8);

                if (code == CREATE_THREAD_DEBUG_EVENT)
                {
                    int newTid = Marshal.ReadInt32(evt, 12);
                    ArmThread(newTid);
                }
                else if (code == EXCEPTION_DEBUG_EVENT)
                {
                    uint exCode = (uint)Marshal.ReadInt32(evt, 12);
                    uint exAddr = (uint)Marshal.ReadInt32(evt, 24);
                    if (exCode == EXCEPTION_SINGLE_STEP)
                    {
                        _hits++;
                        Report(tid, exAddr);
                        ContinueDebugEvent(pid, tid, DBG_CONTINUE);
                        if (_hits >= _maxHits) { break; }
                        continue;
                    }
                    if (exCode == EXCEPTION_BREAKPOINT)
                    {
                        // the initial attach break: let it through
                        ContinueDebugEvent(pid, tid, DBG_CONTINUE);
                        continue;
                    }
                    ContinueDebugEvent(pid, tid, DBG_EXCEPTION_NOT_HANDLED);
                    continue;
                }
                else if (code == EXIT_PROCESS_DEBUG_EVENT)
                {
                    Console.WriteLine("target process exited.");
                    break;
                }
                ContinueDebugEvent(pid, tid, DBG_CONTINUE);
            }

            Console.WriteLine();
            Console.WriteLine("threads armed: " + armed + "   hits: " + _hits
                + "   waited: " + elapsedMs + " ms");
            if (_hits == 0)
            {
                Console.WriteLine("RESULT: nothing wrote 0x" + _target.ToString("X8")
                    + " in this window.");
            }
            DebugActiveProcessStop(pid);
            if (_process != IntPtr.Zero) { CloseHandle(_process); }
            return 0;
        }

        /// <summary>
        /// Print everything needed to work out where the written value came from:
        /// the instruction pointer, the registers (the base of the destination or
        /// source is usually still sitting in one of them) and the code bytes
        /// around EIP.
        /// </summary>
        private static void Report(int tid, uint exAddr)
        {
            Console.WriteLine();
            Console.WriteLine("=== HIT #" + _hits + "  thread " + tid
                + "  exception EIP=0x" + exAddr.ToString("X8") + " ===");
            IntPtr ctx = ReadContext(tid);
            if (ctx == IntPtr.Zero)
            {
                Console.WriteLine("(could not read the thread context)");
                return;
            }
            uint eax = (uint)Marshal.ReadInt32(ctx, OFF_EAX);
            uint ebx = (uint)Marshal.ReadInt32(ctx, OFF_EBX);
            uint ecx = (uint)Marshal.ReadInt32(ctx, OFF_ECX);
            uint edx = (uint)Marshal.ReadInt32(ctx, OFF_EDX);
            uint esi = (uint)Marshal.ReadInt32(ctx, OFF_ESI);
            uint edi = (uint)Marshal.ReadInt32(ctx, OFF_EDI);
            uint ebp = (uint)Marshal.ReadInt32(ctx, OFF_EBP);
            uint esp = (uint)Marshal.ReadInt32(ctx, OFF_ESP);
            uint eip = (uint)Marshal.ReadInt32(ctx, OFF_EIP);
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "EAX={0:X8} EBX={1:X8} ECX={2:X8} EDX={3:X8}", eax, ebx, ecx, edx));
            Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "ESI={0:X8} EDI={1:X8} EBP={2:X8} ESP={3:X8} EIP={4:X8}",
                esi, edi, ebp, esp, eip));

            // EIP points just past the storing instruction, so show what precedes it.
            uint start = eip >= 48 ? eip - 48 : 0x400000;
            byte[] buf;
            if (TryRead(start, 64, out buf))
            {
                Console.WriteLine("code bytes 0x" + start.ToString("X8") + " .. (EIP is at +0x"
                    + (eip - start).ToString("X") + "):");
                for (int row = 0; row < 64; row += 16)
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("  0x").Append((start + (uint)row).ToString("X8")).Append("  ");
                    for (int i = 0; i < 16 && row + i < 64; i++)
                    {
                        sb.Append(buf[row + i].ToString("X2")).Append(' ');
                    }
                    if (row <= (int)(eip - start) && (int)(eip - start) < row + 16)
                    {
                        sb.Append("   <-- EIP at +0x").Append((eip - start - row).ToString("X"));
                    }
                    Console.WriteLine(sb.ToString());
                }
            }
            else
            {
                Console.WriteLine("(could not read code around EIP)");
            }
            Marshal.FreeHGlobal(ctx);
        }

        private static IntPtr ReadContext(int tid)
        {
            IntPtr h = OpenThread(THREAD_GET_CONTEXT | THREAD_SUSPEND_RESUME, false, (uint)tid);
            if (h == IntPtr.Zero) { return IntPtr.Zero; }
            IntPtr ctx = Marshal.AllocHGlobal(CONTEXT_SIZE);
            for (int i = 0; i < CONTEXT_SIZE; i += 4) { Marshal.WriteInt32(ctx, i, 0); }
            Marshal.WriteInt32(ctx, OFF_FLAGS, (int)CONTEXT_FLAGS);
            bool ok = GetThreadContext(h, ctx);
            CloseHandle(h);
            if (!ok) { Marshal.FreeHGlobal(ctx); return IntPtr.Zero; }
            return ctx;
        }

        /// <summary>DR0 = the watched address; DR7 = L0 enabled, RW0 = write, LEN0 = 1 byte.</summary>
        private static bool ArmThread(int tid)
        {
            IntPtr h = OpenThread(THREAD_GET_CONTEXT | THREAD_SET_CONTEXT
                | THREAD_SUSPEND_RESUME, false, (uint)tid);
            if (h == IntPtr.Zero) { return false; }
            IntPtr ctx = Marshal.AllocHGlobal(CONTEXT_SIZE);
            for (int i = 0; i < CONTEXT_SIZE; i += 4) { Marshal.WriteInt32(ctx, i, 0); }
            Marshal.WriteInt32(ctx, OFF_FLAGS, (int)CONTEXT_FLAGS);

            SuspendThread(h);
            bool ok = GetThreadContext(h, ctx);
            if (ok)
            {
                Marshal.WriteInt32(ctx, OFF_DR0, (int)_target);
                Marshal.WriteInt32(ctx, OFF_DR1, 0);
                Marshal.WriteInt32(ctx, OFF_DR2, 0);
                Marshal.WriteInt32(ctx, OFF_DR3, 0);
                Marshal.WriteInt32(ctx, OFF_DR6, 0);
                Marshal.WriteInt32(ctx, OFF_DR7, 0x00010001);
                ok = SetThreadContext(h, ctx);
            }
            ResumeThread(h);
            CloseHandle(h);
            Marshal.FreeHGlobal(ctx);
            return ok;
        }

        private static int ArmAllThreads(int pid)
        {
            int armed = 0;
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD, 0);
            if (snap == IntPtr.Zero || snap == (IntPtr)(-1)) { return 0; }
            THREADENTRY32 te = new THREADENTRY32();
            te.dwSize = (uint)Marshal.SizeOf(typeof(THREADENTRY32));
            if (Thread32First(snap, ref te))
            {
                do
                {
                    if ((int)te.th32OwnerProcessID != pid) { continue; }
                    if (ArmThread((int)te.th32ThreadID)) { armed++; }
                }
                while (Thread32Next(snap, ref te));
            }
            CloseHandle(snap);
            return armed;
        }

        /// <summary>
        /// Ask the kernel whether this process ALREADY has a debugger (read-only).
        ///
        /// This is the likely reason both Cheat Engine and this tool get
        /// ERROR_INVALID_PARAMETER (87) from DebugActiveProcess: MO launches
        /// gamemd.exe through Syringe.exe, which injects Ares.dll using the
        /// debugging API -- and a process has exactly ONE debug port owner.
        ///
        /// ProcessDebugPort (7): non-zero = a debugger is attached.
        /// ProcessDebugObjectHandle (30): non-zero = a debug object exists.
        /// ProcessDebugFlags (31): 0 = the process is being debugged.
        /// </summary>
        private static void DiagnoseDebugPort(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
            if (h == IntPtr.Zero)
            {
                Console.WriteLine("  (cannot open the process to diagnose: Win32="
                    + Marshal.GetLastWin32Error() + ")");
                return;
            }
            IntPtr buf = Marshal.AllocHGlobal(8);
            int ret;
            Marshal.WriteInt64(buf, 0);
            int st = NtQueryInformationProcess(h, 7, buf, 4, out ret);
            uint port = (uint)Marshal.ReadInt32(buf);
            Console.WriteLine("  ProcessDebugPort      = 0x" + port.ToString("X8")
                + "   (status " + st + ")" + (port != 0 ? "   <-- A DEBUGGER IS ALREADY ATTACHED" : ""));

            Marshal.WriteInt64(buf, 0);
            st = NtQueryInformationProcess(h, 30, buf, 4, out ret);
            uint obj = (uint)Marshal.ReadInt32(buf);
            Console.WriteLine("  ProcessDebugObjectHandle = 0x" + obj.ToString("X8")
                + "   (status " + st + ")" + (obj != 0 ? "   <-- debug object present" : ""));

            Marshal.WriteInt64(buf, 0);
            st = NtQueryInformationProcess(h, 31, buf, 4, out ret);
            uint flags = (uint)Marshal.ReadInt32(buf);
            Console.WriteLine("  ProcessDebugFlags     = " + flags
                + "   (0 = being debugged)");

            Console.WriteLine("  => if ProcessDebugPort is non-zero, that is why nobody can attach:");
            Console.WriteLine("     the game was started through Syringe.exe and its debug port is taken.");
            Marshal.FreeHGlobal(buf);
            CloseHandle(h);
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(
            IntPtr process, int infoClass, IntPtr info, int infoLength, out int returnLength);

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

        private static uint ParseHex(string text)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { text = text.Substring(2); }
            return uint.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
    }
}
