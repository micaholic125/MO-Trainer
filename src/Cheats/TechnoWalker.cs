using System;
using System.Collections.Generic;
using MoTrainer.Core;

namespace MoTrainer.Cheats
{
    /// <summary>遍历到的一个 TechnoClass 对象（单位 / 建筑）。</summary>
    public sealed class TechnoObject
    {
        /// <summary>对象基址。</summary>
        public uint Address;

        /// <summary>对象头 4 字节，即虚表指针。它落在游戏模块区间内，是"这确实是个游戏对象"最硬的证据。</summary>
        public uint VTable;
    }

    /// <summary>一次遍历的结果与统计。统计项是给界面和日志用的 —— 失败时要能说清"为什么一个都没找到"。</summary>
    public sealed class TechnoWalkResult
    {
        public readonly List<TechnoObject> Objects = new List<TechnoObject>();

        /// <summary>读到的候选指针总数（含被跳过的）。</summary>
        public int Scanned;

        /// <summary>虚表不在模块区间内而被跳过的数量。**这个数字大就说明数组地址找错了。**</summary>
        public int Skipped;

        /// <summary>重复出现的对象（同一单位在数组里被列了多次）。</summary>
        public int Duplicates;

        /// <summary>因为"类型标记只被极少数对象引用"而被剔除的数量（这些多为假对象）。</summary>
        public int RareTypeRemoved;

        public string Error = "";

        public bool Ok { get { return Error.Length == 0; } }

        public int Count { get { return Objects.Count; } }
    }

    /// <summary>
    /// 全局对象遍历内核：**不依赖"选中列表"**，直接枚举游戏里的所有 TechnoClass 对象。
    ///
    /// 为什么必须有这个东西：
    /// 「选中单位无敌」靠"选中列表"就够了，但**秒杀敌人**天生做不到 ——
    /// 敌方单位不可能出现在你的选中列表里。前期在选中列表上连续几轮失败（见开发日志 S5h~S5l），
    /// 真正的原因是把"玩家选中的东西"当成了"游戏里的对象集合"，这两件事根本不是一回事。
    ///
    /// 正路是走引擎自己的对象登记表：游戏为了逐帧驱动所有单位/建筑，
    /// 内部必然维护着一个"全部 TechnoClass 指针"的数组 —— 校准配置里的
    /// `TechnoArray` / `TechnoArrayCount` 就是为它预留的。
    /// 拿到这个数组后，"我的单位"和"敌方单位"只是同一个集合里按阵营字段筛出来的两类，
    /// 秒杀、无敌、瞬间建造全都能建立在同一个遍历结果上。
    ///
    /// 判定"是不是真对象"只用一条硬标准：**对象头 4 字节必须是落在游戏模块区间内的虚表指针**。
    /// 这条比"能读出来"强得多 —— 数组地址万一找错，读出来的只是一堆恰好可读的数值，
    /// 靠这一条能当场识破（`Skipped` 计数会立刻爆掉）。
    /// </summary>
    public static class TechnoWalker
    {
        /// <summary>单次遍历的对象上限，防止数组长度字段异常时读出天文数字。</summary>
        public const int MaxObjects = 8192;

        /// <summary>
        /// 从已知的 TechnoClass 指针数组遍历。**这是主路径**：
        /// 一次批量读 + 逐项校验，开销与对象数成正比，可以放进锁定线程里周期性执行。
        /// </summary>
        public static TechnoWalkResult WalkArray(
            MemoryIO memory, uint arrayAddress, uint count, uint moduleBase, uint moduleSize)
        {
            TechnoWalkResult result = new TechnoWalkResult();

            if (memory == null) { result.Error = "内存接口不可用"; return result; }
            if (arrayAddress < 0x10000) { result.Error = "对象数组地址无效（未校准？）"; return result; }
            if (moduleSize == 0) { result.Error = "模块区间未知，无法校验对象"; return result; }
            if (count == 0) { result.Error = "对象数量为 0（游戏未开局？）"; return result; }
            if (count > MaxObjects) { count = MaxObjects; }

            // 一次批量读整个指针数组：逐项 ReadUInt32 会产生几千次系统调用，
            // 放进 33ms 的锁定循环里会把帧率拖垮。读失败也要**明确报错**而不是返回空结果，
            // 否则界面上看起来就像"游戏里没有单位"。
            int bytes = (int)count * 4;
            byte[] block = memory.ReadBytesQuiet(arrayAddress, bytes);
            if (block == null || block.Length < bytes)
            {
                result.Error = string.Format("读取对象数组失败（{0} 项 / {1} 字节）", count, bytes);
                return result;
            }

            HashSet<uint> seen = new HashSet<uint>();
            for (int i = 0; i < (int)count; i++)
            {
                uint address = ReadUInt32(block, i * 4);
                result.Scanned++;
                AddCandidate(memory, address, moduleBase, moduleSize, seen, result);
            }

            // **按"类型标记的引用频次"剔除假对象。**
            //
            // 真对象的类型标记会被**大量同类实例共享**（8 个大兵必然是同一个值、4 辆防空车是另一个值），
            // 而假对象的值往往是**孤例**（只出现一次）。
            //
            // 真机实测：不筛的时候遍历出 1790 个"对象"，而用户目测全图只有约 189 个真实单位建筑 ——
            // 近九成是假的。阵营统计、数量对比全都建立在这份错数据上，
            // 于是"我方 309 个""将击杀 1039 个"这类数字全都离谱。
            Dictionary<uint, int> typeCount = new Dictionary<uint, int>();
            for (int i = 0; i < result.Objects.Count; i++)
            {
                int existing;
                typeCount[result.Objects[i].VTable] =
                    typeCount.TryGetValue(result.Objects[i].VTable, out existing) ? existing + 1 : 1;
            }

            List<TechnoObject> kept = new List<TechnoObject>();
            for (int i = 0; i < result.Objects.Count; i++)
            {
                int shared;
                typeCount.TryGetValue(result.Objects[i].VTable, out shared);
                if (shared >= 2) { kept.Add(result.Objects[i]); }
                else { result.RareTypeRemoved++; }
            }

            result.Objects.Clear();
            result.Objects.AddRange(kept);

            return result;
        }

