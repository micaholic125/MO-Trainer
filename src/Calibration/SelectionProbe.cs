using System;
using System.Collections.Generic;
using System.Globalization;
using MoTrainer.Core;
using MoTrainer.Util;

namespace MoTrainer.Calibration
{
    /// <summary>选中列表探测结果。</summary>
    public sealed class SelectionProbeResult
    {
        public bool Found;

        /// <summary>「选中数量」字段地址。</summary>
        public uint CountAddress;
        public int Count;

        /// <summary>存放"对象指针数组"起始地址的那个位置（通常在数量字段附近）。</summary>
        public uint ArraySlotAddress;

        /// <summary>数组本身的起始地址（即 <see cref="ArraySlotAddress"/> 里存的值）。</summary>
        public uint ArrayAddress;

        /// <summary>选中的对象地址，按数组顺序。</summary>
        public readonly List<uint> ObjectAddresses = new List<uint>();

        public readonly List<string> Trace = new List<string>();
        public string Error = "";
    }

    /// <summary>
    /// 在「选中数量」字段附近找出容纳对象指针的数组。
    ///
    /// 依据：RA2/YR 的动态容器（DynamicVectorClass 之类）通常是
    /// 「数组指针 + 容量 + 数量」三件套紧挨着放在同一个对象里，
    /// 所以数量字段附近 ±0x40 之内多半就夹着那个数组指针。
    ///
    /// 判定标准（四条全满足才认 —— 宁可漏报，不可误报）：
    ///   ① 该位置存的值是一个 4 字节对齐、落在可读内存里的指针；
    ///   ② 从该指针开始的 N 个 4 字节值**也都是**这样的指针，且每个都能读出至少 4 字节；
    ///   ③ 这 N 个指针两两不同；
    ///   ④ **每个指针指向的对象，其头 4 字节（类型标记）必须落在游戏模块区间内**（可选校验）。
    ///
    /// 第 ④ 条是真机踩坑后补的，非常关键：
    /// 用户实测发现，只满足 ①②③ 时，**一个游戏对象的"方法表"也能冒充名单** ——
    /// 表里排着一串互不相同、都读得出来的指针（那些是指向函数代码的指针），
    /// 于是探测通过了，但这个"名单"里根本没有单位。
    /// 而真正的对象名单，每一项的头 4 字节都是模块内的类型标记 ——
    /// 方法表那种"内容是机器指令"的假名单会被这一条直接拒掉。
    /// </summary>
    public static class SelectionProbe
    {
        /// <summary>一次探测最多认多少个选中对象，防止读到离谱的数字。</summary>
        public const int MaxCount = 64;

        /// <summary>
        /// 游戏主模块区间。由界面在探测前设置；为 0 表示未知，此时跳过第 ④ 条判定。
        /// 用静态字段而不是改方法签名，是为了不动既有调用点（含自检里的合成内存场景）。
        /// </summary>
        public static uint ModuleBaseHint;
        public static uint ModuleSizeHint;

        /// <summary>
        /// 第 ④ 条判定：这一串指针指向的对象，头 4 字节（类型标记）是否都在模块区间内。
        /// 只要有一个不像对象就整串否掉 —— 假名单（对象方法表）正是在这里露馅的。
        /// </summary>
        private static bool LooksLikeObjectArray(
            MemoryIO memory, List<uint> objects, uint moduleBase, uint moduleSize)
        {
            if (objects == null || objects.Count == 0) { return false; }

            for (int i = 0; i < objects.Count; i++)
            {
                uint head;
                if (!memory.ReadUInt32(objects[i], out head)) { return false; }
                if (head < moduleBase || head >= moduleBase + moduleSize) { return false; }
            }
            return true;
        }

        /// <summary>用户在界面上可以选的探测半径（由小到大依次尝试）。</summary>
        public static readonly int[] Radii = new int[] { 0x40, 0x100, 0x400, 0x800 };

