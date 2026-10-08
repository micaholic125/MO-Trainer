using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using MoTrainer.Calibration;

namespace MoTrainer.Core
{
    /// <summary>
    /// 「现在跑的这个游戏，是不是当初校准过的那一份」—— 运行时版本检查（2026-10-08 晚补，发版前的必做项）。
    ///
    /// 为什么必须有（09-30 起就挂在待办上）：配置里那些地址（金钱链、选中列表、对象名单、血量偏移……）
    /// 都是在**一份具体的 gamemd.exe** 上真机校准出来的。换一个引擎二进制（别的尤里复仇补丁、汉化版、
    /// 被人改过的 exe），内部结构就可能变 —— 那时程序**不会报错**，只会照旧往"它以为是金钱"的地方写，
    /// 这正是最坏的一类失败：**看起来在正常工作，实际在破坏别的内存**。
    ///
    /// 判据用**引擎 exe 文件的 CRC32**（与本工程文档里记的 `0x1B499086` 同一口径）：
    ///   · 不用"内存里的代码哈希" —— Ares/Syringe 会在运行时往代码里打 1450 个 hook，代码区每次都不同；
    ///   · 文件本身是稳定的、可核对的（读文件，绝不写、不改）；
    ///   · 引擎结构（HouseClass / TechnoClass 的布局）由这份 exe 决定，而 MO 的版本更新只改 INI/MIX 数据、
    ///     不改 exe —— 所以"同一份 exe"就等于"同一套结构"，这正是我们要保护的东西。
    ///
    /// 三种结论（记录与报警在 `MainForm`，拒绝写入在 `CheatEngine`）：
    ///   · 校准文件里还没记过 → **记上**（首次运行新检查，日志里写明）；
    ///   · 与记录一致 → 放行；
    ///   · 不一致 → **拒绝一切写入**并说明（宁可停用，也不拿错的地址去写）。
    /// </summary>
    public static class EnvironmentCheck
    {
        /// <summary>一次检查的结论。`Available = false` 表示这次算不出来（跳过检查，不拦）。</summary>
        public sealed class Result
        {
            /// <summary>能不能算出指纹（取不到路径 / 文件读不了时为 false）。</summary>
            public bool Available;

            /// <summary>是否与校准文件里记的一致（首次记录时视为一致）。</summary>
            public bool Match;

            /// <summary>校准文件里原本没记过指纹 —— 调用方应把当前这份记上。</summary>
            public bool FirstRecord;

            /// <summary>当前游戏 exe 的指纹（"0x1B499086" 这种形式）。</summary>
            public string GameCrc = "";

            /// <summary>校准文件里记的指纹。</summary>
            public string ProfileCrc = "";

            /// <summary>游戏主程序文件的完整路径。</summary>
            public string Path = "";

            /// <summary>给用户看的一句话（写进日志、显示在卡片上）。</summary>
            public string Detail = "";
        }

        // ============================================================ CRC32

        private static readonly uint[] _table = BuildTable();

        private static uint[] BuildTable()
        {
            uint[] table = new uint[256];
            for (uint i = 0; i < 256; i++)
            {
                uint value = i;
                for (int bit = 0; bit < 8; bit++)
                {
                    value = (value & 1) != 0 ? (0xEDB88320u ^ (value >> 1)) : (value >> 1);
                }
                table[i] = value;
            }
            return table;
        }

        /// <summary>标准 CRC32（IEEE 802.3，多项式 0xEDB88320）—— 与文档里那个值同一口径。</summary>
        public static uint Crc32(byte[] data, int offset, int count)
        {
            return Finish(Continue(0xFFFFFFFFu, data, offset, count));
        }

