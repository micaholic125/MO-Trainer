using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using MoTrainer.Util;

namespace MoTrainer.Core
{
    /// <summary>AOB 特征码模板，支持 `??` / `?` / `*` 通配字节。</summary>
    public sealed class ScanPattern
    {
        private readonly byte?[] _bytes;

        public ScanPattern(string text)
        {
            _bytes = HexUtil.ParsePattern(text);
            if (_bytes == null || _bytes.Length == 0)
            {
                throw new ArgumentException("非法特征码模板：" + (text == null ? "null" : text));
            }
            if (AnchorIndex < 0)
            {
                throw new ArgumentException("特征码不能全是通配字节：" + text);
            }
            Text = text;
        }

        public byte?[] Bytes { get { return _bytes; } }
        public int Length { get { return _bytes.Length; } }
        public string Text { get; private set; }

        /// <summary>第一个非通配字节的下标，用于快速跳过不匹配位置。</summary>
        public int AnchorIndex
        {
            get
            {
                for (int i = 0; i < _bytes.Length; i++)
                {
                    if (_bytes[i].HasValue) { return i; }
                }
                return -1;
            }
        }

        public bool HasWildcards
        {
            get
            {
                for (int i = 0; i < _bytes.Length; i++)
                {
                    if (!_bytes[i].HasValue) { return true; }
                }
                return false;
            }
        }

        /// <summary>由十六进制字符串构造，失败返回 null（不抛异常，便于配置读取）。</summary>
        public static ScanPattern TryCreate(string text)
        {
            if (string.IsNullOrEmpty(text)) { return null; }
            try { return new ScanPattern(text); }
            catch { return null; }
        }

        public override string ToString()
        {
            return Text;
        }
    }

    public sealed class ScanOptions
    {
        public int ChunkSize = 256 * 1024;
        public int MaxHits = 4096;

        /// <summary>工作线程数；0 表示自动（取 CPU 核数的一半，夹在 1~4 之间）。</summary>
        public int ThreadCount;

        /// <summary>只匹配 4 字节对齐的地址。游戏里的整型字段绝大多数 4 字节对齐，开启可显著降噪。</summary>
        public bool FourByteAligned;

        /// <summary>命中第一个即停止（用于"这个特征码存不存在"的判断）。</summary>
        public bool StopAfterFirstHit;

        public int EffectiveThreadCount
        {
            get
            {
                if (ThreadCount > 0) { return ThreadCount; }
                int count = Environment.ProcessorCount / 2;
                if (count < 1) { count = 1; }
                if (count > 4) { count = 4; }
                return count;
            }
        }
    }

    public sealed class ScanProgress
    {
        public long BytesScanned;
        public long TotalBytes;
        public int Hits;
        public double Percent;
    }

    public sealed class ScanReport
    {
        public List<uint> Hits = new List<uint>();
        public long BytesScanned;
        public int ChunksScanned;
        public int ChunksSkipped;
        public bool Cancelled;
        public bool TruncatedByHitLimit;
        public TimeSpan Elapsed;
        public string Error = "";

        public bool Ok { get { return Error.Length == 0; } }

        public string Summary()
        {
            return string.Format(CultureInfo.InvariantCulture,
                "命中 {0} 处｜扫描 {1:N0} MB｜分块 {2} 个（跳过 {3}）｜耗时 {4:N0} ms{5}{6}",
                Hits.Count, BytesScanned / 1048576.0, ChunksScanned, ChunksSkipped, Elapsed.TotalMilliseconds,
                TruncatedByHitLimit ? "｜已达命中上限" : "",
                Cancelled ? "｜已取消" : "");
        }
    }

    /// <summary>
    /// 特征码扫描器：分块读取 + 多线程比对，支持取消与进度回调。
    /// 工作线程优先级为 BelowNormal，避免抢占游戏帧。
    /// </summary>
    public sealed class AobScanner
    {
        private const int MaxHitsPerChunk = 10000;

        private readonly IProcessHandle _target;

        public AobScanner(IProcessHandle target)
        {
            if (target == null) { throw new ArgumentNullException("target"); }
            _target = target;
        }

