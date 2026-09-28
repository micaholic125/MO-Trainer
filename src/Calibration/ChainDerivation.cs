using System;
using System.Collections.Generic;
using MoTrainer.Core;

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 指针链推导：用"同对象基准字段"的链加相对偏移，得到目标字段的链。
    ///
    /// 为什么需要它：指针回溯的最大偏移只有 0x400，而 HouseClass 在 MO 里有 20KB 以上。
    /// 字段在对象内的偏移一旦超过上限，"指向该对象的全局指针"就落在搜索范围之外，
    /// 回溯必然 0 命中 —— 这个坑在真机上实测踩过（电力字段 `House + 0x53A4`）。
    ///
    /// 但同对象内的相对偏移是**固定的结构事实**，用基准链加上差值即可得到目标链，
    /// 顺带继承基准链的多条候选链，跨会话的稳健性一并拿到。
    /// </summary>
    public static class ChainDerivation
    {
        /// <summary>
        /// 推导最多这么多条链。与 <see cref="FieldLocator.MaxChains"/> 保持一致，
        /// 免得推导出一堆链又被 FieldLocator 悄悄截断。
        /// </summary>
        public const int MaxChains = FieldLocator.MaxChains;

        /// <summary>
        /// 用基准字段的链推导目标字段的链。
        /// 返回推导出的链条数（0 表示基准不可用或全部溢出）。
        /// </summary>
        public static List<PointerChain> Derive(
            FieldLocator baseLocator, uint baseAddress, uint targetAddress, out long relativeOffset)
        {
            relativeOffset = 0;
            List<PointerChain> result = new List<PointerChain>();

            if (baseLocator == null || !baseLocator.IsCalibrated) { return result; }
            if (baseAddress == 0 || targetAddress == 0) { return result; }

            relativeOffset = (long)targetAddress - (long)baseAddress;

            List<PointerChain> baseChains = baseLocator.Chains;
            for (int i = 0; i < baseChains.Count && result.Count < MaxChains; i++)
            {
                uint[] offsets = baseChains[i].Offsets;
                if (offsets == null || offsets.Length == 0) { continue; }

                long last = (long)offsets[offsets.Length - 1] + relativeOffset;
                if (last < 0 || last > uint.MaxValue) { continue; }

                uint[] derived = new uint[offsets.Length];
                Array.Copy(offsets, derived, offsets.Length);
                derived[offsets.Length - 1] = (uint)last;
                result.Add(new PointerChain(derived));
            }
            return result;
        }
    }
}
