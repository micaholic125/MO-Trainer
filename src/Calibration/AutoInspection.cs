using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using MoTrainer.Core;

namespace MoTrainer.Calibration
{
    /// <summary>一个"疑似类型对象指针"的字段偏移。</summary>
    public sealed class TypePointerHit
    {
        /// <summary>对象内的偏移。</summary>
        public uint Offset;

        /// <summary>该组里一共有多少个对象参与统计。</summary>
        public int Members;

        /// <summary>这些对象在该偏移上取出**多少个不同的取值**。</summary>
        public int DistinctCount;

        /// <summary>取值里出现次数最多的那个（用于报告展示）。</summary>
        public uint SampleTarget;

        /// <summary>目标对象里找得到的"建造 ID 字符串"（如 GAPOWR）；空串 = 没找到。</summary>
        public string IdString = "";

        /// <summary>目标看起来像"阵营房子"（在其 +0x30C 处读到的是金钱）→ 那不是类型对象。</summary>
        public bool LooksLikeHouse;

        /// <summary>共用程度：成员数 ÷ 不同取值数。越大越像"同类共用一份"。</summary>
        public float Sharing { get { return DistinctCount <= 0 ? 0f : (float)Members / DistinctCount; } }
    }

    /// <summary>一次"找类型对象"的分析结果。</summary>
    public sealed class TypePointerAnalysis
    {
        public int Groups;
        public int MembersScanned;
        public readonly List<TypePointerHit> Hits = new List<TypePointerHit>();
        public readonly List<string> GroupLines = new List<string>();
        public string Note = "";

        public bool Ok { get { return Note.Length == 0; } }

        /// <summary>最像"类型对象指针"的那一个：先要有建造 ID 字符串，再按共用程度排。</summary>
        public TypePointerHit Best
        {
            get
            {
                TypePointerHit best = null;
                for (int i = 0; i < Hits.Count; i++)
                {
                    TypePointerHit hit = Hits[i];
                    if (hit.IdString.Length == 0 || hit.LooksLikeHouse) { continue; }
                    if (best == null || hit.Sharing > best.Sharing) { best = hit; }
                }
                return best;
            }
        }
    }

    /// <summary>
    /// 「自动巡检」—— 不需要用户额外点击、也不需要额外开局的自动分析。
    ///
    /// 起因（`CLAUDE.md` 六、用户特征）：剩下的真机项本来是"一项一趟"：
    /// 开一局 → 在游戏里选中某个东西 → 点按钮 → 导出日志发回。用户已经连续配合 6 轮。
    /// 这里把其中**能靠程序自己算的**部分做成"正常玩一局 + 点一次导出"就自动完成。
    ///
    /// 当前包含两项：
    ///   ① **找"类型对象"**（`FindTypePointers`）—— 电力 B 方案卡在这里；
    ///   ② **主链反查**（`RefitMainChain`）—— `[S3]` 的主链 0x683D4C 早已 0 命中，
    ///      一直靠候选链兜底；从真实房子地址反推静态根，就能把它重新校准回来。
    ///
    /// **本类只读，绝不写游戏内存。**
    /// </summary>
    public static class AutoInspection
    {
        /// <summary>只扫对象头部这么多字节 —— 类型指针在头部（电力侦查时锁定过 +0x0E4 一带）。</summary>
        public const int HeaderBytes = 0x800;

        /// <summary>同一类型表至少要有这么多对象，统计才有意义。</summary>
        public const int MinGroupMembers = 4;

        /// <summary>一个类型表最多取这么多对象参与统计（够用，且别把报告和耗时撑爆）。</summary>
        public const int MaxMembersPerGroup = 64;

        /// <summary>每个类型表最多记这么多候选偏移。</summary>
        public const int MaxHitsPerGroup = 8;

