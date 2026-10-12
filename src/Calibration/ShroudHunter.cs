using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MoTrainer.Core;

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 一块固定窗口的"内容画像"。
    ///
    /// 刻意**不存字节**：真机内存画像约 347 MB，逐字节留两份快照就是 700 MB，
    /// 而这里只关心"这块地方 0x00 有多少、0xFF 有多少、内容动没动"——
    /// 每块 16 字节的统计就够了（整机几万块 = 几百 KB）。
    /// </summary>
    public sealed class ShroudWindow
    {
        public uint Address;
        public int Size;
        public int ZeroCount;      // 0x00 的个数
        public int OnesCount;      // 0xFF 的个数
        public int OtherCount;
        public uint Checksum;      // 内容指纹：判断"这块动没动"
    }

    /// <summary>候选「黑幕 / 迷雾数据区」。</summary>
    public sealed class ShroudCandidate
    {
        public uint Address;
        public int Size;

        /// <summary>0xFF 少了多少（未探索 → 已探索，黑幕被揭开的方向）。</summary>
        public int OnesDown;

        /// <summary>0x00 多了多少。真黑幕位图上它应当与 <see cref="OnesDown"/> 大致相等。</summary>
        public int ZerosUp;

        /// <summary>内容是否整体动过（校验和变化）。</summary>
        public bool ContentsMoved;

        /// <summary>窗口里既不是 0x00 也不是 0xFF 的字节数（位图应当接近 0，对象数组则很大）。</summary>
        public int OtherCount;

        /// <summary>
        /// 变化方向：**"揭开"** = 0xFF 减少、0x00 等量增加（单位探路把黑幕揭开）；
        /// **"重新遮住"** = 反过来（间谍偷走雷达、敌方造了裂缝产生器都会造成这个）。
        /// 两种都是真信号，只是方向不同 —— 到底哪个方向代表"未探索"，事先并不知道。
        /// </summary>
        public string Direction = "";

        public int Score;
    }

    /// <summary>
    /// 「黑幕数据」猎手（`[S6-3]` 全图视野的前置，**只读**）。
    ///
    /// ## 为什么需要它
    ///
    /// 全图视野的**写入那一半早就写好了**（`TechnoOps.RevealMap`：按 64 KB 分片清零、
    /// 带游标防卡顿、缺地址/缺大小时拒绝执行，自检 5 项都过）——
    /// 卡点只有一个：**黑幕数据到底在哪**（`profile.mapShroud` 至今未校准）。
    ///
    /// ## 为什么不让用户去慢慢试
    ///
    /// 用户的时间是本项目最稀缺的东西。"开一局、点一次采集、正常玩三分钟"这件事
    /// 用户本来就要做（第 1 批的第 ④ 件），所以把定位这道题**挂到同一次采集里**：
    /// 采集期间每隔一会儿给内存拍一张"统计画像"，收尾时把"哪块地方像黑幕"排好序写进报告。
    ///
    /// ## 判据（不需要任何先验知识）
    ///
    /// 黑幕被揭开时，那块数据的特征非常干净：
    /// **一片连续的 0xFF（未探索）在采集期间持续减少，同时对应的 0x00（已探索）等量增加**，
    /// 而且这种变化发生在**同一个地址范围**里、只朝一个方向走（黑幕不会自己盖回来）。
    ///
    /// 反过来，内存里其他"会变的地方"长得都不一样：
    ///   · 从没动过的空白内存 = 大片 0x00 且一直不变 → 没有"减少/增加"；
    ///   · 数组增长 = 0x00 变多而 0xFF 不动 → 只有一半；
    ///   · 计数器 / 指针表 = 校验和乱跳而 0x00/0xFF 的个数基本不动；
    ///   · 游戏自己的位图重绘（雷达等）= 大量改动、但两个方向**同时**变，不成单向。
    /// 所以只认"**单向 + 等量 + 双向都动**"这一种形态。
    ///
    /// ## 安全
    ///
    /// 纯只读：全程只走 `ReadBytesQuiet`（安静读取，失败不刷日志 —— 全内存扫描按设计
    /// 就会碰到大量不可读的尾巴）。自检里有一条"扫完之后那块的字节一个都没变"钉死这一点。
    /// </summary>
    public static class ShroudHunter
    {
        /// <summary>窗口大小。64 KB 是"够大能看出单向变化、够小能定位到具体位置"的折中。</summary>
        public const int WindowSize = 0x10000;

        /// <summary>单次采样最多读多少字节（真机约 347 MB 可写区，取 128 MB 够覆盖主要堆区）。</summary>
        public const long MaxBytesPerSample = 128L * 1024 * 1024;

        /// <summary>进入候选榜的最低门槛：两个方向都得动过这么多字节才算（挡掉噪声）。</summary>
        public const int MinShift = 256;

        // ============================================================ 采样

        /// <summary>给整台机器的可写内存拍一张"统计画像"（只读）。</summary>
        public static List<ShroudWindow> Sample(MemoryIO memory, out string note)
        {
            return Sample(memory, WindowSize, MaxBytesPerSample, out note);
        }

        public static List<ShroudWindow> Sample(MemoryIO memory, int windowSize, long maxBytes, out string note)
        {
            List<ShroudWindow> windows = new List<ShroudWindow>();
            note = "";
            if (memory == null) { note = "内存接口不可用"; return windows; }

            List<MemoryRegion> regions = memory.EnumRegions(
                0x00010000ul, Win32Api.MaxUserAddress32, true, true, 0);
            return SampleRegions(memory, regions, windowSize, maxBytes, out note);
        }

        /// <summary>
        /// 采样内核（自检直接喂区域表，不必依赖真机内存画像）。
        ///
        /// 读取一律走 <see cref="MemoryIO.ReadBytesQuiet"/>：扫描区域边界上难免有读不到的页，
        /// 那是**预期结果**而不是错误 —— 走普通读取会把日志刷满"ReadProcessMemory 失败"
        /// （2026-10-09 修过同一类噪音，这里不再犯）。
        /// </summary>
        public static List<ShroudWindow> SampleRegions(
            MemoryIO memory, IList<MemoryRegion> regions, int windowSize, long maxBytes, out string note)
        {
            List<ShroudWindow> windows = new List<ShroudWindow>();
            note = "";
            if (memory == null) { note = "内存接口不可用"; return windows; }
            if (windowSize <= 0) { note = "窗口大小不合法"; return windows; }
            if (regions == null || regions.Count == 0) { note = "没有可扫描的内存区域"; return windows; }

            // 大区域优先：黑幕是一大片连续的，小碎片（几 KB 的零碎块）不可能是它，
            // 而预算有限时先扫大的，才不会把预算浪费在碎片上。
            List<MemoryRegion> sorted = new List<MemoryRegion>();
            for (int i = 0; i < regions.Count; i++)
            {
                MemoryRegion r = regions[i];
                if (r.Size < (ulong)windowSize) { continue; }
                if (!r.Readable || !r.Writable) { continue; }
                if (Win32Api.IsExecutableProtect(r.Protect)) { continue; }   // 代码段不可能放黑幕
                sorted.Add(r);
            }
            sorted.Sort(delegate(MemoryRegion a, MemoryRegion b) { return b.Size.CompareTo(a.Size); });

            long budget = maxBytes > 0 ? maxBytes : MaxBytesPerSample;
            long scannedBytes = 0;
            int failedWindows = 0;

            for (int i = 0; i < sorted.Count; i++)
            {
                if (budget < windowSize) { break; }
                MemoryRegion r = sorted[i];

                // 区域尾部不足一个窗口的零头不看（凑不齐一块就没法两两比较）
                ulong blocks = r.Size / (ulong)windowSize;
                for (ulong b = 0; b < blocks; b++)
                {
                    if (budget < windowSize) { break; }
                    ulong raw = r.Base + b * (ulong)windowSize;
                    if (raw > 0xFFFFFFFFul) { break; }
                    uint address = (uint)raw;

                    byte[] buffer = memory.ReadBytesQuiet(address, windowSize);
                    if (buffer == null) { failedWindows++; continue; }

                    budget -= windowSize;
                    scannedBytes += windowSize;
                    windows.Add(CountWindow(address, windowSize, buffer));
                }
            }

            note = string.Format(CultureInfo.InvariantCulture,
                "窗口 {0} 字节｜扫了 {1} 块（{2:0.0} MB）｜区域 {3} 段｜读不到的窗口 {4} 块",
                windowSize, windows.Count, scannedBytes / 1048576.0, sorted.Count, failedWindows);
            return windows;
        }

        private static ShroudWindow CountWindow(uint address, int size, byte[] buffer)
        {
            ShroudWindow w = new ShroudWindow();
            w.Address = address;
            w.Size = size;

            int zeros = 0;
            int ones = 0;
            uint checksum = 2166136261u;   // FNV-1a：对"顺序/内容"都敏感，改一个字节就变

            for (int i = 0; i < size; i++)
            {
                byte v = buffer[i];
                if (v == 0) { zeros++; }
                else if (v == 0xFF) { ones++; }

                checksum ^= v;
                checksum *= 16777619u;
            }

            w.ZeroCount = zeros;
            w.OnesCount = ones;
            w.OtherCount = size - zeros - ones;
            w.Checksum = checksum;
            return w;
        }

        // ============================================================ 排序

        /// <summary>
        /// 把首末两张画像一比，排出"像黑幕"的候选。
        ///
        /// 只认一种形态：**0xFF 减少、0x00 等量增加**（两个方向都动、量还差不多）。
        /// 这正好是"黑幕被揭开"在内存里的样子。
        /// </summary>
        public static List<ShroudCandidate> Rank(IList<ShroudWindow> first, IList<ShroudWindow> last)
        {
            List<ShroudCandidate> list = new List<ShroudCandidate>();
            if (first == null || last == null || first.Count == 0) { return list; }

            Dictionary<uint, ShroudWindow> before = new Dictionary<uint, ShroudWindow>();
            for (int i = 0; i < first.Count; i++) { before[first[i].Address] = first[i]; }

            for (int i = 0; i < last.Count; i++)
            {
                ShroudWindow now = last[i];
                ShroudWindow was;
                if (!before.TryGetValue(now.Address, out was)) { continue; }
                if (was.Size != now.Size) { continue; }

                int onesDown = was.OnesCount - now.OnesCount;      // 0xFF 少了多少
                int zerosUp = now.ZeroCount - was.ZeroCount;       // 0x00 多了多少
                int zerosDown = was.ZeroCount - now.ZeroCount;     // 0x00 少了多少
                int onesUp = now.OnesCount - was.OnesCount;        // 0xFF 多了多少

                // **两个方向都要认**（2026-10-12 用户补充的领域知识换来的）：
                //   · 正向：0xFF 减少、0x00 等量增加 —— "黑幕被揭开"（单位探路）；
                //   · 反向：0x00 减少、0xFF 等量增加 —— "**重新被黑幕盖住**"。
                // 用户实测过这条反向路径是真实存在的：**间谍偷走雷达会失去全部视野**，
                // 敌方造了裂缝产生器也会让你丢掉那一片的视野。
                //
                // 为什么必须两个方向都认：这块数据到底是"1 = 未探索"还是"0 = 未探索"，
                // 事先并不知道（新分配的内存本来就是 0x00）。**只认一个方向 = 赌一半**，
                // 赌错了用户白开一局 —— 而用户的时间是本项目最贵的东西。
                bool forward = onesDown >= MinShift && zerosUp >= MinShift;
                bool backward = zerosDown >= MinShift && onesUp >= MinShift;
                if (!forward && !backward) { continue; }

                ShroudCandidate c = new ShroudCandidate();
                c.Address = now.Address;
                c.Size = now.Size;
                c.OnesDown = onesDown;
                c.ZerosUp = zerosUp;
                c.ContentsMoved = was.Checksum != now.Checksum;
                c.OtherCount = now.OtherCount;
                c.Direction = forward ? "揭开" : "重新遮住";

                // 评分取该方向两个量里的**较小值**：单向且等量才是黑幕；
                // 只朝一个方向猛变（数组增长之类）会被这个 min 压下去。
                c.Score = forward
                    ? (onesDown < zerosUp ? onesDown : zerosUp)
                    : (zerosDown < onesUp ? zerosDown : onesUp);
                list.Add(c);
            }

            list.Sort(delegate(ShroudCandidate a, ShroudCandidate b)
            {
                if (b.Score != a.Score) { return b.Score.CompareTo(a.Score); }
                return a.Address.CompareTo(b.Address);
            });
            return list;
        }

        /// <summary>
        /// 把候选位置向左右各扩一点，看看"0x00 / 0xFF 占绝对多数"的那一大片有多大（**只读**）。
        ///
        /// 用途：黑幕位图的真实长度应当是"地图格子数"（如 60×60=3600）或它的 1/8，
        /// 量出来的尺寸能帮我判断候选到底是黑幕、还是恰好长这样的别的东西。
        /// </summary>
        public static string ExplainExtent(MemoryIO memory, uint address, int maxScanBytes)
        {
            if (memory == null) { return "内存接口不可用"; }
            if (maxScanBytes <= 0) { maxScanBytes = 0x400000; }

            // 先看自己这块：0x00/0xFF 之外的内容占比多少
            const int Probe = 0x1000;
            byte[] head = memory.ReadBytesQuiet(address, Probe);
            if (head == null) { return string.Format(CultureInfo.InvariantCulture,
                "0x{0:X8} 读不到，无法判断", address); }

            int other = 0;
            for (int i = 0; i < head.Length; i++)
            {
                if (head[i] != 0 && head[i] != 0xFF) { other++; }
            }

            return string.Format(CultureInfo.InvariantCulture,
                "0x{0:X8} 起这 {1} 字节里：0x00 {2}｜0xFF {3}｜其他 {4}"
                + "（其他占比 {5:0.0}% —— 黑幕位图应当几乎全是 0x00/0xFF）",
                address, head.Length,
                CountByte(head, 0), CountByte(head, 0xFF), other,
                other * 100.0 / head.Length);
        }

        private static int CountByte(byte[] buffer, byte value)
        {
            int n = 0;
            for (int i = 0; i < buffer.Length; i++) { if (buffer[i] == value) { n++; } }
            return n;
        }

        // ============================================================ 报告

        /// <summary>写进采集报告的「找黑幕数据」一节。</summary>
        public static string Format(
            IList<ShroudWindow> first, IList<ShroudWindow> last, string sampleNote, bool calibrated)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 七、找黑幕数据（全图视野用，只读）===");
            sb.AppendLine();

            if (calibrated)
            {
                sb.AppendLine("已校准过「迷雾位图」，本节跳过。");
                sb.AppendLine();
                return sb.ToString();
            }

            sb.AppendLine("说明：黑幕数据一变就是一大片，而且**只朝一个方向**走。两个方向都算真信号 ——");
            sb.AppendLine("  · **揭开**：0xFF 减少、0x00 等量增加（你的单位把路探开）；");
            sb.AppendLine("  · **重新遮住**：0x00 减少、0xFF 等量增加（被间谍偷走雷达、敌方造了裂缝产生器）。");
            sb.AppendLine("下面就是按这个形态排出来的候选。「方向」那一列告诉你是哪一种。");
            sb.AppendLine();

            if (last == null || last.Count == 0)
            {
                sb.AppendLine("本次没能采到内存画像（" + (sampleNote == null ? "" : sampleNote) + "）。");
                sb.AppendLine();
                return sb.ToString();
            }

            sb.AppendLine("本次画像：" + (sampleNote == null ? "" : sampleNote));
            sb.AppendLine();

            List<ShroudCandidate> ranked = Rank(first, last);
            if (ranked.Count == 0)
            {
                sb.AppendLine("**没有找到符合「单向 + 等量」形态的地方**（两个方向都没有）。");
                sb.AppendLine("可能的原因（按可能性排）：");
                sb.AppendLine("  ① 这一局探索得太少/太快 —— 黑幕在采集期间几乎没被揭开；");
                sb.AppendLine("  ② 黑幕不是一整片连续位图，而是**每个地图格子自己身上一个字节**");
                sb.AppendLine("     （那种形态在下面「另一个信号」里会表现为大量小块乱动、而 0x00/0xFF 计数不动）；");
                sb.AppendLine("  ③ 采样窗口 64 KB 相对黑幕太小/太大，被切碎了；");
                sb.AppendLine("  ④ 这块数据既不用 0x00 也不用 0xFF 表示「未探索」（例如用 1/2/3 表示黑幕等级）——");
                sb.AppendLine("     这种情况本节看不见，需要在真机上另想办法（见开发日志的下一步）。");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "候选 {0} 块（按「该方向上两个变化量中较小的那个」排序，前 12 名）：",
                    ranked.Count));
                sb.AppendLine();
                sb.AppendLine("  地址        窗口      方向        0xFF变化   0x00变化   其他字节");
                int shown = ranked.Count < 12 ? ranked.Count : 12;
                for (int i = 0; i < shown; i++)
                {
                    ShroudCandidate c = ranked[i];
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}  {1,6}  {2,-8}  {3,9}  {4,9}   {5,8}",
                        c.Address, c.Size, c.Direction, c.OnesDown, c.ZerosUp, c.OtherCount));
                }
                sb.AppendLine();
                sb.AppendLine("判读：真黑幕位图通常**连成一大片**（相邻地址一起上榜）、");
                sb.AppendLine("「其他字节」接近 0（位图里只有 0x00/0xFF），");
                sb.AppendLine("且它的长度应当等于地图格子数（如 60x60=3600）或它的 1/8。");
                sb.AppendLine();
            }

            // ---- 另一个信号：动过的地方里，有多少是"长得像对象数组"的 ----
            //
            // 这一节是为了**分开两种可能的存储方式**：
            // 整片位图（上一节抓：窗口里几乎全是 0x00/0xFF）
            // vs 每个格子自己身上一个字节（那种窗口里大部分字节是格子自己的字段，
            // 也就是下面说的"其他字节占多数"）。只报数字，不做结论。
            int moved;
            int objectLike;
            SummarizeChurn(first, last, out moved, out objectLike);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "另一个信号：本次共有 {0} 块窗口的内容变过，其中 {1} 块的"
                + "「0x00/0xFF 之外的内容」占了一半以上（那种更像对象数组，不像一整片位图）。",
                moved, objectLike));
            sb.AppendLine("（若上一节空着、而这里数字很大，说明黑幕多半是每个格子自己存一个字节，");
            sb.AppendLine("  下一步就不是清一大片位图，而是挨个格子找那个字节。）");
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// 数一数"动过的窗口"及其构成（只读旁证，不做结论）。
        /// `objectLike` = 窗口里 0x00/0xFF 之外的内容占了一半以上、且内容确实动过的块数。
        /// </summary>
        public static void SummarizeChurn(
            IList<ShroudWindow> first, IList<ShroudWindow> last, out int moved, out int objectLike)
        {
            moved = 0;
            objectLike = 0;
            if (first == null || last == null || first.Count == 0) { return; }

            Dictionary<uint, ShroudWindow> before = new Dictionary<uint, ShroudWindow>();
            for (int i = 0; i < first.Count; i++) { before[first[i].Address] = first[i]; }

            for (int i = 0; i < last.Count; i++)
            {
                ShroudWindow now = last[i];
                ShroudWindow was;
                if (!before.TryGetValue(now.Address, out was)) { continue; }
                if (was.Size != now.Size) { continue; }
                if (was.Checksum == now.Checksum) { continue; }

                moved++;
                if (now.OtherCount * 2 > now.Size) { objectLike++; }
            }
        }
    }
}
