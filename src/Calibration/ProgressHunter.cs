using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MoTrainer.Core;

namespace MoTrainer.Calibration
{
    /// <summary>
    /// 一个字段（对象内的某个偏移）在整局采集里的取值时间线。
    ///
    /// 为什么要单独存"时间线"而不是像以前那样存一串"变化文字"：
    /// 2026-10-09 那份真机报告暴露了一个**把数据毁掉的缺陷** —— 变化条数封顶 400，
    /// 而新建造的对象在出生那一瞬会有一两百个字段同时被初始化（"初始化洪流"），
    /// 名额当场被吃光，之后真正要看的**建造过程**一条都没记上。
    /// （证据：29 个被跟踪对象里 10 个撞上 400 上限；1 号对象的 +0x0C4 只留下 3 次变化。）
    ///
    /// 改成"按偏移各自记账"之后，初始化洪流里每个偏移只占 1~2 条，
    /// 而变化了几十次的"进度字段"照样完整。
    /// </summary>
    public sealed class OffsetSeries
    {
        /// <summary>单个偏移最多记这么多变化点（再多就只更新首尾与次数，并如实标注"已截断"）。</summary>
        public const int MaxPoints = 200;

        public readonly int Offset;

        /// <summary>第一次看到它变化时的采样号，以及**变化之前**的取值。</summary>
        public readonly int FirstSample;
        public readonly uint FirstValue;

        /// <summary>变化次数（含截断后不再逐条记录的那些）。</summary>
        public int Changes;

        /// <summary>最后一次变化的采样号。</summary>
        public int LastChangeSample;

        /// <summary>最近一次变化后的取值。</summary>
        public uint LastValue;

        /// <summary>变化点记满、后面的取值没有逐条记住（如实标注，不假装完整）。</summary>
        public bool Capped;

        /// <summary>变化点的采样号与取值（一一对应）。</summary>
        public readonly List<int> Samples = new List<int>();
        public readonly List<uint> Values = new List<uint>();

        public OffsetSeries(int offset, int firstSample, uint firstValue)
        {
            Offset = offset;
            FirstSample = firstSample;
            FirstValue = firstValue;
            LastChangeSample = firstSample;
            LastValue = firstValue;
        }

        public void Record(int sample, uint value)
        {
            Changes++;
            LastChangeSample = sample;
            LastValue = value;
            if (Samples.Count < MaxPoints)
            {
                Samples.Add(sample);
                Values.Add(value);
            }
            else
            {
                Capped = true;
            }
        }

        /// <summary>把"变化之前的取值 + 每次变化后的取值"拼成一条完整序列。</summary>
        public List<uint> ToSequence()
        {
            List<uint> seq = new List<uint>(Values.Count + 1);
            seq.Add(FirstValue);
            for (int i = 0; i < Values.Count; i++) { seq.Add(Values[i]); }
            return seq;
        }
    }

    /// <summary>一个"像建造进度"的候选字段。</summary>
    public sealed class ProgressCandidate
    {
        /// <summary>在跟踪集合里的序号（与报告「二、被跟踪对象」里的 #N 一致）。</summary>
        public int ObjectNumber;

        public uint Address;
        public uint VTable;
        public string Source = "";

        public OffsetSeries Series;
        public int Score;
        public readonly List<string> Reasons = new List<string>();

        /// <summary>停下之后过了几拍都没再动（越大越像"做完了"）。</summary>
        public int QuietAfter;

        /// <summary>单调程度：朝向同一方向的变化步数 ÷ 总步数。</summary>
        public float Monotonic;

        /// <summary>推进率：变化次数 ÷ 变化窗口的拍数（接近 1 = 每一拍都在推进）。</summary>
        public float Rate;

        /// <summary>锯齿式"复位"次数（一件造完立刻接着造下一件时会看到）。</summary>
        public int Resets;

        /// <summary>最后一次变化是否落在某个"造好了"时刻的旁边。</summary>
        public bool NearCompletionMoment;

