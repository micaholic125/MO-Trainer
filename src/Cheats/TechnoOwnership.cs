using System;
using System.Collections.Generic;
using System.Globalization;
using MoTrainer.Core;

namespace MoTrainer.Cheats
{
    /// <summary>
    /// 「我方是谁」的**金钱锚定**判定（2026-10-07 做 [S6-1] 秒杀接线时新增）。
    ///
    /// 背景：秒杀敌人必须分清敌我。此前唯一的判据是配置里那个**人工试出**的阵营偏移
    /// （+0xF0），既没有自动校验，真机数据里读出来的值也很可疑
    /// （0x00010000 / 0x00010001 这种既不像指针、又和用户实际单位数对不上）。
    /// 拿一个"不知道是什么"的字段去写血量 = 可能把自己人一起清空，绝不能接受。
    ///
    /// 本类的办法：**用金钱当锚点，反查出真正的阵营字段**。
    /// 单位对象里必然有一个指针指向自己的 HouseClass；而"属于我的那个 HouseClass 里
    /// 装着我的金钱"—— 这条判据正是无敌功能的归属校验在用的
    /// （`CheatEngine.ObjectReferencesMoney`，2026-09-22 起真机验证过）。
    /// 于是：在一个**确认属于我方**的对象里逐 4 字节找指针 P，只要 `*(P + 0x30C)` 正好是
    /// 当前金钱值，这个 P 就指向我方 HouseClass，它所在的偏移就是**货真价实的阵营字段**。
    ///
    /// 两个关键设计（自检里都有对应断言）：
    ///   · 分类判据是"字段值 == 我方 House 地址"—— 同一阵营的所有对象指向同一个
    ///     HouseClass 地址，所以不需要猜"这个值是什么意思"；
    ///   · 偏移必须对**所有**确认属于我方的样本都成立（取**交集**）——
    ///     只在一个对象上成立的偏移可能只是"扫到了邻居对象里的字段"这类巧合。
    ///     （真机内存里同类对象挨着摆，一个对象的 0x1000 扫描窗口里往往会扫到隔壁对象的
    ///      同名字段——交集正是用来滤掉这种"看起来也成立"的偏移的。）
    /// </summary>
    public static class TechnoOwnership
    {
        /// <summary>HouseClass 里金钱字段的偏移（真机三次会话一致，与无敌的归属校验同源）。</summary>
        public const uint HouseMoneyOffset = 0x30C;

        /// <summary>在对象开头多少字节内找 HouseClass 指针（与无敌的归属校验一致）。</summary>
        public const int ScanBytes = 0x1000;

        /// <summary>单个对象最多试多少个"像指针"的值（防止在垃圾对象上无限试探）。</summary>
        private const int MaxProbesPerObject = 512;

        /// <summary>建立锚点时最多用几个样本对象。</summary>
        public const int MaxAnchorSamples = 8;

        /// <summary>抽检默认复核多少个"判为敌方"的对象。</summary>
        public const int DefaultVerifySamples = 128;

        /// <summary>金钱锚定出来的"我方锚点"。</summary>
        public sealed class OwnerAnchor
        {
            /// <summary>阵营字段在对象内的偏移（金钱锚定出来的，不是人工试出来的）。</summary>
            public uint OwnerOffset;

            /// <summary>我方 HouseClass 的地址（阵营字段里装着的那个值）。</summary>
            public uint MyHouse;

            /// <summary>支持这个偏移的样本数（都是金钱锚定确认属于我方的对象）。</summary>
            public int Samples;

            /// <summary>同样在各样本上命中、但未被采用的其它偏移（供日志留证）。</summary>
            public List<uint> OtherOffsets = new List<uint>();
        }

