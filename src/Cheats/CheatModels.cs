using System;
using System.Globalization;
using MoTrainer.Calibration;

namespace MoTrainer.Cheats
{
    /// <summary>
    /// 联机同步风险分级。
    ///
    /// RA2/YR 是**帧同步（lockstep）**架构：每个客户端各自跑完整模拟，只交换玩家指令，
    /// 并周期性比对校验和，一旦不一致就弹 "out of sync" 掉线。
    /// 所以改动"参与校验的游戏状态"= 必然掉线；只影响本机渲染的 = 一般无影响。
    /// </summary>
    public enum SynchRisk
    {
        /// <summary>低：一般不影响同步（本机界面数值、视野渲染等）。</summary>
        Low = 0,

        /// <summary>中：本身不参与校验，但会通过后续指令间接影响同步，属延迟性风险。</summary>
        Medium = 1,

        /// <summary>高：直接改动参与校验的状态，单方面修改必然掉线。</summary>
        High = 2
    }

    /// <summary>功能唯一标识。落盘时用这些名字，改动会破坏旧配置文件。</summary>
    public enum CheatId
    {
        Money = 0,
        PowerOutput = 1,
        SelectedInvincible = 2,
        KillEnemies = 3,
        InstantBuild = 4,
        RevealMap = 5
    }

    /// <summary>把一个功能项在配置里用的稳定名字集中在一处，避免散落的字符串字面量。</summary>
    public static class CheatNames
    {
        public static string Key(CheatId id)
        {
            switch (id)
            {
                case CheatId.Money: return "money";
                case CheatId.PowerOutput: return "powerOutput";
                case CheatId.SelectedInvincible: return "selectedInvincible";
                case CheatId.KillEnemies: return "killEnemies";
                case CheatId.InstantBuild: return "instantBuild";
                case CheatId.RevealMap: return "revealMap";
                default: return "unknown";
            }
        }

        public static bool TryParseKey(string key, out CheatId id)
        {
            id = CheatId.Money;
            if (string.IsNullOrEmpty(key)) { return false; }

            CheatId[] all = (CheatId[])Enum.GetValues(typeof(CheatId));
            for (int i = 0; i < all.Length; i++)
            {
                if (string.Equals(Key(all[i]), key, StringComparison.OrdinalIgnoreCase))
                {
                    id = all[i];
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// 一个可开关的功能项。
    ///
    /// <see cref="Locator"/> 为空表示这个功能的地址还没校准出来 ——
    /// 界面上必须置灰，绝不能让它假装能用（否则玩家一开就静默失效）。
    /// </summary>
    public sealed class CheatFeature
    {
        public readonly CheatId Id;
        public readonly string Name;
        public readonly string Description;
        public readonly SynchRisk Risk;

        /// <summary>字段定位信息。未校准时为 null。</summary>
        public FieldLocator Locator;

        /// <summary>目标值（界面显示值，写入时会按 ValueScale 换算）。</summary>
        public uint TargetValue;

        /// <summary>是否处于开启（持续锁定）状态。</summary>
        public bool Enabled;

        /// <summary>输入校验范围。</summary>
        public uint MinValue = 0;
        public uint MaxValue = 2000000000;

        /// <summary>是否需要在界面上输入数值（无敌、清场这类是一次性动作，不需要）。</summary>
        public readonly bool NeedsValue;

        /// <summary>鼠标悬停时显示的详细说明（风险原因、注意事项）。空串表示不显示。</summary>
        public string Detail = "";

        // ---------------- 运行时状态（不落盘） ----------------

        /// <summary>当前解析到的字段地址；0 表示尚未解析或解析失败。</summary>
        public uint Address;

        /// <summary>距上次解析指针链经过的轮次，用于定期重新解析。</summary>
        public int TicksSinceResolve;

        /// <summary>成功写入次数，界面用来证明"确实在工作"。</summary>
        public long WriteCount;

        /// <summary>最近一次读到的游戏内数值，界面用来显示实际效果。</summary>
        public uint LastSeenValue;

        /// <summary>最近一次错误，空串表示正常。</summary>
        public string LastError = "";

        public CheatFeature(CheatId id, string name, string description, SynchRisk risk, bool needsValue)
        {
            Id = id;
            Name = name;
            Description = description;
            Risk = risk;
            NeedsValue = needsValue;
        }

        public bool IsCalibrated { get { return Locator != null && Locator.IsCalibrated; } }

        /// <summary>是否真的在起作用（已开启 + 已校准 + 解析到地址）。</summary>
        public bool IsActive { get { return Enabled && IsCalibrated && Address != 0; } }

        /// <summary>卡片上显示的风险标签。刻意做短：完整解释放在鼠标悬停提示里，
        /// 卡片上只要一眼能看出颜色等级即可。</summary>
        public string RiskText
        {
            get
            {
                switch (Risk)
                {
                    case SynchRisk.Low: return "风险 低";
                    case SynchRisk.Medium: return "风险 中";
                    default: return "风险 高";
                }
            }
        }

        public string RiskName
        {
            get
            {
                switch (Risk)
                {
                    case SynchRisk.Low: return "低";
                    case SynchRisk.Medium: return "中";
                    default: return "高";
                }
            }
        }

        public string DescribeTarget()
        {
            if (!NeedsValue) { return "—"; }
            if (!IsCalibrated) { return "待校准"; }
            return TargetValue.ToString(CultureInfo.InvariantCulture);
        }
    }
}
