using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MoTrainer.Util;

namespace MoTrainer.Core
{
    public enum AttachState
    {
        /// <summary>未找到游戏进程。</summary>
        NotRunning = 0,
        /// <summary>已获取进程句柄与模块信息。</summary>
        Attached = 1,
        /// <summary>找到了进程但连接失败（多为权限不足）。</summary>
        Error = 2
    }

    /// <summary>
    /// 查找并附加到 gamemd.exe。带 1.5 秒看门狗，游戏退出自动掉线、重启后自动重连。
    /// </summary>
    public sealed class ProcessAttach : IProcessHandle, IDisposable
    {
        public const string TargetProcessName = "gamemd";
        public static readonly uint ExpectedImageBase = 0x00400000;

        private const int ErrorAccessDenied = 5;

        private const uint DesiredAccess =
            Win32Api.PROCESS_QUERY_INFORMATION |
            Win32Api.PROCESS_VM_OPERATION |
            Win32Api.PROCESS_VM_READ |
            Win32Api.PROCESS_VM_WRITE;

        private const int WatchdogIntervalMs = 1500;

        private readonly object _gate = new object();
        private Timer _watchdog;
        private Process _process;
        private IntPtr _handle = IntPtr.Zero;
        private AttachState _state = AttachState.NotRunning;
        private int _pid;
        private uint _moduleBase;
        private uint _moduleSize;
        private string _moduleName = "";
        private string _lastError = "";
        private int _lastErrorCode;
        private bool _disposed;

        /// <summary>状态发生变化时触发。可能在后台线程触发，订阅方需自行切回 UI 线程。</summary>
        public event Action StatusChanged;

        public AttachState State { get { lock (_gate) { return _state; } } }
        public string LastError { get { lock (_gate) { return _lastError; } } }
        public string ModuleName { get { lock (_gate) { return _moduleName; } } }

        /// <summary>最近一次的 Win32 错误码；0 表示无错误。</summary>
        public int LastErrorCode { get { lock (_gate) { return _lastErrorCode; } } }

        /// <summary>上次失败是否因为权限不足（游戏以更高权限运行）。</summary>
        public bool LastErrorWasAccessDenied
        {
            get { lock (_gate) { return _lastErrorCode == ErrorAccessDenied; } }
        }

        public IntPtr Handle { get { lock (_gate) { return _handle; } } }
        public int ProcessId { get { lock (_gate) { return _pid; } } }
        public uint ModuleBase { get { lock (_gate) { return _moduleBase; } } }
        public uint ModuleSize { get { lock (_gate) { return _moduleSize; } } }

        public bool IsReady
        {
            get { lock (_gate) { return _state == AttachState.Attached && _handle != IntPtr.Zero; } }
        }

        /// <summary>基址是否与预期一致（导出/重定位异常时为 false）。</summary>
        public bool ImageBaseAsExpected
        {
            get { lock (_gate) { return _state == AttachState.Attached && _moduleBase == ExpectedImageBase; } }
        }

        public string Describe()
        {
            lock (_gate)
            {
                if (_state == AttachState.Attached)
                {
                    return string.Format(CultureInfo.InvariantCulture,
                        "pid={0} module={1} base=0x{2:X8} size=0x{3:X}",
                        _pid, _moduleName, _moduleBase, _moduleSize);
                }
                if (_state == AttachState.Error) { return "error: " + _lastError; }
                return "not attached";
            }
        }

        public void StartWatchdog()
        {
            lock (_gate)
            {
                if (_disposed) { return; }
                if (_watchdog == null)
                {
                    _watchdog = new Timer(OnWatchdogTick, null, 0, WatchdogIntervalMs);
                }
            }
        }

        /// <summary>尝试附加一次。返回是否已处于已连接状态。</summary>
        public bool TryAttach()
        {
            bool changed;
            bool result = TryAttachCore(out changed);
            if (changed) { RaiseStatusChanged(); }
            return result;
        }

        public void Detach()
        {
            bool changed;
            lock (_gate)
            {
                changed = _state != AttachState.NotRunning || _handle != IntPtr.Zero;
                DetachInternal();
                _state = AttachState.NotRunning;
                _lastError = "";
                _lastErrorCode = 0;
            }
            Logger.Info("Attach", "detached by request");
            if (changed) { RaiseStatusChanged(); }
        }

