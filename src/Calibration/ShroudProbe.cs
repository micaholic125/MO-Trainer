using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using MoTrainer.Core;

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 「黑幕试探」的目标：一行配置写两个数 —— **地址 长度**（十进制或 0x 十六进制都认）。
    /// 配置放在 `dist\config\shroud-probe.txt`，**换目标不用重编译**（与 `HouseProbe` 同一套路）。
    ///
    /// 例：
    /// <code>
    /// 0x29B00000 4096
    /// </code>
    /// 只清这一小片（最多 <see cref="ShroudProbe.MaxLength"/> 字节），绝不整片清 ——
    /// 地址是不是黑幕还不知道，先拿一小片问游戏。
    /// </summary>
    public sealed class ShroudProbeSpec
    {
        public uint Address;
        public int Length;
        public string Raw = "";

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "0x{0:X8} 起 {1} 字节", Address, Length);
        }

        public static bool TryParse(string text, out ShroudProbeSpec spec, out string error)
        {
            spec = null;
            error = "";

            if (string.IsNullOrEmpty(text)) { error = "配置是空的（要写「地址 长度」两个数）"; return false; }

            string[] parts = text.Replace(",", " ").Replace("\t", " ")
                .Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            List<string> fields = new List<string>();
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].StartsWith("#")) { break; }      // 支持行尾注释
                fields.Add(parts[i]);
            }
            if (fields.Count < 2) { error = "要写「地址 长度」两个数，例如：0x29B00000 4096"; return false; }

            uint address;
            if (!TryParseNumber(fields[0], out address))
            {
                error = "第一个数（地址）看不懂：" + fields[0];
                return false;
            }
            uint length;
            if (!TryParseNumber(fields[1], out length))
            {
                error = "第二个数（长度）看不懂：" + fields[1];
                return false;
            }

            spec = new ShroudProbeSpec();
            spec.Address = address;
            spec.Length = (int)length;
            spec.Raw = text.Trim();
            return true;
        }

        private static bool TryParseNumber(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) { return false; }
            string s = text.Trim();
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToUInt32(s.Substring(2), 16);
                    return true;
                }
                value = Convert.ToUInt32(s, 10);
                return true;
            }
            catch (Exception) { return false; }
        }
    }

    /// <summary>第一次点击（**只读**）：念出"要动哪儿、那块现在长什么样、能不能写"。</summary>
    public sealed class ShroudProbeScout
    {
        public string Note = "";
        public uint Address;
        public int Length;
        public bool Writable;
        public bool Readable;
        public int ZeroCount;      // 前 512 字节里的构成
        public int OnesCount;
        public int OtherCount;
        public string HeadHex = "";

        public bool Ok { get { return Note.Length == 0; } }

        public string Describe()
        {
            if (!Ok) { return Note; }
            return string.Format(CultureInfo.InvariantCulture,
                "目标：0x{0:X8} 起 {1} 字节｜可读 {2}｜可写 {3}\r\n"
                + "前 512 字节的构成：0x00 {4}｜0xFF {5}｜其他 {6}\r\n"
                + "开头 32 字节：{7}",
                Address, Length, Readable ? "是" : "否", Writable ? "是" : "否",
                ZeroCount, OnesCount, OtherCount, HeadHex);
        }
    }

    /// <summary>第二次点击的结果。</summary>
    public sealed class ShroudProbeResult
    {
        /// <summary>非空 = 这一轮**什么都没做**（或没能还原），原因在这里。</summary>
        public string Note = "";

        public uint Address;
        public int Length;

        /// <summary>有没有真的写进去过。</summary>
        public bool Wrote;

        /// <summary>还原动作做了没有（与"还原成功"是两回事，分开记）。</summary>
        public bool RestoreAttempted;

        /// <summary>还原之后**回读比对**是否与原始内容逐字节一致。</summary>
        public bool RestoreVerified;

        public int HeldMs;

        public string HeadBeforeHex = "";
        public string HeadAfterHex = "";

        public bool Ok { get { return Note.Length == 0; } }

        public string Describe()
        {
            if (Note.Length > 0 && !Wrote) { return Note; }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "目标：0x{0:X8} 起 {1} 字节｜按住 {2} 毫秒（期间每 {3} 毫秒重写一次，防止游戏自己画回来）",
                Address, Length, HeldMs, ShroudProbe.RewriteEveryMs));
            sb.AppendLine("写入前 32 字节：" + HeadBeforeHex);
            sb.AppendLine("写入时 32 字节：" + HeadAfterHex);
            sb.AppendLine(RestoreVerified
                ? "✅ 已还原（回读逐字节一致）"
                : "❌ **还原没能确认**（回读与原始内容不一致）—— 请把这行发给我，并**不要关程序**");
            if (Note.Length > 0) { sb.AppendLine("备注：" + Note); }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 「黑幕试探」—— `[S6-3]` 全图视野的定位工具（全项目**第三个**往游戏里写实验值的地方，
    /// 前两个是「电力试探」与「房子试探」）。
    ///
    /// ## 它要回答的问题
    ///
    /// 采集报告「七」给出的候选是"**像**黑幕"的地方，不是"**是**黑幕"。
    /// 判定只有一条路：**往那一小片写 0，看游戏画面上的黑幕是不是真的散开了一块**，
    /// 然后立刻还原。这一步必须由真人看着屏幕回答，程序替不了。
    ///
    /// ## 安全设计（与前两个试探同一标准）
    ///
    ///   · **点两次才真写**：第一次只读侦查（念出地址、构成、能不能写），第二次才动；
    ///   · 目标写在配置里（`config\shroud-probe.txt`），**换目标不用重编译**；
    ///   · 只碰**一小片**（上限 <see cref="MaxLength"/>），绝不整片清；
    ///   · 写前用 `VirtualQueryEx` 查**首尾都已提交且可写**，不可写直接拒绝；
    ///   · **`finally` 里无条件还原 + 回读逐字节确认**（哪怕中途异常也还原）；
    ///   · 按住期间**每 200 毫秒重写一次** —— 因为"游戏会不会把黑幕自己画回来"正是待验证的事之一，
    ///     只写一次的话，那种情况会表现为"屏幕上什么都没发生"，把一次机会白白浪费；
    ///   · 试探值只存在约 3.5 秒；期间**不要关程序**（还原是在同一个函数里做的）。
    ///
    /// 电力试探那次的经验：写入生效、也确实还原了，而界面读数纹丝不动 ——
    /// **否定一个候选同样是有价值的结论**，所以这里把"写了什么、还原没还原"全部摊开写进日志。
    /// </summary>
    public static class ShroudProbe
    {
        public const string FileName = "shroud-probe.txt";

        /// <summary>一次最多清多少（一小片；整张图的清除由接线后的功能分片做）。</summary>
        public const int MaxLength = 0x10000;

        /// <summary>试探值按住多久（够用户把视线移到游戏画面上）。</summary>
        public const int HoldMs = 3500;

        /// <summary>按住期间多久重写一次（防"游戏每帧把黑幕画回来"）。</summary>
        public const int RewriteEveryMs = 200;

        /// <summary>侦查时多看几个字节（念构成用）。</summary>
        private const int ScoutProbeBytes = 512;

        // ============================================================ 配置

        public static string PathIn(string configFolder)
        {
            return Path.Combine(configFolder, FileName);
        }

        /// <summary>读目标。文件不存在 / 内容看不懂时**如实说明**，绝不猜一个地址去写。</summary>
        public static bool TryLoadTarget(string configFolder, out ShroudProbeSpec spec, out string error)
        {
            spec = null;
            error = "";

            string path = PathIn(configFolder);
            if (!File.Exists(path))
            {
                error = "还没定目标：请把候选地址写进 " + path + "（一行：「地址 长度」，例如 0x29B00000 4096）";
                return false;
            }

            string text;
            try { text = File.ReadAllText(path, Encoding.UTF8); }
            catch (Exception ex) { error = "读不到目标文件：" + ex.Message; return false; }

            // 只认第一行非空、非注释的行
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) { continue; }
                return ShroudProbeSpec.TryParse(line, out spec, out error);
            }

            error = "目标文件里没有有效的一行（要写「地址 长度」）";
            return false;
        }

        // ============================================================ 第一次点击：只读侦查

        public static ShroudProbeScout Scout(MemoryIO memory, ShroudProbeSpec spec)
        {
            ShroudProbeScout scout = new ShroudProbeScout();
            if (spec != null) { scout.Address = spec.Address; scout.Length = spec.Length; }

            if (memory == null) { scout.Note = "内存接口不可用"; return scout; }
            if (spec == null) { scout.Note = "没有目标（先写好配置）"; return scout; }
            if (spec.Address < 0x10000)
            {
                scout.Note = "地址小得不合理（0x10000 以下不是游戏数据）—— 拒绝";
                return scout;
            }
            if (spec.Length <= 0 || spec.Length > MaxLength)
            {
                scout.Note = string.Format(CultureInfo.InvariantCulture,
                    "长度必须在 1 ~ {0} 之间（这次写的是 {1}）—— 拒绝", MaxLength, spec.Length);
                return scout;
            }

            int probe = scout.Length < ScoutProbeBytes ? scout.Length : ScoutProbeBytes;
            byte[] head = memory.ReadBytesQuiet(spec.Address, probe);
            if (head == null)
            {
                scout.Note = string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8} 读不到内容 —— 地址多半不对，拒绝试探", spec.Address);
                return scout;
            }

            scout.Readable = true;
            scout.Writable = memory.IsWritable(spec.Address)
                && memory.IsWritable(spec.Address + (uint)(spec.Length - 1));

            for (int i = 0; i < head.Length; i++)
            {
                if (head[i] == 0) { scout.ZeroCount++; }
                else if (head[i] == 0xFF) { scout.OnesCount++; }
                else { scout.OtherCount++; }
            }

            int show = head.Length < 32 ? head.Length : 32;
            StringBuilder hex = new StringBuilder();
            for (int i = 0; i < show; i++)
            {
                if (i > 0) { hex.Append(' '); }
                hex.Append(head[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            scout.HeadHex = hex.ToString();
            return scout;
        }

        // ============================================================ 第二次点击：真写 + 还原

        /// <summary>
        /// 走一遍：**读原值 → 反复写 0（按住约 3.5 秒）→ 无条件还原 → 回读确认**。
        ///
        /// 这个方法会阻塞三秒多，**必须由调用方放在后台线程里跑**（界面不能卡）。
        /// </summary>
        public static ShroudProbeResult Run(MemoryIO memory, ShroudProbeSpec spec)
        {
            ShroudProbeResult result = new ShroudProbeResult();
            if (spec != null) { result.Address = spec.Address; result.Length = spec.Length; }

            if (memory == null) { result.Note = "内存接口不可用"; return result; }
            if (spec == null) { result.Note = "没有目标（先写好配置）"; return result; }
            if (spec.Address < 0x10000)
            {
                result.Note = "地址小得不合理（0x10000 以下不是游戏数据）—— 拒绝";
                return result;
            }
            if (spec.Length <= 0 || spec.Length > MaxLength)
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "长度必须在 1 ~ {0} 之间（这次写的是 {1}）—— 拒绝", MaxLength, spec.Length);
                return result;
            }

            // 写前查可写：首尾都要可写（中间是同一片页，按页查首尾足够）
            if (!memory.IsWritable(spec.Address)
                || !memory.IsWritable(spec.Address + (uint)(spec.Length - 1)))
            {
                result.Note = "目标页不可写（VirtualQueryEx 判定）—— 拒绝写入，一个字节都没动";
                return result;
            }

            byte[] original = memory.ReadBytes(spec.Address, spec.Length);
            if (original == null)
            {
                result.Note = "读不到原始内容 —— 拒绝试探（读不到就谈不上还原）";
                return result;
            }
            result.HeadBeforeHex = HexOf(original, 32);

            byte[] zeros = new byte[spec.Length];
            DateTime started = DateTime.UtcNow;
            try
            {
                // 按住期间反复写：只写一次的话，"游戏会自己画回来"那种情况
                // 在屏幕上看起来和"地址不对"一模一样，白白浪费一次机会。
                while ((DateTime.UtcNow - started).TotalMilliseconds < HoldMs)
                {
                    if (memory.Write(spec.Address, zeros, 0, spec.Length)) { result.Wrote = true; }
                    Thread.Sleep(RewriteEveryMs);
                }

                byte[] live = memory.ReadBytesQuiet(spec.Address, zeros.Length < 32 ? zeros.Length : 32);
                result.HeadAfterHex = live == null ? "(读不到)" : HexOf(live, live.Length);
            }
            finally
            {
                // **无条件还原**：黑幕是游戏自己的状态，无论上面发生了什么都要放回去。
                result.RestoreAttempted = true;
                memory.Write(spec.Address, original, 0, original.Length);

                byte[] back = memory.ReadBytesQuiet(spec.Address, spec.Length);
                result.RestoreVerified = SameBytes(back, original);
                result.HeldMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
            }

            if (!result.RestoreVerified)
            {
                result.Note = "还原后回读与原始内容不一致（可能这块内存本来就每帧在变）—— "
                    + "请把这行连同现场发给我";
            }
            else if (!result.Wrote)
            {
                result.Note = "一个字节都没写进去（WriteProcessMemory 全失败）—— 地址多半不对";
            }
            return result;
        }

        private static string HexOf(byte[] buffer, int max)
        {
            if (buffer == null) { return "(空)"; }
            int n = buffer.Length < max ? buffer.Length : max;
            StringBuilder hex = new StringBuilder();
            for (int i = 0; i < n; i++)
            {
                if (i > 0) { hex.Append(' '); }
                hex.Append(buffer[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            return hex.ToString();
        }

        private static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) { return false; }
            for (int i = 0; i < a.Length; i++) { if (a[i] != b[i]) { return false; } }
            return true;
        }
    }
}