        /// <summary>
        /// 在对象头部找一个"**被同类型表的多个对象共用、但不同型号各不相同**"的指针字段。
        ///
        /// 为什么不能直接拿「类型表」当"同一种建筑"用（2026-10-09 用真机报告核过）：
        /// 真机报告里类型表只有十来个（116 个 / 64 个 / 23 个 / 16 个各一组），
        /// 说明这个"类型表"对应的是 **C++ 类别**（所有建筑共用一个），
        /// **不是"电量厂 / 兵营 / 矿厂"这种型号**。所以"同类型表的两个对象"并不保证是同一型号，
        /// 直接拿它做"同类对比法"会得出错误结论。
        ///
        /// 改用这个判据（可以在同类别内部自己找出型号）：
        ///   · 同一个类型表内，某个偏移上的取值**明显少于对象数** —— 说明有多个对象共用同一个取值；
        ///   · 共用得越厉害（成员数 ÷ 不同取值数 越大），越像"型号说明书"这种**同类共用一份**的东西；
        ///   · 再拿"目标里有没有建造 ID 字符串（如 GAPOWR）"当**确认**——
        ///     这是 `PowerScout` 已经用过的、类型对象独有的特征。
        ///
        /// 排掉两个必然会撞上的假阳性：
        ///   · **偏移 0** 就是类型表本身，它"同类相同、异类不同"全都满足 —— 但它不是我们找的类型对象；
        ///   · **阵营指针**：同一方的建筑全指向同一个房子，共用程度也很高 ——
        ///     用"目标在金钱偏移处读到的是不是你的金钱"把房子认出来排掉。
        /// </summary>
        public static TypePointerAnalysis FindTypePointers(
            MemoryIO memory, uint moduleBase, uint moduleSize,
            IList<HarvestObjectRow> roster, uint houseMoneyOffset, uint myMoney)
        {
            TypePointerAnalysis result = new TypePointerAnalysis();
            if (memory == null) { result.Note = "没有内存访问器"; return result; }
            if (roster == null || roster.Count == 0) { result.Note = "对象名单是空的（本次没采到）"; return result; }

            // 按类型表分组
            Dictionary<uint, List<uint>> groups = new Dictionary<uint, List<uint>>();
            for (int i = 0; i < roster.Count; i++)
            {
                HarvestObjectRow row = roster[i];
                if (row.Address < 0x10000 || row.VTable == 0) { continue; }
                if (row.VTable < moduleBase || row.VTable >= moduleBase + moduleSize) { continue; }

                List<uint> list;
                if (!groups.TryGetValue(row.VTable, out list))
                {
                    list = new List<uint>();
                    groups[row.VTable] = list;
                }
                if (list.Count < MaxMembersPerGroup) { list.Add(row.Address); }
            }

            List<KeyValuePair<uint, List<uint>>> ordered = new List<KeyValuePair<uint, List<uint>>>();
            foreach (KeyValuePair<uint, List<uint>> pair in groups)
            {
                if (pair.Value.Count >= MinGroupMembers) { ordered.Add(pair); }
            }
            ordered.Sort(delegate(KeyValuePair<uint, List<uint>> a, KeyValuePair<uint, List<uint>> b)
            {
                return b.Value.Count.CompareTo(a.Value.Count);
            });

            result.Groups = ordered.Count;
            if (ordered.Count == 0)
            {
                result.Note = string.Format(CultureInfo.InvariantCulture,
                    "场上没有任何一个「类型表」下有 {0} 个以上对象，无法做同类对比（多造几个同型号建筑再采一次）",
                    MinGroupMembers);
                return result;
            }

            for (int g = 0; g < ordered.Count; g++)
            {
                uint vtable = ordered[g].Key;
                List<uint> members = ordered[g].Value;
                result.MembersScanned += members.Count;
                result.GroupLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "类型表 0x{0:X8}：{1} 个对象参与统计", vtable, members.Count));

                List<TypePointerHit> local = new List<TypePointerHit>();