        private void OnWatchdogTick(object state)
        {
            try
            {
                bool changed;
                TryAttachCore(out changed);
                if (changed) { RaiseStatusChanged(); }
            }
            catch (Exception ex)
            {
                Logger.Error("Attach", "看门狗异常", ex);
            }
        }

        private bool TryAttachCore(out bool changed)
        {
            changed = false;
            lock (_gate)
            {
                if (_disposed) { return false; }

                // 已连接：只需确认进程仍活着
                if (_handle != IntPtr.Zero)
                {
                    if (IsProcessAlive())
                    {
                        return true;
                    }
                    Logger.Info("Attach", "游戏进程已退出，等待重新启动");
                    DetachInternal();
                    _state = AttachState.NotRunning;
                    _lastError = "";
                    _lastErrorCode = 0;
                    changed = true;
                    return false;
                }

                Process target = FindGameProcess();
                if (target == null)
                {
                    if (_state != AttachState.NotRunning)
                    {
                        _state = AttachState.NotRunning;
                        changed = true;
                    }
                    return false;
                }

                int pid;
                try { pid = target.Id; }
                catch (Exception ex)
                {
                    Logger.Warn("Attach", "读取进程 Id 失败：" + ex.Message);
                    target.Dispose();
                    return false;
                }

                IntPtr handle = Win32Api.OpenProcess(DesiredAccess, false, pid);
                if (handle == IntPtr.Zero)
                {
                    int code = Win32Api.LastErrorCode();
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "OpenProcess 失败，{0} 请确认本程序以管理员身份运行，且游戏未以更高权限启动。",
                        Win32Api.FormatWin32Error(code));
                    LogAttachRetry(msg, code, _lastErrorCode);
                    target.Dispose();
                    _state = AttachState.Error;
                    _lastError = msg;
                    _lastErrorCode = code;
                    changed = true;
                    return false;
                }

                uint moduleBase;
                uint moduleSize;
                string moduleName;
                if (!ReadMainModule(handle, out moduleBase, out moduleSize, out moduleName))
                {
                    int code = Win32Api.LastErrorCode();
                    string msg = string.Format(CultureInfo.InvariantCulture,
                        "枚举主模块失败，{0}。", Win32Api.FormatWin32Error(code));
                    LogAttachRetry(msg, code, _lastErrorCode);
                    Win32Api.CloseHandle(handle);
                    target.Dispose();
                    _state = AttachState.Error;
                    _lastError = msg;
                    _lastErrorCode = code;
                    changed = true;
                    return false;
                }

                _process = target;
                _handle = handle;
                _pid = pid;
                _moduleBase = moduleBase;
                _moduleSize = moduleSize;
                _moduleName = moduleName;
                _lastError = "";
                _lastErrorCode = 0;
                _state = AttachState.Attached;
                changed = true;

                Logger.Info("Attach", "已连接 " + Describe());
                if (moduleBase != ExpectedImageBase)
                {
                    Logger.Warn("Attach", string.Format(CultureInfo.InvariantCulture,
                        "主模块基址与预期不一致：预期 0x{0:X8}，实际 0x{1:X8}，静态地址定位需按实际基址换算。",
                        ExpectedImageBase, moduleBase));
                }

                if (moduleSize == 0)
                {
                    Logger.Warn("Attach", "主模块大小为 0，区域枚举与扫描可能受影响");
                }
                return true;
            }
        }

