using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MoTrainer.Core;

namespace MoTrainer.Tools
{
    /// <summary>
    /// 「黑幕形状」扫描器 —— **一次性开发工具，不参与产品编译**（放在 `tools\` 下，`build.ps1` 只编 `src\`）。
    ///
    /// ## 为什么要另做一个
    ///
    /// 前三次采集用的都是**"哪块地方在变"**的判据（`ShroudHunter`）。那套判据能缩小范围，
    /// 但 2026-10-12 第二次转储显示：排在前面的候选全是"0x00 为主 + 一堆杂值"的数据表/缓冲区，
    /// **没有一个是"一大片同值里嵌着一块连通区域"** —— 也就是没有一个是位图。
    ///
    /// 于是换一个**不依赖时间**的判据：直接找"**长得像地图**"的内存。
    /// 地图数据的特征很硬：
    ///   · 只有**两种值占绝对多数**（未探索 / 已探索）；
    ///   · 而且是**成片**的 —— 随机数据、压缩数据、渲染缓冲的"平均连续长度"都在 2 左右，
    ///     而一张 60x60 的地图扫过去，同一行里连续几十个格子同值很常见。
    ///
    /// 于是评分 = 「前两种值的占比」×「平均连续长度」，全程**只读**
    /// （只用 `ReadBytesQuiet`，一个字节都不写）。
    ///
    /// 用法：`ShroudShapeScan.exe <输出文件> [窗口字节数]`
    /// </summary>
    public static class ShroudShapeScan
    {
        private const int DefaultWindow = 0x10000;      // 64 KB
        private const uint MinAddress = 0x10000;
        private const long MaxBytes = 512L * 1024 * 1024;

        public static int Main(string[] args)
        {
            // 模式二：dump <规格文件> <输出文件> —— 把指定区域的原样字节倒出来（只读）。
            // 为什么要它：扫出来的结构（例如"每 512 字节一条记录、大多是 0xFF"）
            // 光看字符图看不出记录内部长什么样，得看原始字节。
            if (args != null && args.Length >= 3
                && string.Equals(args[0], "dump", StringComparison.OrdinalIgnoreCase))
            {
                return DumpMode(args[1], args[2]);
            }

            // 模式三/四：snap <文件>（拍指纹）与 diff <指纹文件> <输出文件>（比对并把变了的块倒出来）。
            // 用途：**受控对照实验** —— 拍一张指纹 → 让用户用一次「侦察卫星」（或让单位探进黑幕）
            // → 再拍一张 → 比对。**哪儿变了，哪儿就是视野数据**。
            // 比"用户慢慢探路"强得多：一次揭开的是一整片，信号又大又干净。
            if (args != null && args.Length >= 2
                && string.Equals(args[0], "snap", StringComparison.OrdinalIgnoreCase))
            {
                return SnapMode(args[1]);
            }
            if (args != null && args.Length >= 3
                && string.Equals(args[0], "diff", StringComparison.OrdinalIgnoreCase))
            {
                return DiffMode(args[1], args[2]);
            }
            // 模式五：watch <秒数> <输出文件> —— 拍指纹 → 等 N 秒（用户在这段时间里做动作）
            // → 再读一遍 → 比对，**只倒"最像地图"的那些块**。
            // 为什么要合成一条：源快照与比对之间隔着"用户手动操作"，拆成两次就要两次提权点击，
            // 而且中间状态还容易被别的操作搅浑。
            if (args != null && args.Length >= 3
                && string.Equals(args[0], "watch", StringComparison.OrdinalIgnoreCase))
            {
                int seconds;
                if (!int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds)
                    || seconds < 5) { seconds = 60; }
                return WatchMode(seconds, args[2]);
            }

            string outPath = (args != null && args.Length > 0) ? args[0] : "shroud-shape-scan.txt";
            int window = DefaultWindow;
            if (args != null && args.Length > 1)
            {
                int parsed;
                if (int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    && parsed >= 4096) { window = parsed; }
            }

