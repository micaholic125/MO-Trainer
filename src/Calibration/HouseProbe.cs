using System;
using System.Globalization;
using System.Text;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 「字段试探」的目标：**写哪儿**（我方房子本体，还是"正在生产的东西"）+ **写什么**。
    ///
    /// 目标配置写在 `dist\config\house-probe.txt`（一行），两种写法：
    ///   `0x2DC +100000`   ← 两段 = 写**我方房子**的 +0x2DC（旧写法，仍然认）
    ///   `handle 0x60 =1`  ← 三段 = **跟着房子 +0x53BC 走**，写「正在生产的东西」的 +0x60
    /// 放文件而不是写死在代码里：**换目标不用重新编译**（重编译 = 关掉正在跑的修改器）。
    /// 解析失败必须说得出为什么（绝不"按钮能点、程序什么都不做"）。
    /// </summary>
    public sealed class HouseProbeSpec
    {
        /// <summary>true = 目标不是房子本体，而是房子 +0x53BC 指向的**正在生产的东西**。</summary>
        public bool FollowProducingHandle;

        /// <summary>要写的偏移（相对于上面那个对象）。</summary>
        public uint Offset;

        /// <summary>true = 在"当前值"上加/减（`+100000`）；false = 直接写这个值（`=1`）。</summary>
        public bool IsDelta;

        public uint Value;
        public string Raw = "";

        /// <summary>给界面用的一句话：「写哪儿，写什么」。</summary>
        public string Describe()
        {
            string where = FollowProducingHandle
                ? string.Format(CultureInfo.InvariantCulture, "「正在生产的东西」+ 0x{0:X}", Offset)
                : string.Format(CultureInfo.InvariantCulture, "我方房子 + 0x{0:X}", Offset);
            string what = IsDelta
                ? string.Format(CultureInfo.InvariantCulture, "在当前值上 +{0}", Value)
                : string.Format(CultureInfo.InvariantCulture, "直接写 {0}", Value);
            return where + "，" + what;
        }

        /// <summary>
        /// 解析一行配置，形如：
        ///   0x2DC +100000      ← 我方房子 +0x2DC，在当前值上加 10 万
        ///   0x57A4 =0          ← 我方房子 +0x57A4，直接写 0
        ///   handle 0x60 =1     ← 「正在生产的东西」+0x60，直接写 1
        /// </summary>
        public static bool TryParse(string text, out HouseProbeSpec spec, out string error)
        {
            spec = null;
            error = "";

            if (text == null || text.Trim().Length == 0)
            {
                error = "配置是空的。请写一行，例如：0x2DC +100000 或 handle 0x60 =1";
                return false;
            }

            string[] parts = text.Trim().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string offsetText;
            string valueText;
            bool follow = false;

            if (parts[0].Equals("house", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 3)
                {
                    error = "写了 `house` 但后面缺东西。要三段：house 0x2DC +100000";
                    return false;
                }
                offsetText = parts[1];
                valueText = parts[2];
            }
            else if (parts[0].Equals("handle", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 3)
                {
                    error = "写了 `handle` 但后面缺东西。要三段：handle 0x60 =1";
                    return false;
                }
                follow = true;
                offsetText = parts[1];
                valueText = parts[2];
            }
            else if (parts.Length >= 3)
            {
                error = "这一行看不懂。只有两种写法：\n"
                      + "  · 两段 `0x2DC +100000` —— 写我方房子\n"
                      + "  · 三段 `handle 0x60 =1` —— 写「正在生产的东西」";
                return false;
            }
            else if (parts.Length < 2)
            {
                error = "配置格式不对。要两段：偏移 和 值，例如：0x2DC +100000";
                return false;
            }
            else
            {
                offsetText = parts[0];
                valueText = parts[1];
            }

            if (offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                offsetText = offsetText.Substring(2);
            }
            uint offset;
            if (!uint.TryParse(offsetText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out offset))
            {
                error = "偏移看不懂：「" + offsetText + "」（要十六进制，例如 0x2DC）";
                return false;
            }

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
                error = "数值看不懂：「" + valueText + "」（要十进制整数，例如 +100000 或 =1）";
                return false;
            }

            HouseProbeSpec parsed = new HouseProbeSpec();
            parsed.FollowProducingHandle = follow;
            parsed.Offset = offset;
            parsed.IsDelta = isDelta;
            parsed.Value = value;
            parsed.Raw = text.Trim();
            spec = parsed;
            return true;
        }
    }

    /// <summary>一次「字段试探」的结果（含写前 / 写入后 / 盯完后 / 还原后四个读数）。</summary>
    /// <summary>盯的那几秒里的一次读数（默认每 300 毫秒一次）—— 事后靠它看清"这几秒到底发生了什么"。</summary>
    public sealed class HoldSample
    {
        public int Ms;
        public uint Value;
        public bool ValueOk;
        public uint Money;
        public bool MoneyOk;
        public uint Handle;
        public bool HandleOk;
    }

    public sealed class HouseProbeResult
    {
        public string Note = "";

        /// <summary>true = 这次不是写房子本体，而是跟着房子 +0x53BC 写的「正在生产的东西」。</summary>
        public bool FollowedHandle;

        public uint HouseAddress;

        /// <summary>真正被写的对象（房子本体，或把手指向的那个东西）。</summary>
        public uint TargetObject;

        /// <summary>被写对象的类型表（头部 4 字节）—— 用来核对"是不是我验过的那一类"。</summary>
        public uint TargetTypeTable;

        public uint Offset;
        public uint FieldAddress;

        public uint Before;
        public uint Written;
        public uint AfterWrite;
        public uint AfterHold;
        public uint AfterRestore;

        public bool Wrote;
        public bool ReadAfterWrite;
        public bool ReadAfterHold;
        public bool ReadAfterRestore;
        public bool Restored;

        /// <summary>我们一共写成功多少次（**锁定式反复写**，不是写一次）。</summary>
        public int Writes;

        /// <summary>这几十毫秒一次的空隙里，**游戏把这一格写回去过几次**（0 = 它没跟我们抢这一格）。</summary>
        public int GameWrites;

        /// <summary>盯的过程里的读数时间线。</summary>
        public System.Collections.Generic.List<HoldSample> Samples =
            new System.Collections.Generic.List<HoldSample>();

        /// <summary>那颗东西从把手上消失了（造好时游戏就会把它收走）。</summary>
        public bool TargetGone;

        /// <summary>盯的过程中读不到那一格了（不敢盲写回去）。</summary>
        public bool LostField;

        /// <summary>因为上面某条而**故意没有还原**（写回去会出问题）。</summary>
        public bool SkippedRestore;

        public string SkipReason = "";

        public uint MoneyBefore;
        public uint MoneyAfter;
        public bool ReadMoney;

        public int HoldMsHeld;

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
                "我方房子 0x{0:X8}", HouseAddress));

            if (FollowedHandle)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "跟着房子 + 0x{0:X} 走 → 「正在生产的东西」0x{1:X8}（类型表 0x{2:X8}）",
                    HarvestSession.ProducingPointerOffset, TargetObject, TargetTypeTable));
            }

            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "写的那一格：0x{0:X8}（对象 0x{1:X8} + 0x{2:X}）",
                FieldAddress, TargetObject, Offset));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "写前 {0} → **反复写 {1}**（成功 {2} 次，每 {3} 毫秒一次）｜一共盯了 {4} 秒",
                Before, Written, Writes, HouseProbe.RewriteEveryMs,
                (HoldMsHeld / 1000.0).ToString("0.0", CultureInfo.InvariantCulture)));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "这几秒里**游戏把这一格写回去过 {0} 次**（0 = 它没跟我们抢这一格）", GameWrites));

            if (Samples.Count > 0)
            {
                text.AppendLine("读数（这一格的值 / 你的钱 / 那颗东西还在不在）：");
                for (int i = 0; i < Samples.Count; i++)
                {
                    if (Samples.Count > 12 && i == 5) { text.AppendLine("  ……"); }
                    if (Samples.Count > 12 && i >= 5 && i < Samples.Count - 5) { continue; }

                    HoldSample s = Samples[i];
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  +{0:0.0}s｜{1}｜{2}｜{3}",
                        s.Ms / 1000.0,
                        s.ValueOk ? s.Value.ToString(CultureInfo.InvariantCulture) : "读不到",
                        s.MoneyOk ? s.Money.ToString(CultureInfo.InvariantCulture) : "读不到",
                        s.HandleOk ? (s.Handle == TargetObject ? "在" : "**不在了**") : "读不到"));
                }
            }

            if (SkippedRestore)
            {
                text.AppendLine("**故意不还原**：" + SkipReason);
            }
            else if (Restored)
            {
                text.AppendLine(AfterRestore == Before
                    ? "已还原成原值 ✓"
                    : string.Format(CultureInfo.InvariantCulture,
                        "写回了原值 {0}，但马上读到的是 {1} —— **游戏又把它写成了自己的值**（这本身就说明它在管这一格）",
                        Before, AfterRestore));
            }
            else
            {
                text.AppendLine("**没能还原** ⚠");
            }

            return text.ToString();
        }
    }

    /// <summary>
    /// 「字段试探」—— 全项目第二个会往游戏里写**实验值**的地方（第一个是「电力试探」）。
    ///
    /// 为什么需要它：`[S6-2]` 的"进度字段"只能**写一下看行为**（形态判据已经用遍）。
    /// 第一轮写的是房子的 `+0x2DC`（2026-10-10 17:40，写进去了、建造毫无反应 → 干净否掉）。
    /// 第二轮跟着**房子 +0x53BC 指向的「正在生产的东西」**走 —— 写它的 `+0x60`（"还差多少没付完"）。
    /// 真机结果（2026-10-10 18:16）：值**确实写进去了**、3 秒里**没被游戏改回去**，
    /// **但那 3 秒里金钱一分没扣**（而同一时刻"永不停歇"的计数器照常走 ⇒ 不是游戏卡住），
    /// 收尾写回原值后**扣钱立刻恢复** ⇒ **这一格确实是生产逻辑读的**，只是写 1 让它停住了。
    ///
    /// 第三轮（本版）两处改进：
    ///   ① 由"写一次"改成**锁定式反复写**（每 60 毫秒一次）—— 依据是用户 2026-10-10 的补充：
    ///      这游戏"钱"有一份**隐藏真值**、**单写一次几乎不改变游戏状态**，修改器的金钱功能靠的就是每拍重写；
    ///   ② 盯的过程里每 300 毫秒记一次读数（值 / 金钱 / 那颗东西还在不在），
    ///      并把"游戏在这几十毫秒里抢回去过几次"数出来 —— 事后能看清这几秒到底发生了什么。
    ///   另外：**那颗东西从把手上消失就立刻停手且不还原**（造好时游戏就会把它收走）。
    ///
    /// 安全设计（与 `PowerProbe` 同源，跟着把手走时再加三道门）：
    ///   · 只写**我方房子**（由金钱链解析）或**它的 +0x53BC 指向的对象**，不碰任何别的东西；
    ///   · 跟着把手走时：把手为空 → 拒绝（**没东西在造，写了也不知道是谁的反应**）；
    ///     目标头部必须像游戏对象、且**类别与真机验过的那一类一致**，否则拒绝；
    ///     目标那一格必须是"像个数"的小正数（0 = 没东西在造；大得像地址 = 找错地方了）；
    ///   · **绝不写金钱偏移 `+0x30C`**（那是玩家的钱，没有任何试探理由写它）；
    ///   · 写前先查目标页可写（`VirtualQueryEx`），不可写就拒绝；
    ///   · 锁定式反复写、每次写完回读；**盯完后还原成原值并回读确认**，
    ///     除了两种情况：**那颗东西已从把手上消失**（造好了、被游戏收走）或**读不到那一格** ——
    ///     那时绝不盲写；
    ///   · 缺前置（进程没连、金钱链解析不出来）一律**拒绝执行并说明缺什么**。
    /// </summary>
    public static class HouseProbe
    {
        /// <summary>金钱偏移：绝不往这里写。</summary>
        public const uint MoneyOffset = 0x30C;

        /// <summary>房子对象按 0x6000 字节看待（采集器盯的就是这么大），超出就拒绝。</summary>
        public const uint MaxOffset = 0x6000;

        /// <summary>「正在生产的东西」按 0x1000 字节看待（采集器盯的就是这么大），超出就拒绝。</summary>
        public const uint ProducingMaxOffset = 0x1000;

        /// <summary>
        /// "像个数"的上限：超过这个数就当它是地址（与 `ProgressHunter.LooksLikeAddress` 同口径）。
        /// 「还差多少钱没付完」不会长到 2.6 亿。
        /// </summary>
        public const uint MaxSaneFieldValue = 0x10000000;

        /// <summary>
        /// 真机验过的**唯一**一类「正在生产的东西」（2026-10-10 17:54，造矿场那一局）。
        /// 别的类别一律拒绝写：**宁可少写一次、回头补验，也不往没验过的对象上写**。
        /// </summary>
        public const uint VerifiedProducingTypeTable = 0x007E88D0;

        /// <summary>写完之后盯这么久再收尾（够用户看清"是不是立刻造好"）。</summary>
        public const int HoldMs = 3000;

        /// <summary>
        /// 每这么多毫秒**重写一次**（锁定式，不是写一次就完）。
        /// 依据：用户 2026-10-10 的补充 —— 这游戏里"钱"有一份**隐藏真值**，
        /// **单写一次几乎不改变游戏状态**，修改器的金钱功能靠的就是每拍重写（锁定）。
        /// 所以试探也必须这么写，否则"没反应"可能只是因为我们只写了一次。
        /// </summary>
        public const int RewriteEveryMs = 60;

        /// <summary>每这么多毫秒记一次读数（值 / 金钱 / 那颗东西还在不在），写进报告供事后分析。</summary>
        public const int SampleEveryMs = 300;

        /// <summary>一次试探要写的对象与偏移（只读部分）。</summary>
        private sealed class Target
        {
            public bool Ok;
            public string Note = "";
            public uint House;
            public uint Object;
            public uint TypeTable;
            public bool FollowedHandle;
            public uint Value;
            public bool ReadValue;
            public uint MaxOffset;
            public string ExtraNote = "";
        }

        /// <summary>
        /// 只读侦查：把"现在会写哪儿、那一格现在是多少"读出来（**一个字节都不写**）。
        /// 认不出来也照实说明卡在哪一步 —— 用户在点第二次之前就该看到这些。
        /// </summary>
        public static string Preview(
            MemoryIO memory, CalibrationProfile profile, uint moduleBase, HouseProbeSpec spec)
        {
            return Preview(memory, profile, moduleBase, spec, VerifiedProducingTypeTable);
        }

        /// <summary>
        /// 同上的重载：`expectedProducingType` 必须与 `Run` 用**同一个值**，
        /// 否则会出现"侦查说能写、真写时被拒"（或反过来）这种自相矛盾 —— 自动检体里会核对这一点。
        /// </summary>
        public static string Preview(
            MemoryIO memory, CalibrationProfile profile, uint moduleBase, HouseProbeSpec spec,
            uint expectedProducingType)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 只读侦查（一个字节都没写）===");
            if (spec == null)
            {
                text.AppendLine("  没有试探参数。");
                return text.ToString();
            }

            try
            {
                Target target = Resolve(memory, profile, moduleBase, spec, expectedProducingType);
                if (!target.Ok)
                {
                    text.AppendLine("  现在写不了，原因：" + target.Note);
                    text.AppendLine("  （不要紧：**先去游戏里造一样东西**，等它开始建造时再点第二次 —— 那时会重新读一次现场。）");
                    return text.ToString();
                }

                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  我方房子：0x{0:X8}", target.House));
                if (target.FollowedHandle)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  跟着 + 0x{0:X} 走 → 「正在生产的东西」：0x{1:X8}（类型表 0x{2:X8}）",
                        HarvestSession.ProducingPointerOffset, target.Object, target.TypeTable));
                }
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  预备写的格子：0x{0:X8}（对象 0x{1:X8} + 0x{2:X}）现在是 {3}",
                    target.Object + spec.Offset, target.Object, spec.Offset,
                    target.ReadValue ? target.Value.ToString(CultureInfo.InvariantCulture) : "读不到"));
                if (target.ExtraNote.Length > 0) { text.AppendLine("  " + target.ExtraNote); }
                text.AppendLine("  准备写：" + spec.Describe());
            }
            catch (Exception ex)
            {
                text.AppendLine("  只读侦查出错了（没写任何东西）：" + ex.GetType().Name + ": " + ex.Message);
            }
            return text.ToString();
        }

        /// <summary>解析出"要写的对象"，并把所有"不许写"的门走一遍（全是只读操作）。</summary>
        private static Target Resolve(
            MemoryIO memory, CalibrationProfile profile, uint moduleBase,
            HouseProbeSpec spec, uint expectedProducingType)
        {
            Target target = new Target();
            target.MaxOffset = spec.FollowProducingHandle ? ProducingMaxOffset : MaxOffset;

            if (memory == null) { target.Note = "内存接口不可用"; return target; }
            if (profile == null || !profile.Money.IsCalibrated)
            {
                target.Note = "金钱未校准 —— 认不出我方房子（先跑一次校准向导的金钱步骤）";
                return target;
            }
            if (spec.Offset == MoneyOffset)
            {
                target.Note = "**拒绝**：+0x30C 是你的金钱，没有任何试探理由写它";
                return target;
            }
            if (spec.Offset >= target.MaxOffset)
            {
                target.Note = string.Format(CultureInfo.InvariantCulture,
                    "**拒绝**：偏移 0x{0:X} 超出{1}的范围（上限 0x{2:X}）",
                    spec.Offset, spec.FollowProducingHandle ? "「正在生产的东西」" : "房子对象", target.MaxOffset);
                return target;
            }

            uint moneyAddress;
            string error;
            if (!profile.Money.TryResolve(memory, moduleBase, out moneyAddress, out error))
            {
                target.Note = "金钱地址解析不出来：" + error;
                return target;
            }
            if (moneyAddress < MoneyOffset + 0x10000)
            {
                target.Note = string.Format(CultureInfo.InvariantCulture,
                    "金钱地址 0x{0:X8} 小得不合理，拒绝执行", moneyAddress);
                return target;
            }

            target.House = moneyAddress - MoneyOffset;
            target.Object = target.House;

            if (spec.FollowProducingHandle)
            {
                uint handle;
                if (!memory.ReadUInt32(target.House + HarvestSession.ProducingPointerOffset, out handle))
                {
                    target.Note = string.Format(CultureInfo.InvariantCulture,
                        "读不到房子的「正在生产」把手（0x{0:X8}）",
                        target.House + HarvestSession.ProducingPointerOffset);
                    return target;
                }
                if (handle == 0)
                {
                    target.Note = "现在**没有任何东西在造**（房子的「正在生产」把手是空的）—— "
                                + "试探必须**在建造进行中**点，否则写了也不知道是谁的反应";
                    return target;
                }

                uint typeTable;
                if (!memory.ReadUInt32(handle, out typeTable))
                {
                    target.Note = string.Format(CultureInfo.InvariantCulture,
                        "把手指向 0x{0:X8}，但读不到它的头部", handle);
                    return target;
                }
                uint moduleSize = profile.ModuleSize;
                if (moduleSize == 0)
                {
                    target.Note = "配置里没记「模块大小」，没法核对把手指向的东西是不是游戏里的对象 —— "
                                + "先重跑一次校准向导（或确认 profile.json 里有 moduleSize）";
                    return target;
                }
                if (typeTable < moduleBase || typeTable >= moduleBase + moduleSize)
                {
                    target.Note = string.Format(CultureInfo.InvariantCulture,
                        "把手指向的 0x{0:X8} 头部是 0x{1:X8}，**不像游戏里的对象** —— 拒绝写它",
                        handle, typeTable);
                    return target;
                }
                if (typeTable != expectedProducingType)
                {
                    target.Note = string.Format(CultureInfo.InvariantCulture,
                        "把手指向的 0x{0:X8} 类别是 0x{1:X8}，**不是我真机验过的那一类（0x{2:X8}）** —— "
                        + "没验过的对象上不写值。请把这个类别号发回来，我补验后再放开。",
                        handle, typeTable, expectedProducingType);
                    return target;
                }

                target.Object = handle;
                target.TypeTable = typeTable;
                target.FollowedHandle = true;
            }

            uint field = target.Object + spec.Offset;
            if (!memory.IsWritable(field))
            {
                target.Note = string.Format(CultureInfo.InvariantCulture,
                    "目标那一页不可写（VirtualQueryEx 判定）—— 拒绝写入。地址 0x{0:X8}", field);
                return target;
            }

            target.ReadValue = memory.ReadUInt32(field, out target.Value);
            if (!target.ReadValue)
            {
                target.Note = string.Format(CultureInfo.InvariantCulture,
                    "读不到那一格的现值（0x{0:X8}）", field);
                return target;
            }

            if (spec.FollowProducingHandle)
            {
                if (target.Value == 0)
                {
                    target.Note = string.Format(CultureInfo.InvariantCulture,
                        "「正在生产的东西」的 +0x{0:X} 现在是 0 —— 要么此刻真的没东西在造、要么这一格本来就是 0。"
                        + "**为了不写错地方，拒绝执行**（在建造进行中再点一次）。", spec.Offset);
                    return target;
                }
                if (target.Value >= MaxSaneFieldValue)
                {
                    target.Note = string.Format(CultureInfo.InvariantCulture,
                        "「正在生产的东西」的 +0x{0:X} 现在是 {1}，**大得像地址** —— "
                        + "这一格不像「还差多少钱」，拒绝执行。", spec.Offset, target.Value);
                    return target;
                }
                target.ExtraNote = string.Format(CultureInfo.InvariantCulture,
                    "（对象头部 4 字节 = 0x{0:X8}，与真机验过的那一类一致 ✓）", target.TypeTable);
            }

            target.Ok = true;
            return target;
        }

        public static HouseProbeResult Run(
            MemoryIO memory, CalibrationProfile profile, uint moduleBase, HouseProbeSpec spec)
        {
            return Run(memory, profile, moduleBase, spec, HoldMs, VerifiedProducingTypeTable);
        }

        /// <summary>
        /// 真写一次。`holdMs` / `expectedProducingType` 是给自动体检用的（合成场景里没有真游戏，
        /// 也不能真等 3 秒）；**正常调用走上面那个两参数版本，用的是真机验证过的值**。
        /// </summary>
        public static HouseProbeResult Run(
            MemoryIO memory, CalibrationProfile profile, uint moduleBase, HouseProbeSpec spec,
            int holdMs, uint expectedProducingType)
        {
            HouseProbeResult result = new HouseProbeResult();
            if (spec == null) { result.Note = "没有试探参数"; return result; }
            if (memory == null) { result.Note = "内存接口不可用"; return result; }

            Target target = Resolve(memory, profile, moduleBase, spec, expectedProducingType);
            if (!target.Ok) { result.Note = target.Note; return result; }

            result.FollowedHandle = target.FollowedHandle;
            result.HouseAddress = target.House;
            result.TargetObject = target.Object;
            result.TargetTypeTable = target.TypeTable;
            result.Offset = spec.Offset;
            result.FieldAddress = target.Object + spec.Offset;
            result.Before = target.Value;
            result.HoldMsHeld = holdMs;

            uint moneyAddress = 0;
            string moneyError;
            bool moneyOk = profile.Money.TryResolve(memory, moduleBase, out moneyAddress, out moneyError);
            if (moneyOk) { result.ReadMoney = memory.ReadUInt32(moneyAddress, out result.MoneyBefore); }

            uint write = spec.IsDelta ? unchecked(target.Value + spec.Value) : spec.Value;
            result.Written = write;

            try
            {
                Logger.Info("HouseProbe", string.Format(CultureInfo.InvariantCulture,
                    "字段试探：0x{0:X8}（对象 0x{1:X8} +0x{2:X}；跟着把手={3}）{4} → {5}，锁定式反复写（每 {6} 毫秒一次）",
                    result.FieldAddress, target.Object, spec.Offset, target.FollowedHandle,
                    target.Value, write, RewriteEveryMs));

                int elapsed = 0;
                bool everWritten = false;
                result.HoldMsHeld = holdMs;

                while (elapsed < holdMs)
                {
                    // ① 先读一眼：这一格还是我们上次写的值吗？
                    //    不是 ⇒ **游戏在这几十毫秒里自己写过一次**（它在跟我们抢这一格）。
                    uint nowValue;
                    bool nowOk = memory.ReadUInt32(result.FieldAddress, out nowValue);
                    if (everWritten && nowOk && nowValue != write) { result.GameWrites++; }

                    // ② 重写 —— **锁定式**，与金钱锁定同一个套路（用户提示：这游戏单写一次几乎没用）。
                    if (memory.WriteUInt32(result.FieldAddress, write))
                    {
                        result.Writes++;
                        everWritten = true;
                    }

                    // ③ 定时记读数（事后靠这条时间线看清这几秒里发生了什么）。
                    if (elapsed % SampleEveryMs == 0)
                    {
                        HoldSample sample = new HoldSample();
                        sample.Ms = elapsed;
                        sample.ValueOk = memory.ReadUInt32(result.FieldAddress, out sample.Value);
                        if (moneyOk) { sample.MoneyOk = memory.ReadUInt32(moneyAddress, out sample.Money); }
                        if (target.FollowedHandle)
                        {
                            sample.HandleOk = memory.ReadUInt32(
                                target.House + HarvestSession.ProducingPointerOffset, out sample.Handle);
                        }
                        result.Samples.Add(sample);

                        // 那颗东西从把手上消失了 —— 造好时游戏就会把它收走。这时**立刻停手**。
                        if (sample.HandleOk && sample.Handle != target.Object)
                        {
                            result.TargetGone = true;
                            break;
                        }
                        if (!sample.ValueOk)
                        {
                            result.LostField = true;
                            break;
                        }
                    }

                    System.Threading.Thread.Sleep(RewriteEveryMs);
                    elapsed += RewriteEveryMs;
                }

                result.Wrote = result.Writes > 0;

                // 第一条读数正好是"刚写完就读"的那一次，用它当"写入后读到"。
                if (result.Samples.Count > 0)
                {
                    result.AfterWrite = result.Samples[0].Value;
                    result.ReadAfterWrite = result.Samples[0].ValueOk;
                }

                uint afterHold;
                result.ReadAfterHold = memory.ReadUInt32(result.FieldAddress, out afterHold);
                result.AfterHold = afterHold;

                if (moneyOk && result.ReadMoney)
                {
                    uint moneyAfter;
                    result.ReadMoney = memory.ReadUInt32(moneyAddress, out moneyAfter);
                    if (result.ReadMoney) { result.MoneyAfter = moneyAfter; }
                }
            }
            finally
            {
                if (result.TargetGone)
                {
                    result.SkippedRestore = true;
                    result.SkipReason = "那颗东西已经从把手上消失了（造好时游戏就会把它收走）—— "
                                      + "那个对象多半已经不在了，硬写回去是往别人的地盘上写";
                }
                else if (result.LostField)
                {
                    result.SkippedRestore = true;
                    result.SkipReason = "盯的过程中读不到那一格了 —— 不知道现在是什么状况，不敢盲写回去";
                }
                else
                {
                    // **还原**：试探值最多只存在于上面那几秒里。
                    uint restored;
                    result.Restored = memory.WriteUInt32(result.FieldAddress, target.Value);
                    result.ReadAfterRestore = memory.ReadUInt32(result.FieldAddress, out restored);
                    result.AfterRestore = restored;

                    Logger.Info("HouseProbe", string.Format(CultureInfo.InvariantCulture,
                        "字段试探收尾：写 {0} 次、游戏抢回去 {1} 次；还原 {2}（写回 {3}，回读到 {4}）",
                        result.Writes, result.GameWrites,
                        result.Restored && restored == target.Value ? "成功" : "**失败**",
                        target.Value, restored));
                }

                if (result.SkippedRestore)
                {
                    Logger.Info("HouseProbe", "字段试探收尾：**故意不还原** —— " + result.SkipReason);
                }
            }

            return result;
        }
    }
}
