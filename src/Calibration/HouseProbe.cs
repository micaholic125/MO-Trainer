using System;
using System.Globalization;
using System.Text;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Calibration
{
    /// <summary>「房子字段试探」的目标：往我方房子的某个偏移写一个值、盯几秒、再还原。</summary>
    public sealed class HouseProbeSpec
    {
        /// <summary>要写的偏移（房子内）。</summary>
        public uint Offset;

        /// <summary>true = 在"当前值"上加/减（`+100000`）；false = 直接写这个值（`=2147483647`）。</summary>
        public bool IsDelta;

        public uint Value;
        public string Raw = "";

        /// <summary>
        /// 解析一行配置，形如：
        ///   0x2DC +100000      ← 在当前值上加 10 万（试探"付满就造好"这条假设）
        ///   0x57A4 =0          ← 直接写 0
        /// 解析失败必须说得出为什么（绝不"按钮能点、程序什么都不做"）。
        /// </summary>
        public static bool TryParse(string text, out HouseProbeSpec spec, out string error)
        {
            spec = null;
            error = "";

            if (text == null || text.Trim().Length == 0)
            {
                error = "配置是空的。请写一行，例如：0x2DC +100000";
                return false;
            }

            string[] parts = text.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
            {
                error = "配置格式不对。要两段：偏移 和 值，例如：0x2DC +100000";
                return false;
            }

            string offsetText = parts[0];
            if (offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                offsetText = offsetText.Substring(2);
            }
            uint offset;
            if (!uint.TryParse(offsetText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
            {
                error = "偏移看不懂：「" + parts[0] + "」（要十六进制，例如 0x2DC）";
                return false;
            }

            string valueText = parts[1];
            bool isDelta = true;
            if (valueText.StartsWith("=", StringComparison.Ordinal))
            {
                isDelta = false;
                valueText = valueText.Substring(1);
            }
            else if (valueText.StartsWith("+", StringComparison.Ordinal))
            {
                valueText = valueText.Substring(1);
            }

            uint value;
            if (!uint.TryParse(valueText, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error = "数值看不懂：「" + parts[1] + "」（要十进制整数，例如 +100000 或 =0）";
                return false;
            }

            HouseProbeSpec parsed = new HouseProbeSpec();
            parsed.Offset = offset;
            parsed.IsDelta = isDelta;
            parsed.Value = value;
            parsed.Raw = text.Trim();
            spec = parsed;
            return true;
        }
    }

    /// <summary>一次「房子字段试探」的结果（含写前 / 写入后 / 还原后三个读数）。</summary>
    public sealed class HouseProbeResult
    {
        public string Note = "";
        public uint HouseAddress;
        public uint FieldAddress;
        public uint Offset;
        public uint Before;
        public uint Written;
        public uint AfterWrite;
        public uint AfterRestore;
        public bool Wrote;
        public bool Restored;
        public bool ReadAfterWrite;
        public bool ReadAfterRestore;

        public bool Ok { get { return Note.Length == 0; } }

        public string Explain()
        {
            StringBuilder text = new StringBuilder();
            if (!Ok)
            {
                text.AppendLine("试探没有执行：" + Note);
                return text.ToString();
            }
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "房子 0x{0:X8} + 0x{1:X} = 0x{2:X8}", HouseAddress, Offset, FieldAddress));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "写前 {0}｜写入 {1}（{2}）｜写入后读到 {3}｜还原后读到 {4}",
                Before, Written, Wrote ? "已写" : "**没写成**",
                ReadAfterWrite ? AfterWrite.ToString(CultureInfo.InvariantCulture) : "读不到",
                ReadAfterRestore ? AfterRestore.ToString(CultureInfo.InvariantCulture) : "读不到"));
            text.AppendLine(Restored
                ? (AfterRestore == Before ? "已还原成原值 ✓" : "**还原后与原值不一致** ⚠")
                : "**没能还原** ⚠");
            return text.ToString();
        }
    }

    /// <summary>
    /// 「房子字段试探」—— 全项目第二个会往游戏里写**实验值**的地方（第一个是「电力试探」）。
    ///
    /// 为什么需要它：`[S6-2]` 追了两轮真机采集，把"进度字段"的形状判据用遍了也没找到 ——
    /// 数据反而指出一个更可能的事实：这类引擎的建造是**按拍付钱、付满就造好**
    /// （真机证据：房子的 `+0x2DC` 与金钱的减少**逐拍完全相同**、总数也等于花掉的钱）。
    /// 也就是说"进度"很可能**不是一个每拍自增的独立字段**，而是那本"已付多少"的流水账 ——
    /// 若果真如此，**往账上加大数就能让当前建造立刻造好**。
    /// 这种"形态判据猜不出来"的问题，只能**写一下看行为**（与「电力试探」同源的做法）。
    ///
    /// 安全设计（与 `PowerProbe` 同源）：
    ///   · 只写**我方房子**（由金钱链解析，本就是可信的），不碰任何别的对象；
    ///   · **绝不写金钱偏移 `+0x30C`**（那是玩家的钱，没有任何试探理由写它）；
    ///   · 写前先查目标页可写（`VirtualQueryEx`），不可写就拒绝；
    ///   · 写完回读校验、**无论中途出什么事都在 finally 里还原**，并回读确认还原成功；
    ///   · 缺前置（进程没连、金钱链解析不出来）一律**拒绝执行并说明缺什么**。
    /// </summary>
    public static class HouseProbe
    {
        /// <summary>金钱偏移：绝不往这里写。</summary>
        public const uint MoneyOffset = 0x30C;

        /// <summary>房子对象按 0x6000 字节看待（采集器盯的就是这么大），超出就拒绝。</summary>
        public const uint MaxOffset = 0x6000;

        /// <summary>写完之后盯这么久再还原（够用户看清"是不是立刻造好"）。</summary>
        public const int HoldMs = 3000;

        public static HouseProbeResult Run(
            MemoryIO memory, CalibrationProfile profile, uint moduleBase, HouseProbeSpec spec)
        {
            HouseProbeResult result = new HouseProbeResult();
            if (memory == null) { result.Note = "内存接口不可用"; return result; }
            if (spec == null) { result.Note = "没有试探参数"; return result; }
            if (profile == null || !profile.Money.IsCalibrated)
            {
                result.Note = "金钱未校准 —— 认不出我方房子（先跑一次校准向导的金钱步骤）";
                return result;
            }

            result.Offset = spec.Offset;
            if (spec.Offset == MoneyOffset)
            {
                result.Note = "**拒绝**：+0x30C 是你的金钱，没有任何试探理由写它";
                return result;
            }
            if (spec.Offset >= MaxOffset)
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "**拒绝**：偏移 0x{0:X} 超出房子对象范围（上限 0x{1:X}）", spec.Offset, MaxOffset);
                return result;
            }

            uint moneyAddress;
            string error;
            if (!profile.Money.TryResolve(memory, moduleBase, out moneyAddress, out error))
            {
                result.Note = "金钱地址解析不出来：" + error;
                return result;
            }
            if (moneyAddress < MoneyOffset + 0x10000)
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "金钱地址 0x{0:X8} 小得不合理，拒绝执行", moneyAddress);
                return result;
            }

            uint house = moneyAddress - MoneyOffset;
            uint field = house + spec.Offset;
            result.HouseAddress = house;
            result.FieldAddress = field;

            if (!memory.IsWritable(field))
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "目标页不可写（VirtualQueryEx 判定）—— 拒绝写入。地址 0x{0:X8}", field);
                return result;
            }

            uint before;
            if (!memory.ReadUInt32(field, out before))
            {
                result.Note = string.Format(CultureInfo.InvariantCulture, "读不到字段现值（0x{0:X8}）", field);
                return result;
            }
            result.Before = before;

            uint target = spec.IsDelta ? unchecked(before + spec.Value) : spec.Value;
            result.Written = target;

            try
            {
                Logger.Info("HouseProbe", string.Format(CultureInfo.InvariantCulture,
                    "房子试探：0x{0:X8}（房子 0x{1:X8} +0x{2:X}）{3} → {4}",
                    field, house, spec.Offset, before, target));

                result.Wrote = memory.WriteUInt32(field, target);
                uint afterWrite;
                result.ReadAfterWrite = memory.ReadUInt32(field, out afterWrite);
                result.AfterWrite = afterWrite;

                System.Threading.Thread.Sleep(HoldMs);
            }
            finally
            {
                // **无条件还原**：试探值最多只存在于上面那几秒里。
                uint restored;
                result.Restored = memory.WriteUInt32(field, before);
                result.ReadAfterRestore = memory.ReadUInt32(field, out restored);
                result.AfterRestore = restored;

                Logger.Info("HouseProbe", string.Format(CultureInfo.InvariantCulture,
                    "房子试探收尾：还原 {0}（写回 {1}，回读到 {2}）",
                    result.Restored && restored == before ? "成功" : "**失败**", before, restored));
            }

            return result;
        }
    }
}