                // 偏移 0 是类型表本身：它一定"同类相同"，会干扰排序，直接跳过
                for (int offset = 4; offset + 4 <= HeaderBytes; offset += 4)
                {
                    uint first = 0;
                    bool firstSet = false;
                    bool bad = false;
                    List<uint> distinct = new List<uint>();
                    Dictionary<uint, int> counts = new Dictionary<uint, int>();

                    for (int m = 0; m < members.Count; m++)
                    {
                        uint value;
                        if (!memory.ReadUInt32(members[m] + (uint)offset, out value)) { bad = true; break; }

                        // 必须是一个"像指针"的值：类型对象不会落在 64KB 以下，也不会是 0 / -1
                        if (value < 0x10000 || value > 0x7FFFFFFF) { bad = true; break; }

                        if (!firstSet) { first = value; firstSet = true; }

                        int existing;
                        if (counts.TryGetValue(value, out existing)) { counts[value] = existing + 1; }
                        else { counts[value] = 1; distinct.Add(value); }
                    }
                    if (bad || !firstSet) { continue; }

                    // 每个成员各不相同 = 这是"按对象"的指针（例如对象自己的界面数据），不是"按类型"
                    if (distinct.Count >= members.Count) { continue; }

                    TypePointerHit hit = new TypePointerHit();
                    hit.Offset = (uint)offset;
                    hit.Members = members.Count;
                    hit.DistinctCount = distinct.Count;
                    hit.SampleTarget = first;
                    local.Add(hit);
                }

                // 共用得越厉害越可疑，取前几名（不在这里就下结论，让报告把候选都摊开）
                local.Sort(delegate(TypePointerHit a, TypePointerHit b)
                {
                    if (a.DistinctCount != b.DistinctCount) { return a.DistinctCount.CompareTo(b.DistinctCount); }
                    return a.Offset.CompareTo(b.Offset);
                });

                for (int i = 0; i < local.Count && i < MaxHitsPerGroup; i++)
                {
                    TypePointerHit hit = local[i];
                    hit.IdString = FindIdString(memory, hit.SampleTarget, 0x400);
                    hit.LooksLikeHouse = LooksLikeHouse(memory, hit.SampleTarget, houseMoneyOffset, myMoney);
                    result.Hits.Add(hit);
                }
            }

