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

        /// <summary>盯着的那几秒里，**游戏自己把这一格改掉了**（说明我们的写入被游戏接手了）。</summary>
        public bool GameChangedIt;

        /// <summary>因为上面那条而**故意没有还原**（把旧值塞回去会跟游戏打架）。</summary>
        public bool SkippedRestore;

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
                "写前 {0}｜写入 {1}（{2}）｜写入后读到 {3}｜{4} 秒后读到 {5}",
                Before, Written, Wrote ? "已写" : "**没写成**",
                ReadAfterWrite ? AfterWrite.ToString(CultureInfo.InvariantCulture) : "读不到",
                (HoldMsHeld / 1000.0).ToString("0.0", CultureInfo.InvariantCulture),
                ReadAfterHold ? AfterHold.ToString(CultureInfo.InvariantCulture) : "读不到"));

            if (ReadMoney)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "你的金钱：写前 {0} → {1} 秒后 {2}{3}",
                    MoneyBefore, (HoldMsHeld / 1000.0).ToString("0.0", CultureInfo.InvariantCulture),
                    MoneyAfter,
                    MoneyAfter == MoneyBefore ? "（这期间没有扣钱）" : "（这期间扣了钱）"));
            }

            if (SkippedRestore)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "这一格在这几秒里**被游戏自己改过**（我们写的是 {0}，现在是 {1}）——",
                    Written, AfterHold));
                text.AppendLine("**故意不还原**：游戏已经接手这一格，把旧值硬塞回去反而会跟它打架。");
            }
            else if (Restored)
            {
                text.AppendLine(AfterRestore == Before
                    ? "已还原成原值 ✓"
                    : string.Format(CultureInfo.InvariantCulture,
                        "**还原后与原值不一致** ⚠（原值 {0}，还原后读到 {1}）", Before, AfterRestore));
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
    /// 第二轮（本版）跟着**房子 +0x53BC 指向的「正在生产的东西」**走 ——
    /// 真机数据指出：那个对象的 `+0x60` **每拍正好减少"这一拍扣掉的钱"、造好时正好落到 0**
    /// （一局只造一座矿场：1926 → 0，而矿场造价 2000 = 首拍已付 74 + 剩 1926）。
    ///
    /// 安全设计（与 `PowerProbe` 同源，跟着把手走时再加三道门）：
    ///   · 只写**我方房子**（由金钱链解析）或**它的 +0x53BC 指向的对象**，不碰任何别的东西；
    ///   · 跟着把手走时：把手为空 → 拒绝（**没东西在造，写了也不知道是谁的反应**）；
    ///     目标头部必须像游戏对象、且**类别与真机验过的那一类一致**，否则拒绝；
    ///     目标那一格必须是"像个数"的小正数（0 = 没东西在造；大得像地址 = 找错地方了）；
    ///   · **绝不写金钱偏移 `+0x30C`**（那是玩家的钱，没有任何试探理由写它）；
    ///   · 写前先查目标页可写（`VirtualQueryEx`），不可写就拒绝；
    ///   · 写完回读校验；**盯完后：游戏没动过就无条件还原并回读确认；
    ///     游戏自己动过就不还原**（那一格已经被游戏接手，塞回旧值会跟它打架）；
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

            uint moneyAddress;
            string moneyError;
            if (profile.Money.TryResolve(memory, moduleBase, out moneyAddress, out moneyError))
            {
                result.ReadMoney = memory.ReadUInt32(moneyAddress, out result.MoneyBefore);
            }

            uint write = spec.IsDelta ? unchecked(target.Value + spec.Value) : spec.Value;
            result.Written = write;

            try
            {
                Logger.Info("HouseProbe", string.Format(CultureInfo.InvariantCulture,
                    "字段试探：0x{0:X8}（对象 0x{1:X8} +0x{2:X}；跟着把手={3}）{4} → {5}",
                    result.FieldAddress, target.Object, spec.Offset, target.FollowedHandle,
                    target.Value, write));

                result.Wrote = memory.WriteUInt32(result.FieldAddress, write);
                uint afterWrite;
                result.ReadAfterWrite = memory.ReadUInt32(result.FieldAddress, out afterWrite);
                result.AfterWrite = afterWrite;

                System.Threading.Thread.Sleep(holdMs);

                uint afterHold;
                result.ReadAfterHold = memory.ReadUInt32(result.FieldAddress, out afterHold);
                result.AfterHold = afterHold;
                if (result.Wrote && result.ReadAfterHold && afterHold != write)
                {
                    result.GameChangedIt = true;
                }

                if (result.ReadMoney)
                {
                    uint moneyAfter;
                    if (memory.ReadUInt32(moneyAddress, out moneyAfter)) { result.MoneyAfter = moneyAfter; }
                    else { result.ReadMoney = false; }
                }
            }
            finally
            {
                if (result.GameChangedIt)
                {
                    // 游戏自己动过这一格 ⇒ 这一格现在归游戏管。硬塞回旧值会跟它打架（可能让它重新开始扣钱）。
                    result.SkippedRestore = true;
                    Logger.Info("HouseProbe", string.Format(CultureInfo.InvariantCulture,
                        "字段试探收尾：这一格被游戏自己改过（{0} → {1}），**故意不还原**",
                        write, result.AfterHold));
                }
                else
                {
                    // **无条件还原**：试探值最多只存在于上面那几秒里。
                    uint restored;
                    result.Restored = memory.WriteUInt32(result.FieldAddress, target.Value);
                    result.ReadAfterRestore = memory.ReadUInt32(result.FieldAddress, out restored);
                    result.AfterRestore = restored;

                    Logger.Info("HouseProbe", string.Format(CultureInfo.InvariantCulture,
                        "字段试探收尾：还原 {0}（写回 {1}，回读到 {2}）",
                        result.Restored && restored == target.Value ? "成功" : "**失败**",
                        target.Value, restored));
                }
            }

            return result;
        }
    }
}
