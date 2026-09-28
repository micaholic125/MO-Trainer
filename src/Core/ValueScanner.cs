using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using MoTrainer.Util;

namespace MoTrainer.Core
{
    /// <summary>候选值的筛选方式。</summary>
    public enum ValueMatch
    {
        /// <summary>当前值等于指定值。</summary>
        EqualTo = 0,
        /// <summary>与上一轮相比没有变化。</summary>
        Unchanged = 1,
        /// <summary>与上一轮相比发生了变化（无论增减）。</summary>
        Changed = 2,
        /// <summary>与上一轮相比变大了。</summary>
        Increased = 3,
        /// <summary>与上一轮相比变小了。</summary>
        Decreased = 4,
        /// <summary>当前值大于指定值。</summary>
        GreaterThan = 5,
        /// <summary>当前值小于指定值。</summary>
        LessThan = 6
    }

    /// <summary>一个候选地址及其上一轮读到的值。</summary>
    public sealed class ScanCandidate
    {
        public uint Address;
        public uint PreviousValue;

        /// <summary>命中时的比较宽度（4 或 2 字节）。游戏里不少数值是 short，宽度信息要留到写回时用。</summary>
        public int Width = 4;

        /// <summary>展示用备注（例如"从 400 掉到 23，-94%"）。只影响界面，不参与任何判定。</summary>
        public string Note = "";

        public ScanCandidate() { }

        public ScanCandidate(uint address, uint previousValue)
            : this(address, previousValue, 4)
        {
        }

        public ScanCandidate(uint address, uint previousValue, int width)
        {
            Address = address;
            PreviousValue = previousValue;
            Width = width;
        }

        public override string ToString()
        {
            return "0x" + Address.ToString("X8", CultureInfo.InvariantCulture)
                + (Width == 2 ? "（2 字节）" : "");
        }
    }

    public sealed class ValueScanOptions
    {
        public int MaxResults = 500000;
        public bool FourByteAligned = true;
        public int ChunkSize = 1024 * 1024;

        /// <summary>
        /// 是否兼做 2 字节匹配。
        /// RA2/YR 里不少字段是 short，若只按 4 字节比会完全找不到
        /// （4 字节读出来的高 2 字节是别的字段，数值自然对不上）。
        /// 开启后步进按 2 字节走，同一位置优先记 4 字节命中，避免重复。
        /// </summary>
        public bool MatchUInt16 = false;

        /// <summary>结果值下限（含）。用于排除明显不合理的值，默认不过滤。</summary>
        public uint MinValue = 0;

        /// <summary>结果值上限（含）。默认不过滤。</summary>
        public uint MaxValue = uint.MaxValue;

        public bool HasValueFilter { get { return MinValue != 0 || MaxValue != uint.MaxValue; } }
    }

    public sealed class ValueScanReport
    {
        public List<ScanCandidate> Candidates = new List<ScanCandidate>();
        public long PositionsTested;
        public long BytesScanned;
        public int ChunksScanned;
        public bool Truncated;
        public bool Cancelled;
        public TimeSpan Elapsed;
        public string Error = "";