        public ScanReport Scan(
            IList<MemoryRegion> regions,
            ScanPattern pattern,
            ScanOptions options,
            CancellationToken token,
            Action<ScanProgress> progress)
        {
            if (pattern == null) { throw new ArgumentNullException("pattern"); }
            if (options == null) { options = new ScanOptions(); }

            ScanReport report = new ScanReport();
            Stopwatch watch = Stopwatch.StartNew();

            List<Chunk> chunks = BuildChunks(regions, options);
            report.ChunksSkipped = CountSkipped(regions, options);

            long totalBytes = 0;
            for (int i = 0; i < chunks.Count; i++) { totalBytes += chunks[i].Length; }

            if (chunks.Count == 0)
            {
                watch.Stop();
                report.Elapsed = watch.Elapsed;
                return report;
            }

            ScanState state = new ScanState(_target, token);
            state.Chunks = chunks;
            state.Pattern = pattern;
            state.Options = options;
            state.TotalBytes = totalBytes;

            int threadCount = options.EffectiveThreadCount;
            if (threadCount > chunks.Count) { threadCount = chunks.Count; }
            if (threadCount < 1) { threadCount = 1; }

            List<Thread> workers = new List<Thread>(threadCount);
            try
            {
                for (int i = 0; i < threadCount; i++)
                {
                    Thread thread = new Thread(new ThreadStart(state.Work));
                    thread.IsBackground = true;
                    thread.Priority = ThreadPriority.BelowNormal;
                    thread.Name = "AobScan" + i;
                    thread.Start();
                    workers.Add(thread);
                }

                while (true)
                {
                    bool alive = false;
                    for (int i = 0; i < workers.Count; i++)
                    {
                        if (workers[i].IsAlive) { alive = true; break; }
                    }
                    if (!alive) { break; }

                    if (progress != null) { progress(state.Snapshot()); }
                    Thread.Sleep(120);
                }

                if (progress != null) { progress(state.Snapshot()); }
            }
            catch (Exception ex)
            {
                report.Error = ex.GetType().Name + ": " + ex.Message;
                Logger.Error("AobScanner", "扫描失败", ex);
                state.Cancel();
            }

            watch.Stop();
            report.Elapsed = watch.Elapsed;
            report.BytesScanned = Interlocked.Read(ref state.BytesScanned);
            report.ChunksScanned = Volatile.Read(ref state.ChunksScanned);
            report.Cancelled = token.IsCancellationRequested;
            report.TruncatedByHitLimit = state.Truncated;
            report.Hits = state.TakeHits();
            return report;
        }

        /// <summary>便捷入口：只扫模块映像区域。</summary>
        public ScanReport ScanModule(
            MemoryIO memory,
            ScanPattern pattern,
            ScanOptions options,
            uint moduleBase,
            uint moduleSize,
            CancellationToken token,
            Action<ScanProgress> progress)
        {
            List<MemoryRegion> regions = memory.EnumRegions(
                moduleBase, (ulong)moduleBase + moduleSize, true, false, 0);
            if (regions.Count == 0)
            {
                ScanReport empty = new ScanReport();
                empty.Error = "无法枚举模块内存区域";
                return empty;
            }
            return Scan(regions, pattern, options, token, progress);
        }

        // ============================================================ 内部

        private sealed class Chunk
        {
            public uint Start;
            public uint RegionEnd;
            /// <summary>起始位置个数（不含尾部重叠区）。</summary>
            public int Length;
        }

        /// <summary>扫描过程中的共享状态；每个工作线程持有独立的 MemoryIO。</summary>
        private sealed class ScanState
        {
            private readonly IProcessHandle _target;
            private readonly CancellationToken _token;

            public ScanState(IProcessHandle target, CancellationToken token)
            {
                _target = target;
                _token = token;
            }

            public List<Chunk> Chunks;
            public ScanPattern Pattern;
            public ScanOptions Options;
            public long TotalBytes;

            public long BytesScanned;
            public int ChunksScanned;
            public int HitCount;
            public bool Truncated;
            public bool ForceStop;

            private readonly object _gate = new object();
            private readonly List<uint> _hits = new List<uint>();
            private int _next = -1;

            public void Cancel()
            {
                ForceStop = true;
            }

            public ScanProgress Snapshot()
            {
                ScanProgress snapshot = new ScanProgress();
                snapshot.BytesScanned = Interlocked.Read(ref BytesScanned);
                snapshot.TotalBytes = TotalBytes;
                snapshot.Hits = Volatile.Read(ref HitCount);
                snapshot.Percent = TotalBytes <= 0 ? 0 : snapshot.BytesScanned * 100.0 / TotalBytes;
                return snapshot;
            }

            public List<uint> TakeHits()
            {
                lock (_gate) { return new List<uint>(_hits); }
            }

            private void AddHits(List<uint> localHits)
            {
                lock (_gate)
                {
                    for (int i = 0; i < localHits.Count; i++)
                    {
                        if (_hits.Count >= Options.MaxHits)
                        {
                            Truncated = true;
                            break;
                        }
                        _hits.Add(localHits[i]);
                        HitCount++;
                    }
                }
                if (Truncated) { ForceStop = true; }
            }

