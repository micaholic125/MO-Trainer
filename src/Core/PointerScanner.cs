using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using MoTrainer.Util;

namespace MoTrainer.Core
{
    /// <summary>
    /// 一条可持久化的指针路径。
    /// Offsets 是完整偏移链（首元素为相对模块基址的静态偏移），语义与 <see cref="PointerChain"/> 一致。
    /// </summary>
    public sealed class PointerPath
    {
        public uint[] Offsets;
        public uint StaticAddress;

        /// <summary>静态基址是否位于可写页（.data）。可写页的路径才可能是真正的全局指针变量。</summary>
        public bool StaticIsWritable;

        /// <summary>解引用层数（0 表示静态偏移本身就是叶子）。</summary>
        public int LevelCount;

        public bool IsEmpty { get { return Offsets == null || Offsets.Length == 0; } }

        public PointerChain ToChain()
        {
            return new PointerChain(Offsets);
        }

        public string Format(uint moduleBase)
        {
            return ToChain().Format(moduleBase);
        }

        public override string ToString()
        {
            return ToChain().Serialize();
        }
    }

    public sealed class PointerScanOptions
    {
        /// <summary>最大回溯层数。YR 的对象链通常 2~3 层。</summary>
        public int MaxLevels = 4;

        /// <summary>对象内字段偏移上限（4 字节对齐步进）。</summary>
        public int MaxOffset = 0x400;

        /// <summary>每层保留的候选目标上限，防止组合爆炸。</summary>
        public int MaxTargetsPerLevel = 48;

        /// <summary>最多输出多少条静态路径。</summary>
        public int MaxPaths = 32;

        public int ChunkSize = 1 << 20;

        public bool FourByteAligned = true;
    }

    public sealed class PointerScanReport
    {
        public List<PointerPath> Paths = new List<PointerPath>();
        public int LevelsScanned;
        public long BytesScanned;
        public bool Cancelled;
        public string Error = "";
        public TimeSpan Elapsed;
        public List<string> Trace = new List<string>();

