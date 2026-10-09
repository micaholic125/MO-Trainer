using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Cheats
{
    /// <summary>一次"电力增强"的执行结果。</summary>
    public sealed class PowerBoostResult
    {
        /// <summary>本次提升的型号数（说明书份数）。</summary>
        public int Types;

        /// <summary>这些型号里属于我方的建筑座数。</summary>
        public int Buildings;

        /// <summary>本次真正写入了几个字段。</summary>
        public int Writes;

        /// <summary>其中"敌方也拥有"的型号数（同型号共用说明书 → 敌方一并受益）。</summary>
        public int SharedTypes;

        /// <summary>非空 = 本次什么都没改，这句话是**给玩家看的原因**。</summary>
        public string Error = "";

        /// <summary>
        /// 失败原因属于"**这一帧对不上、下一帧多半就好了**"那一类（认不出哪个阵营是我）。
        ///
        /// 为什么要单独标出来：判据是"某个阵营的金钱 == 我此刻的金钱"，而这两次读**不是同一瞬间**——
        /// 玩家正在花钱/进账时就会对不上。真机日志 2026-10-09 16:56:53 抓到过一次，一拍之后自己恢复。
        /// 这种失败是 fail-closed（一个字节都没写），但会让功能断断续续，所以调用方据此**重读金钱再试一次**。
        /// 用布尔量而不是去匹配错误文本 —— 文本改一个字就失效了。
        /// </summary>
        public bool AnchorMiss;

        /// <summary>给日志/界面看的简短结论；空串表示"这一拍什么都没发生"。</summary>
        public string Note = "";
    }

    /// <summary>
    /// 「电力」功能的执行体：**把我方发电建筑对应的"型号说明书"里的发电量改大**。
    ///
    /// ## 为什么不是改建筑、也不是改房子的电力数字
    ///
    /// 2026-09-22 的实测：往房子对象里那个"电力"字段写 30000，界面确实显示 30000，
    /// 可游戏照样判「电力不足」—— 那只是个显示值。
    /// 2026-10-09 的零写入证明（`开发日志/2026-10-09.md` 第十节）进一步查明：
    /// **发电量根本不在建筑身上**（把 5 座建筑逐 4 字节翻遍，一个 150 都没有），
    /// 而是记在**同型号共用一份的"型号说明书"**（类型对象）里：
    ///
    ///   建筑对象 **+0x520** → 型号说明书；说明书里 **+0xEE0** = 该型号的发电量。
    ///
    /// 证据：把用户名下全部建筑按型号求和 —— 磁暴反应堆×3×150 + 核反应堆×2×2000 +
    /// 建造厂×1×60 = **4510**，与游戏自己显示的电力产出一分不差；且全场只有这一个偏移
    /// 同时满足"磁暴反应堆 150、核反应堆 2000"。
    ///
    /// ## 代价（必须让玩家知道）
    ///
    /// 说明书是**同型号共用**的 —— 提升了磁暴反应堆的发电量，**同一局里别人（含 AI）
    /// 的同型号建筑也会一起变强**。这是这条路绕不开的，所以：
    ///   · 界面卡片上常驻写明；
    ///   · 执行时顺带统计"有几个型号敌方也有"，写进状态行与日志。
    ///
    /// ## 安全设计（与秒杀/无敌同一套标准）
    ///
    ///   · **只动我方的型号**：先按"阵营 → 该阵营的金钱"锚定出哪一组是我的（见 `Apply` 第 2 步），
    ///     认不出来就**拒绝执行**并说明原因，绝不退而求其次去改"看着像"的型号；
    ///   · **只动发电型号**（`+0xEE0 > 0`），耗电型号一律不碰；
    ///   · 写前查**可写页**、写后**回读校验**，不一致就放弃这一拍；
    ///   · 每动一个字段都**先记下原值**，关闭功能 / 退出程序时**逐个还原**；
    ///   · 换局（游戏重连）时**丢弃记账、不做还原** —— 新一局的内存是完全重建的，
    ///     拿旧地址回写等于往随机位置写值。
    /// </summary>
    public sealed class PowerBoost
    {
        /// <summary>建筑对象 → 型号说明书 的指针偏移。</summary>
        public const uint TypePointerOffset = 0x520;

        /// <summary>型号说明书里的发电量偏移。</summary>
        public const uint TypePowerOffset = 0xEE0;

        /// <summary>阵营指针在对象里的偏移（2026-10-09 真机验证：我方 11 个对象聚成一堆）。</summary>
        public const uint OwnerOffset = 0x14C;

        /// <summary>目标值上限：防止界面上填个离谱的数把游戏搞怪。</summary>
        public const uint MaxTarget = 200000;

        /// <summary>说明书里"型号 ID"字符串的位置（`NAPOWR` 这类；`AutoInspection` 用的是同一套判据）。</summary>
        private const int IdStringOffset = 0x24;

        /// <summary>记账：型号说明书地址 → 我们改动前的原值。</summary>
        private readonly Dictionary<uint, uint> _originals = new Dictionary<uint, uint>();

        /// <summary>已经提升过的型号（用于在关闭时还原）。</summary>
        public int TrackedTypes { get { return _originals.Count; } }

        /// <summary>当前是否还有"我们改动过、尚未还原"的型号。</summary>
        public bool HasChanges { get { return _originals.Count > 0; } }

        // ============================================================ 执行

        /// <summary>
        /// 执行一拍。`objects` 是已经遍历出来的对象清单（引擎那边解析名单 + 遍历）。
        /// 返回结果里 `Error` 非空表示**这一次一个字节都没写**，原因在 `Error` 里。
        /// </summary>
        public PowerBoostResult Apply(
            MemoryIO memory, uint moduleBase, uint moduleSize,
            IList<TechnoObject> objects, uint myMoney, uint target)
        {
            PowerBoostResult result = new PowerBoostResult();

            if (memory == null) { result.Error = "内存接口不可用"; return result; }
            if (objects == null || objects.Count == 0)
            {
                result.Error = "对象名单是空的 —— 请先到「高级功能」页点一次「自动查找总名单」";
                return result;
            }
            if (myMoney == 0)
            {
                result.Error = "读不到金钱值（要靠它认「哪些建筑是我的」）——请确认金钱仍处于已校准状态";
                return result;
            }
            if (target == 0) { result.Error = "「锁定为」不能是 0"; return result; }
            if (target > MaxTarget)
            {
                result.Error = string.Format(CultureInfo.InvariantCulture,
                    "「锁定为」太大（上限 {0}）—— 先调小一点再开", MaxTarget);
                return result;
            }

            // ---- 1. 把每个对象归到"阵营"名下，并记下它的型号说明书 ----
            Dictionary<uint, uint> typeOfOwner = new Dictionary<uint, uint>();   // 对象地址 → 型号说明书
            Dictionary<uint, uint> ownerOfObject = new Dictionary<uint, uint>(); // 对象地址 → 阵营值
            Dictionary<uint, int> ownerCount = new Dictionary<uint, int>();

            int usable = 0;
            for (int i = 0; i < objects.Count; i++)
            {
                uint address = objects[i].Address;
                uint typeAddress;
                if (!TryReadTypeObject(memory, moduleBase, moduleSize, address, out typeAddress)) { continue; }

                uint owner = TechnoWalker.ReadOwner(memory, address, OwnerOffset);
                if (owner < 0x10000) { continue; }

                typeOfOwner[address] = typeAddress;
                ownerOfObject[address] = owner;
                int n;
                ownerCount[owner] = ownerCount.TryGetValue(owner, out n) ? n + 1 : 1;
                usable++;
            }

            if (usable == 0)
            {
                result.Error = "名单里的对象都读不出「型号说明书」—— 名单可能已失效，请先到「高级功能」页重新查找总名单";
                return result;
            }

            // ---- 2. 认哪一组是我的：**用金钱锚定**，不猜、不硬编码 ----
            //
            // 判据：某个阵营的房子，其 +0x30C 处读到的金钱 == 我此刻的金钱值。
            // 全局只有持有你金钱的那个房子满足它；一个都不匹配 = 名单不可信，拒绝执行。
            uint myOwner = 0;
            int matches = 0;
            foreach (KeyValuePair<uint, int> pair in ownerCount)
            {
                uint money;
                if (!TechnoOwnership.TryReadHouseMoney(memory, pair.Key, moduleBase, moduleSize, out money))
                {
                    continue;   // 不是真阵营（指向代码区 / 读不出金钱）
                }
                if (money != myMoney) { continue; }
                myOwner = pair.Key;
                matches++;
            }

            if (matches == 0)
            {
                result.AnchorMiss = true;
                result.Error = "认不出「哪些建筑是我的」（名单里没有任何阵营的金钱与当前金钱相符）——"
                    + "已拒绝执行，一个字节都没写。请确认金钱仍处于已校准状态，并重新点一次「自动查找总名单」";
                return result;
            }
            if (matches > 1)
            {
                result.Error = string.Format(CultureInfo.InvariantCulture,
                    "有 {0} 个阵营的金钱都与当前值相同，无法确定哪个是你 —— 已拒绝执行（宁可不动，也不写错人）",
                    matches);
                return result;
            }

            // ---- 3. 我方对象 → 型号说明书（去重）----
            Dictionary<uint, int> myTypes = new Dictionary<uint, int>();     // 说明书地址 → 座数
            for (int i = 0; i < objects.Count; i++)
            {
                uint address = objects[i].Address;
                uint owner;
                if (!ownerOfObject.TryGetValue(address, out owner) || owner != myOwner) { continue; }

                uint typeAddress = typeOfOwner[address];
                int n;
                myTypes[typeAddress] = myTypes.TryGetValue(typeAddress, out n) ? n + 1 : 1;
            }

            if (myTypes.Count == 0)
            {
                result.Error = "名单里没有认到属于你的建筑 —— 已拒绝执行";
                return result;
            }

            // 同型号"别人也有"的检测：把每个型号的拥有者都数一遍
            Dictionary<uint, HashSet<uint>> typeOwners = new Dictionary<uint, HashSet<uint>>();
            foreach (KeyValuePair<uint, uint> pair in typeOfOwner)
            {
                uint owner = ownerOfObject[pair.Key];
                HashSet<uint> set;
                if (!typeOwners.TryGetValue(pair.Value, out set))
                {
                    set = new HashSet<uint>();
                    typeOwners[pair.Value] = set;
                }
                set.Add(owner);
            }

            // ---- 4. 只提升"发电型号"，逐个写入并回读校验 ----
            StringBuilder who = new StringBuilder();
            foreach (KeyValuePair<uint, int> pair in myTypes)
            {
                uint typeAddress = pair.Key;
                int count = pair.Value;

                uint current;
                if (!memory.ReadUInt32(typeAddress + TypePowerOffset, out current)) { continue; }
                if (current == 0) { continue; }          // 不发电的型号，不碰

                // 原值**只记一次**，之后一直用它还原。
                //
                // 这里曾经多写了一句 `if (original != current) { original = current; ... }`，
                // 本意是"值被外部改动过就重新同步"——**方向恰好写反了**：
                // 我们自己写进去目标值之后，下一拍 current 就等于目标值、必然与记录的原值不同，
                // 于是**每跑一拍就把原值覆盖成目标值**，还原功能在第二拍就废了。
                // 真机日志抓到了它：第一拍 `60→30000`（原值对），第二拍变成 `30000→30000`，
                // 关功能时 `还原 0 个`。**这就是"看起来做了、其实没做"的典型形态。**
                // 教训：记账这种东西，**写下之后就不该再被任何一拍修改**。
                uint original;
                if (!_originals.TryGetValue(typeAddress, out original))
                {
                    original = current;
                    _originals[typeAddress] = original;   // 只记一次
                }

                result.Types++;
                result.Buildings += count;

                HashSet<uint> owners;
                if (typeOwners.TryGetValue(typeAddress, out owners) && owners.Count > 1) { result.SharedTypes++; }

                if (who.Length > 0) { who.Append("、"); }
                who.Append(string.Format(CultureInfo.InvariantCulture,
                    "{0}×{1}（{2}→{3}）", ReadId(memory, typeAddress), count, original, target));

                if (current == target) { continue; }      // 已经是目标值，不必再写

                if (!IsWritable(memory, typeAddress + TypePowerOffset))
                {
                    result.Note = "型号 " + ReadId(memory, typeAddress) + " 的发电量落在不可写页上，已跳过";
                    continue;
                }
                if (!memory.WriteUInt32(typeAddress + TypePowerOffset, target)) { continue; }

                uint back;
                if (!memory.ReadUInt32(typeAddress + TypePowerOffset, out back) || back != target)
                {
                    // 回读不一致 = 没写进去（或又被改回去了）→ 立刻还原，放弃这一拍
                    memory.WriteUInt32(typeAddress + TypePowerOffset, original);
                    continue;
                }
                result.Writes++;
            }

            if (result.Types == 0)
            {
                result.Error = "你的建筑里没有「发电」型号（发电量都读成 0）—— 先造一座电厂再开";
                _originals.Clear();      // 没动过任何东西，记账也清掉
                return result;
            }

            result.Note = "已提升：" + who.ToString();
            return result;
        }

        /// <summary>
        /// 还原所有改动过的型号。**关闭功能、退出程序时都要调**；
        /// 换局（游戏重连）时相反 —— 那时该调 <see cref="ForgetWithoutRestoring"/>。
        /// </summary>
        public int Restore(MemoryIO memory)
        {
            if (memory == null || _originals.Count == 0) { return 0; }

            int restored = 0;
            foreach (KeyValuePair<uint, uint> pair in _originals)
            {
                uint current;
                if (!memory.ReadUInt32(pair.Key + TypePowerOffset, out current)) { continue; }
                if (current == pair.Value) { continue; }        // 已经是原值
                if (!IsWritable(memory, pair.Key + TypePowerOffset)) { continue; }
                if (memory.WriteUInt32(pair.Key + TypePowerOffset, pair.Value)) { restored++; }
            }
            _originals.Clear();
            return restored;
        }

        /// <summary>
        /// 丢弃记账、**不做任何写回**。换局时必须走这条：新一局的内存是重建的，
        /// 旧地址早就不是那份说明书了，拿它回写等于往随机位置写值。
        /// </summary>
        public void ForgetWithoutRestoring()
        {
            _originals.Clear();
        }

        // ============================================================ 辅助

        /// <summary>
        /// 读一个对象的"型号说明书"指针，并确认它**真的是说明书**：
        /// 指针在堆上（≥64KB、不在游戏模块内），且说明书头部 4 字节是模块区间内的类型表。
        /// 名单里混着大量非建筑对象，没有这道确认就会拿垃圾地址当说明书去写。
        /// </summary>
        public static bool TryReadTypeObject(
            MemoryIO memory, uint moduleBase, uint moduleSize, uint objectAddress, out uint typeAddress)
        {
            typeAddress = 0;
            if (objectAddress < 0x10000) { return false; }

            uint pointer;
            if (!memory.ReadUInt32(objectAddress + TypePointerOffset, out pointer)) { return false; }
            if (pointer < 0x10000) { return false; }
            // 上界同样要判：名单里存在整串 F 的"空槽位标记"，只判下界就会拿它去读头部
            // （`TechnoWalker.AddCandidate` 里那条注释记了同一个坑）
            if (pointer > 0x7FFFFFFF) { return false; }
            // 说明书在堆上；落在模块里的"指针"多半是误读（例如把类型表本身当成了指针）
            if (pointer >= moduleBase && pointer < moduleBase + moduleSize) { return false; }

            uint vtable;
            if (!memory.ReadUInt32(pointer, out vtable)) { return false; }
            if (vtable < moduleBase || vtable >= moduleBase + moduleSize) { return false; }

            typeAddress = pointer;
            return true;
        }

        private static bool IsWritable(MemoryIO memory, uint address)
        {
            MemoryRegion region = memory.Describe(address);
            return region != null && region.Writable;
        }

        /// <summary>读说明书里的型号 ID（`NAPOWR` 这类），读不到就返回"?"。</summary>
        private static string ReadId(MemoryIO memory, uint typeAddress)
        {
            byte[] raw = memory.ReadBytesQuiet(typeAddress + IdStringOffset, 16);
            if (raw == null) { return "?"; }

            int length = 0;
            while (length < raw.Length)
            {
                byte b = raw[length];
                bool ok = (b >= (byte)'0' && b <= (byte)'9')
                    || (b >= (byte)'A' && b <= (byte)'Z')
                    || (b >= (byte)'a' && b <= (byte)'z');
                if (!ok) { break; }
                length++;
            }
            if (length < 3) { return "?"; }
            return Encoding.ASCII.GetString(raw, 0, length);
        }
    }
}