            public void Work()
            {
                MemoryIO memory = new MemoryIO(_target);
                List<uint> localHits = new List<uint>(64);

                while (!ForceStop && !_token.IsCancellationRequested)
                {
                    int index = Interlocked.Increment(ref _next);
                    if (index >= Chunks.Count) { break; }

                    if (Options.StopAfterFirstHit && Volatile.Read(ref HitCount) > 0) { break; }

                    Chunk chunk = Chunks[index];

                    // 读取长度要覆盖跨块匹配，但不能越过区域末尾（越界会整块读失败）
                    ulong want = (ulong)chunk.Length + (ulong)Pattern.Length - 1;
                    ulong room = (ulong)chunk.RegionEnd - chunk.Start;
                    if (want > room) { want = room; }
                    if (want < (ulong)Pattern.Length) { continue; }

                    byte[] buffer = memory.ReadRawBytes((IntPtr)(long)chunk.Start, (int)want);
                    Interlocked.Increment(ref ChunksScanned);

                    if (buffer == null)
                    {
                        // 读失败已由 MemoryIO 记入日志，这里只跳过该块，不中断整体扫描
                        continue;
                    }

                    localHits.Clear();
                    ScanBuffer(buffer, chunk.Length, chunk.Start, Pattern, Options, localHits);
                    Interlocked.Add(ref BytesScanned, buffer.Length);

                    if (localHits.Count > 0) { AddHits(localHits); }
                }
            }
        }

        private static List<Chunk> BuildChunks(IList<MemoryRegion> regions, ScanOptions options)
        {
            List<Chunk> chunks = new List<Chunk>();
            if (regions == null) { return chunks; }

            int chunkSize = options.ChunkSize;
            if (chunkSize < 0x1000) { chunkSize = 0x1000; }

            for (int r = 0; r < regions.Count; r++)
            {
                MemoryRegion region = regions[r];
                if (!region.Readable) { continue; }

                ulong end = region.End;
                ulong cursor = region.Base;
                int guard = 0;

                while (cursor < end && guard < 1048576)
                {
                    guard++;
                    ulong remaining = end - cursor;
                    int length = remaining < (ulong)chunkSize ? (int)remaining : chunkSize;
                    if (length <= 0) { break; }

                    Chunk chunk = new Chunk();
                    chunk.Start = (uint)cursor;
                    chunk.RegionEnd = (uint)end;
                    chunk.Length = length;
                    chunks.Add(chunk);

                    cursor += (ulong)length;
                }
            }
            return chunks;
        }

        private static int CountSkipped(IList<MemoryRegion> regions, ScanOptions options)
        {
            if (regions == null) { return 0; }
            int skipped = 0;
            int chunkSize = options.ChunkSize < 0x1000 ? 0x1000 : options.ChunkSize;
            for (int i = 0; i < regions.Count; i++)
            {
                MemoryRegion region = regions[i];
                if (region.Readable) { continue; }
                skipped += (int)((region.Size + (ulong)chunkSize - 1) / (ulong)chunkSize);
            }
            return skipped;
        }

        /// <summary>在缓冲区内寻找模板；命中地址 = bufferAddress + 块内偏移。</summary>
        private static void ScanBuffer(
            byte[] buffer, int scanLength, uint bufferAddress, ScanPattern pattern, ScanOptions options, List<uint> hits)
        {
            byte?[] bytes = pattern.Bytes;
            int plen = bytes.Length;
            if (buffer.Length < plen) { return; }

            int maxStart = buffer.Length - plen;
            if (scanLength - 1 < maxStart) { maxStart = scanLength - 1; }
            if (maxStart < 0) { return; }

            int anchor = pattern.AnchorIndex;
            byte anchorByte = bytes[anchor].Value;

            int step = options.FourByteAligned ? 4 : 1;
            int start = 0;
            if (step > 1)
            {
                int misalign = (int)(bufferAddress % 4);
                if (misalign != 0) { start = 4 - misalign; }
            }

            for (int i = start; i <= maxStart; i += step)
            {
                if (buffer[i + anchor] != anchorByte) { continue; }

                bool matched = true;
                for (int k = 0; k < plen; k++)
                {
                    byte? expected = bytes[k];
                    if (expected.HasValue && buffer[i + k] != expected.Value)
                    {
                        matched = false;
                        break;
                    }
                }

                if (matched)
                {
                    hits.Add(bufferAddress + (uint)i);
                    if (hits.Count >= MaxHitsPerChunk) { return; }
                }
            }
        }
    }
}