        public string ObjectLabel
        {
            get
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "#{0} 0x{1:X8}", ObjectNumber, Address);
            }
        }
    }

    /// <summary>
    /// 「施工进度猎手」—— 从一次采集的数据里，把"建造进度"那个字段挑出来。
    ///
    /// 为什么需要它：`[S6-2]` 瞬间建造差的就这一个字段，而它是本项目**唯一还没定位**的
    /// 功能字段。用户的时间是最稀缺的东西（`CLAUDE.md` 第六节），所以不能让用户"多开几局试试"，
    /// 必须做到"**一次采集就把候选排好序摊在报告里**"。
    ///
    /// 判据（对"进度"这个概念的形态描述，不是猜某个具体偏移）：
    ///   · 建造期间**每一拍都在推进**（单调增或单调减，推进率接近 1）；
    ///   · 造好之后**停下不动**（平台期）—— 这一条是区分"进度"与"计数器/动画"的关键：
    ///     真机数据里有大量每秒都在变的字段（例如建筑 +0x0C4/+0x0C8 每秒 +2000），
    ///     它们永远不停，所以"停不停"比"动不动"有用得多；
    ///   · 取值**像个小数字**而不是地址；
    ///   · 如果它停下的时刻正好对得上"电力产出跳变 / 金钱不再下降"这类**造好了事件**，可信度再加一档。
    ///
    /// 锯齿情形也认：如果用户一件接一件地造，进度字段会"一路降到底，再跳回高点"，
    /// 那就没有平台期，但"复位"本身也是特征（`Resets`）。
    ///
    /// **本类只读，绝不写游戏内存。**
    /// </summary>
    public static class ProgressHunter
    {
        /// <summary>至少要变过这么多次才值得看（变 1~2 次的字段说明不了什么）。</summary>
        public const int MinChanges = 3;

        /// <summary>得分低于这个数就不进候选（避免拿一堆垃圾淹没报告）。</summary>
        public const int MinScore = 8;

        /// <summary>停下之后至少这么多拍没再动，才算"平台期"。</summary>
        public const int PlateauSamples = 3;

        /// <summary>
        /// 找出本次采集里"有东西造好了"的时刻 —— 用来给人眼当参照物，也给排序加分。
        ///
        /// 两个判据：
        ///   · **电力产出跳变**：造好一座发电厂/核电站时它会跳（真机 10-09：60 → 210 → 360 → 510 → 2510 → 4510）；
        ///   · **金钱停止下降**：建造期间金钱是按拍扣的，一件造完又没接着造，扣款就会先停下。
        ///
        /// 返回的是**采样号**（`HarvestSample.Index`），与各字段时间线里的采样号同一口径。
        /// </summary>
        public static List<int> FindCompletionMoments(IList<HarvestSample> samples)
        {
            List<int> moments = new List<int>();
            if (samples == null || samples.Count < 2) { return moments; }

            int drainRun = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                HarvestSample prev = samples[i - 1];
                HarvestSample cur = samples[i];

                if (cur.PowerOutput != prev.PowerOutput)
                {
                    moments.Add(cur.Index);
                }

                if (cur.Money < prev.Money) { drainRun++; }
                else
                {
                    if (drainRun >= 2) { moments.Add(cur.Index); }
                    drainRun = 0;
                }
            }
            return moments;
        }

        /// <summary>
        /// 「生产窗口」掩码：金钱正在下降的那些拍（前后各放宽一拍，容忍采样与扣款错开）。
        ///
        /// 为什么用金钱当尺子：它是本项目里唯一**可观测**的"正在生产"信号 ——
        /// 生产时按拍扣钱，生产一停立刻就停。真进度必须只在这个窗口里推进。
        /// </summary>
        public static HashSet<int> BuildDrainMask(IList<HarvestSample> samples)
        {
            HashSet<int> mask = new HashSet<int>();
            if (samples == null || samples.Count < 2) { return mask; }

            for (int i = 1; i < samples.Count; i++)
            {
                if (samples[i].Money >= samples[i - 1].Money) { continue; }
                mask.Add(samples[i - 1].Index);
                mask.Add(samples[i].Index);
                if (i + 1 < samples.Count) { mask.Add(samples[i + 1].Index); }
            }
            return mask;
        }

        /// <summary>把"造好了"时刻写成报告里可读的几行。</summary>
        public static List<string> DescribeCompletionMoments(IList<HarvestSample> samples)
        {
            List<string> lines = new List<string>();
            if (samples == null || samples.Count < 2) { return lines; }

            int drainRun = 0;
            for (int i = 1; i < samples.Count; i++)
            {
                HarvestSample prev = samples[i - 1];
                HarvestSample cur = samples[i];

                if (cur.PowerOutput != prev.PowerOutput)
                {
                    lines.Add(string.Format(CultureInfo.InvariantCulture,
                        "采样 #{0}（t={1:0.0}s）电力产出 {2} → {3}",
                        cur.Index, cur.Seconds, prev.PowerOutput, cur.PowerOutput));
                }

                if (cur.Money < prev.Money) { drainRun++; }
                else
                {
                    if (drainRun >= 2)
                    {
                        lines.Add(string.Format(CultureInfo.InvariantCulture,
                            "采样 #{0}（t={1:0.0}s）金钱停止下降（此前连续下降 {2} 拍，多半是造完了）",
                            cur.Index, cur.Seconds, drainRun));
                    }
                    drainRun = 0;
                }
            }
            return lines;
        }

        /// <summary>
        /// 给所有（对象 × 偏移）打分并排序 —— 越靠前越像"建造进度"。
        ///
        /// <paramref name="excludedNotes"/> 非空时，把"按硬规则直接排除"的字段如实记进去
        /// （目前只有一条硬规则：取值像地址）。**排除也要说出来**，不能悄悄少给数据 ——
        /// 万一是判据定错了，报告里还留着线索。
        /// </summary>
        public static List<ProgressCandidate> Rank(
            IList<HarvestTracked> tracked,
            IList<HarvestSample> samples,
            uint moduleBase,
            uint moduleSize,
            int maxResults,
            List<string> excludedNotes)
        {
            List<ProgressCandidate> result = new List<ProgressCandidate>();
            if (tracked == null || tracked.Count == 0) { return result; }

            List<int> moments = FindCompletionMoments(samples);
            int lastSample = 0;
            if (samples != null && samples.Count > 0) { lastSample = samples[samples.Count - 1].Index; }

            // 「生产窗口」= 金钱正在下降的那些拍（前后各放宽一拍，容忍采样与扣款错开）。
            // 这是本项目里唯一**可观测**的"正在生产"信号：金钱是按拍扣的，生产一停它就停。
            // 真进度必须"只在这个窗口里推进" —— 2026-10-10 第三次真机才把这条判据用上：
            // 那一局用户造三样、中间各等一会，生产窗口一眼可数（14 拍），
            // 而界面/动画类对象的计数器在整个采集期间都在动，靠这条一眼就能剔掉。
            HashSet<int> drain = BuildDrainMask(samples);

            for (int t = 0; t < tracked.Count; t++)
            {
                HarvestTracked obj = tracked[t];
                if (obj.Series.Count == 0) { continue; }

                List<int> offsets = new List<int>(obj.Series.Keys);
                offsets.Sort();

                for (int k = 0; k < offsets.Count; k++)
                {
                    OffsetSeries series = obj.Series[offsets[k]];

                    // 偏移 0 是"类型表"本身（每个对象都有、且各不相同），不是进度
                    if (series.Offset == 0) { continue; }
                    if (series.Changes < MinChanges) { continue; }

                    // 我方房子上**已知**的字段：金钱（+0x30C）与电力显示值（+0x53A4 / +0x53A8）。
                    // 它们同样"一边生产一边变"，但绝不可能是进度 ——
                    // 2026-10-10 那次它们把前四名全占了，纯属干扰。
                    if (obj.IsPlayerHouse && IsKnownHouseOffset(series.Offset)) { continue; }

                    List<uint> seq = series.ToSequence();
                    int steps = seq.Count - 1;
                    if (steps <= 0) { continue; }

                    int up = 0;
                    int down = 0;

                    // 跨度按**最大最小**算，不能按首尾算：
                    // 一件接一件地造（锯齿）时首尾正好相等，按首尾算跨度就是 0，
                    // "复位"判据会整个失效（2026-10-10 自检抓到的就是这个）。
                    uint minValue = seq[0];
                    uint maxValue = seq[0];
                    for (int i = 0; i < seq.Count; i++)
                    {
                        if (seq[i] < minValue) { minValue = seq[i]; }
                        if (seq[i] > maxValue) { maxValue = seq[i]; }
                    }
                    uint span = maxValue - minValue;

                    for (int i = 0; i < steps; i++)
                    {
                        if (seq[i + 1] > seq[i]) { up++; }
                        else if (seq[i + 1] < seq[i]) { down++; }
                    }
                    bool dominantUp = up >= down;
                    int dominant = dominantUp ? up : down;
                    float monotonic = (float)dominant / steps;

                    // 反向的大跳 = 锯齿里的"复位"（一件造完接着造下一件）
                    int resets = 0;
                    for (int i = 0; i < steps; i++)
                    {
                        bool against = dominantUp ? (seq[i + 1] < seq[i]) : (seq[i + 1] > seq[i]);
                        if (!against) { continue; }
                        uint delta = seq[i] > seq[i + 1] ? seq[i] - seq[i + 1] : seq[i + 1] - seq[i];
                        if (span > 0 && (ulong)delta * 10 >= (ulong)span * 3) { resets++; }
                    }

                    int quietAfter = lastSample - series.LastChangeSample;
                    int window = series.LastChangeSample - series.FirstSample + 1;
                    float rate = window <= 0 ? 0f : (float)series.Changes / window;

                    // 取值像地址 = **直接排除**（不参与打分）。理由：进度是个"计数"，
                    // 不会长成 0x2CB3BC00 这种指针，也不会落在模块区间里。
                    // 真机数据里"每秒 +2000 的计数器"这类噪声虽然不像地址，但会被"永不停歇"挡住。
                    if (LooksLikeAddress(seq, moduleBase, moduleSize))
                    {
                        if (excludedNotes != null && excludedNotes.Count < 8)
                        {
                            excludedNotes.Add(string.Format(CultureInfo.InvariantCulture,
                                "对象 #{0} +0x{1:X3}（变了 {2} 次，取值如 0x{3:X8}）",
                                t + 1, series.Offset, series.Changes, series.FirstValue));
                        }
                        continue;
                    }

                    bool nearMoment = false;
                    for (int m = 0; m < moments.Count; m++)
                    {
                        int distance = moments[m] - series.LastChangeSample;
                        if (distance < 0) { distance = -distance; }
                        if (distance <= 1) { nearMoment = true; break; }
                    }

                    int score = 0;
                    List<string> reasons = new List<string>();
                    bool decreasing = !dominantUp;

                    if (quietAfter >= PlateauSamples)
                    {
                        score += 4;
                        reasons.Add("造完后停下不动（" + quietAfter + " 拍）");
                    }
                    else if (resets >= 1 && monotonic >= 0.7f)
                    {
                        // 真玩起来用户多半是一件接一件地造，进度字段**没有平台期** ——
                        // 只认平台期的话，最常见的那种玩法反而找不到字段。
                        score += 3;
                        reasons.Add("锯齿式推进（复位 " + resets + " 次）");
                    }
                    else
                    {
                        score -= 6;
                        reasons.Add("一直在变、从不停（更像计数器/动画）");
                    }

                    // 方向：进度要么"往下倒数"（剩余时间），要么"往上走满就复位"（已完成量）。
                    if (decreasing) { score += 3; reasons.Add("一路往下（像「剩余时间」）"); }
                    if (resets >= 1) { score += 3; reasons.Add("有复位（一件接一件地造）"); }

                    // **只涨不停、且从头涨到尾** = 累计值/账本的特征（花费总额、帧计数这类）。
                    // 2026-10-10 真机数据里它们排在候选第 2~4 名，全是干扰。
                    if (!decreasing && resets == 0 && quietAfter >= PlateauSamples)
                    {
                        score -= 4;
                        reasons.Add("只涨不停、还从头涨到尾（更像累计值/账本）");
                    }

                    if (monotonic >= 0.9f) { score += 4; reasons.Add("单调"); }
                    else if (monotonic >= 0.7f) { score += 2; reasons.Add("大体单调"); }

                    if (rate >= 0.6f) { score += 3; reasons.Add("每拍都在推进"); }
                    if (series.Changes >= 8) { score += 2; }
                    if (nearMoment) { score += 4; reasons.Add("停下时刻与「造好了」对齐"); }

                    // 与「生产窗口」对齐：真进度只在生产时动，生产一停它就不动。
                    int insideChanges = 0;
                    int outsideChanges = 0;
                    for (int k2 = 0; k2 < series.Samples.Count; k2++)
                    {
                        if (drain.Contains(series.Samples[k2])) { insideChanges++; }
                        else { outsideChanges++; }
                    }
                    // 掩码为空 = 这次采集根本没看到"金钱在掉"（没校准金钱、或全程没生产）——
                    // 那就**不做这条判断**，绝不能拿"没有窗口"当成"处处都在生产之外"。
                    if (drain.Count > 0 && insideChanges + outsideChanges >= 3)
                    {
                        float insideRatio = (float)insideChanges / (insideChanges + outsideChanges);
                        if (insideRatio >= 0.8f)
                        {
                            score += 6;
                            reasons.Add("只在生产时推进（与金钱下降同步）");
                        }
                        else if (insideRatio <= 0.4f)
                        {
                            score -= 4;
                            reasons.Add("生产之外也在变（不是生产进度）");
                        }
                    }

                    if (obj.NewlyAppeared || obj.IsPlayerHouse)
                    {
                        score += 2;
                        reasons.Add(obj.IsPlayerHouse ? "来自我方房子" : "来自新建造的对象");
                    }

                    if (score < MinScore) { continue; }

                    ProgressCandidate candidate = new ProgressCandidate();
                    candidate.ObjectNumber = t + 1;
                    candidate.Address = obj.Address;
                    candidate.VTable = obj.FirstVTable;
                    candidate.Source = obj.Source;
                    candidate.Series = series;
                    candidate.Score = score;
                    candidate.QuietAfter = quietAfter;
                    candidate.Monotonic = monotonic;
                    candidate.Rate = rate;
                    candidate.Resets = resets;
                    candidate.NearCompletionMoment = nearMoment;
                    candidate.Reasons.AddRange(reasons);
                    result.Add(candidate);
                }
            }

            result.Sort(delegate(ProgressCandidate a, ProgressCandidate b)
            {
                if (a.Score != b.Score) { return b.Score.CompareTo(a.Score); }
                if (a.Series.Changes != b.Series.Changes) { return b.Series.Changes.CompareTo(a.Series.Changes); }
                if (a.ObjectNumber != b.ObjectNumber) { return a.ObjectNumber.CompareTo(b.ObjectNumber); }
                return a.Series.Offset.CompareTo(b.Series.Offset);
            });

            if (maxResults > 0 && result.Count > maxResults)
            {
                result.RemoveRange(maxResults, result.Count - maxResults);
            }
            return result;
        }

        /// <summary>
        /// 我方房子上**已知**的字段偏移 —— 它们也会"一边生产一边变"，但绝不可能是进度：
        ///   +0x30C  金钱（三条金钱链的落点，真机反复确认过）
        ///   +0x53A4 电力产出（界面显示值）
        ///   +0x53A8 电力消耗（界面显示值）
        /// 2026-10-10 那次采集里，金钱排在候选第一名 —— 排掉它，报告才好看。
        /// </summary>
        private static bool IsKnownHouseOffset(int offset)
        {
            return offset == 0x30C || offset == 0x53A4 || offset == 0x53A8;
        }

        /// <summary>
        /// 取值像不像一个内存地址（而不是"进度"这种计数）。
        ///
        /// 刻意看**整条序列**而不是只看某一个值：真机里字段的初始值偶尔是垃圾
        /// （"还没初始化"的指针），只盯第一个值会误伤真的进度字段。一半以上像地址才算。
        ///
        /// 门槛设在 0x10000000 以上、或落在模块区间内：
        /// 真机里那些每秒都在涨的计数器（建筑 +0x0C4/+0x0C8，涨到 0x1xxxx 就到头）不该被误伤，
        /// 而指针（0x2CB3BC00 这种堆地址、或模块内的 0x007Exxxx）都会被认出来。
        /// </summary>
        private static bool LooksLikeAddress(List<uint> seq, uint moduleBase, uint moduleSize)
        {
            if (seq == null || seq.Count == 0) { return false; }

            int hits = 0;
            for (int i = 0; i < seq.Count; i++)
            {
                uint value = seq[i];
                if (value >= 0x10000000) { hits++; continue; }
                if (moduleBase != 0 && value >= moduleBase && value < moduleBase + moduleSize) { hits++; }
            }
            return hits * 2 >= seq.Count;
        }

        /// <summary>
        /// 给一个对象扫「型号线索」：它内部的指针若指向一个像"建造 ID"的字符串
        /// （GAPOWR / HARV / E1 这种），那串字符就是它的型号名。
        ///
        /// 为什么需要：报告的读者看得见偏移和取值，却看不出"这是什么东西"。
        /// 2026-10-10 就是卡在这里 —— 两个"倒数到 0"的字段，谁都说不清它们属于兵、车还是别的。
        ///
        /// 判据刻意卡得很死（宁可漏、不要错）：只认 4~24 字符、只含字母数字与 `_ . -`、
        /// 且至少有一个大写字母的串；另外把已知的**方位词**（North/South/…）当噪声排掉 ——
        /// 真机报告里它们反复冒充"建造 ID"（那其实是地图方向的名字）。
        /// </summary>
        public static List<string> ScanTypeIds(MemoryIO memory, uint address, int scanBytes)
        {
            List<string> ids = new List<string>();
            if (memory == null || address < 0x10000) { return ids; }

            byte[] block = memory.ReadBytesQuiet(address, scanBytes);
            if (block == null) { return ids; }

            for (int offset = 0; offset + 4 <= block.Length; offset += 4)
            {
                uint pointer = ReadUInt32(block, offset);
                if (pointer < 0x10000 || pointer > 0x7FFFFFFF) { continue; }

                string id = ReadIdentifier(memory, pointer);
                if (id.Length == 0) { continue; }

                bool dup = false;
                for (int d = 0; d < ids.Count; d++) { if (ids[d] == id) { dup = true; break; } }
                if (dup) { continue; }

                ids.Add(id);
                if (ids.Count >= 8) { break; }
            }
            return ids;
        }

        /// <summary>读一个"建造 ID 风格"的字符串（与 `AutoInspection` 里那份同一口径）。</summary>
        private static string ReadIdentifier(MemoryIO memory, uint address)
        {
            if (address < 0x10000) { return ""; }

            byte[] raw = memory.ReadBytesQuiet(address, 24);
            if (raw == null || raw.Length < 4) { return ""; }

            int length = 0;
            bool sawUpper = false;
            while (length < raw.Length)
            {
                byte b = raw[length];
                if (b == 0) { break; }
                bool ok = (b >= (byte)'0' && b <= (byte)'9')
                    || (b >= (byte)'A' && b <= (byte)'Z')
                    || (b >= (byte)'a' && b <= (byte)'z')
                    || b == (byte)'_' || b == (byte)'.' || b == (byte)'-';
                if (!ok) { return ""; }
                if (b >= (byte)'A' && b <= (byte)'Z') { sawUpper = true; }
                length++;
            }

            if (length < 4 || !sawUpper) { return ""; }

            string text = Encoding.ASCII.GetString(raw, 0, length);
            if (IsKnownNoise(text)) { return ""; }
            return text;
        }

        /// <summary>
        /// 已知噪声：真机报告里反复冒充"建造 ID"的东西。
        /// 方位词是地图方向的名字（每个对象附近都能找到），不是型号。
        /// </summary>
        private static bool IsKnownNoise(string text)
        {
            string upper = text.ToUpperInvariant();
            if (upper == "NORTH" || upper == "SOUTH" || upper == "EAST" || upper == "WEST") { return true; }
            if (upper == "NORTHEAST" || upper == "NORTHWEST" || upper == "SOUTHEAST" || upper == "SOUTHWEST") { return true; }
            if (upper == "NONE" || upper == "TRUE" || upper == "FALSE" || upper == "NULL") { return true; }
            return false;
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }

        /// <summary>
        /// 把一条取值序列写成报告里的一行：最多 maxValues 个数字，连续重复折叠成 `v(×n)`。
        /// 超长时保留头尾（中间用 `…` 省略）并标明省了多少 —— 不假装完整。
        ///
        /// 2026-10-10 由自检抓到并修掉一个越界 bug：原来"序列比上限短"时会拿
        /// 上限当起点去取剩余部分（起点直接超出序列末尾）→ 抛 ArgumentOutOfRangeException。
        /// 而它在最终收尾里跑，等于**用户那一局的报告整个没了**。教训：取区间的三个数
        /// （起点 / 长度 / 集合大小）必须当场夹紧，不能靠"调用方肯定算对了"。
        /// </summary>
        public static string FormatSeries(OffsetSeries series, int maxValues)
        {
            if (series == null) { return "（无）"; }

            List<uint> seq = series.ToSequence();
            if (maxValues < 4) { maxValues = 4; }

            StringBuilder text = new StringBuilder();
            if (seq.Count <= maxValues)
            {
                text.Append(JoinRange(seq, 0, seq.Count));
            }
            else
            {
                int head = maxValues * 2 / 3;
                int tail = maxValues - head;
                text.Append(JoinRange(seq, 0, head));
                text.Append(", …(").Append(seq.Count - head - tail).Append(" 个省略)…, ");
                text.Append(JoinRange(seq, seq.Count - tail, tail));
            }

            if (series.Capped) { text.Append("（变化点太多，末尾未逐条记录）"); }
            return text.ToString();
        }

        /// <summary>把一段取值写成 `a, b, c(×5)` 这种形式（连读 3 次以上折叠）。</summary>
        private static string JoinRange(List<uint> seq, int start, int count)
        {
            StringBuilder text = new StringBuilder();

            // 区间一律当场夹紧：起点不在序列里、长度超出末尾，都当作"能取多少取多少"。
            if (start < 0) { start = 0; }
            if (start > seq.Count) { start = seq.Count; }
            if (count > seq.Count - start) { count = seq.Count - start; }

            int i = start;
            int end = start + count;
            while (i < end)
            {
                int run = 1;
                while (i + run < end && seq[i + run] == seq[i]) { run++; }
                if (text.Length > 0) { text.Append(", "); }
                text.Append(seq[i].ToString(CultureInfo.InvariantCulture));
                if (run >= 3) { text.Append("(×").Append(run).Append(")"); }
                else
                {
                    for (int k = 1; k < run; k++) { text.Append(", ").Append(seq[i].ToString(CultureInfo.InvariantCulture)); }
                }
                i += run;
            }
            return text.ToString();
        }

        /// <summary>写出报告里的「六、施工进度候选字段」整段。</summary>
        public static string Format(
            IList<HarvestTracked> tracked,
            IList<HarvestSample> samples,
            uint moduleBase,
            uint moduleSize)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 六、施工进度候选字段（[S6-2] 瞬间建造用）===");
            text.AppendLine("（目的：找到「建造进度」那个字段。判据是形态而不是猜偏移 ——");
            text.AppendLine("  建造期间每一拍都在推进（单调），造好之后停下不动；");
            text.AppendLine("  「一直在变、从不停」的计数器与「从不变化」的死字段都会被排到后面。");
            text.AppendLine("  下面先列本次采集里「有东西造好了」的时刻，用来给人眼当参照。）");
            text.AppendLine();

            List<string> moments = DescribeCompletionMoments(samples);
            if (moments.Count == 0)
            {
                text.AppendLine("本次采集里**没有出现**「造好了」的迹象（电力没跳、金钱也没停过下降）——");
                text.AppendLine("  要么这局没造东西，要么造的东西不是发电厂（电力不跳）。");
                text.AppendLine("  候选**照给**（判据不只靠这个），但下一局最好先点开始采集、再动手造，");
                text.AppendLine("  并且至少完整造完一座发电厂 —— 那样结论才更硬。");
                text.AppendLine();
            }
            else
            {
                text.AppendLine("本次采集的「造好了」时刻（供对照）：");
                for (int i = 0; i < moments.Count && i < 20; i++) { text.AppendLine("  " + moments[i]); }
                if (moments.Count > 20) { text.AppendLine("  …（共 " + moments.Count + " 处）"); }
                text.AppendLine();
            }

            List<string> excluded = new List<string>();
            List<ProgressCandidate> candidates = Rank(tracked, samples, moduleBase, moduleSize, 15, excluded);

            if (excluded.Count > 0)
            {
                // 排除也要说出来：万一是判据定错了，报告里还留着线索
                text.AppendLine("按硬规则排除的字段（取值像地址 —— 进度是个计数，不会长成指针）：");
                for (int i = 0; i < excluded.Count; i++) { text.AppendLine("  排除：" + excluded[i]); }
                text.AppendLine();
            }

            if (candidates.Count == 0)
            {
                text.AppendLine("候选字段：**一个都没有**（没有任何字段同时满足「在推进」和「推进完停下」）。");
                text.AppendLine("  最常见的原因：采集期间没有把某样东西**从头造到尾**。");
                text.AppendLine("  下一局的做法：点开始采集 → 再造 → 等它造完 → 再等几秒 → 停止。");
                text.AppendLine();
                return text.ToString();
            }

            text.AppendLine("候选字段（按可信度排，越靠前越像「建造进度」）：");
            for (int i = 0; i < candidates.Count; i++)
            {
                ProgressCandidate c = candidates[i];
                text.AppendLine();
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,2}. 对象 {1} 类型表 0x{2:X8} {3}",
                    i + 1, c.ObjectLabel, c.VTable,
                    c.Source.Length > 0 ? "｜" + c.Source : (tracked[c.ObjectNumber - 1].NewlyAppeared ? "｜新造出来的" : "")));

                List<string> hints = tracked[c.ObjectNumber - 1].TypeIdHints;
                if (hints.Count > 0)
                {
                    text.AppendLine("      型号线索：" + string.Join(", ", hints.ToArray()));
                }
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "      偏移 +0x{0:X3}｜变了 {1} 次｜单调 {2:0.00}｜推进率 {3:0.00}｜停下后 {4} 拍没动｜得分 {5}",
                    c.Series.Offset, c.Series.Changes, c.Monotonic, c.Rate, c.QuietAfter, c.Score));
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "      取值序列：{0} → … → {1}",
                    c.Series.FirstValue, c.Series.LastValue));
                text.AppendLine("      明细：" + FormatSeries(c.Series, 60));
                text.AppendLine("      依据：" + string.Join("；", c.Reasons.ToArray()));
            }

            ProgressCandidate best = candidates[0];
            text.AppendLine();
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "结论：最像「建造进度」的是 对象 {0} 的 **+0x{1:X}**（得分 {2}）。",
                best.ObjectLabel, best.Series.Offset, best.Score));
            text.AppendLine("      下一步**不是**直接接线，而是真机试探：往这个字段写一个值 → 看建造是否立刻完成 →");
            text.AppendLine("      立即还原（做法与「电力试探」同源，见 docs/05）。写错字段是要出事的，所以必须试探过才接线。");
            text.AppendLine();
            return text.ToString();
        }

        /// <summary>
        /// 取一个对象里"变化最多"的前几个偏移（报告「二、被跟踪对象」用）——
        /// 完整取值序列太长，那一节只列变化最多的几个，明细在「六」里。
        /// </summary>
        public static List<OffsetSeries> TopOffsets(HarvestTracked tracked, int max)
        {
            List<OffsetSeries> list = new List<OffsetSeries>();
            if (tracked == null) { return list; }

            foreach (KeyValuePair<int, OffsetSeries> pair in tracked.Series) { list.Add(pair.Value); }
            list.Sort(delegate(OffsetSeries a, OffsetSeries b)
            {
                if (a.Changes != b.Changes) { return b.Changes.CompareTo(a.Changes); }
                return a.Offset.CompareTo(b.Offset);
            });
            if (max > 0 && list.Count > max) { list.RemoveRange(max, list.Count - max); }
            return list;
        }
    }
}
