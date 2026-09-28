using System;

namespace MoTrainer.Core
{
    /// <summary>
    /// 内存内核所需的最小进程句柄契约。
    /// 主程序由 <see cref="ProcessAttach"/> 实现；自检程序可以用自己的实现，
    /// 从而在没有游戏运行时也能验证读写链路。
    /// </summary>
    public interface IProcessHandle
    {
        /// <summary>已打开的进程句柄；未就绪时为 IntPtr.Zero。</summary>
        IntPtr Handle { get; }

        /// <summary>句柄是否可立即用于读写。</summary>
        bool IsReady { get; }

        int ProcessId { get; }

        /// <summary>主模块基址（32 位目标，故用 uint）。</summary>
        uint ModuleBase { get; }

        /// <summary>主模块映像大小。</summary>
        uint ModuleSize { get; }

        /// <summary>用于日志的可读描述。</summary>
        string Describe();
    }
}
