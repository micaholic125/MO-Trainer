using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace MoTrainer.Core
{
    /// <summary>MEMORY_BASIC_INFORMATION。x64 下应为 48 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public UIntPtr RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    /// <summary>MODULEINFO。x64 下应为 24 字节。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MODULEINFO
    {
        public IntPtr lpBaseOfDll;
        public uint SizeOfImage;
        public IntPtr EntryPoint;
    }

    /// <summary>
    /// 集中声明本工具用到的全部 P/Invoke。
    /// 红线：只允许进程内存读写类 API，禁止注入 / 远程线程 / 代码 patch。
    /// </summary>
    public static class Win32Api
    {
        // ---- 进程访问权限 ----
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;

        // ---- 内存状态 ----
        public const uint MEM_COMMIT = 0x1000;
        public const uint MEM_RESERVE = 0x2000;
        public const uint MEM_FREE = 0x10000;
        public const uint MEM_PRIVATE = 0x20000;
        public const uint MEM_MAPPED = 0x40000;
        public const uint MEM_IMAGE = 0x1000000;

        // ---- 页保护 ----
        public const uint PAGE_NOACCESS = 0x01;
        public const uint PAGE_READONLY = 0x02;
        public const uint PAGE_READWRITE = 0x04;
        public const uint PAGE_WRITECOPY = 0x08;
        public const uint PAGE_EXECUTE = 0x10;
        public const uint PAGE_EXECUTE_READ = 0x20;
        public const uint PAGE_EXECUTE_READWRITE = 0x40;
        public const uint PAGE_EXECUTE_WRITECOPY = 0x80;
        public const uint PAGE_GUARD = 0x100;
        public const uint PAGE_NOCACHE = 0x200;
        public const uint PAGE_WRITECOMBINE = 0x400;

        // ---- 模块枚举过滤 ----
        public const uint LIST_MODULES_32BIT = 0x01;
        public const uint LIST_MODULES_64BIT = 0x02;
        public const uint LIST_MODULES_ALL = 0x03;

        // ---- DWM 深色标题栏 ----
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;

        /// <summary>32 位用户态地址空间上限，用于区域枚举的默认终点。</summary>
        public const ulong MaxUserAddress32 = 0x7FFFFFFFul;

        // ================= 进程与内存 =================

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReadProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress, [Out] byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool WriteProcessMemory(
            IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, IntPtr nSize, out IntPtr lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualQueryEx(
            IntPtr hProcess, IntPtr lpAddress, out MEMORY_BASIC_INFORMATION lpBuffer, IntPtr dwLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr VirtualAllocEx(
            IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flAllocationType, uint flProtect);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint dwFreeType);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool VirtualProtectEx(
            IntPtr hProcess, IntPtr lpAddress, IntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        // ================= 模块枚举（psapi） =================

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumProcessModulesEx(
            IntPtr hProcess, [Out] IntPtr[] lphModule, uint cb, out uint lpcbNeeded, uint dwFilterFlag);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetModuleInformation(
            IntPtr hProcess, IntPtr hModule, out MODULEINFO lpmodinfo, uint cb);

        [DllImport("psapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint GetModuleBaseName(
            IntPtr hProcess, IntPtr hModule, StringBuilder lpBaseName, uint nSize);

        // ================= 窗口 =================

        [DllImport("dwmapi.dll", PreserveSig = true)]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        // ================= 辅助 =================

        public static string LastErrorMessage()
        {
            int code = Marshal.GetLastWin32Error();
            return FormatWin32Error(code);
        }

        public static int LastErrorCode()
        {
            return Marshal.GetLastWin32Error();
        }

        public static string FormatWin32Error(int code)
        {
            string text;
            try { text = new Win32Exception(code).Message; }
            catch { text = "unknown"; }
            return string.Format(CultureInfo.InvariantCulture, "Win32={0} ({1})", code, text);
        }

        /// <summary>页保护位是否允许写入。</summary>
        public static bool IsWritableProtect(uint protect)
        {
            if ((protect & PAGE_GUARD) != 0) { return false; }
            uint baseProtect = protect & 0xFF;
            if (baseProtect == PAGE_NOACCESS) { return false; }
            return baseProtect == PAGE_READWRITE
                || baseProtect == PAGE_WRITECOPY
                || baseProtect == PAGE_EXECUTE_READWRITE
                || baseProtect == PAGE_EXECUTE_WRITECOPY;
        }

        public static bool IsExecutableProtect(uint protect)
        {
            uint baseProtect = protect & 0xFF;
            return baseProtect == PAGE_EXECUTE
                || baseProtect == PAGE_EXECUTE_READ
                || baseProtect == PAGE_EXECUTE_READWRITE
                || baseProtect == PAGE_EXECUTE_WRITECOPY;
        }

        // ============================================================ 全局热键
        // 说明：游戏是独占全屏时可能吞掉热键（Windows 会把前台热键优先给全屏独占窗口）。
        // 因此界面上同时提示"建议切无边框窗口化"，双保险。

        public const int WM_HOTKEY = 0x0312;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_SHIFT = 0x0004;
        public const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
