using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MoTrainer.Core;

namespace MoTrainer.Tools
{
    /// <summary>
    /// 「周期性字段」扫描器 —— **一次性开发工具，不参与产品编译**。
    ///
    /// ## 要找什么
    ///
    /// 2026-10-12 的三套判据（"哪块在变" / "长什么样" / "受控对照"）都没找到"像地图的扁平位图"，
    /// 于是换第三种可能：**黑幕不是一张大位图，而是"每格一个字节、藏在格子记录内部"**。
    ///
    /// 那种存法有两个硬特征，本工具就是照着这两个特征写的：
    ///
    ///   ① **按固定步长排列**：格子数组是定长记录，所以"某个字段"的字节在内存里
    ///      每隔 S 字节出现一次（S = 记录长度）；同一字段在每条记录里的偏移 r 固定。
    ///   ② **地图格子在内存里是行优先排列的**（第 i 个元素 = 第 y 行第 x 列），
    ///      而"已探索区域"在二维上**连成一片** ⇒ 取那个字段的取值序列，
    ///      在**滞后一个地图宽度 W** 时自相关会明显偏高：
    ///      `v[i] == v[i+W]` 的命中率远高于随机值（随机的约 50%）。
    ///
    /// 第 ① 条用来把候选从"整台机器"缩到"少数几组 (S, r)"；
    /// 第 ② 条用来把"碰巧两值的字段"（时间戳、标志位、指针低位…）与"真正的地图"分开。
    ///
    /// **全程只读**（只用 `ReadBytesQuiet`），一个字节都不写。
    ///
    /// 用法：`PeriodicScan.exe <输出文件> [最长扫描 MB]`
    /// </summary>
    public static class PeriodicScan
    {
        private const int Window = 0x10000;          // 每次看 64 KB
        private const uint MinAddress = 0x10000;
        private const long DefaultMaxMb = 400;

        /// <summary>候选记录长度（格子对象的大小通常是这几档）。</summary>
        private static readonly int[] Strides = new int[] { 16, 32, 64, 128, 256, 512, 1024 };

        /// <summary>候选地图宽度（= 行字节数 = 每行的格子数）。</summary>
        private static readonly int[] Widths = new int[]
        {
            32, 40, 48, 56, 60, 64, 72, 80, 96, 100, 112, 120, 128, 144, 160, 192, 200, 224, 256
        };

        public static int Main(string[] args)
        {
            string outPath = (args != null && args.Length > 0) ? args[0] : "periodic-scan.txt";
            long maxBytes = DefaultMaxMb * 1024L * 1024L;
            if (args != null && args.Length > 1)
            {
                long parsed;
                if (long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    && parsed > 0) { maxBytes = parsed * 1024L * 1024L; }
            }

            Console.WriteLine("=== 周期性字段扫描（只读） ===");
            ProcessAttach attach = new ProcessAttach();
            if (!attach.TryAttach())
            {
                Console.WriteLine("附加游戏失败：" + attach.Describe());
                Console.WriteLine("（游戏若是管理员身份启动的，本工具也必须以管理员运行）");
                return 2;
            }
            Console.WriteLine("已附加：" + attach.Describe());

            MemoryIO io = new MemoryIO(attach);
            List<MemoryRegion> regions = io.EnumRegions(MinAddress, Win32Api.MaxUserAddress32, true, true, 0);

            int[] histogram = new int[256 * 1024];        // 每个余数位一张 256 项的直方图（复用）
            List<Finding> findings = new List<Finding>();
            long scanned = 0;
            int windows = 0;
            DateTime started = DateTime.UtcNow;

            for (int i = 0; i < regions.Count && scanned < maxBytes; i++)
            {
                MemoryRegion region = regions[i];
                if (region.Size < (ulong)Window) { continue; }
                if (!region.Readable || !region.Writable) { continue; }
                if (Win32Api.IsExecutableProtect(region.Protect)) { continue; }

                ulong blocks = region.Size / (ulong)Window;
                for (ulong b = 0; b < blocks && scanned < maxBytes; b++)
                {
                    ulong raw = region.Base + b * (ulong)Window;
                    if (raw > 0xFFFFFFFFul) { break; }
                    byte[] data = io.ReadBytesQuiet((uint)raw, Window);
                    if (data == null) { continue; }
                    scanned += Window;
                    windows++;

                    for (int s = 0; s < Strides.Length; s++)
                    {
                        ScanStride((uint)raw, data, Strides[s], histogram, findings);
                    }
                }
            }

            findings.Sort(delegate(Finding a, Finding b) { return b.Score.CompareTo(a.Score); });

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== 周期性字段扫描结果（只读） ===");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "扫了 {0} 个 64KB 窗口（{1:0.0} MB）｜命中 {2} 组｜耗时 {3:0.0} 秒",
                windows, scanned / 1048576.0, findings.Count, (DateTime.UtcNow - started).TotalSeconds));
            sb.AppendLine();
            sb.AppendLine("判据：某个「步长 S + 偏移 r」上的字节只有两三种取值（≥95%），");
            sb.AppendLine("      且它的取值序列**滞后一个地图宽度 W 时自相关偏高**（≥0.65；随机值约 0.5）。");
            sb.AppendLine("      列出的「同值率」就是那个命中率 —— 真正的地图应当明显高于 0.65。");
            sb.AppendLine();