        /// <summary>分块喂数据（读大文件时用，避免一次性载入整个 exe）。</summary>
        public static uint Continue(uint running, byte[] data, int offset, int count)
        {
            if (data == null) { return running; }
            if (offset < 0) { offset = 0; }
            if (count < 0 || offset + count > data.Length) { count = data.Length - offset; }

            uint crc = running;
            for (int i = 0; i < count; i++)
            {
                crc = _table[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
            }
            return crc;
        }

        /// <summary>收尾（异或全 1）。</summary>
        public static uint Finish(uint running)
        {
            return running ^ 0xFFFFFFFFu;
        }

        /// <summary>
        /// 算一个文件的 CRC32。
        /// 用 `FileShare.ReadWrite` 打开：游戏正在跑的时候 exe 仍可读（本程序**只读**它，绝不写）。
        /// </summary>
        public static bool TryComputeFileCrc32(string path, out uint crc, out string error)
        {
            crc = 0;
            error = "";
            if (string.IsNullOrEmpty(path)) { error = "路径为空"; return false; }

            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[1 << 16];
                    uint running = 0xFFFFFFFFu;
                    while (true)
                    {
                        int read = stream.Read(buffer, 0, buffer.Length);
                        if (read <= 0) { break; }
                        running = Continue(running, buffer, 0, read);
                    }
                    crc = Finish(running);
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        /// <summary>取某个进程的主程序文件路径（用 .NET 的 Process，底层就是系统接口）。</summary>
        public static bool TryGetModulePath(int processId, out string path, out string error)
        {
            path = "";
            error = "";
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    path = process.MainModule.FileName;
                    if (string.IsNullOrEmpty(path)) { error = "系统没有返回主程序路径"; return false; }
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ============================================================ 检查（同一进程只算一次）

        private static readonly object _cacheLock = new object();
        private static readonly Dictionary<int, Result> _cache = new Dictionary<int, Result>();

        /// <summary>
        /// 对某个游戏进程做一次检查。CRC 要把整个 exe 读一遍，所以**同一 pid 只算一次**并缓存
        ///（游戏重启 → 新 pid → 重新算）。
        /// </summary>
        public static Result Verify(CalibrationProfile profile, int processId)
        {
            lock (_cacheLock)
            {
                Result cached;
                if (_cache.TryGetValue(processId, out cached)) { return cached; }
            }

            Result result = VerifyCore(profile, processId);

            lock (_cacheLock) { _cache[processId] = result; }
            return result;
        }

        /// <summary>清掉缓存（自检与人工排查用）。</summary>
        public static void ResetCache()
        {
            lock (_cacheLock) { _cache.Clear(); }
        }

        private static Result VerifyCore(CalibrationProfile profile, int processId)
        {
            Result result = new Result();

            string path;
            string error;
            if (!TryGetModulePath(processId, out path, out error))
            {
                result.Detail = "取不到游戏主程序路径（" + error + "）—— 本次跳过版本检查";
                return result;
            }
            result.Path = path;

            uint crc;
            if (!TryComputeFileCrc32(path, out crc, out error))
            {
                result.Detail = "读不了游戏主程序文件（" + error + "）—— 本次跳过版本检查";
                return result;
            }

            result.Available = true;
            result.GameCrc = string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", crc);
            result.ProfileCrc = profile == null || profile.ExeCrc32 == null ? "" : profile.ExeCrc32;

            if (result.ProfileCrc.Length == 0)
            {
                result.FirstRecord = true;
                result.Match = true;
                result.Detail = string.Format(CultureInfo.InvariantCulture,
                    "校准文件里还没有记录游戏版本指纹 —— 已把当前这份记上：{0}（{1}）",
                    result.GameCrc, path);
                return result;
            }

            result.Match = string.Equals(result.ProfileCrc, result.GameCrc, StringComparison.OrdinalIgnoreCase);
            result.Detail = result.Match
                ? string.Format(CultureInfo.InvariantCulture,
                    "游戏版本指纹与校准记录一致（{0}）", result.GameCrc)
                : string.Format(CultureInfo.InvariantCulture,
                    "**游戏版本与校准记录不一致**：当前是 {0}，校准文件记的是 {1} —— "
                    + "地址是在另一份引擎上校准出来的，照旧写入可能改坏别的内存，已停用全部写入功能。"
                    + "请换回原来的游戏版本，或重新跑一次「校准向导」把这台机器上的地址重新校准一遍",
                    result.GameCrc, result.ProfileCrc);
            return result;
        }
    }
}