        /// <summary>用依次放大的半径尝试探测，返回第一个成功的结果。</summary>
        public static SelectionProbeResult ProbeWithEscalation(MemoryIO memory, uint countAddress, int count)
        {
            SelectionProbeResult last = null;
            for (int i = 0; i < Radii.Length; i++)
            {
                SelectionProbeResult attempt = Probe(memory, countAddress, count, Radii[i]);
                if (attempt.Found)
                {
                    // 记录"用了多大半径才找到"，方便判断这个假设是否可靠
                    attempt.Trace.Add(string.Format(CultureInfo.InvariantCulture,
                        "探测半径 ±0x{0:X}（第 {1} 档）", Radii[i], i + 1));
                    return attempt;
                }
                last = attempt;
            }
            return last ?? new SelectionProbeResult();
        }

        public static SelectionProbeResult Probe(MemoryIO memory, uint countAddress, int count, int radius)
        {
            SelectionProbeResult result = new SelectionProbeResult();
            result.CountAddress = countAddress;
            result.Count = count;

            if (memory == null || countAddress == 0)
            {
                result.Error = "参数无效";
                return result;
            }
            if (count <= 0)
            {
                result.Error = "选中数量必须大于 0";
                return result;
            }
            if (count > MaxCount)
            {
                result.Error = string.Format(CultureInfo.InvariantCulture,
                    "选中数量 {0} 过大（最多支持 {1} 个）", count, MaxCount);
                return result;
            }
            if (radius < 4) { radius = 4; }

            uint start = countAddress > (uint)radius ? countAddress - (uint)radius : 0;
            start = start & 0xFFFFFFFCu;
            uint end = countAddress + (uint)radius;

            int checkedSlots = 0;
            for (uint slot = start; slot <= end; slot += 4)
            {
                if (slot == countAddress) { continue; }   // 数量字段自己不是数组指针
                checkedSlots++;

                uint arrayAddress;
                if (!memory.ReadUInt32(slot, out arrayAddress)) { continue; }
                if (!IsPlausiblePointer(arrayAddress)) { continue; }

                List<uint> objects = ReadObjectArray(memory, arrayAddress, count);
                if (objects == null) { continue; }

                // 第 ④ 条：每个元素指向的对象，头 4 字节必须是模块内的类型标记。
                // moduleSize == 0 表示调用方没提供模块信息（自检里的合成内存场景），此时跳过，
                // 保持旧的宽松行为 —— 真机路径一定会带上模块区间。
                if (ModuleSizeHint != 0
                    && !LooksLikeObjectArray(memory, objects, ModuleBaseHint, ModuleSizeHint))
                {
                    checkedSlots++;
                    continue;
                }

                result.Found = true;
                result.ArraySlotAddress = slot;
                result.ArrayAddress = arrayAddress;
                result.ObjectAddresses.AddRange(objects);
                result.Trace.Add(string.Format(CultureInfo.InvariantCulture,
                    "在 0x{0:X8}（距数量字段 {1:+#;-#;0} 字节）找到数组指针 0x{2:X8}，"
                    + "其前 {3} 个元素均为互不相同的有效对象指针",
                    slot, (long)slot - (long)countAddress, arrayAddress, count));
                return result;
            }

            result.Error = string.Format(CultureInfo.InvariantCulture,
                "在数量字段附近 ±0x{0:X} 的 {1} 个位置里没有找到「指针数组」结构", radius, checkedSlots);
            return result;
        }

        private static bool IsPlausiblePointer(uint address)
        {
            if (address < 0x10000) { return false; }       // 空指针区 / DOS 头
            if ((address & 3u) != 0) { return false; }     // 对象指针必然 4 字节对齐
            return true;
        }

        /// <summary>读取并校验对象指针数组；任何一项不合格就整体判否。</summary>
        private static List<uint> ReadObjectArray(MemoryIO memory, uint arrayAddress, int count)
        {
            byte[] buffer = memory.ReadBytes(arrayAddress, count * 4);
            if (buffer == null || buffer.Length < count * 4) { return null; }

            List<uint> addresses = new List<uint>(count);
            for (int i = 0; i < count; i++)
            {
                uint value = HexUtil.ReadUInt32(buffer, i * 4);
                if (!IsPlausiblePointer(value)) { return null; }
                if (addresses.Contains(value)) { return null; }

                // 每个元素本身还必须真的可以读 —— 排除"数组指针指向一段恰好像指针的数据"
                uint probe;
                if (!memory.ReadUInt32(value, out probe)) { return null; }

                addresses.Add(value);
            }
            return addresses;
        }
    }
}