        public bool Ok { get { return Error.Length == 0; } }

        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "候选 {0} 个｜扫描 {1:N0} MB｜比对 {2:N0} 个位置｜耗时 {3:N0} ms{4}{5}",
                Candidates.Count, BytesScanned / 1048576.0, PositionsTested,
                Elapsed.TotalMilliseconds,
                Truncated ? "｜已达候选上限（建议先用更独特的数值开局）" : "",
                Cancelled ? "｜已取消" : "");
        }
    }

    /// <summary>
    /// 数值搜索器：首轮扫描 + 变化筛选，用于把校准向导的候选从百万级收敛到个位数。
    /// 单线程分块读取，逻辑简单可预测；实际瓶颈在 ReadProcessMemory，多线程收益有限。
    /// </summary>
    public sealed class ValueScanner
    {
        private readonly MemoryIO _memory;

        public ValueScanner(MemoryIO memory)
        {
            if (memory == null) { throw new ArgumentNullException("memory"); }
            _memory = memory;
        }

        public MemoryIO Memory { get { return _memory; } }

        // ============================================================ 首轮扫描

        public ValueScanReport Scan(
            IList<MemoryRegion> regions,
            uint value,
            ValueScanOptions options,
            CancellationToken token,
            Action<ValueScanReport> progress)
        {
            if (options == null) { options = new ValueScanOptions(); }

            ValueScanReport report = new ValueScanReport();
            Stopwatch watch = Stopwatch.StartNew();

            if (regions == null || regions.Count == 0)
            {
                report.Error = "没有可扫描的内存区域";
                watch.Stop();
                report.Elapsed = watch.Elapsed;
                return report;
            }

            int chunkSize = options.ChunkSize < 0x1000 ? 0x1000 : options.ChunkSize;
            byte[] pattern = new byte[4];
            HexUtil.WriteUInt32(pattern, 0, value);

            int reportEvery = 0;
            try
            {
                for (int r = 0; r < regions.Count && !report.Truncated; r++)
                {
                    MemoryRegion region = regions[r];
                    if (!region.Readable) { continue; }

                    ulong end = region.End;
                    ulong cursor = region.Base;

                    while (cursor < end)
                    {
                        if (token.IsCancellationRequested) { report.Cancelled = true; break; }

                        ulong remaining = end - cursor;
                        int length = remaining < (ulong)chunkSize ? (int)remaining : chunkSize;
                        if (length < 4) { break; }

                        byte[] buffer = _memory.ReadRawBytes((IntPtr)(long)cursor, length);
                        report.ChunksScanned++;
                        if (buffer == null) { cursor += (ulong)length; continue; }

                        report.BytesScanned += buffer.Length;
                        int positions = CompareChunk(buffer, 0, buffer.Length, cursor, pattern, value, options, report);

                        cursor += (ulong)length;

                        if (report.Truncated) { break; }

                        reportEvery++;
                        if (progress != null && (reportEvery % 16) == 0) { progress(report); }
                    }

                    if (report.Cancelled) { break; }
                }
            }
            catch (Exception ex)
            {
                report.Error = ex.GetType().Name + ": " + ex.Message;
                Logger.Error("ValueScanner", "数值扫描失败", ex);
            }

            watch.Stop();
            report.Elapsed = watch.Elapsed;
            if (progress != null) { progress(report); }
            return report;
        }

        private static int CompareChunk(
            byte[] buffer, int offset, int length, ulong bufferAddress,
            byte[] pattern, uint value, ValueScanOptions options, ValueScanReport report)
        {
            int found = 0;
            bool wide = options.MatchUInt16;

            int step = wide ? 2 : 1;
            if (options.FourByteAligned) { step = wide ? 2 : 4; }

            int limit4 = offset + length - 4;   // 4 字节比较的边界
            int limit2 = offset + length - 2;   // 2 字节比较的边界
            int limit = wide ? limit2 : limit4;
            if (limit < offset) { return 0; }

            int start = offset;
            if (options.FourByteAligned)
            {
                ulong misalign = bufferAddress % (ulong)step;
                if (misalign != 0) { start = offset + (int)((ulong)step - misalign); }
            }

            byte p0 = pattern[0];
            byte p1 = pattern[1];
            byte p2 = pattern[2];
            byte p3 = pattern[3];
            bool hasRange = options.HasValueFilter;

            for (int i = start; i <= limit; i += step)
            {
                report.PositionsTested++;

                ulong address = bufferAddress + (ulong)(i - offset);

                // 先试 4 字节：命中就记 4 字节并跳到下一个位置，
                // 免得同一处又被当成 2 字节命中而重复计入
                if (i <= limit4
                    && buffer[i] == p0 && buffer[i + 1] == p1
                    && buffer[i + 2] == p2 && buffer[i + 3] == p3
                    && (!options.FourByteAligned || (address % 4UL) == 0))
                {
                    if (!hasRange || (value >= options.MinValue && value <= options.MaxValue))
                    {
                        if (Add(report, address, value, 4, options)) { found++; }
                        if (report.Truncated) { break; }
                    }
                    continue;
                }

                // 再试 2 字节
                if (wide && buffer[i] == p0 && buffer[i + 1] == p1)
                {
                    if (!hasRange || (value >= options.MinValue && value <= options.MaxValue))
                    {
                        if (Add(report, address, value, 2, options)) { found++; }
                        if (report.Truncated) { break; }
                    }
                }
            }
            return found;
        }

        private static bool Add(
            ValueScanReport report, ulong address, uint value, int width, ValueScanOptions options)
        {
            report.Candidates.Add(new ScanCandidate((uint)address, value, width));
            if (report.Candidates.Count >= options.MaxResults)
            {
                report.Truncated = true;
            }
            return true;
        }

        // ============================================================ 变化筛选

        /// <summary>
        /// 对候选列表做一次筛选，返回保留下来的数量。
        /// 保留下来的候选会把 PreviousValue 更新为当前值，便于连续多轮收敛。
        /// </summary>
        public int Refilter(List<ScanCandidate> candidates, ValueMatch match, uint targetValue)
        {
            if (candidates == null) { return 0; }

            int write = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                ScanCandidate candidate = candidates[i];

                // 必须按命中时的宽度读取：2 字节字段如果按 4 字节读，
                // 高 2 位是邻居字段的数据，永远比不出正确的值
                uint current;
                if (!ReadCurrent(candidate, out current)) { continue; }

                if (!Keep(current, candidate.PreviousValue, match, targetValue)) { continue; }

                candidate.PreviousValue = current;
                candidates[write] = candidate;
                write++;
            }

            if (write < candidates.Count)
            {
                candidates.RemoveRange(write, candidates.Count - write);
            }
            return write;
        }

        /// <summary>刷新所有候选的当前值（不筛选），用于展示与再次筛选前对齐。</summary>
        public int Refresh(List<ScanCandidate> candidates)
        {
            if (candidates == null) { return 0; }

            int alive = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                uint current;
                if (!ReadCurrent(candidates[i], out current)) { continue; }
                candidates[i].PreviousValue = current;
                alive++;
            }
            return alive;
        }

        /// <summary>按候选记录的比较宽度读取当前值。</summary>
        private bool ReadCurrent(ScanCandidate candidate, out uint value)
        {
            if (candidate.Width == 2)
            {
                ushort narrow;
                if (!_memory.ReadUInt16(candidate.Address, out narrow)) { value = 0; return false; }
                value = narrow;
                return true;
            }
            return _memory.ReadUInt32(candidate.Address, out value);
        }

        private static bool Keep(uint current, uint previous, ValueMatch match, uint target)
        {
            switch (match)
            {
                case ValueMatch.EqualTo: return current == target;
                case ValueMatch.Unchanged: return current == previous;
                case ValueMatch.Changed: return current != previous;
                case ValueMatch.Increased: return current > previous;
                case ValueMatch.Decreased: return current < previous;
                case ValueMatch.GreaterThan: return current > target;
                case ValueMatch.LessThan: return current < target;
                default: return false;
            }
        }

        // ============================================================ 读写与统计

        public bool TryReadValue(uint address, out uint value)
        {
            return _memory.ReadUInt32(address, out value);
        }

        public bool WriteValue(uint address, uint value)
        {
            return _memory.WriteUInt32Verified(address, value);
        }

        /// <summary>候选按所属区域分组，用于报告与"是否都落在同一个对象里"的判断。</summary>
        public Dictionary<string, int> GroupByRegion(List<ScanCandidate> candidates)
        {
            Dictionary<string, int> groups = new Dictionary<string, int>();
            if (candidates == null) { return groups; }

            for (int i = 0; i < candidates.Count; i++)
            {
                MemoryRegion region = _memory.Describe(candidates[i].Address);
                string key = region == null
                    ? "unknown"
                    : string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", region.Base);

                int count;
                if (groups.TryGetValue(key, out count)) { groups[key] = count + 1; }
                else { groups.Add(key, 1); }
            }
            return groups;
        }
    }
}