            int show = findings.Count < 30 ? findings.Count : 30;
            for (int i = 0; i < show; i++)
            {
                Finding f = findings[i];
                sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "--- [{0}] 0x{1:X8}　步长 {2}｜偏移 0x{3:X}｜取值 0x{4:X2}({5}) / 0x{6:X2}({7})｜"
                    + "宽度 {8} 时同值率 {9:0.000}（对比度 {15:0.000}）｜少数方 {11} 段/最长 {12}｜包围盒占 {13:0.00} 填实 {14:0.00}｜边缘起伏 {16:0.0} ---",
                    i + 1, f.Address, f.Stride, f.Offset, f.ValueA, f.CountA, f.ValueB, f.CountB,
                    f.Width, f.Rate, f.SecondRate, f.MinorityRuns, f.LongestMinorityRun,
                    f.BoxShare, f.BoxFill, f.Contrast, f.EdgeSd));

                // 前 6 名直接把二维图打进来 —— 这样能拿去和用户的小地图截图**对形状**
                if (i < 6 && f.Series != null)
                {
                    sb.AppendLine("    二维图（宽 " + f.Width + "，`.` = 最多的值，`#` = 次多的）：");
                    int rows = f.Series.Length / f.Width;
                    if (rows > 40) { rows = 40; }
                    for (int r = 0; r < rows; r++)
                    {
                        sb.Append("      ");
                        for (int c = 0; c < f.Width; c++)
                        {
                            byte v = f.Series[r * f.Width + c];
                            if (v == f.ValueA) { sb.Append('.'); }
                            else if (v == f.ValueB) { sb.Append('#'); }
                            else { sb.Append('+'); }
                        }
                        sb.AppendLine();
                    }
                    sb.AppendLine();
                }
            }

            try { File.WriteAllText(outPath, sb.ToString(), new UTF8Encoding(true)); }
            catch (Exception ex) { Console.WriteLine("写文件失败：" + ex.Message); return 3; }

            Console.WriteLine("结果已写入：" + outPath);
            attach.Dispose();
            return 0;
        }

        private sealed class Finding
        {
            public uint Address;
            public int Stride;
            public int Offset;
            public int ValueA;
            public int ValueB;
            public int CountA;
            public int CountB;
            public int Width;
            public double Rate;
            public double SecondRate;
            public int MinorityRuns;
            public int LongestMinorityRun;
            public double BoxFill;
            public double BoxShare;
            public double Contrast;
            public double EdgeSd;
            public int RowCount;
            public byte[] Series;
            public double Score;
        }

        /// <summary>
        /// 在一个 64 KB 窗口里，看某个步长 <paramref name="stride"/> 的**每一个余数位**：
        /// 哪些余数位上的字节只有两三种取值？那些就是"字段偏移"候选。
        /// </summary>
        private static void ScanStride(uint baseAddress, byte[] data, int stride, int[] histogram, List<Finding> findings)
        {
            if (stride > data.Length) { return; }
            int residues = stride;
            int slots = residues * 256;
            if (slots > histogram.Length) { return; }

            // 清空（只清用得到的部分）
            for (int i = 0; i < slots; i++) { histogram[i] = 0; }

            int length = data.Length / stride * stride;
            for (int i = 0; i < length; i++)
            {
                histogram[(i % stride) * 256 + data[i]]++;
            }

            int perClass = length / stride;
            if (perClass < 256) { return; }                 // 样本太少，说明不了问题

            for (int r = 0; r < residues; r++)
            {
                int bestA = -1;
                int bestB = -1;
                int countA = 0;
                int countB = 0;
                for (int v = 0; v < 256; v++)
                {
                    int c = histogram[r * 256 + v];
                    if (c > countA) { countB = countA; bestB = bestA; countA = c; bestA = v; }
                    else if (c > countB) { countB = c; bestB = v; }
                }
                if (bestA < 0 || bestB < 0) { continue; }

                double cover = (countA + countB) / (double)perClass;
                if (cover < 0.95) { continue; }             // 必须几乎只有两三种取值
                if (countB * 40 < perClass) { continue; }   // 少数那一方至少 2.5%（一点都没有就不是地图）

                Finding f = EvaluateResidue(baseAddress, data, stride, r, bestA, bestB, countA, countB);
                if (f != null) { findings.Add(f); }
            }
        }

        /// <summary>把该余数位上的取值串成序列，测"滞后一个地图宽度"的自相关。</summary>
        private static Finding EvaluateResidue(
            uint baseAddress, byte[] data, int stride, int residue,
            int valueA, int valueB, int countA, int countB)
        {
            int count = 0;
            for (int i = residue; i < data.Length; i += stride) { count++; }
            if (count < 256) { return null; }

            byte[] series = new byte[count];
            int k = 0;
            for (int i = residue; i < data.Length; i += stride) { series[k++] = data[i]; }

            double bestRate = 0;
            int bestWidth = 0;
            double secondRate = 0;
            double rateSum = 0;
            int rateCount = 0;
            for (int w = 0; w < Widths.Length; w++)
            {
                int width = Widths[w];
                if (width >= count) { continue; }

                int same = 0;
                int total = 0;
                for (int i = 0; i + width < count; i++)
                {
                    total++;
                    if (series[i] == series[i + width]) { same++; }
                }
                if (total == 0) { continue; }
                double rate = same / (double)total;
                rateSum += rate;
                rateCount++;
                if (rate > bestRate)
                {
                    secondRate = bestRate;
                    bestRate = rate;
                    bestWidth = width;
                }
                else if (rate > secondRate) { secondRate = rate; }
            }

            if (bestWidth == 0 || bestRate < 0.60) { return null; }
            double avgRate = rateCount > 0 ? rateSum / rateCount : 0;

            // **对比度**：2026-10-12 第五版加的，也是最后一条判据。
            // 前四版的假货（"整段 A 接整段 B"）在任何滞后上都高度重合，
            // 于是"最高同值率"这个指标对它毫无办法。
            // 真正的"按地图行宽排列"必须**在那个宽度上明显高于其它宽度** —— 差得越开越可信。
            if (bestRate - avgRate < 0.12) { return null; }

            // **少数方必须"成块"，不能是"满屏重复的花纹"** —— 2026-10-12 第一版漏了这一条：
            // 只测"滞后 W 时同值率高"，于是"只跟列有关"的字段（地形/地块编号之类，
            // 每一行长得一模一样）拿到 1.000 的高分，把真正的候选挤下去了。
            // 地图上的"已探索区"是**连成一片**的：少数方的平均连续长度应当明显长于重复花纹。
            int minorityValue = countA < countB ? valueA : valueB;
            int minorityCount = countA < countB ? countA : countB;
            int minorityRuns = 0;
            bool inMinority = false;
            int longestMinorityRun = 0;
            int currentRun = 0;
            for (int i = 0; i < series.Length; i++)
            {
                bool isMinority = series[i] == minorityValue;
                if (isMinority)
                {
                    currentRun++;
                    if (!inMinority) { minorityRuns++; inMinority = true; }
                    if (currentRun > longestMinorityRun) { longestMinorityRun = currentRun; }
                }
                else { inMinority = false; currentRun = 0; }
            }
            if (minorityRuns == 0) { return null; }
            double minorityAvgRun = minorityCount / (double)minorityRuns;
            if (minorityAvgRun < 6.0) { return null; }      // 成块（花纹的连续长度只有 1~3）

            // **在二维上也要"成块"** —— 2026-10-12 第三版加的：
            // 把少数方的位置摊成 (行, 列) 网格，看它是不是挤在一个紧凑的范围里。
            //   · 真地图的"已探索区"= 一块区域 ⇒ 包围盒不大、而且**填得比较实**；
            //   · "只跟列有关"的花纹 ⇒ 包围盒就是整张图，填得很空；
            //   · "一整块 X 接一整块 Y" ⇒ 包围盒就是整张图，而且填得极满。
            int gridRows = series.Length / bestWidth;
            if (gridRows < 4) { return null; }
            int minRow = int.MaxValue, maxRow = -1, minCol = int.MaxValue, maxCol = -1;
            for (int i = 0; i < gridRows * bestWidth; i++)
            {
                if (series[i] != minorityValue) { continue; }
                int row = i / bestWidth;
                int col = i % bestWidth;
                if (row < minRow) { minRow = row; }
                if (row > maxRow) { maxRow = row; }
                if (col < minCol) { minCol = col; }
                if (col > maxCol) { maxCol = col; }
            }
            if (maxRow < 0) { return null; }
            double boxRows = maxRow - minRow + 1;
            double boxCols = maxCol - minCol + 1;
            double boxArea = boxRows * boxCols;
            double gridArea = (double)gridRows * bestWidth;
            double fill = minorityCount / boxArea;                 // 包围盒里填得多实
            double boxShare = boxArea / gridArea;                  // 包围盒占了整张图的多少
            if (fill < 0.25) { return null; }                      // 太散（列花纹就是这样）
            if (boxShare > 0.75) { return null; }                  // 一占就是整张图（两块拼接/大块）
            if (boxShare < 0.03) { return null; }                  // 太小的一丁点，不像一片区域

            // **少数方的占比要落在合理区间**（2026-10-12 第四版加的，也是最管用的一条）：
            // 用户给的小地图截图显示，已探索区域约占全图的**三到五成**。
            // 前面三版抓到的假货，少数方占比都只有 2%~5%（细长花纹）或 99%（整块拼接），
            // 一条就把它们全排掉了。
            double minorityShare = minorityCount / (double)series.Length;
            if (minorityShare < 0.10 || minorityShare > 0.70) { return null; }

            // **每一行里少数方的起止列必须有变化**（2026-10-12 第六版，最后一条判据）。
            // 前五版的假货都是"每一行一模一样的条带"（起止列零变化）——
            // 而地图上的已探索区域轮廓**一定参差不齐**（看用户给的小地图截图就知道）。
            double firstColSum = 0, firstColSumSq = 0, lastColSum = 0, lastColSumSq = 0;
            int rowCount = 0;
            for (int r = 0; r < gridRows; r++)
            {
                int first = -1, last = -1;
                for (int c = 0; c < bestWidth; c++)
                {
                    if (series[r * bestWidth + c] != minorityValue) { continue; }
                    if (first < 0) { first = c; }
                    last = c;
                }
                if (first < 0) { continue; }
                rowCount++;
                firstColSum += first; firstColSumSq += (double)first * first;
                lastColSum += last; lastColSumSq += (double)last * last;
            }
            if (rowCount < 4) { return null; }
            double firstVar = firstColSumSq / rowCount - (firstColSum / rowCount) * (firstColSum / rowCount);
            double lastVar = lastColSumSq / rowCount - (lastColSum / rowCount) * (lastColSum / rowCount);
            if (firstVar < 0) { firstVar = 0; }
            if (lastVar < 0) { lastVar = 0; }
            double firstSd = Math.Sqrt(firstVar);
            double lastSd = Math.Sqrt(lastVar);
            if (firstSd + lastSd < 4.0) { return null; }   // 起止列几乎不变 ⇒ 条带，不是区域

            Finding f = new Finding();
            f.Address = baseAddress;
            f.Stride = stride;
            f.Offset = residue;
            f.ValueA = valueA;
            f.ValueB = valueB;
            f.CountA = countA;
            f.CountB = countB;
            f.Width = bestWidth;
            f.Rate = bestRate;
            f.SecondRate = secondRate;
            f.MinorityRuns = minorityRuns;
            f.LongestMinorityRun = longestMinorityRun;
            f.BoxFill = fill;
            f.BoxShare = boxShare;
            f.Contrast = bestRate - avgRate;
            f.EdgeSd = firstSd + lastSd;
            f.RowCount = rowCount;
            f.Series = series;
            // 评分：同值率 + 块的长度（越长越像地图上的连通区域）
            // 评分：同值率 + 包围盒填得有多实（越实越像"一片区域"）
            f.Score = (bestRate - avgRate) * 200.0 + (firstSd + lastSd) * 5.0;
            return f;
        }
    }
}