            if (result.Hits.Count == 0)
            {
                result.Note = "没有找到任何「多个对象共用同一取值」的指针字段 —— 本局的建筑数可能太少";
            }
            return result;
        }

        /// <summary>
        /// 目标对象里有没有"指向可打印字符串的指针" —— 类型对象里存着建造 ID（如 GAPOWR）与
        /// EVA 语音名，这是它最像"说明书"的特征（`PowerScout` 起就在用这条判据）。
        /// </summary>
        private static string FindIdString(MemoryIO memory, uint address, int scanBytes)
        {
            if (address < 0x10000) { return ""; }
            byte[] block = memory.ReadBytesQuiet(address, scanBytes);
            if (block == null) { return ""; }

            for (int i = 0; i + 4 <= block.Length; i += 4)
            {
                uint pointer = ReadUInt32(block, i);
                if (pointer < 0x10000 || pointer > 0x7FFFFFFF) { continue; }

                string candidate = ReadIdentifier(memory, pointer);
                if (candidate.Length > 0) { return candidate; }
            }
            return "";
        }

        /// <summary>
        /// 读一个"建造 ID 风格"的字符串。
        ///
        /// 刻意卡得很死（只认 A-Z a-z 0-9 _ . -），因为内存里到处都是看起来像字符串的字节：
        /// 放宽一格就会满屏都是"疑似 ID"，等于没有信息。宁可漏，不要错。
        /// </summary>
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
                if (!ok) { return ""; }          // 出现任何别的字符 → 不是 ID，直接否掉
                if (b >= (byte)'A' && b <= (byte)'Z') { sawUpper = true; }
                length++;
            }

            // 建造 ID 至少 4 个字符且含大写（GAPOWR / GAPILE / NAPOWR）。
            // 不含大写的短串多半是别的东西（"true"、"none" 这类）。
            if (length < 4 || !sawUpper) { return ""; }
            return Encoding.ASCII.GetString(raw, 0, length);
        }

        /// <summary>目标看起来像"阵营房子"吗 —— 判据是它在金钱偏移处读到的正好是你的金钱。</summary>
        private static bool LooksLikeHouse(MemoryIO memory, uint address, uint moneyOffset, uint myMoney)
        {
            if (address < 0x10000 || moneyOffset == 0 || myMoney == 0) { return false; }

            uint value;
            if (!memory.ReadUInt32(address + moneyOffset, out value)) { return false; }
            return value == myMoney;
        }

        /// <summary>把分析结果写成报告段落（人可读，也能直接回传分析）。</summary>
        public static string FormatTypePointers(TypePointerAnalysis analysis, bool rosterUsable)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 四、自动巡检 · 找「类型对象」（电力 B 方案用）===");
            text.AppendLine("（目的：找到那个「同型号共用一份的说明书」—— 电厂的发电量记在里面。");
            text.AppendLine("  判据：同一类型表内，某偏移的取值被多个对象**共用**（取值种数明显少于对象数）；");
            text.AppendLine("  确认：取值指向的对象里有「建造 ID」字符串，如 GAPOWR；");
            text.AppendLine("  排除：偏移 0（那是类型表本身）与阵营指针（指向房子，非类型）。）");
            text.AppendLine();

            if (!rosterUsable)
            {
                text.AppendLine("（本次没有读到对象名单，跳过）");
                text.AppendLine();
                return text.ToString();
            }

            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "参与统计：{0} 个类型表，共 {1} 个对象", analysis.Groups, analysis.MembersScanned));
            for (int i = 0; i < analysis.GroupLines.Count; i++)
            {
                text.AppendLine("  " + analysis.GroupLines[i]);
            }
            if (analysis.Note.Length > 0)
            {
                text.AppendLine("  ！" + analysis.Note);
            }
            text.AppendLine();

            if (analysis.Hits.Count == 0)
            {
                text.AppendLine("候选字段：无");
                text.AppendLine();
                return text.ToString();
            }

            text.AppendLine("候选字段（按「共用程度」排，越靠前越像说明书）：");
            text.AppendLine("  类型表      偏移      共用        不同取值  指向        建造ID提示        像房子");
            for (int i = 0; i < analysis.Hits.Count; i++)
            {
                TypePointerHit hit = analysis.Hits[i];
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  {0,-11} +0x{1:X3}  {2,2}个对象/{3,-3}种 {4,8:0.0}  0x{5:X8}  {6,-18} {7}",
                    "", hit.Offset, hit.Members, hit.DistinctCount, hit.Sharing,
                    hit.SampleTarget,
                    hit.IdString.Length > 0 ? hit.IdString : "（无）",
                    hit.LooksLikeHouse ? "**是**（排除）" : "否"));
            }

            TypePointerHit best = analysis.Best;
            text.AppendLine();
            if (best == null)
            {
                text.AppendLine("结论：**没有找到带「建造 ID 字符串」的候选** —— 本局能看到的型号太少，");
                text.AppendLine("      换一局多造几座同型号建筑再采一次（这一步的判据就是靠多个同型号对象比对出来的）。");
            }
            else
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "结论：最像「类型对象指针」的是对象 **+0x{0:X}** —— 目标像 0x{1:X8}，"
                    + "里面找到建造 ID「{2}」（{3} 个对象共用 {4} 种取值）",
                    best.Offset, best.SampleTarget, best.IdString, best.Members, best.DistinctCount));
                text.AppendLine("      下一步：对同一类型表里**属于不同型号**的两个对象读这个偏移，");
                text.AppendLine("      取值应当不同；对同型号的两个对象读，取值应当相同。两条都成立才算真找到。");
            }
            text.AppendLine();
            return text.ToString();
        }

        // ============================================================ 主链反查（S3）

        /// <summary>
        /// 从"金钱此刻真正落在哪个对象上"反推静态指针链。
        ///
        /// 为什么需要：主链 `0x683D4C → +0x30C` 在真机上**早就 0 命中**了，
        /// 一直靠候选链里的第 2 条兜底（2026-09-22 起每次会话都回退几十上百次）。
        /// 主链是"第一顺位"，它不准就意味着每次解析都要多试几次；
        /// 而且一旦候选链哪天也失效，就没有后备了。
        ///
        /// 做法：金钱地址 = 房子 + `0x30C`（这三条链的真机口径一致），
        /// 拿房子地址去 `PointerScanner` 反查 —— 它本来就是干这个的，只是以前从没在真机上跑过这条路径。
        ///
        /// 耗时：全内存回溯，可能要十几秒，所以**放在采集线程里跑**，界面不会卡住。
        /// 用 `CancelAfter` 卡死上限，超时也照样出报告（如实写明"超时"）。
        /// </summary>
        public static string RefitMainChain(
            ICalibrationTarget target, CalibrationProfile profile, uint secondsBudget)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 五、自动巡检 · 主链反查（[S3] 用）===");
            text.AppendLine("（目的：主链 0x683D4C 在真机上早就 0 命中，一直靠候选链兜底。");
            text.AppendLine("  这里从「金钱此刻真正落在的房子」反推它该挂在哪条静态根上。）");
            text.AppendLine();

            if (target == null || !target.IsReady) { text.AppendLine("（游戏未连接，跳过）"); text.AppendLine(); return text.ToString(); }
            if (profile == null || !profile.Money.IsCalibrated)
            {
                text.AppendLine("（金钱未校准，无法反查）");
                text.AppendLine();
                return text.ToString();
            }

            uint moneyAddress = profile.Money.ResolveOrZero(target.Memory, target.ModuleBase);
            if (moneyAddress == 0)
            {
                text.AppendLine("（金钱地址当前解析不出来，无法反查）");
                text.AppendLine();
                return text.ToString();
            }

            uint houseAddress = moneyAddress - MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset;
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "金钱地址 0x{0:X8} → 房子对象 0x{1:X8}（减去金钱偏移 0x{2:X}）",
                moneyAddress, houseAddress, MoTrainer.Cheats.TechnoOwnership.HouseMoneyOffset));
            text.AppendLine("正在全内存反查指针链…");
            text.AppendLine();

            try
            {
                PointerScanOptions options = new PointerScanOptions();
                options.MaxLevels = 4;

                System.Threading.CancellationTokenSource cancel = new System.Threading.CancellationTokenSource();
                if (secondsBudget > 0) { cancel.CancelAfter(TimeSpan.FromSeconds(secondsBudget)); }

                PointerScanner scanner = new PointerScanner(target.Memory);
                PointerScanReport report = scanner.Scan(houseAddress, target.ModuleBase, target.ModuleSize,
                    target.PointerRegions, options, cancel.Token, delegate(string s) { });

                if (report.Cancelled)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "**超时**（超过 {0} 秒，已中止）—— 这条不完整，下次可以加预算再跑", secondsBudget));
                }
                else if (!report.Ok)
                {
                    text.AppendLine("反查失败：" + report.Error);
                }
                else if (report.Paths.Count == 0)
                {
                    text.AppendLine("反查 0 命中 —— 房子对象可能不在可回溯的区域内（这本身也是一条有用的结论）");
                }
                else
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "反查成功：找到 {0} 条候选链，耗时 {1:0.0} 秒，扫描 {2:N0} 字节",
                        report.Paths.Count, report.Elapsed.TotalSeconds, report.BytesScanned));
                    text.AppendLine();
                    text.AppendLine("候选链（前 8 条，静态根 + 各级偏移）：");
                    for (int i = 0; i < report.Paths.Count && i < 8; i++)
                    {
                        text.AppendLine("  " + report.Paths[i].Format(target.ModuleBase));
                    }
                    text.AppendLine();
                    text.AppendLine("现用主链：" + DescribeChain(profile.Money.Chain));
                    text.AppendLine("判据：反查结果里若有哪条**比现用主链更短**、且静态根落在模块的数据段，");
                    text.AppendLine("      就值得拿它当新主链（改完必须换一局复验，见 docs/05 的「跨会话复验」）。");
                }
            }
            catch (Exception ex)
            {
                text.AppendLine("反查过程出错：" + ex.Message);
            }

            text.AppendLine();
            return text.ToString();
        }

        private static string DescribeChain(PointerChain chain)
        {
            if (chain == null || chain.IsEmpty) { return "(空)"; }
            uint[] offsets = chain.Offsets;
            StringBuilder text = new StringBuilder();
            for (int i = 0; i < offsets.Length; i++)
            {
                if (i > 0) { text.Append(" → "); }
                text.Append(i == 0
                    ? string.Format(CultureInfo.InvariantCulture, "静态根 0x{0:X}", offsets[i])
                    : string.Format(CultureInfo.InvariantCulture, "+0x{0:X}", offsets[i]));
            }
            return text.ToString();
        }

        private static uint ReadUInt32(byte[] buffer, int offset)
        {
            return (uint)(buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }
    }
}