        /// <summary>
        /// 附加失败时的日志：**同一个错误码只喊一次**。
        ///
        /// 为什么必须折叠（2026-10-09 用户反馈"启动修改器时弹出来了一些提醒"）：
        /// 看门狗每 1.5 秒试一轮，而游戏**启动中 / 退出中**必然连续失败十几次 ——
        /// 真机日志里是 **22 条一模一样**的 `[ERR] 枚举主模块失败`，把日志面板刷成一片红。
        /// 那其实是预期现象（紧接着就 `已连接`），却让人以为程序坏了。
        ///
        /// 折叠规则：错误码与上次不同 → **WARN**（说明还在重试）；同一个码再次出现 → **INFO**。
        /// 连上成功或进程退出时 `_lastErrorCode` 会被清零，所以下一次真出问题仍会照常喊。
        /// **状态条、`_lastError`、错误文本一个字都没改** —— 折叠掉的只是重复行，不是信息。
        ///
        /// 做成 `public static` 并把"上次的错误码"当参数传进来，是为了让**自检能直接验它**
        /// （订阅 `Logger.OnLog` 抓级别）——否则这条改动就只是一句没人验证过的话。
        /// </summary>
        public static void LogAttachRetry(string msg, int code, int lastCode)
        {
            if (code == lastCode)
            {
                Logger.Info("Attach", msg + "（同一错误重复，已折叠）");
            }
            else
            {
                Logger.Warn("Attach", msg + "（游戏可能正在启动或退出，程序每 1.5 秒重试一次）");
            }
        }

        private bool IsProcessAlive()
        {
            if (_process == null) { return false; }
            try
            {
                _process.Refresh();
                return !_process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private void DetachInternal()
        {
            if (_handle != IntPtr.Zero)
            {
                Win32Api.CloseHandle(_handle);
                _handle = IntPtr.Zero;
            }
            if (_process != null)
            {
                try { _process.Dispose(); }
                catch { }
                _process = null;
            }
            _pid = 0;
            _moduleBase = 0;
            _moduleSize = 0;
            _moduleName = "";
        }

        private void RaiseStatusChanged()
        {
            Action handler = StatusChanged;
            if (handler != null)
            {
                try { handler(); }
                catch (Exception ex) { Logger.Warn("Attach", "状态回调异常：" + ex.Message); }
            }
        }

        private static Process FindGameProcess()
        {
            Process[] list;
            try
            {
                list = Process.GetProcessesByName(TargetProcessName);
            }
            catch (Exception ex)
            {
                Logger.Warn("Attach", "枚举进程失败：" + ex.Message);
                return null;
            }

            if (list == null || list.Length == 0) { return null; }

            Process best = null;
            for (int i = 0; i < list.Length; i++)
            {
                Process p = list[i];
                bool hasWindow = false;
                try { hasWindow = p.MainWindowHandle != IntPtr.Zero; }
                catch { }
                if (hasWindow) { best = p; break; }
                if (best == null) { best = p; }
            }

            for (int i = 0; i < list.Length; i++)
            {
                if (!object.ReferenceEquals(list[i], best))
                {
                    try { list[i].Dispose(); }
                    catch { }
                }
            }
            return best;
        }

        private static bool ReadMainModule(IntPtr handle, out uint moduleBase, out uint moduleSize, out string moduleName)
        {
            moduleBase = 0;
            moduleSize = 0;
            moduleName = "";

            // 目标为 32 位进程，必须显式要求 LIST_MODULES_32BIT
            IntPtr[] modules = new IntPtr[256];
            uint needed = 0;
            bool ok = Win32Api.EnumProcessModulesEx(
                handle, modules, (uint)(modules.Length * IntPtr.Size), out needed, Win32Api.LIST_MODULES_32BIT);
            if (!ok) { return false; }
            if (needed < (uint)IntPtr.Size) { return false; }

            MODULEINFO info;
            ok = Win32Api.GetModuleInformation(
                handle, modules[0], out info, (uint)Marshal.SizeOf(typeof(MODULEINFO)));
            if (!ok) { return false; }

            moduleBase = (uint)info.lpBaseOfDll.ToInt64();
            moduleSize = info.SizeOfImage;

            StringBuilder name = new StringBuilder(260);
            if (Win32Api.GetModuleBaseName(handle, modules[0], name, (uint)name.Capacity) > 0)
            {
                moduleName = name.ToString();
            }
            else
            {
                moduleName = "?";
            }
            return true;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) { return; }
                _disposed = true;
                if (_watchdog != null)
                {
                    try { _watchdog.Dispose(); }
                    catch { }
                    _watchdog = null;
                }
                DetachInternal();
                _state = AttachState.NotRunning;
            }
        }
    }
}