        /// <summary>
        /// 判断一个"归属值"是不是**真实的阵营对象**（HouseClass 在堆上），并顺带读出它的金钱。
        ///
        /// 为什么要这道判据（2026-10-08 真机数据）：名单里混着大量非单位对象，
        /// 它们 +0x14C 位置上的值五花八门 —— 实测抓到过**指向代码区**的值（0x0087E294）、
        /// 小整数（0x000000FF）等等。这些"归属"不可能是阵营，对应的对象也**绝不能杀**。
        ///
        /// 判据（全部满足才算真阵营）：地址 ≥ 0x10000、4 字节对齐、**不在游戏模块区间内**
        ///（阵营对象在堆上，代码区里的值一定是误读）、且 `+0x30C` 的金钱读得出来。
        ///
        /// 金钱读数还有第二个用途：**中立 / 民用房子的金钱通常恒为 0**，
        /// 而真人 / AI 玩家的金钱会随建造消费变化 —— 秒杀用它来"保护疑似中立的阵营"
        ///（`money == 0` ⇒ 视作中立，跳过不杀；宁可少杀、绝不误杀）。
        /// </summary>
        public static bool TryReadHouseMoney(
            MemoryIO memory, uint ownerValue, uint moduleBase, uint moduleSize, out uint money)
        {
            money = 0;
            if (memory == null || ownerValue < 0x10000) { return false; }
            if ((ownerValue & 3) != 0) { return false; }
            if (ownerValue >= moduleBase && ownerValue < moduleBase + moduleSize) { return false; }
            return memory.ReadUInt32(ownerValue + HouseMoneyOffset, out money);
        }

        /// <summary>一次"按归属分类"的结果。</summary>
        public sealed class OwnerClassification
        {
            /// <summary>我方对象数。</summary>
            public int Mine;

            /// <summary>归属读不出来 / 不像真阵营的对象数（**跳过，绝不写**）。</summary>
            public int Unknown;

            /// <summary>可击杀的对象（归属是真实的、非我方的、且金钱非 0 的阵营）。</summary>
            public readonly List<TechnoObject> Killable = new List<TechnoObject>();

            /// <summary>可击杀部分按阵营分组：归属 → 对象列表。</summary>
            public readonly Dictionary<uint, List<TechnoObject>> KillableByGroup =
                new Dictionary<uint, List<TechnoObject>>();

            /// <summary>疑似中立 / 民用的阵营（金钱读数为 0）：归属 → 对象列表。**这些不杀。**</summary>
            public readonly Dictionary<uint, List<TechnoObject>> NeutralByGroup =
                new Dictionary<uint, List<TechnoObject>>();

            public int KillableCount { get { return Killable.Count; } }

            public int KillableGroupCount { get { return KillableByGroup.Count; } }

            public int NeutralCount
            {
                get
                {
                    int total = 0;
                    foreach (KeyValuePair<uint, List<TechnoObject>> pair in NeutralByGroup)
                    {
                        total += pair.Value.Count;
                    }
                    return total;
                }
            }
        }

        /// <summary>
        /// 把一批对象按归属分成四类：**我方 / 可击杀 / 疑似中立（金钱 0，不杀）/ 归属存疑（不杀）**。
        ///
        /// 引擎与「预演」共用这一份实现 —— 预演上看到的数字与真正开杀时用的名单**完全一致**，
        /// 不允许出现"预演说杀 30 个、实际杀 178 个"这种两套判据的情况。
        /// </summary>
        public static OwnerClassification ClassifyOwners(
            MemoryIO memory, IList<TechnoObject> objects, uint ownerOffset, uint myHouse,
            uint moduleBase, uint moduleSize)
        {
            OwnerClassification result = new OwnerClassification();
            if (memory == null || objects == null || ownerOffset == 0) { return result; }

            for (int i = 0; i < objects.Count; i++)
            {
                uint address = objects[i].Address;
                uint owner;
                if (!memory.ReadUInt32(address + ownerOffset, out owner)) { result.Unknown++; continue; }
                if (owner == 0) { result.Unknown++; continue; }
                if (owner == myHouse) { result.Mine++; continue; }

                uint money;
                if (!TryReadHouseMoney(memory, owner, moduleBase, moduleSize, out money))
                {
                    // 归属值不像真阵营（代码区 / 小整数 / 读不到）→ 存疑，绝不杀
                    result.Unknown++;
                    continue;
                }

                Dictionary<uint, List<TechnoObject>> groups = money == 0
                    ? result.NeutralByGroup      // 疑似中立 / 民用（金钱恒为 0）→ 保护，不杀
                    : result.KillableByGroup;

                List<TechnoObject> bucket;
                if (!groups.TryGetValue(owner, out bucket))
                {
                    bucket = new List<TechnoObject>();
                    groups[owner] = bucket;
                }
                bucket.Add(objects[i]);
                if (money != 0) { result.Killable.Add(objects[i]); }
            }
            return result;
        }

