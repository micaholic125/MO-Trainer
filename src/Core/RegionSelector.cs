using System;
using System.Collections.Generic;
using System.Globalization;

namespace MoTrainer.Core
{
    /// <summary>
    /// 扫描目标区域的筛选条件。
    /// 游戏堆数据几乎都在 MEM_PRIVATE 的可写区域，默认只扫这一类可以大幅降低噪声与耗时。
    /// </summary>
    public sealed class RegionSelector
    {
        public bool IncludePrivate = true;
        public bool IncludeImage = false;
        public bool IncludeMapped = false;

        public bool RequireWritable = true;
        public bool RequireReadable = true;

        /// <summary>单个区域的最小字节数，用于跳过零碎小区域。</summary>
        public ulong MinimumSize = 0x1000;

        /// <summary>是否为"扫堆数据（数值搜索）"的推荐组合。</summary>
        public static RegionSelector ForHeapData()
        {
            RegionSelector selector = new RegionSelector();
            selector.IncludePrivate = true;
            selector.IncludeImage = false;
            selector.IncludeMapped = false;
            selector.RequireWritable = true;
            selector.RequireReadable = true;
            selector.MinimumSize = 0x1000;
            return selector;
        }

        /// <summary>是否为"扫模块内特征码"的推荐组合（只读也可，但不含堆）。</summary>
        public static RegionSelector ForModule()
        {
            RegionSelector selector = new RegionSelector();
            selector.IncludePrivate = false;
            selector.IncludeImage = true;
            selector.IncludeMapped = false;
            selector.RequireWritable = false;
            selector.RequireReadable = true;
            selector.MinimumSize = 0x1000;
            return selector;
        }

        public bool Accept(MemoryRegion region)
        {
            if (region == null) { return false; }
            if (region.State != Win32Api.MEM_COMMIT) { return false; }
            if (region.Size < MinimumSize) { return false; }
            if (RequireReadable && !region.Readable) { return false; }
            if (RequireWritable && !region.Writable) { return false; }

            if (region.Type == Win32Api.MEM_PRIVATE) { return IncludePrivate; }
            if (region.Type == Win32Api.MEM_IMAGE) { return IncludeImage; }
            if (region.Type == Win32Api.MEM_MAPPED) { return IncludeMapped; }
            return false;
        }

        public List<MemoryRegion> Apply(IList<MemoryRegion> regions)
        {
            List<MemoryRegion> result = new List<MemoryRegion>();
            if (regions == null) { return result; }
            for (int i = 0; i < regions.Count; i++)
            {
                if (Accept(regions[i])) { result.Add(regions[i]); }
            }
            return result;
        }

        public ulong TotalBytes(IList<MemoryRegion> regions)
        {
            ulong total = 0;
            if (regions == null) { return 0; }
            for (int i = 0; i < regions.Count; i++) { total += regions[i].Size; }
            return total;
        }

        public string Describe()
        {
            string types = "";
            if (IncludePrivate) { types += "private "; }
            if (IncludeImage) { types += "image "; }
            if (IncludeMapped) { types += "mapped "; }
            if (types.Length == 0) { types = "none "; }

            return string.Format(CultureInfo.InvariantCulture,
                "types=[{0}] writable={1} readable={2} minSize=0x{3:X}",
                types.Trim(), RequireWritable, RequireReadable, MinimumSize);
        }
    }
}
