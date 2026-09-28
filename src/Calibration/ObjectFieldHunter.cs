using System;
using System.Collections.Generic;
using System.Globalization;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Calibration
{
    /// <summary>某个对象在某一时刻的一段内存快照。</summary>
    public sealed class ObjectSnapshot
    {
        public uint BaseAddress;
        public byte[] Bytes;

        public bool IsValid { get { return Bytes != null && Bytes.Length >= 4; } }
        public int Length { get { return Bytes == null ? 0 : Bytes.Length; } }

        public bool TryGet(int offset, out uint value)
        {
            value = 0;
            if (Bytes == null || offset < 0 || offset + 4 > Bytes.Length) { return false; }
            value = HexUtil.ReadUInt32(Bytes, offset);
            return true;
        }
    }

    /// <summary>按偏移汇总后的字段变化：同一个偏移可能在多个对象上同时发生了变化。</summary>
    public sealed class FieldChangeSummary
    {
        public uint Offset;
        public int AffectedObjects;

        /// <summary>这些发生变化的对象里，有多少个已确认归属本方（引用了本方阵营对象）。</summary>
        public int OwnedObjects;

        /// <summary>展示出来的那对"受伤前 → 受伤后"是否取自本方对象。非本方的只是备胎参考值。</summary>
        public bool SampleOwned;

        public uint Before;
        public uint After;

        public long Delta { get { return (long)After - (long)Before; } }

        /// <summary>减少量占受伤前的千分比。挑血量时"掉得多"比"原始值小"更说明问题。</summary>
        public int DropPermille
        {
            get
            {
                if (Before == 0 || After >= Before) { return 0; }
                return (int)((ulong)(Before - After) * 1000UL / Before);
            }
        }
    }

    /// <summary>对象内某个字段从 Before 变成了 After。</summary>
    public sealed class FieldChange
    {
        public uint Offset;
        public uint Before;
        public uint After;

        /// <summary>这个对象是否已确认归属本方。排名时优先看本方对象上的变化。</summary>
        public bool Owned;

        public long Delta { get { return (long)After - (long)Before; } }

        public string Describe()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "+0x{0:X4}   {1} → {2}   (变化 {3:+#;-#;0})", Offset, Before, After, Delta);
        }
    }

    /// <summary>
    /// 对象内部字段猎手：靠"同一对象前后两次快照的差异"来定位字段。
    ///
    /// 为什么需要它：像"单位血量"这种字段，我们在界面上看不到精确数字，
    /// 也就没法用数值匹配（金钱/电力那套）。但可以反过来利用"受伤 → 血量必然变小"
    /// 这个强约束：对比受伤前后，谁变小了谁就是候选。
    ///
    /// 过滤条件刻意收紧，宁缺勿滥：
    ///   · 受伤前的值必须落在合理的血量区间（几百到几十万）；
    ///   · 减少量必须合理 —— 这一条同时排除了"指针被释放/对象重排"这类巨大跳变。
    /// </summary>
    public static class ObjectFieldHunter
    {
        // 注：多对象结果按偏移汇总的入口是 MergeByOffset，见下方
        /// <summary>血量类字段都在对象低偏移处，默认只看开头 8KB。</summary>
        public const int DefaultSnapshotLength = 0x2000;

        public static ObjectSnapshot Snapshot(MemoryIO memory, uint baseAddress, int length)
        {
            ObjectSnapshot snapshot = new ObjectSnapshot();
            snapshot.BaseAddress = baseAddress;
            if (memory == null || baseAddress == 0 || length < 4) { return snapshot; }
            if (length > 0x10000) { length = 0x10000; }
            snapshot.Bytes = memory.ReadBytes(baseAddress, length);
            return snapshot;
        }

        /// <summary>找出两次快照之间"变小了"且幅度合理的字段。</summary>
        public static List<FieldChange> FindDecreased(
            ObjectSnapshot before, ObjectSnapshot after,
            uint minBefore, uint maxBefore, uint minDrop, uint maxDrop, int limit)
        {
            List<FieldChange> result = new List<FieldChange>();
            if (before == null || after == null || !before.IsValid || !after.IsValid) { return result; }
            if (before.BaseAddress != after.BaseAddress) { return result; }

            int length = Math.Min(before.Length, after.Length);
            for (int offset = 0; offset + 4 <= length; offset += 4)
            {
                uint oldValue;
                uint newValue;
                if (!before.TryGet(offset, out oldValue)) { break; }
                if (!after.TryGet(offset, out newValue)) { break; }

                if (newValue >= oldValue) { continue; }

                uint drop = oldValue - newValue;
                if (oldValue < minBefore || oldValue > maxBefore) { continue; }
                if (drop < minDrop || drop > maxDrop) { continue; }

                FieldChange change = new FieldChange();
                change.Offset = (uint)offset;
                change.Before = oldValue;
                change.After = newValue;
                result.Add(change);
            }

            // 受伤前的数值越小越可能是血量（其余小数值字段更可能是计数器）
            result.Sort(delegate(FieldChange a, FieldChange b)
            {
                int byBefore = a.Before.CompareTo(b.Before);
                if (byBefore != 0) { return byBefore; }
                return a.Offset.CompareTo(b.Offset);
            });

            if (limit > 0 && result.Count > limit) { result.RemoveRange(limit, result.Count - limit); }
            return result;
        }

        /// <summary>
        /// 把"每个对象各自的变化列表"按**偏移**汇总。
        ///
        /// 为什么需要这一步：挨打的往往不是第一个选中的单位，所以必须把所有选中单位都比一遍；
        /// 而同一类单位的字段偏移是相同的 —— 按偏移合并后，
        /// **"在多个单位上同时变化"的那个偏移就是最强候选**（所有单位共有的字段，比如血量）。
        /// 排序也据此来：吻合的单位越多越靠前，其次才是"受伤前数值越小越像血量"。
        /// </summary>
        public static List<FieldChangeSummary> MergeByOffset(IList<List<FieldChange>> perObject)
        {
            List<FieldChangeSummary> merged = new List<FieldChangeSummary>();
            if (perObject == null) { return merged; }

            for (int i = 0; i < perObject.Count; i++)
            {
                List<FieldChange> changes = perObject[i];
                if (changes == null) { continue; }

                for (int k = 0; k < changes.Count; k++)
                {
                    FieldChange change = changes[k];

                    FieldChangeSummary existing = null;
                    for (int m = 0; m < merged.Count; m++)
                    {
                        if (merged[m].Offset == change.Offset) { existing = merged[m]; break; }
                    }

                    if (existing != null)
                    {
                        existing.AffectedObjects++;
                        if (change.Owned) { existing.OwnedObjects++; }

                        // 代表值优先取**本方对象**上的那次变化（非本方只是个备胎）；
                        // 同一档里取减少量最大的那次。同一类单位的满血值相同，
                        // 所以"满血 → 受伤后"这对数字最直观、最容易人工核对。
                        bool better;
                        if (change.Owned != existing.SampleOwned) { better = change.Owned; }
                        else { better = change.Delta < existing.Delta; }

                        if (better)
                        {
                            existing.Before = change.Before;
                            existing.After = change.After;
                            existing.SampleOwned = change.Owned;
                        }
                        continue;
                    }

                    FieldChangeSummary item = new FieldChangeSummary();
                    item.Offset = change.Offset;
                    item.AffectedObjects = 1;
                    item.OwnedObjects = change.Owned ? 1 : 0;
                    item.SampleOwned = change.Owned;
                    item.Before = change.Before;
                    item.After = change.After;
                    merged.Add(item);
                }
            }

            merged.Sort(delegate(FieldChangeSummary a, FieldChangeSummary b)
            {
                // 1) 先看"本方对象"上的命中数。
                //    实测教训：只按"掉幅比例"排，会把**非本方对象**上 6312 → 170（-97%）
                //    这种数字顶到第一（比如某个无关结构里的计数器），而真正的血量
                //    是动员兵的 125 → 23（-82%），排名反而靠后。归属校验必须参与排序。
                int byOwned = b.OwnedObjects.CompareTo(a.OwnedObjects);
                if (byOwned != 0) { return byOwned; }

                // 2) 再看多少个对象同时变化
                int byCount = b.AffectedObjects.CompareTo(a.AffectedObjects);
                if (byCount != 0) { return byCount; }

                // 3) 最后比掉幅比例：玩家是按"把它打惨"来操作的，血量跌幅必然大
                return b.DropPermille.CompareTo(a.DropPermille);
            });
            return merged;
        }

        /// <summary>统计变化/未变字段数，用于诊断"这次对比到底有没有意义"。</summary>
        public static void Summarize(ObjectSnapshot before, ObjectSnapshot after,
            out int changedCount, out int unchangedCount)
        {
            changedCount = 0;
            unchangedCount = 0;
            if (before == null || after == null || !before.IsValid || !after.IsValid) { return; }

            int length = Math.Min(before.Length, after.Length);
            for (int offset = 0; offset + 4 <= length; offset += 4)
            {
                uint oldValue;
                uint newValue;
                if (!before.TryGet(offset, out oldValue)) { break; }
                if (!after.TryGet(offset, out newValue)) { break; }
                if (oldValue == newValue) { unchangedCount++; } else { changedCount++; }
            }
        }
    }
}
