using System;
using System.Collections.Generic;
using MoTrainer.Core;

namespace MoTrainer.Cheats
{
    /// <summary>一个"疑似全对象名单"的候选。</summary>
    public sealed class TechnoArrayCandidate
    {
        /// <summary>名单在内存里的位置（存放"指针"的那个格子，不是数组本身）。</summary>
        public uint SlotAddress;

        /// <summary>数组首地址。</summary>
        public uint ArrayAddress;

        /// <summary>数量字段的位置（0 表示没找到合适的）。</summary>
        public uint CountAddress;

        /// <summary>数量字段读出来的值。</summary>
        public uint Count;

        /// <summary>数组开头连续多少个元素通过了"是真实单位"的校验。</summary>
        public int ValidObjects;

        /// <summary>你选中的单位在这个名单里出现了几次。**大于 0 才说明找对了。**</summary>
        public int HintHits;

        public string Describe()
        {
            return string.Format(
                "名单指针格 0x{0:X8}｜数组 0x{1:X8}｜数量 {2}（字段 0x{3:X8}）｜有效对象 {4}｜命中选中单位 {5}",
                SlotAddress, ArrayAddress, Count, CountAddress, ValidObjects, HintHits);
        }
    }

    /// <summary>
    /// 自动查找「所有单位和建筑的总名单」。
    ///
    /// 思路（不需要任何外部工具，也不需要猜）：
    /// 游戏把"当前选中的单位"也存在一份名单里，而**你选中的单位必然也在总名单里** ——
    /// 所以拿它的地址当线索，去内存里反查"哪个名单里含有它"。
    ///
    /// 搜索范围刻意收窄：只在**已知的选中名单附近**找。
    /// 依据是这类容器在引擎里是同一批全局变量，通常挨着放在同一个数据块里 ——
    /// 既省掉了全内存扫描（几秒的卡顿），也大幅降低了误报概率。
    /// 而且每个候选都要过两道硬校验：数组里存的是不是真实对象、以及**含不含你选中的那个单位**。
    /// </summary>
    public static class TechnoArrayFinder
    {
        /// <summary>
        /// 在已知容器附近多大范围内找（左右各 0x20000 = 128KB）。
        ///
        /// 起初只搜 ±8KB —— 真机实测：把"选中名单自己"排除之后，小范围内就没有别的候选了，
        /// 说明引擎那张总名单**不在选中名单旁边**。这类全局容器会散落在整个数据段里，
        /// 所以范围要放到能覆盖数据段才有意义。
        /// 每个候选都要过"元素是不是真对象"的硬校验，范围放大只是多筛一些，不会降低准确性。
        /// </summary>
        public const int SearchRadius = 0x400000;

        /// <summary>
        /// 候选数组最多检查多少个元素。
        /// 取值要**明显大于一份真实名单的长度**，否则多个候选都会撞到上限、彼此无法比较 ——
        /// 真机实测：上限 512 时有好几个候选都报"有效对象 512"，排序失去意义。
        /// </summary>
        public const int MaxEntries = 4096;

        /// <summary>返回的候选上限。</summary>
        public const int MaxCandidates = 8;

        /// <summary>
        /// 查找总名单。
        /// `nearAddress` 是已知的选中名单容器地址（那个"指针格"）；
        /// `hints` 是当前选中单位的地址（可以为空，但为空时无法确认找对了）。
        /// </summary>
        public static List<TechnoArrayCandidate> Find(
            MemoryIO memory, uint moduleBase, uint moduleSize, uint nearAddress, IList<uint> hints)
        {
            List<TechnoArrayCandidate> found = new List<TechnoArrayCandidate>();
            if (memory == null || moduleSize == 0 || nearAddress < 0x10000) { return found; }

            uint start = nearAddress > SearchRadius ? nearAddress - SearchRadius : 0;
            uint end = nearAddress + SearchRadius;

            uint[] slots = ReadWindow(memory, start, end);
            if (slots == null) { return found; }

            HashSet<uint> seenArrays = new HashSet<uint>();
            for (int i = 0; i < slots.Length; i++)
            {
                uint slotAddress = start + (uint)(i * 4);
                uint arrayAddress = slots[i];

                // 候选必须是"指向堆里的数组"
                if (arrayAddress < 0x10000 || arrayAddress > 0x7FFFFFFF) { continue; }
                if (arrayAddress >= moduleBase && arrayAddress < moduleBase + moduleSize) { continue; }

                // **必须排除"选中名单自己"。**
                // 查找的线索就是"你选中的单位"，而选中名单**必然**包含它 ——
                // 不排除的话这个候选永远排第一，于是每次"找到的总名单"其实是选中名单本身。
                // 真机实测踩到过：保存下来的"总名单"和选中名单是同一个地址。
                if (slotAddress == nearAddress) { continue; }
                if (!seenArrays.Add(arrayAddress)) { continue; }

                TechnoArrayCandidate candidate = Inspect(memory, slotAddress, arrayAddress, moduleBase, moduleSize, hints);
                if (candidate != null) { found.Add(candidate); }
            }

            // 排序：先看"含不含你选中的单位"，再看有效对象数量。
            // 总名单在这两项上都应该是全场最大的。
            found.Sort(delegate(TechnoArrayCandidate a, TechnoArrayCandidate b)
            {
                if (a.HintHits != b.HintHits) { return b.HintHits.CompareTo(a.HintHits); }
                return b.ValidObjects.CompareTo(a.ValidObjects);
            });

            if (found.Count > MaxCandidates) { found.RemoveRange(MaxCandidates, found.Count - MaxCandidates); }
            return found;
        }

