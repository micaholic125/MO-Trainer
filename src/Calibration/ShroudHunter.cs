using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        /// <summary>
        /// 这一块里出现最多的前几个**具体字节值**及其个数（含 0x00 / 0xFF，见 `CountWindow`）。
        ///
        /// 为什么不能只看 0x00 / 0xFF 两个数：**我（AI）推测**这块数据未必用 0x00/0xFF 两值表示 ——
        /// 这类引擎的"看见没看见"常用几个小序号（0/1/2/3）表示浓淡，
        /// 若真如此，探开时变的是 `2 → 0` 之类，**0xFF 一个都不会动**，只认 0x00/0xFF 的判据完全看不见它。
        /// （**这只是我的推测，没有任何证据**：用户补充的是"失去视野会重新被黑幕遮盖"，
        ///   并没有说过什么"等级"。2026-10-12 我一度把自己的推测写成"用户说过"，
        ///   被用户当场指出 —— 引用别人的话必须一字不差，见开发日志附录六。）
        /// 记下"前几个值"之后，**任意两种值**之间的此消彼长都能被抓到，不必事先押注是哪两种。
        /// </summary>
        public byte[] TopValues = new byte[TopValueCount];

        public int[] TopCounts = new int[TopValueCount];

        /// <summary>每块记前几个值。4 个够用：这类数据通常只有两三种取值。</summary>
        public const int TopValueCount = 4;
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

        /// <summary>被消耗掉的那种值 / 新出现的那种值（例如 0xFF → 0x00）。</summary>
        public byte ValueFrom;
        public byte ValueTo;

        /// <summary>ValueFrom 少了多少、ValueTo 多了多少。</summary>
        public int FromDown;
        public int ToUp;

        /// <summary>0x00 / 0xFF 这一对专用（自检与报告用；值对不是它们时只作参考）。</summary>
        /// <summary>
        /// 变化方向。0xFF→0x00 记「揭开」、0x00→0xFF 记「重新遮住」，
        /// 其余（例如 0x02→0x00 这种"小序号"记法）如实写 "0x02 → 0x00"。
        /// </summary>
        public string Direction = "";

        /// <summary>目标值在各张画像里的个数（看它是"一路慢慢涨"还是"某一张里跳一次"）。</summary>
        public string Series = "";
        public int Increases;      // 递增的段数
        public int Comparisons;    // 参与比较的段数

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

        /// <summary>
        /// 单次采样最多读多少字节。
        ///
        /// 2026-10-12 从 128 MB 放开到 512 MB：用户第 2 批那次的报告显示只扫了 128 MB
        /// （真机可写区约 200 段、合计约 347 MB）—— **万一黑幕数据正好落在没扫到的那部分，就白跑一局**。
        /// 读整机的代价并不高（内存读取本身是 GB/s 级），真正贵的是用户的那三分钟。
        /// </summary>
        public const long MaxBytesPerSample = 512L * 1024 * 1024;

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
            int[] histogram = new int[256];      // 复用一份直方图缓冲（见 CountWindow）

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
                    windows.Add(CountWindow(address, windowSize, buffer, histogram));
                }
            }

            note = string.Format(CultureInfo.InvariantCulture,
                "窗口 {0} 字节｜扫了 {1} 块（{2:0.0} MB）｜区域 {3} 段｜读不到的窗口 {4} 块",
                windowSize, windows.Count, scannedBytes / 1048576.0, sorted.Count, failedWindows);
            return windows;
        }

        private static ShroudWindow CountWindow(uint address, int size, byte[] buffer, int[] histogram)
        {
            ShroudWindow w = new ShroudWindow();
            w.Address = address;
            w.Size = size;

            // 直方图缓冲区由调用方复用（每块都 new 一个 256 项的数组会拖垮 GC）
            for (int i = 0; i < 256; i++) { histogram[i] = 0; }

            uint checksum = 2166136261u;   // FNV-1a：对"顺序/内容"都敏感，改一个字节就变
            for (int i = 0; i < size; i++)
            {
                byte v = buffer[i];
                histogram[v]++;

                checksum ^= v;
                checksum *= 16777619u;
            }

            w.ZeroCount = histogram[0];
            w.OnesCount = histogram[0xFF];
            w.OtherCount = size - w.ZeroCount - w.OnesCount;
            w.Checksum = checksum;

            // 取出现最多的前几个值（并列时取数值小的，保证同样的输入产出同样的顺序）
            for (int slot = 0; slot < ShroudWindow.TopValueCount; slot++)
            {
                int bestValue = -1;
                int bestCount = 0;
                for (int v = 0; v < 256; v++)
                {
                    if (histogram[v] > bestCount) { bestCount = histogram[v]; bestValue = v; }
                }
                if (bestValue < 0 || bestCount == 0) { break; }

                w.TopValues[slot] = (byte)bestValue;
                w.TopCounts[slot] = bestCount;
                histogram[bestValue] = 0;      // 取走，下一轮找次多的
            }
            return w;
        }

        // ============================================================ 排序

        /// <summary>两两比较（自检用；正式路径见下面的多样本重载）。</summary>
        public static List<ShroudCandidate> Rank(IList<ShroudWindow> first, IList<ShroudWindow> last)
        {
            List<IList<ShroudWindow>> samples = new List<IList<ShroudWindow>>();
            samples.Add(first);
            samples.Add(last);
            return Rank(samples);
        }

        /// <summary>
        /// 把**整段采集**的若干张画像排成候选。
        ///
        /// 判据（2026-10-12 第 2 批之后重写，三处都是那一局换来的）：
        ///
        /// ① **任意两种值之间的此消彼长**，不再只认 0x00/0xFF ——
        ///    **我推测**引擎可能用小序号（0/1/2/3）而不是 0x00/0xFF 表示浓淡 ——
        ///    若真如此，只认 0xFF 的判据完全看不见它。（这是推测，不是用户说的，见字段注释里的记档。）
        /// ② 首末两张定"变了多少"，**中间每一张**用来判"是不是一路慢慢变的"。
        ///    这一条是分水岭：黑幕是**随着单位探路一点点积累**的；
        ///    而「游戏新建了一个对象」是**某一张里一下子**变的。只看首末根本分不开这两种
        ///    —— 第 2 批那次的六个候选（其中两行数字一模一样）就是被这一条怀疑的。
        /// ③ 仍然要求"两种值此消彼长、量还差不多"，把"只朝一个方向猛变"（数组增长之类）排掉。
        /// </summary>
        public static List<ShroudCandidate> Rank(IList<IList<ShroudWindow>> samples)
        {
            List<ShroudCandidate> list = new List<ShroudCandidate>();
            if (samples == null || samples.Count < 2) { return list; }

            IList<ShroudWindow> first = samples[0];
            IList<ShroudWindow> last = samples[samples.Count - 1];
            if (first == null || last == null || first.Count == 0) { return list; }

            Dictionary<uint, ShroudWindow> before = IndexByAddress(first);

            for (int i = 0; i < last.Count; i++)
            {
                ShroudWindow now = last[i];
                ShroudWindow was;
                if (!before.TryGetValue(now.Address, out was)) { continue; }
                if (was.Size != now.Size) { continue; }

                // 候选的"值对"来自首末两张各自的前几个值 —— 两边都考虑的并集，
                // 因为被消耗掉的那种值在**末张**里可能已经掉出前几名了。
                for (int a = 0; a < ShroudWindow.TopValueCount; a++)
                {
                    for (int b = 0; b < ShroudWindow.TopValueCount; b++)
                    {
                        byte fromValue = was.TopValues[a];
                        byte toValue = now.TopValues[b];
                        if (fromValue == toValue) { continue; }
                        if (a > 0 && was.TopCounts[a] == 0) { continue; }
                        if (b > 0 && now.TopCounts[b] == 0) { continue; }

                        int fromDown = CountOf(was, fromValue) - CountOf(now, fromValue);
                        int toUp = CountOf(now, toValue) - CountOf(was, toValue);
                        if (fromDown < MinShift || toUp < MinShift) { continue; }

                        ShroudCandidate c = new ShroudCandidate();
                        c.Address = now.Address;
                        c.Size = now.Size;
                        c.ValueFrom = fromValue;
                        c.ValueTo = toValue;
                        c.FromDown = fromDown;
                        c.ToUp = toUp;
                        c.OnesDown = CountOf(was, 0xFF) - CountOf(now, 0xFF);
                        c.ZerosUp = CountOf(now, 0x00) - CountOf(was, 0x00);
                        c.OtherCount = now.OtherCount;
                        c.ContentsMoved = was.Checksum != now.Checksum;
                        c.Direction = DescribeDirection(fromValue, toValue);

                        int increases;
                        int comparisons;
                        c.Series = BuildSeries(samples, now.Address, toValue, out increases, out comparisons);
                        c.Increases = increases;
                        c.Comparisons = comparisons;

                        // 评分取两个量里的**较小值**：单向且等量才是黑幕。
                        c.Score = fromDown < toUp ? fromDown : toUp;
                        list.Add(c);
                        break;      // 一块只收一个值对（前几个值是按个数排的，第一个命中的就是主对）
                    }
                }
            }

            list.Sort(delegate(ShroudCandidate a, ShroudCandidate b)
            {
                if (b.Score != a.Score) { return b.Score.CompareTo(a.Score); }
                return a.Address.CompareTo(b.Address);
            });
            return list;
        }

        /// <summary>
        /// 「一路慢慢变」榜：按"递增段数"排（并列时按变化量）。
        ///
        /// 为什么要有这一榜：真黑幕是**随着探路一点点积累**的，而对象新建是**一下子**变的。
        /// 首末两张看不出区别，中间每一张能 —— 这一榜就是专门给这个用的。
        /// </summary>
        public static List<ShroudCandidate> RankByProgress(IList<IList<ShroudWindow>> samples)
        {
            List<ShroudCandidate> list = Rank(samples);
            List<ShroudCandidate> sustained = new List<ShroudCandidate>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Comparisons >= 2 && list[i].Increases >= 2) { sustained.Add(list[i]); }
            }
            sustained.Sort(delegate(ShroudCandidate a, ShroudCandidate b)
            {
                if (b.Increases != a.Increases) { return b.Increases.CompareTo(a.Increases); }
                if (b.Comparisons != a.Comparisons) { return b.Comparisons.CompareTo(a.Comparisons); }
                return b.Score.CompareTo(a.Score);
            });
            return sustained;
        }

        private static Dictionary<uint, ShroudWindow> IndexByAddress(IList<ShroudWindow> windows)
        {
            Dictionary<uint, ShroudWindow> map = new Dictionary<uint, ShroudWindow>();
            for (int i = 0; i < windows.Count; i++) { map[windows[i].Address] = windows[i]; }
            return map;
        }

        /// <summary>这一块里某个值的个数（不在前几名里就按 0 算 —— 说明它在这块里很少）。</summary>
        private static int CountOf(ShroudWindow window, byte value)
        {
            if (value == 0) { return window.ZeroCount; }
            if (value == 0xFF) { return window.OnesCount; }
            for (int i = 0; i < ShroudWindow.TopValueCount; i++)
            {
                if (window.TopValues[i] == value && window.TopCounts[i] > 0) { return window.TopCounts[i]; }
            }
            return 0;
        }

        private static string DescribeDirection(byte from, byte to)
        {
            if (from == 0xFF && to == 0x00) { return "揭开"; }
            if (from == 0x00 && to == 0xFF) { return "重新遮住"; }
            return string.Format(CultureInfo.InvariantCulture,
                "0x{0:X2} → 0x{1:X2}", from, to);
        }

        /// <summary>
        /// 把"目标值"在各张画像里的个数连成一串，并数出**递增了几段**。
        /// 一个对象新建 → 只在某一段里跳一次（递增 1）；一路探路 → 段段都在涨。
        /// </summary>
        private static string BuildSeries(
            IList<IList<ShroudWindow>> samples, uint address, byte value,
            out int increases, out int comparisons)
        {
            increases = 0;
            comparisons = 0;
            StringBuilder sb = new StringBuilder();

            int previous = -1;
            for (int s = 0; s < samples.Count; s++)
            {
                IList<ShroudWindow> shot = samples[s];
                if (shot == null) { continue; }

                int count = -1;
                for (int i = 0; i < shot.Count; i++)
                {
                    if (shot[i].Address == address) { count = CountOf(shot[i], value); break; }
                }
                if (count < 0) { continue; }

                if (sb.Length > 0) { sb.Append(" → "); }
                sb.Append(count.ToString(CultureInfo.InvariantCulture));

                if (previous >= 0)
                {
                    comparisons++;
                    if (count > previous) { increases++; }
                }
                previous = count;
            }

            // 太长就只留头尾（报告里一行放不下几百个数）
            string text = sb.ToString();
            if (text.Length > 120)
            {
                text = text.Substring(0, 60) + " … " + text.Substring(text.Length - 50);
            }
            return text;
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
        /// <summary>
        /// 把候选窗口的**原始字节**存成文件（**只读**），并画一张"长相图"。
        ///
        /// 为什么非要有这一步：报告里的统计只能说明"这块在变"，说明不了"它长什么样"。
        /// 而黑幕位图有一个**肉眼可辨的形状** —— 一大片同值里嵌着一块**连通的**"已探索"区域；
        /// 对象数组、字符串缓冲区、渲染缓冲区长的是别的样子。
        /// 2026-10-12 两次采集都因为"只有数字、没有形状"而无法定论，
        /// 所以这一步**放进采集收尾自动做**：用户不必为此多开一局游戏。
        ///
        /// 输出：`reports\shrouddump-日期-时间.txt`（每个候选一段：值分布 + 最长游程 + 字符图）。
        /// 字符图的规则：该窗口里**最多的那个值**画成 `.`、**次多的**画成 `#`、其余画成 `+`
        /// —— 这样"一大片 + 一块连通区域"会直接显形。
        /// </summary>
        public static string DumpTopCandidates(
            MemoryIO memory, IList<IList<ShroudWindow>> samples, string folder,
            uint moduleBase, uint moduleSize, int maxCandidates, out string note)
        {
            note = "";
            if (memory == null) { note = "内存接口不可用"; return ""; }
            if (samples == null || samples.Count < 2) { note = "快照不足两张"; return ""; }
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                note = "报告目录不存在：" + folder;
                return "";
            }

            List<ShroudCandidate> ranked = RankByProgress(samples);
            if (ranked.Count == 0) { ranked = Rank(samples); }
            if (ranked.Count == 0) { note = "没有候选可存"; return ""; }

            int count = ranked.Count < maxCandidates ? ranked.Count : maxCandidates;
            string path = Path.Combine(folder, string.Format(CultureInfo.InvariantCulture,
                "shrouddump-{0:yyyyMMdd-HHmmss}.txt", DateTime.Now));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 黑幕候选现场转储（只读） ===");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("说明：每个候选取它那一整块窗口（" + WindowSize + " 字节）的原始内容；");
            sb.AppendLine("      字符图里 `.` = 这块里最多的那个值，`#` = 次多的，`+` = 其余。");
            sb.AppendLine("      黑幕位图应当长成「一大片 `.` 里嵌着一块连通的 `#` 区域」。");
            sb.AppendLine();

            int saved = 0;
            for (int i = 0; i < count; i++)
            {
                ShroudCandidate c = ranked[i];
                byte[] bytes = memory.ReadBytesQuiet(c.Address, c.Size);
                if (bytes == null)
                {
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "--- [{0}] 0x{1:X8} 读不到（地址可能已经失效） ---", i + 1, c.Address));
                    sb.AppendLine();
                    continue;
                }
                saved++;

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "--- [{0}] 0x{1:X8}　变化 {2}｜{3}｜递增 {4}/{5} 段 ---",
                    i + 1, c.Address, c.Direction,
                    InModule(c.Address, moduleBase, moduleSize) ? "在游戏主程序范围内" : "在堆/动态内存里",
                    c.Increases, c.Comparisons));
                sb.AppendLine("  各段个数：" + c.Series);
                sb.AppendLine("  值分布（前 6 名）：" + DescribeTopValues(bytes, 6));
                sb.AppendLine("  最长同值游程（前 3 名）：" + DescribeRuns(bytes, 3));
                sb.AppendLine("  字符图（每行 64 字节，前 64 行）：");
                AppendAsciiMap(sb, bytes, 64, 64);
                sb.AppendLine();
            }

            try
            {
                File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                note = "写转储文件失败：" + ex.Message;
                return "";
            }

            note = string.Format(CultureInfo.InvariantCulture,
                "候选原始字节已存成 {0}（列了 {1} 个，读到 {2} 个）", path, count, saved);
            return path;
        }

        private static bool InModule(uint address, uint moduleBase, uint moduleSize)
        {
            return moduleSize != 0 && address >= moduleBase && address < moduleBase + moduleSize;
        }

        /// <summary>前几名值的个数。</summary>
        private static string DescribeTopValues(byte[] bytes, int top)
        {
            int[] histogram = new int[256];
            for (int i = 0; i < bytes.Length; i++) { histogram[bytes[i]]++; }

            StringBuilder sb = new StringBuilder();
            for (int slot = 0; slot < top; slot++)
            {
                int best = -1;
                int bestCount = 0;
                for (int v = 0; v < 256; v++)
                {
                    if (histogram[v] > bestCount) { bestCount = histogram[v]; best = v; }
                }
                if (best < 0 || bestCount == 0) { break; }
                if (sb.Length > 0) { sb.Append("｜"); }
                sb.Append(string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X2} 出现 {1} 次", best, bestCount));
                histogram[best] = 0;
            }
            return sb.ToString();
        }

        /// <summary>最长同值游程（前几名）—— "一大片"到底有多长，一眼能看出来。</summary>
        private static string DescribeRuns(byte[] bytes, int top)
        {
            List<string> runs = new List<string>();
            int bestLength = 0;
            int start = 0;
            byte value = bytes.Length > 0 ? bytes[0] : (byte)0;
            for (int i = 1; i <= bytes.Length; i++)
            {
                if (i < bytes.Length && bytes[i] == value) { continue; }
                int length = i - start;
                if (length > bestLength)
                {
                    bestLength = length;
                    runs.Add(string.Format(CultureInfo.InvariantCulture,
                        "偏移 0x{0:X} 起 0x{1:X2} × {2}", start, value, length));
                    if (runs.Count > 64) { runs.RemoveAt(0); }
                }
                if (i < bytes.Length) { start = i; value = bytes[i]; }
            }

            runs.Reverse();      // 只会在"更长"时才追加，所以倒过来就是最长的在前
            int show = runs.Count < top ? runs.Count : top;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < show; i++)
            {
                if (i > 0) { sb.Append("｜"); }
                sb.Append(runs[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 画字符图：最多的值 → `.`、次多的 → `#`、其余 → `+`。**不做任何结论**，
        /// 只是把形状摊开给读报告的人看。
        /// </summary>
        private static void AppendAsciiMap(StringBuilder sb, byte[] bytes, int columns, int maxRows)
        {
            int[] histogram = new int[256];
            for (int i = 0; i < bytes.Length; i++) { histogram[bytes[i]]++; }

            int first = -1;
            int second = -1;
            for (int slot = 0; slot < 2; slot++)
            {
                int best = -1;
                int bestCount = 0;
                for (int v = 0; v < 256; v++)
                {
                    if (histogram[v] > bestCount) { bestCount = histogram[v]; best = v; }
                }
                if (best < 0) { break; }
                if (slot == 0) { first = best; } else { second = best; }
                histogram[best] = 0;
            }

            int rows = (bytes.Length + columns - 1) / columns;
            if (rows > maxRows) { rows = maxRows; }
            for (int r = 0; r < rows; r++)
            {
                sb.Append("    ");
                for (int c = 0; c < columns; c++)
                {
                    int index = r * columns + c;
                    if (index >= bytes.Length) { break; }
                    byte v = bytes[index];
                    if (v == first) { sb.Append('.'); }
                    else if (v == second) { sb.Append('#'); }
                    else { sb.Append('+'); }
                }
                sb.AppendLine();
            }
        }

        /// <summary>写进采集报告的「七、找黑幕数据」一节。</summary>
        public static string Format(
            IList<IList<ShroudWindow>> samples, string sampleNote, bool calibrated)
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

            sb.AppendLine("说明：黑幕数据一变就是一大片，而且**只朝一个方向**走。**任意两种值的此消彼长都算**");
            sb.AppendLine("（不再假设「未探索」一定是 0xFF：这类引擎也可能用小序号（2、3…）表示浓淡 ——");
            sb.AppendLine("  这一条是**我（AI）的推测**，不是你说过的，写在这里以便你判断该信几分。）");
            sb.AppendLine("更要紧的是「递增段数」那一列：**黑幕是随着探路一点点积累的**，");
            sb.AppendLine("而「游戏新建了一个对象」是**某一张里一下子**变的 —— 这一列就是拿来分开这两种的。");
            sb.AppendLine();

            if (samples == null || samples.Count == 0 || samples[samples.Count - 1] == null
                || samples[samples.Count - 1].Count == 0)
            {
                sb.AppendLine("本次没能采到内存画像（" + (sampleNote == null ? "" : sampleNote) + "）。");
                sb.AppendLine();
                return sb.ToString();
            }

            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "本次画像：{0}｜共 {1} 张（首末两张定「变了多少」，中间每一张判「是不是一路慢慢变」）",
                sampleNote == null ? "" : sampleNote, samples.Count));
            sb.AppendLine();

            List<ShroudCandidate> ranked = Rank(samples);
            if (ranked.Count == 0)
            {
                sb.AppendLine("**没有找到符合「两种值此消彼长」的地方。**");
                sb.AppendLine("可能的原因（按可能性排）：");
                sb.AppendLine("  ① 这一局探索得太少/太快 —— 黑幕在采集期间几乎没被揭开；");
                sb.AppendLine("  ② 黑幕不是一整片连续位图，而是**每个地图格子自己身上一个字节**；");
                sb.AppendLine("  ③ 采样窗口 64 KB 相对黑幕太小/太大，被切碎了；");
                sb.AppendLine("  ④ 变化的两种值都很「小众」，没能挤进每块的前 4 名（那说明这块地方太杂，不像位图）。");
                sb.AppendLine();
            }
            else
            {
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "候选 {0} 块（按变化量排，前 10 名）：", ranked.Count));
                sb.AppendLine();
                sb.AppendLine("  地址        值变化        变化量   递增段数   其他字节");
                int shown = ranked.Count < 10 ? ranked.Count : 10;
                for (int i = 0; i < shown; i++)
                {
                    ShroudCandidate c = ranked[i];
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}  {1,-12}  {2,7}   {3,2}/{4,-2}     {5,7}",
                        c.Address, c.Direction, c.Score, c.Increases, c.Comparisons, c.OtherCount));
                }
                sb.AppendLine();
                sb.AppendLine("  （「值变化」列：0xFF→0x00 = 揭开，0x00→0xFF = 重新遮住，其余如 0x02→0x00 是「小序号」记法。）");
                sb.AppendLine();

                sb.AppendLine();
                sb.AppendLine("**一路慢慢变**的候选（按「递增段数」排，前 8 名）—— **先看这一榜**：");
                List<ShroudCandidate> sustained = RankByProgress(samples);
                if (sustained.Count == 0)
                {
                    sb.AppendLine("  （没有：所有候选都只在一两张里跳变过 —— 那更像对象新建/释放，不像探路。）");
                }
                else
                {
                    int shown2 = sustained.Count < 8 ? sustained.Count : 8;
                    for (int i = 0; i < shown2; i++)
                    {
                        ShroudCandidate c = sustained[i];
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                            "  0x{0:X8}  {1}｜递增 {2}/{3} 段｜各段个数：{4}",
                            c.Address, c.Direction, c.Increases, c.Comparisons, c.Series));
                    }
                }
                sb.AppendLine();
                sb.AppendLine("判读：真黑幕位图通常**长度等于地图格子数**（如 60x60=3600）或它的 1/8，");
                sb.AppendLine("且在「一路慢慢变」那一榜里靠前。");
                sb.AppendLine();
            }

            // ---- 旁证：动过的地方里，有多少是"长得像对象数组"的 ----
            IList<ShroudWindow> first = samples[0];
            IList<ShroudWindow> last = samples[samples.Count - 1];
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