        /// <summary>把一批对象按"类型表 → 个数"统计出来（显示用；数量多的在前）。</summary>
        public static List<KeyValuePair<uint, int>> CountTypes(IList<TechnoObject> objects)
        {
            Dictionary<uint, int> counts = new Dictionary<uint, int>();
            if (objects != null)
            {
                for (int i = 0; i < objects.Count; i++)
                {
                    uint type = objects[i].VTable;
                    int existing;
                    counts[type] = counts.TryGetValue(type, out existing) ? existing + 1 : 1;
                }
            }

            List<KeyValuePair<uint, int>> ordered = new List<KeyValuePair<uint, int>>();
            foreach (KeyValuePair<uint, int> pair in counts) { ordered.Add(pair); }
            ordered.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });
            return ordered;
        }

        /// <summary>
        /// 在**一个对象**里找出所有"指向 HouseClass 且该 House 装着给定金钱"的字段偏移。
        ///
        /// `maxMatches` 用来控制代价：无敌功能的归属校验只关心"有没有"（传 1，
        /// 命中第一个立刻收工）；建立锚点时要看"有哪些"（传多些，做交集用）。
        /// 命中条件与 `CheatEngine.ObjectReferencesMoney` 完全相同——那一条是真机验证过的。
        /// </summary>
        public static List<uint> FindHouseFields(
            MemoryIO memory, uint objectAddress, uint money, int maxMatches, out int probes)
        {
            List<uint> found = new List<uint>();
            probes = 0;
            if (memory == null || objectAddress < 0x10000 || money == 0) { return found; }
            if (maxMatches <= 0) { maxMatches = 1; }

            byte[] head = memory.ReadBytesQuiet(objectAddress, ScanBytes);
            if (head == null) { return found; }

            for (int offset = 0; offset + 4 <= head.Length; offset += 4)
            {
                uint pointer = ReadUInt32(head, offset);
                if (pointer < 0x10000 || pointer > 0x7FFFFFFF) { continue; }
                if (probes >= MaxProbesPerObject) { break; }
                probes++;

                // 只认**金钱正好出现在 House 的金钱偏移上**。
                // 早先的判据是"目标块里随便哪个位置等于金钱值"，真机翻过车：
                // 玩家把金钱锁成整数后，这个数字在内存里到处都是，敌方单位也被判成"本方"。
                byte[] hit = memory.ReadBytesQuiet(pointer + HouseMoneyOffset, 4);
                if (hit == null) { continue; }
                if (ReadUInt32(hit, 0) != money) { continue; }

                found.Add((uint)offset);
                if (found.Count >= maxMatches) { break; }
            }
            return found;
        }

        /// <summary>
        /// 从一组"选中的对象"建立我方锚点。
        ///
        /// 通过条件（缺一不可，任何一条不满足就返回 false 并说明原因，调用方必须拒绝执行）：
        ///   ① 至少有一个对象能被金钱锚定**确认属于我方**；
        ///   ② 这些对象有一个**共同**的"指向我方 House 的字段偏移"（取交集）；
        ///   ③ 它们在该偏移上的取值**完全一致**（同一个 House 地址）。
        /// </summary>
        public static bool TryBuildAnchor(
            MemoryIO memory, IList<uint> candidates, uint money,
            out OwnerAnchor anchor, out string reason)
        {
            anchor = null;
            reason = "";
            if (memory == null) { reason = "内存接口不可用"; return false; }
            if (candidates == null || candidates.Count == 0)
            {
                reason = "没有可用的样本对象（先在游戏里选中一个自己的单位）";
                return false;
            }
            if (money == 0) { reason = "读不到当前金钱值"; return false; }

            List<uint> confirmed = new List<uint>();     // 金钱锚定确认属于我方的对象
            List<List<uint>> perObject = new List<List<uint>>();
            List<uint> allSeen = new List<uint>();

            int examined = 0;
            for (int i = 0; i < candidates.Count && i < MaxAnchorSamples; i++)
            {
                uint objectAddress = candidates[i];
                if (objectAddress < 0x10000) { continue; }
                examined++;

                int probes;
                List<uint> offsets = FindHouseFields(memory, objectAddress, money, 16, out probes);
                if (offsets.Count == 0) { continue; }   // 不是"我方"（或 House 指针不在扫描窗口内）

                confirmed.Add(objectAddress);
                perObject.Add(offsets);
                for (int k = 0; k < offsets.Count; k++)
                {
                    if (!allSeen.Contains(offsets[k])) { allSeen.Add(offsets[k]); }
                }
            }

            if (confirmed.Count == 0)
            {
                reason = examined == 0
                    ? "没有可用的样本对象"
                    : "选中的对象里找不到任何「金钱锚定」特征（金钱未校准？选中的不是自己的单位？）";
                return false;
            }

            // 取交集：只保留"对每个确认样本都成立"的偏移
            List<uint> common = new List<uint>(perObject[0]);
            for (int i = 1; i < perObject.Count; i++)
            {
                for (int k = common.Count - 1; k >= 0; k--)
                {
                    if (!perObject[i].Contains(common[k])) { common.RemoveAt(k); }
                }
                if (common.Count == 0) { break; }
            }

            if (common.Count == 0)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "确认属于我方的 {0} 个样本对象没有共同的阵营字段偏移（各自命中的偏移：{1}）",
                    confirmed.Count, DescribeOffsets(allSeen));
                return false;
            }

            common.Sort();
            uint chosen = common[0];

            // 取值必须完全一致：同一阵营的所有对象都指向同一个 HouseClass。
            // 不一致说明这个偏移上的指针其实指向"两个不同的、恰好都装着同样金钱值"的结构，不可信。
            uint houseValue = 0;
            for (int i = 0; i < confirmed.Count; i++)
            {
                uint value;
                if (!memory.ReadUInt32(confirmed[i] + chosen, out value)) { continue; }
                if (value == 0) { continue; }

                if (houseValue == 0) { houseValue = value; }
                else if (value != houseValue)
                {
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "阵营字段 +0x{0:X} 在不同样本上取值不一致（0x{1:X8} / 0x{2:X8}），不可信",
                        chosen, houseValue, value);
                    return false;
                }
            }

            if (houseValue == 0) { reason = "阵营字段读不出我方标识（值为 0）"; return false; }

            anchor = new OwnerAnchor();
            anchor.OwnerOffset = chosen;
            anchor.MyHouse = houseValue;
            anchor.Samples = confirmed.Count;
            for (int i = 0; i < common.Count; i++)
            {
                if (common[i] != chosen) { anchor.OtherOffsets.Add(common[i]); }
            }
            return true;
        }

        /// <summary>
        /// 抽检：把"判为敌方"的对象复核一遍，确认其中没有**连着我方 House 的**。
        ///
        /// 这是开写之前唯一一道**用独立判据复核分类结果**的关卡。
        /// 主判据（阵营字段）一旦整体性出错（偏移找错、语义根本不是阵营），
        /// 后果是把自己人当敌人清空；抽检抓的就是这类**系统性错误**。
        ///
        /// **扫描范围必须是 0x400，不能是 0x1000**（2026-10-08 真机实证后的修正）：
        /// 真机上同类对象在内存里是**紧挨着摆**的（实测间距 0x708），
        /// 第一次实现扫了 0x1000 字节，把"后面 3.6KB 处的自己人单位"也扫了进去 ——
        /// 它 +0x14C 的归属指针正好落在窗口内，于是**把邻居当成了它自己**、误报"未通过"，
        /// 让本来正确的分类被拦下（真机原话：被判为敌方的对象 0x2856C008 其实连着你方 House，
        /// 而线索单位 0x2856CE18 就在它后面 0xE10 字节处）。
        /// 敌我判据的归属字段在 +0x14C，取 0x400 足以覆盖"对象自己的"归属字段，
        /// 而邻居对象的归属字段至少落在 objectSize + 0x14C ≈ 0x800 之后，不会被扫到。
        /// </summary>
        public static bool VerifyEnemySample(
            MemoryIO memory, IList<TechnoObject> objects,
            uint ownerOffset, uint myHouse, int sampleCount,
            out int enemySeen, out int checkedCount, out uint suspectAddress, out string reason)
        {
            enemySeen = 0;
            checkedCount = 0;
            suspectAddress = 0;
            reason = "";
            if (memory == null) { reason = "内存接口不可用"; return false; }
            if (objects == null) { reason = "遍历结果为空"; return false; }
            if (sampleCount <= 0) { sampleCount = DefaultVerifySamples; }

            for (int i = 0; i < objects.Count; i++)
            {
                uint address = objects[i].Address;
                uint owner;
                if (!memory.ReadUInt32(address + ownerOffset, out owner)) { continue; }
                if (owner == 0 || owner == myHouse) { continue; }   // 归属不明 / 我方 —— 都不在抽检范围

                enemySeen++;
                if (checkedCount >= sampleCount) { continue; }
                checkedCount++;

                byte[] head = memory.ReadBytesQuiet(address, VerifyScanBytes);
                if (head == null) { continue; }   // 读不动：按"没查出问题"处理，但不计入已复核

                bool connectedToMyHouse = false;
                for (int offset = 0; offset + 4 <= head.Length; offset += 4)
                {
                    if (ReadUInt32(head, offset) == myHouse) { connectedToMyHouse = true; break; }
                }
                if (connectedToMyHouse)
                {
                    // 这个对象被判为敌方，却连着我方 House —— 分类出错，决不能开写
                    suspectAddress = address;
                    reason = string.Format(CultureInfo.InvariantCulture,
                        "被判为敌方的对象 0x{0:X8} 其实连着你方 House（第 {1} 个抽检样本）",
                        address, checkedCount);
                    return false;
                }
            }

            if (enemySeen > 0 && checkedCount == 0)
            {
                reason = string.Format(CultureInfo.InvariantCulture,
                    "名单里有 {0} 个判为敌方的对象，但一个都读不动，无法复核", enemySeen);
                return false;
            }
            return true;
        }

        /// <summary>
        /// 抽检扫描窗口。**0x400 是上限，不要调大**（缘由见 VerifyEnemySample 的注释：
        /// 调大就会扫到邻居对象的归属字段，把"邻居是我方"误报成"它是我方"）。
        /// </summary>
        public const int VerifyScanBytes = 0x400;

        private static string DescribeOffsets(List<uint> offsets)
        {
            if (offsets == null || offsets.Count == 0) { return "无"; }
            System.Text.StringBuilder text = new System.Text.StringBuilder();
            for (int i = 0; i < offsets.Count && i < 8; i++)
            {
                if (i > 0) { text.Append("、"); }
                text.Append(string.Format(CultureInfo.InvariantCulture, "+0x{0:X}", offsets[i]));
            }
            return text.ToString();
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }
    }
}