            Console.WriteLine("=== 黑幕形状扫描（只读） ===");
            ProcessAttach attach = new ProcessAttach();
            if (!attach.TryAttach())
            {
                Console.WriteLine("附加游戏失败：" + attach.Describe());
                Console.WriteLine("（游戏若是管理员身份启动的，本工具也必须以管理员运行）");
                return 2;
            }
            Console.WriteLine("已附加：" + attach.Describe());

            MemoryIO io = new MemoryIO(attach);
            DateTime started = DateTime.UtcNow;

            List<MemoryRegion> regions = io.EnumRegions(MinAddress, Win32Api.MaxUserAddress32, true, true, 0);
            Console.WriteLine("可写区域：" + regions.Count + " 段");

            List<Hit> hits = new List<Hit>();
            byte[] buffer = new byte[window];
            int[] histogram = new int[256];
            long scanned = 0;
            int failed = 0;

            for (int i = 0; i < regions.Count; i++)
            {
                MemoryRegion region = regions[i];
                if (region.Size < (ulong)window) { continue; }
                if (!region.Readable || !region.Writable) { continue; }
                if (Win32Api.IsExecutableProtect(region.Protect)) { continue; }

                ulong blocks = region.Size / (ulong)window;
                for (ulong b = 0; b < blocks; b++)
                {
                    if (scanned >= MaxBytes) { break; }
                    ulong raw = region.Base + b * (ulong)window;
                    if (raw > 0xFFFFFFFFul) { break; }

                    byte[] data = io.ReadBytesQuiet((uint)raw, window);
                    if (data == null) { failed++; continue; }
                    scanned += window;

                    Hit hit = Evaluate((uint)raw, window, data, histogram);
                    if (hit != null)
                    {
                        hits.Add(hit);
                        if (hits.Count > 4000) { PruneLowest(hits); }
                    }
                }
            }