        /// <summary>检查一个候选数组：元素是不是真实对象、数量字段在哪、含不含选中的单位。</summary>
        private static TechnoArrayCandidate Inspect(
            MemoryIO memory, uint slotAddress, uint arrayAddress,
            uint moduleBase, uint moduleSize, IList<uint> hints)
        {
            byte[] block = memory.ReadBytesQuiet(arrayAddress, MaxEntries * 4);
            if (block == null || block.Length < 8) { return null; }

            int valid = 0;
            bool sawZero = false;
            for (int k = 0; k + 4 <= block.Length; k += 4)
            {
                uint value = ReadUInt32(block, k);
                if (value == 0) { sawZero = true; break; }

                uint vtable;
                if (!memory.ReadUInt32(value, out vtable) || vtable == 0) { break; }
                if (vtable < moduleBase || vtable >= moduleBase + moduleSize) { break; }
                valid++;
            }

            // 至少要有几个像样的对象，才能算候选
            if (valid < 3) { return null; }

            int hits = 0;
            if (hints != null && hints.Count > 0)
            {
                HashSet<uint> wanted = new HashSet<uint>();
                for (int h = 0; h < hints.Count; h++) { wanted.Add(hints[h]); }

                // **必须是"名单里直接装着它"，不能是"某个对象引用了它"。**
                //
                // 真机踩坑：早先为了兜住"包一层指针"的情况，额外做了"再跳一层比对" ——
                // 结果凡是"内部字段指向该单位"的对象都被算成命中（单位彼此引用很常见），
                // 于是选出了一张**根本不含该单位的名单**，后面交叉验证全对不上。
                // 现在只认直接成员关系；"包一层"由调用方同时提供两种口径的线索来兜。
                for (int k = 0; k + 4 <= block.Length; k += 4)
                {
                    if (wanted.Contains(ReadUInt32(block, k))) { hits++; }
                }

                if (hits > hints.Count) { hits = hints.Count; }
            }

            TechnoArrayCandidate candidate = new TechnoArrayCandidate();
            candidate.SlotAddress = slotAddress;
            candidate.ArrayAddress = arrayAddress;
            candidate.ValidObjects = valid;
            candidate.HintHits = hits;
            candidate.Count = (uint)valid;

            // 数量字段：这类容器是 {指针, 容量, 数量}，所以数量通常是指针格 +8；
            // 不同引擎版本偶尔是 -4，两个都试，取合理的那个。
            FindCount(memory, slotAddress, valid, sawZero, candidate);
            return candidate;
        }

        private static void FindCount(
            MemoryIO memory, uint slotAddress, int valid, bool sawZero, TechnoArrayCandidate candidate)
        {
            uint[] probes = new uint[] { slotAddress + 8, slotAddress - 4, slotAddress + 4, slotAddress - 8 };
            for (int i = 0; i < probes.Length; i++)
            {
                if (probes[i] < 0x10000) { continue; }

                uint value;
                if (!memory.ReadUInt32(probes[i], out value)) { continue; }
                if (value == 0 || value > TechnoWalker.MaxObjects) { continue; }

                // 数量必须与"实际数到的有效对象数"对得上。
                //
                // 真机实测：这类容器里那个 +8 的字段是**容量**（能装多少），不是实际数量 ——
                // 实测值 257，而数组里真正只有几个有效对象。存错了会让后面的扫描按 257 去读，
                // 读出一堆垃圾、通过率只有 3%（真机日志原样如此）。
                // 所以只接受"和数到的个数接近"的值，否则宁可不记这个字段。
                if (value < (uint)valid || value > (uint)valid + 8) { continue; }

                candidate.CountAddress = probes[i];
                candidate.Count = value;
                return;
            }
        }

        /// <summary>
        /// 把一段地址范围读成 uint 数组。
        ///
        /// **必须分块读。** 一整段 256KB 里只要有一处不可读，整块读取就失败 ——
        /// 真机实测正是这样导致"一个候选都没找到"：范围放大后必然跨越不可读的空洞。
        /// 分块之后读不动的块跳过即可，不会牵连其它块。
        /// </summary>
        private static uint[] ReadWindow(MemoryIO memory, uint start, uint end)
        {
            int bytes = (int)(end - start);
            if (bytes <= 0 || bytes > 0x1000000) { return null; }
            if ((bytes & 3) != 0) { bytes -= bytes % 4; }

            uint[] values = new uint[bytes / 4];
            bool any = false;

            const int chunk = 0x1000;
            for (int offset = 0; offset < bytes; offset += chunk)
            {
                int len = bytes - offset < chunk ? bytes - offset : chunk;

                byte[] block = memory.ReadBytesQuiet(start + (uint)offset, len);
                if (block == null || block.Length < len) { continue; }

                any = true;
                for (int i = 0; i + 4 <= len; i += 4)
                {
                    values[(offset + i) / 4] = ReadUInt32(block, i);
                }
            }
            return any ? values : null;
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