        public bool Ok { get { return Error.Length == 0; } }

        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "静态路径 {0} 条｜回溯 {1} 层｜扫描 {2:N0} MB｜耗时 {3:N0} ms{4}",
                Paths.Count, LevelsScanned, BytesScanned / 1048576.0, Elapsed.TotalMilliseconds,
                Cancelled ? "｜已取消" : "");
        }
    }

    /// <summary>
    /// 指针回溯扫描器：把一个"本次运行有效的绝对地址"反推成
    /// `[[模块基址+静态偏移]+偏移...]` 形式的持久化偏移链。
    ///
    /// 算法（每层一次全内存扫描，比"逐目标 × 逐偏移搜索"快几个数量级）：
    /// ① 把当前层所有目标地址与候选偏移组合成"要找的值"哈希表；
    /// ② 用地址范围预筛（只有落在 [最小目标-最大偏移, 最大目标] 内的值才查哈希），
    ///    这一步能滤掉 99.9% 的位置，使整层扫描保持在秒级；
    /// ③ 命中的地址若落在模块映像范围内且页可写，即为静态根，路径成立；
    ///    否则作为下一层的目标继续回溯。
    ///
    /// 只接受**可写页**上的静态根：真正的全局指针变量在 .data 里；
    /// 只读 .text 中的偶然巧合会解析出"看起来能用"的假路径，必须排除。
    /// </summary>
    public sealed class PointerScanner
    {
        private const int OffsetStep = 4;

        private readonly MemoryIO _memory;

        public PointerScanner(MemoryIO memory)
        {
            if (memory == null) { throw new ArgumentNullException("memory"); }
            _memory = memory;
        }

        public PointerScanReport Scan(
            uint leafAddress,
            uint moduleBase,
            uint moduleSize,
            IList<MemoryRegion> regions,
            PointerScanOptions options,
            CancellationToken token,
            Action<string> progress)
        {
            if (options == null) { options = new PointerScanOptions(); }

            PointerScanReport report = new PointerScanReport();
            Stopwatch watch = Stopwatch.StartNew();

            if (leafAddress == 0)
            {
                report.Error = "叶子地址无效";
                watch.Stop();
                report.Elapsed = watch.Elapsed;
                return report;
            }
            if (regions == null || regions.Count == 0)
            {
                report.Error = "没有可用于回溯的内存区域";
                watch.Stop();
                report.Elapsed = watch.Elapsed;
                return report;
            }

            ulong moduleEnd = (ulong)moduleBase + moduleSize;

            // 目标：地址 -> 该地址解引用后需要追加的偏移（按链顺序）
            Dictionary<uint, List<uint>> targets = new Dictionary<uint, List<uint>>();
            targets.Add(leafAddress, new List<uint>());

            report.Trace.Add(string.Format(CultureInfo.InvariantCulture,
                "回溯起点 0x{0:X8}，模块范围 0x{1:X8}-0x{2:X8}，最大偏移 0x{3:X}，最大层数 {4}",
                leafAddress, moduleBase, moduleEnd, options.MaxOffset, options.MaxLevels));

            try
            {
                for (int level = 0; level < options.MaxLevels; level++)
                {
                    if (token.IsCancellationRequested) { report.Cancelled = true; break; }
                    if (targets.Count == 0) { break; }
                    if (report.Paths.Count >= options.MaxPaths) { break; }

                    report.LevelsScanned = level + 1;

                    Dictionary<uint, List<uint>> lookup;
                    uint low;
                    uint high;
                    BuildLookup(targets, options, out lookup, out low, out high);

                    List<Hit> hits = new List<Hit>(256);
                    long bytes;
                    CollectLevel(regions, lookup, low, high, options, token, hits, out bytes);
                    report.BytesScanned += bytes;

                    Dictionary<uint, List<uint>> next = new Dictionary<uint, List<uint>>();
                    int rootCount = 0;

                    for (int i = 0; i < hits.Count; i++)
                    {
                        Hit hit = hits[i];
                        List<uint> offsets;
                        if (!lookup.TryGetValue(hit.Content, out offsets)) { continue; }

                        for (int k = 0; k < offsets.Count; k++)
                        {
                            uint offset = offsets[k];
                            uint target = hit.Content + offset;

                            List<uint> tail;
                            if (!targets.TryGetValue(target, out tail)) { continue; }

                            List<uint> newTail = new List<uint>(tail.Count + 1);
                            newTail.Add(offset);
                            newTail.AddRange(tail);

                            bool isRoot = hit.Address >= moduleBase && (ulong)hit.Address < moduleEnd;
                            if (isRoot)
                            {
                                bool writable = _memory.IsWritable(hit.Address);
                                if (!writable)
                                {
                                    // 只读页上的"指针"几乎肯定是代码字节的巧合，丢弃
                                    continue;
                                }

                                PointerPath path = new PointerPath();
                                uint[] chain = new uint[newTail.Count + 1];
                                chain[0] = hit.Address - moduleBase;
                                for (int c = 0; c < newTail.Count; c++) { chain[c + 1] = newTail[c]; }
                                path.Offsets = chain;
                                path.StaticAddress = hit.Address;
                                path.StaticIsWritable = true;
                                path.LevelCount = level + 1;

                                if (!ContainsPath(report.Paths, chain))
                                {
                                    report.Paths.Add(path);
                                    rootCount++;
                                }
                            }
                            else
                            {
                                List<uint> existing;
                                if (next.TryGetValue(hit.Address, out existing))
                                {
                                    // 同一地址只保留更短的尾巴，避免组合爆炸
                                    if (newTail.Count < existing.Count) { next[hit.Address] = newTail; }
                                }
                                else
                                {
                                    next.Add(hit.Address, newTail);
                                }
                            }
                        }
                    }

                    report.Trace.Add(string.Format(CultureInfo.InvariantCulture,
                        "第 {0} 层：目标 {1} 个｜命中 {2} 次｜新增静态路径 {3} 条｜累计 {4} 条｜耗时累计 {5:N0} ms",
                        level + 1, targets.Count, hits.Count, rootCount, report.Paths.Count,
                        watch.Elapsed.TotalMilliseconds));

                    if (progress != null)
                    {
                        progress(string.Format(CultureInfo.InvariantCulture,
                            "第 {0} 层回溯完成：命中 {1} 次，静态路径 {2} 条",
                            level + 1, hits.Count, report.Paths.Count));
                    }

                    if (next.Count > options.MaxTargetsPerLevel)
                    {
                        List<uint> keys = new List<uint>(next.Keys);
                        keys.Sort();
                        Dictionary<uint, List<uint>> trimmed = new Dictionary<uint, List<uint>>();
                        for (int i = 0; i < options.MaxTargetsPerLevel; i++)
                        {
                            trimmed.Add(keys[i], next[keys[i]]);
                        }
                        report.Trace.Add(string.Format(CultureInfo.InvariantCulture,
                            "下一层候选 {0} 个，按地址截断为 {1} 个", next.Count, trimmed.Count));
                        next = trimmed;
                    }

                    targets = next;
                }
            }
            catch (Exception ex)
            {
                report.Error = ex.GetType().Name + ": " + ex.Message;
                Logger.Error("PointerScanner", "指针回溯失败", ex);
            }

            watch.Stop();
            report.Elapsed = watch.Elapsed;

            // 排序：层数少的优先，层数相同则静态地址更靠前的优先（更接近已知的全局对象）
            report.Paths.Sort(delegate(PointerPath a, PointerPath b)
            {
                if (a.LevelCount != b.LevelCount) { return a.LevelCount.CompareTo(b.LevelCount); }
                return a.StaticAddress.CompareTo(b.StaticAddress);
            });

            if (report.Paths.Count > options.MaxPaths)
            {
                report.Paths.RemoveRange(options.MaxPaths, report.Paths.Count - options.MaxPaths);
            }

            Logger.Info("PointerScanner", report.Summary());
            if (report.Paths.Count == 0 && report.Error.Length == 0)
            {
                Logger.Warn("PointerScanner", "未找到可持久化的静态指针链（可能需要提高最大层数或偏移上限）");
            }
            return report;
        }

        // ============================================================ 内部

        private sealed class Hit
        {
            public uint Address;
            public uint Content;
        }

        private static void BuildLookup(
            Dictionary<uint, List<uint>> targets, PointerScanOptions options,
            out Dictionary<uint, List<uint>> lookup, out uint low, out uint high)
        {
            lookup = new Dictionary<uint, List<uint>>(targets.Count * 8);
            low = uint.MaxValue;
            high = 0;

            foreach (KeyValuePair<uint, List<uint>> pair in targets)
            {
                uint target = pair.Key;
                for (int offset = 0; offset <= options.MaxOffset; offset += OffsetStep)
                {
                    if (target <= (uint)offset) { break; } // 下溢保护

                    uint value = target - (uint)offset;
                    if (value == 0) { continue; }          // 空指针无意义

                    List<uint> offsets;
                    if (!lookup.TryGetValue(value, out offsets))
                    {
                        offsets = new List<uint>(1);
                        lookup.Add(value, offsets);
                    }
                    if (!offsets.Contains((uint)offset)) { offsets.Add((uint)offset); }

                    if (value < low) { low = value; }
                    if (value > high) { high = value; }
                }
            }
        }

        private void CollectLevel(
            IList<MemoryRegion> regions,
            Dictionary<uint, List<uint>> lookup,
            uint low, uint high,
            PointerScanOptions options,
            CancellationToken token,
            List<Hit> hits,
            out long bytesScanned)
        {
            bytesScanned = 0;
            int chunkSize = options.ChunkSize < 0x1000 ? 0x1000 : options.ChunkSize;

            for (int r = 0; r < regions.Count; r++)
            {
                if (token.IsCancellationRequested) { return; }

                MemoryRegion region = regions[r];
                if (!region.Readable) { continue; }

                ulong end = region.End;
                ulong cursor = region.Base;

                while (cursor < end)
                {
                    if (token.IsCancellationRequested) { return; }

                    ulong remaining = end - cursor;
                    int length = remaining < (ulong)chunkSize ? (int)remaining : chunkSize;
                    if (length < 4) { break; }

                    byte[] buffer = _memory.ReadRawBytes((IntPtr)(long)cursor, length);
                    if (buffer != null)
                    {
                        bytesScanned += buffer.Length;
                        AccumulateHits(buffer, buffer.Length, (uint)cursor, low, high, lookup,
                            options.FourByteAligned, hits);
                    }
                    cursor += (ulong)length;
                }
            }
        }

        private static void AccumulateHits(
            byte[] buffer, int length, uint bufferAddress,
            uint low, uint high, Dictionary<uint, List<uint>> lookup,
            bool aligned, List<Hit> hits)
        {
            int start = 0;
            if (aligned)
            {
                int misalign = (int)(bufferAddress % 4);
                if (misalign != 0) { start = 4 - misalign; }
            }

            int limit = length - 4;

            // 绝大多数位置会被地址范围预筛直接跳过，只有落在窄窗口内的值才查哈希
            for (int i = start; i <= limit; i += (aligned ? 4 : 1))
            {
                uint value = (uint)(buffer[i]
                    | (buffer[i + 1] << 8)
                    | (buffer[i + 2] << 16)
                    | (buffer[i + 3] << 24));

                if (value < low || value > high) { continue; }
                if (!lookup.ContainsKey(value)) { continue; }

                Hit hit = new Hit();
                hit.Address = bufferAddress + (uint)i;
                hit.Content = value;
                hits.Add(hit);
            }
        }

        private static bool ContainsPath(List<PointerPath> paths, uint[] chain)
        {
            for (int i = 0; i < paths.Count; i++)
            {
                uint[] other = paths[i].Offsets;
                if (other.Length != chain.Length) { continue; }
                bool same = true;
                for (int k = 0; k < chain.Length; k++)
                {
                    if (other[k] != chain[k]) { same = false; break; }
                }
                if (same) { return true; }
            }
            return false;
        }
    }
}