            hits.Sort(delegate(Hit a, Hit b) { return b.Score.CompareTo(a.Score); });

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 黑幕形状扫描结果（只读） ===");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "窗口 {0} 字节｜扫了 {1:0.0} MB｜读不到 {2} 块｜命中 {3} 块｜耗时 {4:0.0} 秒",
                window, scanned / 1048576.0, failed, hits.Count, (DateTime.UtcNow - started).TotalSeconds));
            sb.AppendLine();
            sb.AppendLine("判据：前两种值合计占比 ≥ 55%，且「平均连续长度」≥ 6（随机数据约 2）。");
            sb.AppendLine("字符图：最多的值 → `.`，次多的 → `#`，其余 → `+`；每行 128 字节，最多 48 行。");
            sb.AppendLine();

            int show = hits.Count < 30 ? hits.Count : 30;
            sb.AppendLine("== 全部命中的摘要（按评分排，最多列 400 条）—— 万一前 30 名的图都不像，可以照着地址挑 ==");
            int listCount = hits.Count < 400 ? hits.Count : 400;
            for (int i = 0; i < listCount; i++)
            {
                Hit h = hits[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  [{0}] 0x{1:X8}　0x{2:X2}/0x{3:X2}　两种值 {4:0.0}%｜少数方 {5:0.0}%｜平均连续 {6:0.0}｜最长 {7}｜段数 {8}｜评分 {9:0}",
                    i + 1, h.Address, h.ValueA, h.ValueB, h.TwoShare * 100.0, h.Minority * 100.0,
                    h.AvgRun, h.LongestRun, h.Runs, h.Score));
            }
            sb.AppendLine();

            for (int i = 0; i < show; i++)
            {
                Hit h = hits[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "--- [{0}] 0x{1:X8}　两种值 0x{2:X2} / 0x{3:X2}　占比 {4:0.0}%｜少数方 {5:0.0}%｜平均连续 {6:0.0}｜最长游程 {7}｜段数 {8}｜评分 {9:0} ---",
                    i + 1, h.Address, h.ValueA, h.ValueB, h.TwoShare * 100.0, h.Minority * 100.0,
                    h.AvgRun, h.LongestRun, h.Runs, h.Score));
                sb.AppendLine("  字符图（前 48 行 × 128 字节）：");
                AppendMap(sb, h, 128, 48);
                sb.AppendLine();
            }

            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("写文件失败：" + ex.Message); return 3; }

            Console.WriteLine("结果已写入：" + outPath);
            attach.Dispose();
            return 0;
        }

        /// <summary>
        /// 「倒原始字节」模式：规格文件每行 `地址 长度`（支持 0x 与注释），
        /// 把每段按 16 字节一行（偏移 + 十六进制 + 可打印字符）写进输出文件。**只读**。
        /// </summary>
        private static int DumpMode(string specPath, string outPath)
        {
            if (!File.Exists(specPath)) { Console.WriteLine("规格文件不存在：" + specPath); return 4; }

            ProcessAttach attach = new ProcessAttach();
            if (!attach.TryAttach()) { Console.WriteLine("附加游戏失败：" + attach.Describe()); return 2; }
            MemoryIO io = new MemoryIO(attach);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 指定区域原始字节（只读） ===");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine();

            string[] lines = File.ReadAllLines(specPath, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) { continue; }

                string[] parts = line.Replace(",", " ").Split(new char[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) { continue; }

                uint address;
                uint length;
                if (!TryParseNumber(parts[0], out address) || !TryParseNumber(parts[1], out length)) { continue; }
                if (length > 0x40000) { length = 0x40000; }

                byte[] data = io.ReadBytesQuiet(address, (int)length);
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "--- 0x{0:X8} 起 {1} 字节{2} ---", address, length, data == null ? "（读不到）" : ""));
                if (data == null) { sb.AppendLine(); continue; }

                for (int offset = 0; offset < data.Length; offset += 16)
                {
                    sb.Append(string.Format(CultureInfo.InvariantCulture, "  {0:X4}  ", offset));
                    for (int c = 0; c < 16; c++)
                    {
                        if (offset + c < data.Length)
                        {
                            sb.Append(data[offset + c].ToString("X2", CultureInfo.InvariantCulture));
                        }
                        else { sb.Append("  "); }
                        sb.Append(' ');
                    }
                    sb.Append(" |");
                    for (int c = 0; c < 16 && offset + c < data.Length; c++)
                    {
                        byte v = data[offset + c];
                        sb.Append(v >= 32 && v < 127 ? (char)v : '.');
                    }
                    sb.AppendLine("|");
                }
                sb.AppendLine();
            }

            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("写文件失败：" + ex.Message); return 3; }

            Console.WriteLine("已写入：" + outPath);
            attach.Dispose();
            return 0;
        }

        /// <summary>块大小：比对与指纹都用它（16 KB —— 够细，能把变化定到一小片）。</summary>
        private const int BlockSize = 0x4000;

        /// <summary>拍一张"内存指纹"：每 16 KB 一个校验和（只读）。</summary>
        private static int SnapMode(string outPath)
        {
            ProcessAttach attach = new ProcessAttach();
            if (!attach.TryAttach()) { Console.WriteLine("附加游戏失败：" + attach.Describe()); return 2; }
            MemoryIO io = new MemoryIO(attach);

            List<MemoryRegion> regions = io.EnumRegions(MinAddress, Win32Api.MaxUserAddress32, true, true, 0);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# 内存指纹　每块 " + BlockSize + " 字节　生成 "
                + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

            int blocks = 0;
            for (int i = 0; i < regions.Count; i++)
            {
                MemoryRegion region = regions[i];
                if (region.Size < (ulong)BlockSize) { continue; }
                if (!region.Readable || !region.Writable) { continue; }
                if (Win32Api.IsExecutableProtect(region.Protect)) { continue; }

                ulong count = region.Size / (ulong)BlockSize;
                for (ulong b = 0; b < count; b++)
                {
                    ulong raw = region.Base + b * (ulong)BlockSize;
                    if (raw > 0xFFFFFFFFul) { break; }
                    byte[] data = io.ReadBytesQuiet((uint)raw, BlockSize);
                    if (data == null) { continue; }
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8} {1:X8}", (uint)raw, Checksum(data)));
                    blocks++;
                }
            }

            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(false)); }
            catch (Exception ex) { Console.WriteLine("写文件失败：" + ex.Message); return 3; }
            Console.WriteLine("指纹已写入：" + outPath + "（" + blocks + " 块）");
            attach.Dispose();
            return 0;
        }

        /// <summary>比对指纹，并把**变过的那些块**的原样字节倒出来（只读）。</summary>
        private static int DiffMode(string snapPath, string outPath)
        {
            if (!File.Exists(snapPath)) { Console.WriteLine("指纹文件不存在：" + snapPath); return 4; }
            ProcessAttach attach = new ProcessAttach();
            if (!attach.TryAttach()) { Console.WriteLine("附加游戏失败：" + attach.Describe()); return 2; }
            MemoryIO io = new MemoryIO(attach);

            Dictionary<uint, uint> old = new Dictionary<uint, uint>();
            string[] lines = File.ReadAllLines(snapPath, Encoding.UTF8);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) { continue; }
                string[] parts = line.Split(' ');
                if (parts.Length < 2) { continue; }
                uint address;
                uint sum;
                if (!TryParseNumber(parts[0], out address)) { continue; }
                // 校验和是**十六进制且不带 0x**（见 SnapMode 的 X8 格式）——
                // 第一版这里用了"认 0x 前缀、否则按十进制"的通用解析，于是 23706 行里
                // 只有校验和**碰巧全是数字**的 449 行被读进来（2026-10-12 踩过，白费用户一次点击）。
                try { sum = Convert.ToUInt32(parts[1], 16); }
                catch (Exception) { continue; }
                old[address] = sum;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 指纹比对（只读） ===");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine("指纹文件：" + snapPath + "（" + old.Count + " 块）");
            sb.AppendLine();

            int changed = 0;
            int dumped = 0;
            const int MaxDump = 60;
            List<string> summary = new List<string>();

            foreach (KeyValuePair<uint, uint> pair in old)
            {
                byte[] data = io.ReadBytesQuiet(pair.Key, BlockSize);
                if (data == null) { continue; }
                if (Checksum(data) == pair.Value) { continue; }

                changed++;
                summary.Add(string.Format(CultureInfo.InvariantCulture, "  0x{0:X8}", pair.Key));
                if (dumped >= MaxDump) { continue; }
                dumped++;

                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "--- 变了：0x{0:X8}（{1} 字节）---", pair.Key, BlockSize));
                for (int offset = 0; offset < data.Length; offset += 16)
                {
                    sb.Append(string.Format(CultureInfo.InvariantCulture, "  {0:X4}  ", offset));
                    for (int c = 0; c < 16; c++)
                    {
                        sb.Append(offset + c < data.Length
                            ? data[offset + c].ToString("X2", CultureInfo.InvariantCulture) + " "
                            : "   ");
                    }
                    sb.Append(" |");
                    for (int c = 0; c < 16 && offset + c < data.Length; c++)
                    {
                        byte v = data[offset + c];
                        sb.Append(v >= 32 && v < 127 ? (char)v : '.');
                    }
                    sb.AppendLine("|");
                }
                sb.AppendLine();
            }

            sb.Insert(0, string.Format(CultureInfo.InvariantCulture,
                "本次共有 {0} 块变了（最多倒 {1} 块，实际倒 {2} 块）\r\n",
                changed, MaxDump, dumped));

            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("写文件失败：" + ex.Message); return 3; }
            Console.WriteLine("比对完成：变了 " + changed + " 块，详情写入 " + outPath);
            attach.Dispose();
            return 0;
        }

        /// <summary>
        /// 「拍指纹 → 等 N 秒 → 再读 → 比对，并把**最像地图**的变化块倒出来」。
        ///
        /// 与 <see cref="DiffMode"/> 的区别是**排序**：变化的块可能上千（游戏跑起来的杂音就有这么多），
        /// 按地址顺序倒前 60 块等于随机抽样（2026-10-12 那次就是这样，倒出来的全是杂音）。
        /// 这里先给每块算一个"像不像地图"的分（两种值占多数 + 成段 + 少数方有份量），
        /// **只倒分数最高的那些**。
        /// </summary>
        private static int WatchMode(int seconds, string outPath)
        {
            ProcessAttach attach = new ProcessAttach();
            if (!attach.TryAttach()) { Console.WriteLine("附加游戏失败：" + attach.Describe()); return 2; }
            MemoryIO io = new MemoryIO(attach);

            Console.WriteLine("第一遍（拍指纹）…");
            int blockCount;
            Dictionary<uint, uint> before = Fingerprint(io, out blockCount);
            Console.WriteLine("指纹 " + blockCount + " 块；现在开始等待 " + seconds + " 秒 —— 请在这段时间里操作游戏");

            System.Threading.Thread.Sleep(seconds * 1000);

            Console.WriteLine("第二遍（比对）…");
            List<uint> changed = new List<uint>();
            List<Hit> hits = new List<Hit>();
            int[] histogram = new int[256];

            foreach (KeyValuePair<uint, uint> pair in before)
            {
                byte[] data = io.ReadBytesQuiet(pair.Key, BlockSize);
                if (data == null) { continue; }
                if (Checksum(data) == pair.Value) { continue; }
                changed.Add(pair.Key);

                Hit hit = Evaluate(pair.Key, BlockSize, data, histogram);
                if (hit != null) { hits.Add(hit); }
            }

            hits.Sort(delegate(Hit a, Hit b) { return b.Score.CompareTo(a.Score); });

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 变化块里「最像地图」的那些（只读） ===");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "观测 {0} 秒｜共 {1} 块变化｜其中「像地图」的 {2} 块（判据：两值占比 ≥85%、少数方 ≥2%、成段）",
                seconds, changed.Count, hits.Count));
            sb.AppendLine();

            int show = hits.Count < 40 ? hits.Count : 40;
            for (int i = 0; i < show; i++)
            {
                Hit h = hits[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "--- [{0}] 0x{1:X8}　0x{2:X2}/0x{3:X2}　两值 {4:0.0}%｜少数方 {5:0.0}%｜平均连续 {6:0.0}｜最长 {7}｜段数 {8} ---",
                    i + 1, h.Address, h.ValueA, h.ValueB, h.TwoShare * 100.0, h.Minority * 100.0,
                    h.AvgRun, h.LongestRun, h.Runs));
                AppendMap(sb, h, 128, 40);
                sb.AppendLine();
            }

            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("写文件失败：" + ex.Message); return 3; }
            Console.WriteLine("结果已写入：" + outPath);
            attach.Dispose();
            return 0;
        }

        private static Dictionary<uint, uint> Fingerprint(MemoryIO io, out int blockCount)
        {
            Dictionary<uint, uint> map = new Dictionary<uint, uint>();
            List<MemoryRegion> regions = io.EnumRegions(MinAddress, Win32Api.MaxUserAddress32, true, true, 0);
            for (int i = 0; i < regions.Count; i++)
            {
                MemoryRegion region = regions[i];
                if (region.Size < (ulong)BlockSize) { continue; }
                if (!region.Readable || !region.Writable) { continue; }
                if (Win32Api.IsExecutableProtect(region.Protect)) { continue; }

                ulong count = region.Size / (ulong)BlockSize;
                for (ulong b = 0; b < count; b++)
                {
                    ulong raw = region.Base + b * (ulong)BlockSize;
                    if (raw > 0xFFFFFFFFul) { break; }
                    byte[] data = io.ReadBytesQuiet((uint)raw, BlockSize);
                    if (data == null) { continue; }
                    map[(uint)raw] = Checksum(data);
                }
            }
            blockCount = map.Count;
            return map;
        }

        private static uint Checksum(byte[] data)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < data.Length; i++)
            {
                hash ^= data[i];
                hash *= 16777619u;
            }
            return hash;
        }

        private static bool TryParseNumber(string text, out uint value)
        {
            value = 0;
            if (string.IsNullOrEmpty(text)) { return false; }
            string s = text.Trim();
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    value = Convert.ToUInt32(s.Substring(2), 16);
                    return true;
                }
                value = Convert.ToUInt32(s, 10);
                return true;
            }
            catch (Exception) { return false; }
        }

        private sealed class Hit
        {
            public uint Address;
            public int Size;
            public byte ValueA;
            public byte ValueB;
            public double TwoShare;
            public double Minority;
            public double AvgRun;
            public int LongestRun;
            public int Runs;
            public double Score;
            public string TopText = "";
            public byte[] Data;
        }

        private static Hit Evaluate(uint address, int size, byte[] data, int[] histogram)
        {
            for (int i = 0; i < 256; i++) { histogram[i] = 0; }
            for (int i = 0; i < size; i++) { histogram[data[i]]++; }

            int a = -1;
            int b = -1;
            for (int slot = 0; slot < 2; slot++)
            {
                int best = -1;
                int bestCount = 0;
                for (int v = 0; v < 256; v++)
                {
                    if (histogram[v] > bestCount) { bestCount = histogram[v]; best = v; }
                }
                if (best < 0) { break; }
                if (slot == 0) { a = best; } else { b = best; }
                histogram[best] = 0;
            }
            if (a < 0 || b < 0) { return null; }

            // 重新数一遍前两名的个数（上面把前两名"取走"了）
            int countA = 0;
            int countB = 0;
            for (int i = 0; i < size; i++)
            {
                if (data[i] == a) { countA++; }
                else if (data[i] == b) { countB++; }
            }
            double twoShare = (countA + countB) / (double)size;
            if (twoShare < 0.85) { return null; }

            // 少数那一方也得**有份量**：地图黑幕里"已探索"至少该占百分之几
            int minority = countA < countB ? countA : countB;
            if (minority < size * 0.02) { return null; }

            // 平均连续长度 + 最长游程 + 段数
            int runs = 1;
            int longest = 1;
            int current = 1;
            for (int i = 1; i < size; i++)
            {
                if (data[i] == data[i - 1]) { current++; }
                else
                {
                    runs++;
                    if (current > longest) { longest = current; }
                    current = 1;
                }
            }
            if (current > longest) { longest = current; }
            double avgRun = size / (double)runs;

            // **关键的两条**（2026-10-12 第一版就是漏了它们，结果排第一的全是"一大片全 0"）：
            //   · 平均连续长度要落在**中等**区间 —— 地图一行里连续几十格同值很常见，
            //     但**不是**一整块几万字节（那是清零的内存），也不是 2（那是随机/压缩数据）；
            //   · 最长游程**不许占掉太大比例** —— 否则就是"一块大的 + 一堆小的"，不是地图。
            if (avgRun < 4.0 || avgRun > 400.0) { return null; }
            if (longest > size / 8) { return null; }
            if (runs < 200) { return null; }

            Hit hit = new Hit();
            hit.Address = address;
            hit.Size = size;
            hit.ValueA = (byte)a;
            hit.ValueB = (byte)b;
            hit.TwoShare = twoShare;
            hit.Minority = minority / (double)size;
            hit.AvgRun = avgRun;
            hit.LongestRun = longest;
            hit.Runs = runs;
            hit.Score = twoShare * (avgRun < 256.0 ? avgRun : 256.0);
            hit.Data = data;

            StringBuilder top = new StringBuilder();
            for (int slot = 0; slot < 6; slot++)
            {
                int best = -1;
                int bestCount = 0;
                for (int v = 0; v < 256; v++)
                {
                    if (histogram[v] > bestCount) { bestCount = histogram[v]; best = v; }
                }
                if (best < 0 || bestCount == 0) { break; }
                if (top.Length > 0) { top.Append("｜"); }
                top.Append(string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X2} 出现 {1} 次", best, bestCount));
                histogram[best] = 0;
            }
            hit.TopText = top.ToString();
            return hit;
        }

        private static void PruneLowest(List<Hit> hits)
        {
            hits.Sort(delegate(Hit a, Hit b) { return b.Score.CompareTo(a.Score); });
            hits.RemoveRange(2000, hits.Count - 2000);
        }

        private static void AppendMap(StringBuilder sb, Hit hit, int columns, int maxRows)
        {
            int rows = (hit.Data.Length + columns - 1) / columns;
            if (rows > maxRows) { rows = maxRows; }
            for (int r = 0; r < rows; r++)
            {
                sb.Append("    ");
                for (int c = 0; c < columns; c++)
                {
                    int index = r * columns + c;
                    if (index >= hit.Data.Length) { break; }
                    byte v = hit.Data[index];
                    if (v == hit.ValueA) { sb.Append('.'); }
                    else if (v == hit.ValueB) { sb.Append('#'); }
                    else { sb.Append('+'); }
                }
                sb.AppendLine();
            }
        }
    }
}