        /// <summary>
        /// 校验并收录一个候选对象。
        /// 判定顺序刻意从"最便宜、最强"的判据开始：地址合理性 → 虚表是否在模块内 → 去重。
        /// </summary>
        private static void AddCandidate(
            MemoryIO memory, uint address, uint moduleBase, uint moduleSize,
            HashSet<uint> seen, TechnoWalkResult result)
        {
            if (address < 0x10000) { result.Skipped++; return; }

            uint vtable;
            if (!memory.ReadUInt32(address, out vtable) || vtable == 0) { result.Skipped++; return; }
            if (vtable < moduleBase || vtable >= moduleBase + moduleSize) { result.Skipped++; return; }

            // **收紧"真对象"的判据。**
            //
            // 只要求"落在模块区间内"太松了：模块区间有 7.8MB，
            // 大量普通数值 —— 甚至 ASCII 文本（如 0x00747261 就是 "art"）—— 都恰好落在里面。
            // 结果是名单里混进大量假对象，把后面所有统计都带偏：
            // 真机实测"同类对象 1348 个"里大部分是假的，阵营字段怎么找都对不上，
            // 还一度得出"我方 309 个"（实际只有 60~90 个）。
            if ((vtable & 3) != 0) { result.Skipped++; return; }              // 类型表必然 4 字节对齐
            if (vtable < moduleBase + 0x1000) { result.Skipped++; return; }   // 跳过 DOS 头之类的非类型表区域
            if (IsAsciiText(vtable)) { result.Skipped++; return; }            // 4 字节全是可打印字符 → 是文本

            if (!seen.Add(address)) { result.Duplicates++; return; }

            TechnoObject item = new TechnoObject();
            item.Address = address;
            item.VTable = vtable;
            result.Objects.Add(item);
        }

        /// <summary>
        /// 读对象的阵营（HouseClass 指针）。未校准该偏移时返回 0，由调用方决定怎么办。
        ///
        /// 用"指向哪个阵营"来区分敌我，比按对象地址范围猜测可靠得多 ——
        /// 阵营对象在游戏里是全局唯一的，同一个阵营的所有单位都指向同一个地址。
        /// </summary>
        public static uint ReadOwner(MemoryIO memory, uint address, uint ownerOffset)
        {
            if (memory == null || address < 0x10000 || ownerOffset == 0) { return 0; }

            uint owner;
            if (!memory.ReadUInt32(address + ownerOffset, out owner)) { return 0; }
            if (owner < 0x10000) { return 0; }
            return owner;
        }

        /// <summary>读对象血量。未校准该偏移时返回 0（调用方需自己判断"是否可信"）。</summary>
        public static uint ReadHealth(MemoryIO memory, uint address, uint healthOffset)
        {
            if (memory == null || address < 0x10000) { return 0; }

            uint health;
            if (!memory.ReadUInt32(address + healthOffset, out health)) { return 0; }
            return health;
        }

        /// <summary>
        /// 按阵营分组统计：返回"每个阵营有多少个对象"。
        ///
        /// 这是给校准用的探针 —— **真机上只要一开局就能验证 `OffsetTechnoOwner` 对不对**：
        /// 正确的话会看到"少数几个阵营、各自聚集着大量单位"（典型是 2~8 个阵营）；
        /// 如果偏移错了，读出来的是随机数，每个"阵营"只会出现一两次，
        /// 分布是散的。这条判据不需要知道谁是敌谁是友。
        /// </summary>
        public static List<KeyValuePair<uint, int>> GroupByOwner(
            MemoryIO memory, IList<TechnoObject> objects, uint ownerOffset)
        {
            Dictionary<uint, int> counts = new Dictionary<uint, int>();
            List<KeyValuePair<uint, int>> ordered = new List<KeyValuePair<uint, int>>();
            if (objects == null || ownerOffset == 0) { return ordered; }

            for (int i = 0; i < objects.Count; i++)
            {
                uint owner = ReadOwner(memory, objects[i].Address, ownerOffset);
                if (owner == 0) { continue; }

                int existing;
                if (counts.TryGetValue(owner, out existing)) { counts[owner] = existing + 1; }
                else { counts[owner] = 1; }
            }

            foreach (KeyValuePair<uint, int> pair in counts) { ordered.Add(pair); }
            ordered.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });
            return ordered;
        }

        /// <summary>
        /// 4 个字节是否都落在可打印 ASCII 范围内。
        /// 是的话说明这个"类型标记"其实是文本（真机实测抓到了 "art" 这种），不是类型表地址。
        /// </summary>
        private static bool IsAsciiText(uint value)
        {
            for (int i = 0; i < 4; i++)
            {
                uint b = (value >> (i * 8)) & 0xFF;
                if (b < 0x20 || b > 0x7E) { return false; }
            }
            return true;
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
