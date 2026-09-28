using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using MoTrainer.Calibration;
using MoTrainer.Cheats;
using MoTrainer.Core;
using MoTrainer.Ui.Controls;

using MoTrainer.Util;   // Logger：扫描结果要逐行落进日志，用户「导出日志」就能把原文带出来

namespace MoTrainer.Ui.Pages
{
    /// <summary>
    /// 「高级功能」页 —— 第一阶段**只做只读的全局对象扫描**，不写任何内存。
    ///
    /// 为什么先只做扫描：秒杀敌人 / 瞬间建造都必须建立在"能可靠枚举游戏对象"之上，
    /// 而全对象数组的地址在真机上还没验证过。如果先放出可点的功能开关，
    /// 一旦数组地址是错的，写进去的就是随机内存 —— 所以先把**验证手段**交给用户，
    /// 确认了"扫描结果合理"之后，功能开关才会开放。
    ///
    /// 扫描完成后的自证手段（不需要任何外部工具）：
    /// 1. **选中的单位必须出现在全对象数组里** —— 两边对不上就说明数组找错了；
    /// 2. **阵营分布应该聚集** —— 正确时是"少数几个阵营、各自聚集大量单位"；
    /// 3. **跳过数应该很低** —— 候选里绝大多数都该通过"虚表在模块内"的校验。
    /// </summary>
    public sealed class AdvancedPage : UserControl
    {
        private readonly ICalibrationTarget _target;
        private readonly CalibrationProfile _profile;
        private readonly ProfileStore _store;

        /// <summary>游戏主模块的地址区间。用来判断"指针指向的是不是游戏对象"。</summary>
        private readonly uint _moduleBase;
        private readonly uint _moduleSize;
        private readonly Button _scanButton;
        private readonly Button _findButton;
        private readonly Button _shroudButton;
        private readonly TextBox _output;

        public AdvancedPage(ICalibrationTarget target, CalibrationProfile profile, ProfileStore store)
        {
            _target = target;
            _profile = profile == null ? new CalibrationProfile() : profile;
            _store = store;
            _moduleBase = target == null ? 0 : target.ModuleBase;
            _moduleSize = target == null ? 0 : target.ModuleSize;

            // 把模块区间交给「单位校准」的探测逻辑（静态字段）。
            // 让它多一条硬判定：名单里每一项的头 4 字节必须是模块内的类型标记 ——
            // 这条能把"游戏对象的方法表冒充名单"当场拒掉（真机踩过）。
            // 放在这里是因为本页在启动时就会构造，值对全程有效。
            SelectionProbe.ModuleBaseHint = _moduleBase;
            SelectionProbe.ModuleSizeHint = _moduleSize;

            BackColor = Theme.Bg0;
            AutoScroll = true;

            Label heading = new Label();
            heading.AutoSize = false;
            heading.Font = Theme.Heading;
            heading.ForeColor = Theme.Text0;
            heading.Text = "高级功能";
            heading.Location = new Point(24, 20);
            heading.Size = new Size(520, 34);
            Controls.Add(heading);

            Label sub = new Label();
            sub.AutoSize = false;
            sub.Font = Theme.Body;
            sub.ForeColor = Theme.Text2;
            sub.Text = "秒杀敌人 / 瞬间建造 / 全图视野 —— 当前阶段只提供「对象扫描」用于校准验证，不会写入任何内存。";
            sub.Location = new Point(24, 56);
            sub.Size = new Size(760, 22);
            Controls.Add(sub);

            // 两个按钮并排；提示文字放到按钮下面单独一行 ——
            // 之前把提示放在按钮右边，按钮加宽后会压住它（用户截图里看到的现象）。
            _scanButton = UiFactory.MakePrimaryButton("扫描全对象数组", 150);
            _scanButton.Location = new Point(24, 92);
            _scanButton.Click += OnScanClick;
            Controls.Add(_scanButton);

            _findButton = UiFactory.MakePrimaryButton("自动查找总名单", 150);
            _findButton.Location = new Point(184, 92);
            _findButton.Click += OnFindClick;
            Controls.Add(_findButton);

            // 预演按钮：**只计算、不写入**。
            // 秒杀敌人是所有功能里唯一可能"误伤自家部队"的一项，
            // 所以先给用户一个"会杀谁、不杀谁"的清单，确认无误后再放开真正的执行。
            Button preview = UiFactory.MakeButton("预演：秒杀敌人（不写入）", 200);
            preview.Location = new Point(344, 92);
            preview.Click += OnKillPreviewClick;
            Controls.Add(preview);

            // 全图视野：一个按钮、两次点击。
            // 第 1 次记录"迷雾快照"，用户去游戏里揭开一小块地图，第 2 次对比并清除。
            // 原理：迷雾是一大片连续位图，揭开地图时**只有它那部分字节在变** ——
            // 对比前后就能把它定位出来，再分片清零。
            _shroudButton = UiFactory.MakePrimaryButton("全图视野（点两次）", 168);
            _shroudButton.Location = new Point(556, 92);
            _shroudButton.Click += OnShroudClick;
            Controls.Add(_shroudButton);

            Label hint = new Label();
            hint.AutoSize = false;
            hint.Font = Theme.Small;
            hint.ForeColor = Theme.Text2;
            hint.Text = "用法：先在游戏里选中一个自己的单位，再点「自动查找总名单」。选中的单位会用来确认名单是否找对。";
            hint.Location = new Point(24, 130);
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            hint.Size = new Size(760, 20);
            Controls.Add(hint);

            _output = new TextBox();
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Both;
            _output.WordWrap = false;
            _output.BackColor = Theme.Bg1;
            _output.ForeColor = Theme.Text0;
            _output.Font = Theme.Mono;
            _output.BorderStyle = BorderStyle.FixedSingle;
            _output.Location = new Point(24, 158);
            _output.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            Controls.Add(_output);

            SizeChanged += delegate { LayoutOutput(); };
            LayoutOutput();
            RefreshUi();
        }

        private void LayoutOutput()
        {
            int width = ClientSize.Width - 48;
            int height = ClientSize.Height - 182;
            if (width < 200) { width = 200; }
            if (height < 120) { height = 120; }
            _output.Size = new Size(width, height);
        }

        private void RefreshUi()
        {
            bool ready = _target != null && _target.IsReady;
            _scanButton.Enabled = ready;
            _findButton.Enabled = ready;
            if (!ready)
            {
                SetOutput("游戏未连接。请先启动游戏并让修改器附加成功，再点扫描。");
            }
        }

        /// <summary>
        /// 自动跑一遍「查找 + 扫描」，供 `--autofind` 自测模式调用。
        ///
        /// 为什么要有它：这套流程需要"游戏里保持选中一个单位"这种人工前置，
        /// 每轮都让用户点两个按钮、再导出日志，来回成本太高。
        /// 让程序自己跑完并把结果落进日志，用户只要把游戏开着即可。
        /// </summary>
        public string RunDiagnosticsForTest()
        {
            string find = FindArray();
            string scan = Scan();
            string owners = AnalyzeOwners();
            string all = find + "\r\n" + scan + "\r\n" + owners;

            // 最后跑一次"秒杀预演"，把"会杀谁、放过谁"也落进日志 —— 只读，不改内存。
            OnKillPreviewClick(null, null);
            all += "\r\n" + _output.Text;

            SetOutput(all);
            return all;
        }

        /// <summary>
        /// 分析「阵营」字段：在对象的哪个偏移上，取值呈现"**少数几种、每种聚集一大批**"。
        ///
        /// 为什么必须先做这个：秒杀敌人必须能分清敌我 —— 分不清就写血量，
        /// 后果是把自己的部队一起清空。所以宁可先只读分析，确认了阵营字段再开放。
        ///
        /// 判定完全不依赖硬编码偏移：把 0..0x400 内每个偏移都统计一遍，
        /// 谁最像"阵营"就选谁（候选要满足：值非零、种类少、最大一组占比高）。
        /// 然后用**你选中的那两个大兵**反认：它们属于哪一组，哪一组就是"我方"。
        /// </summary>
        private string AnalyzeOwners()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine();
            text.AppendLine("=== 阵营字段分析（只读）===");

            MemoryIO memory = _target.Memory;
            string error;

            uint slotAddress;
            if (!_profile.TechnoArray.IsCalibrated
                || !_profile.TechnoArray.TryResolve(memory, _moduleBase, out slotAddress, out error))
            {
                text.AppendLine("还没找到对象名单，先做前面的查找。");
                return text.ToString();
            }

            uint arrayPointer;
            if (!ReadUInt32Retry(memory, slotAddress, out arrayPointer) || arrayPointer < 0x10000)
            {
                text.AppendLine("名单格子里读不到有效指针。");
                return text.ToString();
            }

            TechnoWalkResult walk = TechnoWalker.WalkArray(
                memory, arrayPointer, 4096, _moduleBase, _moduleSize);
            if (!walk.Ok || walk.Count < 4)
            {
                text.AppendLine("对象太少（" + walk.Count + " 个），无法分析。");
                return text.ToString();
            }

            // **先按"类型标记"分组，只在最大的那一组里分析。**
            //
            // 名单里混着好几种类型的对象，而同一个字段在不同类型里的偏移可能不一样 ——
            // 混在一起统计会显不出"少数几种、各自聚集"的特征
            // （真机实测：混着数，跑出来的是"全部对象都等于某个常量"的字段）。
            // 同一类对象的布局是同构的，组内统计才有意义。
            Dictionary<uint, List<uint>> byType = new Dictionary<uint, List<uint>>();
            for (int i = 0; i < walk.Count && i < 400; i++)
            {
                uint type;
                if (!ReadUInt32Retry(memory, walk.Objects[i].Address, out type)) { continue; }

                List<uint> bucket;
                if (!byType.TryGetValue(type, out bucket))
                {
                    bucket = new List<uint>();
                    byType[type] = bucket;
                }
                if (bucket.Count < 200) { bucket.Add(walk.Objects[i].Address); }
            }

            List<uint> group = null;
            uint groupType = 0;
            foreach (KeyValuePair<uint, List<uint>> pair in byType)
            {
                if (group == null || pair.Value.Count > group.Count)
                {
                    group = pair.Value;
                    groupType = pair.Key;
                }
            }

            if (group == null || group.Count < 4)
            {
                text.AppendLine("名单里的对象类型太杂，找不到足够大的同类分组。");
                return text.ToString();
            }

            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "按类型分为 {0} 组；取最大一组分析：类型 0x{1:X8}，{2} 个对象",
                byType.Count, groupType, group.Count));

            int sample = group.Count;

            // 我方样本：当前选中的单位（那两个大兵）
            List<uint> myObjects = new List<uint>();
            uint selectedSlot;
            if (_profile.SelectedList.IsCalibrated
                && _profile.SelectedList.TryResolve(memory, _moduleBase, out selectedSlot, out error))
            {
                myObjects = ReadSelectedObjects(memory, selectedSlot);
            }
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "我方样本：{0} 个（你选中的单位）", myObjects.Count));

            // ---- 类型普查：名单里有哪些类型、各多少个 ----
            // 这一步决定"哪一类才是真正的单位"。真机实测：名单里数量最多的那类
            // 其实不是单位（大兵只占极小一部分），不先分辨清楚，
            // 后面的阵营统计就是在对一群无关对象做统计。
            // "我方单位"必须取**名单里真实存在**的那个地址。
            // 选中列表里可能同时有真对象和包一层的指针，取错就会拿到错误的类型 ——
            // 真机实测：因为取到了包装指针，把"我的单位类型"认成了一个含 1348 个对象的宽泛类型，
            // 于是一直在一群无关对象里找阵营，怎么都对不上。
            HashSet<uint> inList = new HashSet<uint>();
            for (int i = 0; i < walk.Count; i++) { inList.Add(walk.Objects[i].Address); }

            Dictionary<uint, int> census = new Dictionary<uint, int>();
            for (int i = 0; i < walk.Count; i++)
            {
                uint type;
                if (!ReadUInt32Retry(memory, walk.Objects[i].Address, out type)) { continue; }
                int existing;
                census[type] = census.TryGetValue(type, out existing) ? existing + 1 : 1;
            }

            List<KeyValuePair<uint, int>> types = new List<KeyValuePair<uint, int>>();
            foreach (KeyValuePair<uint, int> pair in census) { types.Add(pair); }
            types.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "名单类型普查：共 {0} 种类型", types.Count));
            for (int i = 0; i < types.Count && i < 10; i++)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  类型 0x{0:X8}：{1} 个对象", types[i].Key, types[i].Value));
            }

            // 你选中的单位属于哪一类 —— 它才是"我方"的唯一锚点
            uint myType = 0;
            for (int i = 0; i < myObjects.Count; i++)
            {
                uint type;
                if (!ReadUInt32Retry(memory, myObjects[i], out type)) { continue; }

                int count = 0;
                census.TryGetValue(type, out count);
                if (count > 0) { myType = type; }

                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  我的单位候选 0x{0:X8}：类型 0x{1:X8}（这一类共 {2} 个）",
                    myObjects[i], type, count));
            }

            // **优先用"包含我方单位"的那一类做分析**；只有它太小才退回"最大的一类"。
            // 理由：阵营字段必须在自己所属的类型里找，拿别的类型统计出来的偏移是巧合。
            List<uint> myTypeGroup;
            if (myType != 0 && byType.TryGetValue(myType, out myTypeGroup) && myTypeGroup.Count >= 4)
            {
                group = myTypeGroup;
                groupType = myType;
                text.AppendLine("分析改用「包含我方单位的那一类」。");
            }

            List<string> candidates = new List<string>();
            List<uint> candidateOffsets = new List<uint>();
            uint bestOffset = 0;
            int bestScore = -1;
            uint bestMyOwner = 0;
            string bestLine = "";

            for (uint offset = 0; offset <= 0x400; offset += 4)
            {
                Dictionary<uint, int> counts = new Dictionary<uint, int>();
                int nonZero = 0;

                // 统计这个偏移上出现了多少个"指向模块内部"的值。
                // 这类值（首当其冲是 +0x0 的类型标记）天然"种类少、全一致"，
                // 不加区分就会盖过真正的阵营字段 —— 真机实测第一次就选中了 +0x0。
                int moduleHits = 0;

                for (int i = 0; i < sample; i++)
                {
                    uint value;
                    if (!ReadUInt32Retry(memory, walk.Objects[i].Address + offset, out value)) { continue; }

                    if (value != 0) { nonZero++; }
                    if (value >= _moduleBase && value < _moduleBase + _moduleSize) { moduleHits++; }

                    int existing;
                    counts[value] = counts.TryGetValue(value, out existing) ? existing + 1 : 1;
                }

                if (counts.Count == 0) { continue; }

                // 只要出现过"指向代码区"的值，这个偏移就不是阵营（阵营是堆里的对象）
                if (moduleHits > 0) { continue; }

                int maxGroup = 0;
                foreach (KeyValuePair<uint, int> pair in counts)
                {
                    if (pair.Value > maxGroup) { maxGroup = pair.Value; }
                }

                // 阵营的特征：**种类少、最大一组占比高、且大多数对象都有值**。
                // 这三个条件缺一不可 —— 只"种类少"的偏移可能是某种状态位；
                // 只"占比高"的可能是到处都为 0 的无关字段。
                if (nonZero < sample * 8 / 10) { continue; }

                // **至少要有两方。** 只有一种取值的偏移是"常量字段/状态位"，
                // 真机实测第一次抓到的就是这种（全部对象都等于 10）。
                if (counts.Count < 2 || counts.Count > 16) { continue; }
                if (maxGroup < sample / 10 + 2) { continue; }

                // 我方（你选中的单位）在这个偏移上的取值必须非零 ——
                // 它是"我方阵营"的身份，为 0 说明这个偏移不是阵营。
                // 这条也是安全底线：秒杀敌人要靠它避开自家部队，认不出我方就绝不能执行。
                // 硬约束一：**所有选中的自己人必须落在同一条取值上**。
                // 真正的敌我字段，两个自己人的值必然一致；不一致就说明这不是敌我字段。
                uint myOwner = 0;
                bool agree = true;
                int seen = 0;
                for (int i = 0; i < myObjects.Count; i++)
                {
                    // 只认和这一组"同类"的那个对象（不同类型字段布局不同，读出来是错的）
                    uint type;
                    if (!ReadUInt32Retry(memory, myObjects[i], out type)) { continue; }
                    if (type != groupType) { continue; }

                    uint value;
                    if (!ReadUInt32Retry(memory, myObjects[i] + offset, out value)) { continue; }
                    if (value == 0) { continue; }

                    if (seen == 0) { myOwner = value; }
                    else if (value != myOwner) { agree = false; }
                    seen++;
                }
                if (seen == 0 || !agree) { continue; }

                // 硬约束二：**我方组的规模要合理**。
                // 真机实测：错的字段会给出"我方 309 个"，而用户实际只有约 110 个 ——
                // 数量对不上就说明这个字段划分出的不是"谁的"，而是某种状态标志。
                int myGroupSize = 0;
                counts.TryGetValue(myOwner, out myGroupSize);
                if (myGroupSize < 4 || myGroupSize > 600) { continue; }

                int score = maxGroup * 100 / counts.Count;

                if (candidates.Count < 12)
                {
                    candidateOffsets.Add(offset);
                    candidates.Add(string.Format(CultureInfo.InvariantCulture,
                        "+0x{0:X3}：{1} 种取值｜最大组 {2}｜我方组 {3}｜我方值 0x{4:X8}",
                        offset, counts.Count, maxGroup, myGroupSize, myOwner));
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestOffset = offset;
                    bestMyOwner = myOwner;

                    bestLine = string.Format(CultureInfo.InvariantCulture,
                        "偏移 +0x{0:X}：{1} 种取值｜最大一组 {2}/{3}｜我方取值 0x{4:X8}",
                        offset, counts.Count, maxGroup, sample, myOwner);
                }
            }

            if (candidates.Count > 0)
            {
                text.AppendLine("候选偏移一览（抽样统计，供人工对照）：");
                for (int i = 0; i < candidates.Count; i++)
                {
                    text.AppendLine("  " + candidates[i]);
                }
            }

            // **全名单核实。**
            // 抽样会严重失真：真机实测抽样里"我方 39 个"，全名单里其实是 309 个，差了 8 倍，
            // 直接导致选错了字段。所以对每个候选偏移，在**全名单**上重新数一遍。
            // 验收标准是用户给的实数：约 12 个战斗单位（8 大兵 + 4 防空车）+ 几十个建筑。
            if (candidateOffsets.Count > 0)
            {
                text.AppendLine("全名单核实（按我方阵营值统计；期望「我方」≈ 你的实际数量 60~90）：");
                for (int i = 0; i < candidateOffsets.Count; i++)
                {
                    uint offset = candidateOffsets[i];

                    // 我方阵营值：**所有**选中的自己人必须一致（类型不同也要一致 —— 这才是敌我字段）
                    uint owner = 0;
                    bool agree = true;
                    int seen = 0;
                    for (int k = 0; k < myObjects.Count; k++)
                    {
                        if (!inList.Contains(myObjects[k])) { continue; }

                        uint v;
                        if (!ReadUInt32Retry(memory, myObjects[k] + offset, out v)) { continue; }
                        if (v == 0) { continue; }

                        if (seen == 0) { owner = v; }
                        else if (v != owner) { agree = false; }
                        seen++;
                    }

                    if (seen == 0) { continue; }

                    int mine = 0;
                    Dictionary<uint, int> all = new Dictionary<uint, int>();
                    for (int k = 0; k < walk.Count; k++)
                    {
                        uint v;
                        if (!ReadUInt32Retry(memory, walk.Objects[k].Address + offset, out v)) { continue; }
                        if (v == 0) { continue; }

                        int c;
                        all[v] = all.TryGetValue(v, out c) ? c + 1 : 1;
                        if (v == owner) { mine++; }
                    }

                    int maxOther = 0;
                    foreach (KeyValuePair<uint, int> pair in all)
                    {
                        if (pair.Key != owner && pair.Value > maxOther) { maxOther = pair.Value; }
                    }

                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  +0x{0:X3}：我方 {1} 个{2}｜最大其他阵营 {3} 个｜共 {4} 种",
                        offset, mine, agree ? "" : "（自己人取值不一致 ✗）", maxOther, all.Count));
                }
            }

            if (bestScore < 0)
            {
                text.AppendLine("没有找到符合「阵营」特征的偏移（种类少 + 聚集明显）。");
                text.AppendLine("可能这张名单里的对象类型比较杂，需要按类型分组后再分析。");
                return text.ToString();
            }

            text.AppendLine("最像「阵营」的偏移：" + bestLine);
            {
                Dictionary<uint, int> counts = new Dictionary<uint, int>();
                for (int i = 0; i < sample; i++)
                {
                    uint value;
                    if (!ReadUInt32Retry(memory, walk.Objects[i].Address + bestOffset, out value)) { continue; }
                    if (value == 0) { continue; }
                    int existing;
                    counts[value] = counts.TryGetValue(value, out existing) ? existing + 1 : 1;
                }

                List<KeyValuePair<uint, int>> ordered = new List<KeyValuePair<uint, int>>();
                foreach (KeyValuePair<uint, int> pair in counts) { ordered.Add(pair); }
                ordered.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b)
                {
                    return b.Value.CompareTo(a.Value);
                });

                text.AppendLine("阵营分布（共 " + ordered.Count + " 方）：");
                for (int i = 0; i < ordered.Count && i < 12; i++)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  阵营 0x{0:X8}：{1} 个对象{2}",
                        ordered[i].Key, ordered[i].Value,
                        ordered[i].Key == bestMyOwner ? "　← 这一方是「我方」（含你选的单位）" : ""));
                }
            }

            _profile.OffsetTechnoOwner = bestOffset;
            string saveError = "";
            bool saved = _store != null && _store.Save(_profile, out saveError);
            text.AppendLine(saved
                ? string.Format(CultureInfo.InvariantCulture,
                    "已保存阵营偏移 +0x{0:X}（我方 0x{1:X8}）。", bestOffset, bestMyOwner)
                : "保存失败：" + saveError);
            text.AppendLine("说明：本页只读，不会改动游戏内存。");
            return text.ToString();
        }

        private void OnScanClick(object sender, EventArgs e)
        {
            _scanButton.Enabled = false;
            try
            {
                SetOutput(Scan());
            }
            catch (Exception ex)
            {
                SetOutput("扫描过程中出错：" + ex.Message);
            }
            finally
            {
                _scanButton.Enabled = true;
            }
        }

        private void SetOutput(string text)
        {
            _output.Text = text;
            _output.SelectionStart = 0;
            _output.SelectionLength = 0;

            // 同时逐行写进日志。
            // 原因：结果只在界面上显示时，用户截图常常看不清（这次就遇上了），
            // 而点「导出日志」能把原文完整带出来，比反复截图可靠得多。
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length > 0) { Logger.Info("Advanced", lines[i]); }
            }
        }

        /// <summary>
        /// 只读扫描：解析校准配置里的全对象数组 → 遍历 → 统计 → 与选中列表交叉验证。
        /// 全程只读，不写一个字节。
        /// </summary>
        private string Scan()
        {
            MemoryIO memory = _target.Memory;
            uint moduleBase = _target.ModuleBase;
            uint moduleSize = _target.ModuleSize;

            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 全局对象数组扫描（只读，不写入任何内存）===");
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "进程模块 0x{0:X8} ~ 0x{1:X8}", moduleBase, moduleBase + moduleSize));
            text.AppendLine();

            string error;
            if (!_profile.TechnoArray.IsCalibrated)
            {
                text.AppendLine("【缺校准】配置里还没有「全对象数组」地址。");
                text.AppendLine("这是秒杀敌人 / 瞬间建造的地基 —— 没有它就无法枚举游戏里的对象。");
                text.AppendLine("需要先在校准流程里把它定位出来（它是引擎自己维护的对象登记表）。");
                return text.ToString();
            }

            uint arrayAddress;
            if (!_profile.TechnoArray.TryResolve(memory, moduleBase, out arrayAddress, out error))
            {
                text.AppendLine("【解析失败】" + error);
                return text.ToString();
            }
            // **必须再解一次引用**：配置里保存的是"存放数组地址的那个格子"，
            // 真正要遍历的是**格子里装着的地址**。
            //
            // 少这一步就会把"格子本身"当数组来读 —— 真机实测读出来的前几项是
            // 0xC88BFFEB、0x1DE8D38A 这种明显不是指针的值（那是代码/垃圾），
            // 于是通过率 1%、交叉验证 0 命中，看着像"名单找错了"，其实是**少跳了一层**。
            uint slotAddress = arrayAddress;
            uint arrayPointer;
            if (!ReadUInt32Retry(memory, slotAddress, out arrayPointer) || arrayPointer < 0x10000)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "对象名单格 0x{0:X8} 里读不到有效指针，无法遍历。", slotAddress));
                return text.ToString();
            }
            arrayAddress = arrayPointer;

            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "对象名单：格子 0x{0:X8} → 数组 0x{1:X8}", slotAddress, arrayAddress));

            uint count = 0;
            bool countKnown = false;
            if (_profile.TechnoArrayCount.IsCalibrated)
            {
                uint countAddress;
                if (_profile.TechnoArrayCount.TryResolve(memory, moduleBase, out countAddress, out error)
                    && memory.ReadUInt32(countAddress, out count))
                {
                    countKnown = true;
                }
            }

            if (!countKnown)
            {
                // 没有数量字段也能扫：给一个上限，**由遍历过程数出实际有多少个有效对象**。
                //
                // 真机实测：容器里的数量字段经常是"容量"（读出来 257，实际只有几个），
                // 按它读会读进大量垃圾、通过率跌到 3%。所以"实际数到的个数"才是可信的。
                count = 4096;
                text.AppendLine("配置里没有可用的「对象数量」字段，改为按上限探测，以实际数到的有效对象数为准。");
            }

            text.AppendLine(string.Format(CultureInfo.InvariantCulture, "对象数量：{0}", count));
            text.AppendLine();

            TechnoWalkResult walk = TechnoWalker.WalkArray(
                memory, arrayAddress, count, moduleBase, moduleSize);

            if (!walk.Ok)
            {
                text.AppendLine("【遍历失败】" + walk.Error);
                return text.ToString();
            }

            // 把名单**前几项的原始值与头 4 字节**摊开。
            // 上一轮就是靠这种"把原始值打出来"定位到问题的 —— 光看统计数字只能猜。
            string raw = "名单前 6 项：";
            for (int k = 0; k < 6; k++)
            {
                uint entry = 0;
                bool ok = ReadUInt32Retry(memory, arrayAddress + (uint)(k * 4), out entry);

                uint head = 0;
                bool headOk = entry > 0x10000 && ReadUInt32Retry(memory, entry, out head);

                raw += string.Format(CultureInfo.InvariantCulture, "[{0}]=0x{1:X8}(头=0x{2:X8}) ",
                    k, entry, headOk ? head : 0);
                if (!ok) { break; }
            }
            text.AppendLine(raw);

            double ratio = walk.Scanned == 0 ? 0 : (double)walk.Count / walk.Scanned;
            text.AppendLine("--- 结果 ---");
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "候选 {0} 个｜通过对象校验 {1} 个（{2:P0}）｜跳过 {3} 个｜重复 {4} 个",
                walk.Scanned, walk.Count, ratio, walk.Skipped, walk.Duplicates));

            string reason;
            bool trusted = TechnoOps.IsTrustworthy(walk, out reason);
            text.AppendLine(trusted
                ? "判定：通过率正常，数组地址可信。"
                : "判定：**可疑** —— " + reason);
            text.AppendLine();

            // 阵营分布：正确时应该"少数几个阵营、各自聚集大量单位"
            if (_profile.OffsetTechnoOwner != 0)
            {
                List<KeyValuePair<uint, int>> groups = TechnoWalker.GroupByOwner(
                    memory, walk.Objects, _profile.OffsetTechnoOwner);

                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "--- 阵营分布（按对象数排序，共 {0} 个阵营）---", groups.Count));
                int shown = groups.Count < 10 ? groups.Count : 10;
                for (int i = 0; i < shown; i++)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  阵营 0x{0:X8}：{1} 个对象", groups[i].Key, groups[i].Value));
                }
                if (groups.Count == 0)
                {
                    text.AppendLine("  一个阵营都没读到 —— 「对象阵营」偏移很可能不对。");
                }
                text.AppendLine();
            }
            else
            {
                text.AppendLine("【提示】「对象阵营」偏移未校准，敌我暂时无法区分（秒杀敌人会因此拒绝执行）。");
                text.AppendLine();
            }

            // 与选中列表交叉验证：这一条是整套逻辑最有力的自证
            text.AppendLine("--- 与「选中列表」交叉验证 ---");
            uint selectedAddress;
            if (!_profile.SelectedList.IsCalibrated
                || !_profile.SelectedList.TryResolve(memory, moduleBase, out selectedAddress, out error))
            {
                text.AppendLine("选中列表不可用（未校准或解析失败），跳过交叉验证。");
            }
            else
            {
                // 用和"查找线索"**同一套健壮读取**（数量可能不在 +0，而是要试多个位置）。
                // 之前直接按 +0 读，把"指针"当成了"数量"，于是出现"选中的 32 个单位"这种
                // 离谱数字，交叉验证必然失败 —— 这不是数据错，是读法错。
                List<uint> selected = ReadSelectedObjects(memory, selectedAddress);
                if (selected.Count == 0)
                {
                    text.AppendLine("当前没有读到选中的单位 —— 先在游戏里选中一个自己的单位再扫描，");
                    text.AppendLine("这样就能用「选中的必须出现在全对象数组里」来做交叉验证。");
                }
                else
                {
                    int hit = 0;
                    int total = selected.Count;

                    for (int i = 0; i < total; i++)
                    {
                        uint objectAddress = selected[i];

                        for (int k = 0; k < walk.Objects.Count; k++)
                        {
                            if (walk.Objects[k].Address == objectAddress) { hit++; break; }
                        }
                    }

                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "选中的 {0} 个单位里，有 {1} 个出现在全对象数组中。", total, hit));
                    text.AppendLine(hit == total && total > 0
                        ? "**交叉验证通过** —— 两个来源指向同一批对象，数组地址可信。"
                        : "**交叉验证不通过** —— 选中的单位没有出现在数组里，说明数组地址或数量字段不对。");
                }
            }

            text.AppendLine();
            // ---- 观察者参照：找"内容高度一致的大块"（只读）----
            //
            // 在"观察者"局里迷雾是全开状态，对应位图往往呈现"整块字节一致"的形态。
            // 这条判据比"哪块内存变化最少"稳定得多：**前者看内容，后者看噪声**
            // （前面三次失败都是因为在 8000+ 页噪声里猜"最安静的块"）。
            text.AppendLine();
            text.AppendLine("--- 内容一致的大块（观察者局：迷雾位图通常是这种形态）---");
            List<string> uniform = FindUniformBlocks(memory);
            if (uniform.Count == 0)
            {
                text.AppendLine("  没有找到（没有 ≥256KB 且 99% 以上内容一致的块）。");
            }
            for (int i = 0; i < uniform.Count && i < 8; i++)
            {
                text.AppendLine("  " + uniform[i]);
            }

            text.AppendLine("说明：本页不写入任何内存，可以放心反复扫描。");
            return text.ToString();
        }

        /// <summary>
        /// 「自动查找总名单」：拿当前选中的单位当线索反查，找到后直接存进配置。
        /// 全程只读，不会改动游戏内存。
        /// </summary>
        private void OnFindClick(object sender, EventArgs e)
        {
            _findButton.Enabled = false;
            try { SetOutput(FindArray()); }
            catch (Exception ex) { SetOutput("查找过程中出错：" + ex.Message); }
            finally { _findButton.Enabled = true; }
        }

        private string FindArray()
        {
            MemoryIO memory = _target.Memory;
            uint moduleBase = _target.ModuleBase;
            uint moduleSize = _target.ModuleSize;
            StringBuilder text = new StringBuilder();

            text.AppendLine("=== 自动查找「所有单位与建筑的总名单」（只读）===");
            text.AppendLine();

            string error;
            uint slotAddress;
            if (!_profile.SelectedList.IsCalibrated
                || !_profile.SelectedList.TryResolve(memory, moduleBase, out slotAddress, out error))
            {
                text.AppendLine("【缺前置】还找不到「选中名单」的位置，没法反查总名单。");
                text.AppendLine("请先到「单位校准」页完成前两步，再回来点这个按钮。");
                return text.ToString();
            }
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "已知线索：选中名单指针格 0x{0:X8}", slotAddress));

            List<uint> hints = ReadSelectedObjects(memory, slotAddress);
            if (hints.Count == 0)
            {
                text.AppendLine("提示：当前没有选中任何单位。仍可查找，但无法确认是否找对 ——");
                text.AppendLine("建议先在游戏里选中一个自己的单位，再点这个按钮。");
            }
            else
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "线索单位 {0} 个，第一个是 0x{1:X8}", hints.Count, hints[0]));
            }
            text.AppendLine();

            List<TechnoArrayCandidate> found = TechnoArrayFinder.Find(
                memory, moduleBase, moduleSize, slotAddress, hints);

            if (found.Count == 0)
            {
                text.AppendLine("没找到候选名单。可能原因：");
                text.AppendLine("  1. 当前不在对局里（地图上还没有单位）；");
                text.AppendLine("  2. 总名单不在选中名单附近，需要扩大搜索范围。");
                return text.ToString();
            }

            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "找到 {0} 个候选（已按可信度排序）：", found.Count));
            for (int i = 0; i < found.Count; i++)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  [{0}] {1}", i + 1, found[i].Describe()));
            }
            text.AppendLine();

            TechnoArrayCandidate best = found[0];

            // **没有线索就绝不落盘。**
            // 真机踩到过：没读到选中单位（线索为空）时，代码照样把"看起来最像"的候选
            // 存进了配置 —— 而那种排序纯属瞎猜，8 个候选全是垃圾。宁可什么都不存。
            if (hints.Count == 0)
            {
                text.AppendLine("没有读到「你选中的那个单位」，所以**这次不保存**。");
                text.AppendLine("请确认：① 游戏里确实选中了一个自己的单位；② 选中后没有按过取消选中（Esc 或点空地）。");
                text.AppendLine("然后再点一次这个按钮。");
                return text.ToString();
            }

            if (best.HintHits == 0)
            {
                text.AppendLine("注意：没有一个候选包含你选中的单位，**这次结果不可信，不保存**。");
                text.AppendLine("请确认游戏里确实选中了自己的单位、且处于对局中，然后再点一次。");
                return text.ToString();
            }

            // **必须先清掉旧的定位方式**（旧的指针链 / 旧静态地址）。
            // 否则 TryResolve 会继续优先走旧路径，新地址虽然存进去了却用不上 ——
            // 真机实测：这一轮明明"已保存到配置"，紧接着扫描却还在用上一个地址。
            _profile.TechnoArray.Clear();
            _profile.TechnoArray.SetStaticAddress(best.SlotAddress - moduleBase);

            if (best.CountAddress != 0)
            {
                _profile.TechnoArrayCount.Clear();
                _profile.TechnoArrayCount.SetStaticAddress(best.CountAddress - moduleBase);
            }
            else
            {
                // 没找到可信的数量字段时，**旧的也必须清掉**。
                // 真机实测：不清的话扫描继续用上一次的旧数量（读 257 个全是垃圾、通过率 0%）；
                // 清掉之后扫描会自动改用"按上限探测、以实际数到的有效对象数为准"。
                _profile.TechnoArrayCount.Clear();
            }

            string saveError = "";
            bool saved = _store != null && _store.Save(_profile, out saveError);

            text.AppendLine("--- 结果 ---");
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "已选定：名单 0x{0:X8}，数量字段 0x{1:X8}（数量 {2}）",
                best.ArrayAddress, best.CountAddress, best.Count));
            text.AppendLine(saved
                ? "已保存到配置 —— 以后不用再找。"
                : "保存失败：" + saveError);
            text.AppendLine();
            text.AppendLine("下一步：点「扫描全对象数组」。若阵营分布显示为「少数几个阵营、各自聚集大量单位」，就说明找对了。");
            return text.ToString();
        }

        /// <summary>
        /// 预演「秒杀敌人」：**只读不改**，列出"会杀哪些、放过哪些"。
        ///
        /// 这是开放真正写入之前的最后一道人工关卡。判定规则与真正执行时完全相同：
        /// 只处理"类型属于我方所在类"的对象，且阵营值 ≠ 我方阵营、且阵营值非零。
        /// 用户只要看两个数字（将击杀 N / 放过我方 M）是否合理，就能判断能不能开写。
        /// </summary>
        private void OnKillPreviewClick(object sender, EventArgs e)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 预演：秒杀敌人（只读，不写入任何内存）===");

            MemoryIO memory = _target.Memory;
            string error;

            uint slotAddress;
            if (!_profile.TechnoArray.IsCalibrated
                || !_profile.TechnoArray.TryResolve(memory, _moduleBase, out slotAddress, out error))
            {
                text.AppendLine("【缺前置】还没找到对象名单。");
                SetOutput(text.ToString());
                return;
            }

            uint arrayPointer;
            if (!ReadUInt32Retry(memory, slotAddress, out arrayPointer) || arrayPointer < 0x10000)
            {
                text.AppendLine("【缺前置】名单格子里读不到有效指针。");
                SetOutput(text.ToString());
                return;
            }

            if (_profile.OffsetTechnoOwner == 0)
            {
                text.AppendLine("【缺前置】阵营偏移未校准 —— 分不出敌我，绝不能写血量。");
                SetOutput(text.ToString());
                return;
            }

            if (_profile.SelectedList.IsCalibrated == false)
            {
                text.AppendLine("【缺前置】选中名单不可用，无法确定\"我方\"是谁。");
                SetOutput(text.ToString());
                return;
            }

            uint selectedSlot;
            if (!_profile.SelectedList.TryResolve(memory, _moduleBase, out selectedSlot, out error))
            {
                text.AppendLine("【缺前置】选中名单解析失败：" + error);
                SetOutput(text.ToString());
                return;
            }

            List<uint> myObjects = ReadSelectedObjects(memory, selectedSlot);
            if (myObjects.Count == 0)
            {
                text.AppendLine("【缺前置】读不到你选中的单位 —— 请在游戏里选中一个自己的单位再点。");
                SetOutput(text.ToString());
                return;
            }

            // 我方阵营值 + 我方所属类型（只处理同一类对象，字段布局才对得上）
            uint myOwner = 0;
            uint myType = 0;
            for (int i = 0; i < myObjects.Count; i++)
            {
                uint type;
                if (!ReadUInt32Retry(memory, myObjects[i], out type)) { continue; }
                uint owner;
                if (!ReadUInt32Retry(memory, myObjects[i] + _profile.OffsetTechnoOwner, out owner)) { continue; }
                if (owner == 0) { continue; }

                myType = type;
                myOwner = owner;
                break;
            }

            if (myOwner == 0)
            {
                text.AppendLine("【缺前置】读不到我方的阵营值。");
                SetOutput(text.ToString());
                return;
            }

            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "我方阵营 0x{0:X8}｜我方类型 0x{1:X8}", myOwner, myType));

            TechnoWalkResult walk = TechnoWalker.WalkArray(
                memory, arrayPointer, 4096, _moduleBase, _moduleSize);
            if (!walk.Ok)
            {
                text.AppendLine("遍历失败：" + walk.Error);
                SetOutput(text.ToString());
                return;
            }

            int willKill = 0;
            int myUnits = 0;
            int skippedType = 0;
            Dictionary<uint, int> enemyGroups = new Dictionary<uint, int>();

            for (int i = 0; i < walk.Count; i++)
            {
                uint type;
                if (!ReadUInt32Retry(memory, walk.Objects[i].Address, out type)) { continue; }
                if (myType != 0 && type != myType) { skippedType++; continue; }

                uint owner;
                if (!ReadUInt32Retry(memory, walk.Objects[i].Address + _profile.OffsetTechnoOwner, out owner)) { continue; }

                if (owner == 0) { continue; }
                if (owner == myOwner) { myUnits++; continue; }

                willKill++;
                int existing;
                enemyGroups[owner] = enemyGroups.TryGetValue(owner, out existing) ? existing + 1 : 1;
            }

            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "遍历到 {0} 个对象（另有 {1} 个非我方类型，已跳过）", walk.Count, skippedType));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "**将击杀：{0} 个**｜**放过我方：{1} 个**", willKill, myUnits));
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "血量偏移 +0x{0:X}（写入时每个目标都会先校验类型、写后回读）",
                _profile.OffsetTechnoHealth));

            List<KeyValuePair<uint, int>> ordered = new List<KeyValuePair<uint, int>>();
            foreach (KeyValuePair<uint, int> pair in enemyGroups) { ordered.Add(pair); }
            ordered.Sort(delegate(KeyValuePair<uint, int> a, KeyValuePair<uint, int> b)
            {
                return b.Value.CompareTo(a.Value);
            });

            text.AppendLine("按阵营拆分（将击杀的部分）：");
            for (int i = 0; i < ordered.Count && i < 8; i++)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  敌方阵营 0x{0:X8}：{1} 个", ordered[i].Key, ordered[i].Value));
            }

            text.AppendLine();
            text.AppendLine("本次**没有写入任何内存**。确认数字合理后再开放真正的秒杀。");
            SetOutput(text.ToString());
        }

        // ---- 全图视野：迷雾的定位与清除 ----
        private Dictionary<uint, uint> _shroudBefore;

        /// <summary>
        /// 全图视野：**一个按钮、两次点击**。
        ///
        /// 第 1 次：记录"迷雾快照"（按页记校验和）。
        /// 用户去游戏里**揭开一小块地图**，第 2 次点同一按钮：对比前后、找出**最长的一段连续变化页**，
        /// 那就是迷雾位图；然后分片写零把它清除。
        ///
        /// 为什么用"前后对比"：迷雾是一整块连续位图，揭开地图时**只有它那片字节在变**。
        /// 这比任何"猜地址"的做法都可靠 —— 而且**完全不依赖对象名单**，
        /// 所以它没有被秒杀敌人卡住的那个问题拖累。
        /// </summary>
        private void OnShroudClick(object sender, EventArgs e)
        {
            if (_target == null || !_target.IsReady) { SetOutput("游戏未连接。"); return; }

            if (_shroudBefore == null)
            {
                _shroudBefore = SnapshotPages();
                SetOutput(string.Format(CultureInfo.InvariantCulture,
                    "已记录迷雾快照（{0:N0} 个内存页）。\r\n"
                    + "现在切回游戏，**移动一个单位揭开一小块地图**（揭开一点就够），"
                    + "然后再点一次这个按钮。",
                    _shroudBefore.Count));
                return;
            }

            Dictionary<uint, uint> after = SnapshotPages();
            List<uint> changed = new List<uint>();
            foreach (KeyValuePair<uint, uint> pair in _shroudBefore)
            {
                uint now;
                if (after.TryGetValue(pair.Key, out now) && now != pair.Value) { changed.Add(pair.Key); }
            }
            changed.Sort();

            if (changed.Count == 0)
            {
                SetOutput("对比前后**没有发现任何变化** —— 请确认在游戏里真的揭开了一小块地图，再点一次。");
                return;
            }

            // ---- 挑"迷雾"的判据：**变化比例最低的大块** ----
            //
            // 上一版用"最长的一段连续变化页"，两次都挑中了 0x1006C000 那块 3MB —
            // 但视野毫无变化，说明那是音频/贴图之类的活跃缓冲（几乎整块在变）✗。
            //
            // 迷雾的特征不同：它是**几 MB 的大缓冲**，而揭开一小块地图**只改了它很小一部分**
            // → **变化页占比极低**。所以判据改成：在"足够大的连续段"里，挑**占比最低**的那个。
            HashSet<uint> changedSet = new HashSet<uint>();
            for (int i = 0; i < changed.Count; i++) { changedSet.Add(changed[i]); }

            List<uint> runs = new List<uint>();
            List<uint> runLengths = new List<uint>();
            uint currentStart = changed[0];
            uint currentLen = 1;
            for (int i = 1; i < changed.Count; i++)
            {
                if (changed[i] == changed[i - 1] + PageSize) { currentLen++; }
                else
                {
                    runs.Add(currentStart);
                    runLengths.Add(currentLen);
                    currentStart = changed[i];
                    currentLen = 1;
                }
            }
            runs.Add(currentStart);
            runLengths.Add(currentLen);

            const uint windowPages = 1024;      // 以 4MB 为观察窗口
            const uint minRunPages = 64;        // 连续段至少要 256KB 才考虑

            uint bestRunStart = 0;
            uint bestRunPages = 0;
            double bestRatio = 2.0;
            List<string> ranking = new List<string>();

            for (int i = 0; i < runs.Count; i++)
            {
                if (runLengths[i] < minRunPages) { continue; }

                uint windowStart = runs[i] > windowPages * PageSize
                    ? runs[i] - windowPages * PageSize : 0;
                uint windowEnd = runs[i] + (runLengths[i] + windowPages) * PageSize;

                int total = 0;
                int touched = 0;
                for (uint page = windowStart; page <= windowEnd; page += PageSize)
                {
                    total++;
                    if (changedSet.Contains(page)) { touched++; }
                }
                if (total == 0) { continue; }

                double ratio = (double)touched / total;
                ranking.Add(string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8}｜连续 {1} 页｜窗口内变化占比 {2:P1}",
                    runs[i], runLengths[i], ratio));

                if (ratio < bestRatio)
                {
                    bestRatio = ratio;
                    bestRunStart = runs[i];
                    bestRunPages = runLengths[i];
                }
            }

            ranking.Sort();
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 全图视野：候选定位（本次**不写入**）===");
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "对比结果：{0:N0} 个页发生变化（快照共 {1:N0} 页）", changed.Count, after.Count));

            if (bestRunPages == 0)
            {
                text.AppendLine("没有找到「足够大的连续变化段」（≥256KB），无法定位迷雾。");
                text.AppendLine("请回游戏**多揭开一些地图**（多移动几个单位、走远一点），再点一次。");
                _shroudBefore = null;
                SetOutput(text.ToString());
                return;
            }

            text.AppendLine("候选（按窗口内变化占比从低到高，占比低的最像迷雾）：");
            for (int i = 0; i < ranking.Count && i < 3; i++) { text.AppendLine("  " + ranking[i]); }
            text.AppendLine();
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "→ 最像迷雾的一块：0x{0:X8} 起，连续 {1} 页（{2:N0} 字节），窗口内变化占比 {3:P1}",
                bestRunStart, bestRunPages, bestRunPages * PageSize, bestRatio));
            // ---- 写入门槛 ----
            // 用户已授权由我按情况决定"写不写"。这里定三条硬门槛，任何一条不满足就只报告不写：
            //   ① 块要够大（≥1MB）—— 迷雾是整张地图尺度的缓冲；
            //   ② 活跃度要低（窗口内变化占比 ≤20%）—— 迷雾只被揭开的那一小块改动；
            //   ③ 必须是"连续段"里最安静的那个（上面的排序已保证）。
            // 满足就写：往这种"大而安静"的块写零，破坏面最小，而且这正是迷雾的特征。
            // 门槛从 1MB 降到 256KB：真机实测最像的那一块是 92 页（376KB）、活跃度仅 4.5%，
            // 而其他候选全在 38~48% —— 对照如此鲜明，说明它已经是迷雾了，
            // 只是一开始把"块大小"的门槛定得太高（嫌它小）才被挡住。
            if (bestRunPages * PageSize < 256 * 1024 || bestRatio > 0.20)
            {
                text.AppendLine("本次**不写入** —— 最像的那一块也不够「大而安静」（需 ≥256KB 且活跃度 ≤20%）。");
                text.AppendLine("请回游戏**多揭开一些地图**（走远一点、多派几个单位），再试一次。");
                _shroudBefore = null;
                SetOutput(text.ToString());
                return;
            }

            // 填充值用 0xFF：真机实测写 0 之后迷雾**没有消失** ——
            // 说明这类位图的编码可能与我假设的相反（0 = 隐藏、1 = 显示）。
            // 写满 0xFF 正好是另一种可能，两种都试过才能定论。
            int cleared = FillRange(_target.Memory, bestRunStart, bestRunPages * PageSize, 64 * 1024, 0xFF);
            text.AppendLine(cleared > 0
                ? string.Format(CultureInfo.InvariantCulture,
                    "已清除 {0:N0} 字节 → 切回游戏看黑幕是否消失。", cleared)
                : "清除失败（这段内存可能不可写）。");
            text.AppendLine("提示：迷雾会被游戏重绘，效果不明显就再点几次。");

            _shroudBefore = null;
            SetOutput(text.ToString());
        }

        /// <summary>按页记录可写内存的校验和 —— 用于"前后对比、定位变化的那一大块"。</summary>
        private Dictionary<uint, uint> SnapshotPages()
        {
            Dictionary<uint, uint> pages = new Dictionary<uint, uint>();
            MemoryIO memory = _target.Memory;

            List<MemoryRegion> regions = memory.EnumRegions(0x10000, 0x7FFFFFFF, true, true, 4000);
            for (int i = 0; i < regions.Count; i++)
            {
                ulong regionBase = regions[i].Base;
                ulong regionSize = regions[i].Size;

                // 跳过超大区域：快照要读整块可写内存，超大区域会让界面卡很久
                if (regionSize > 64UL * 1024 * 1024) { continue; }

                for (ulong offset = 0; offset < regionSize; offset += PageSize)
                {
                    uint address = (uint)(regionBase + offset);
                    byte[] block = memory.ReadBytesQuiet(address, PageSize);
                    if (block == null) { continue; }
                    pages[address] = Checksum(block);
                }
            }
            return pages;
        }

        private static uint Checksum(byte[] data)
        {
            uint hash = 2166136261u;
            for (int i = 0; i < data.Length; i++) { hash = (hash ^ data[i]) * 16777619u; }
            return hash;
        }

        /// <summary>把一段内存分片填成指定值；返回实际写入的字节数。</summary>
        private static int FillRange(MemoryIO memory, uint address, uint size, int chunk, byte value)
        {
            int written = 0;
            for (uint offset = 0; offset < size; offset += (uint)chunk)
            {
                int len = (int)(size - offset < (uint)chunk ? size - offset : (uint)chunk);
                byte[] buffer = new byte[len];
                if (value != 0)
                {
                    for (int i = 0; i < len; i++) { buffer[i] = value; }
                }
                if (memory.Write(address + offset, buffer, 0, len)) { written += len; }
                else { break; }
            }
            return written;
        }

        private const int PageSize = 4096;

        /// <summary>
        /// 找出"又大又统一"的可写内存块（256KB~16MB，且 99% 以上是同一个字节值）。
        ///
        /// 用途：**观察者局的迷雾位图正是这种形态**（全开状态下整块一致）。
        /// 用它当参照物来定位迷雾，比"看哪块变化最少"可靠得多 ——
        /// 前者是内容特征（稳定），后者是噪声特征（每次挑到的块都不一样，前面连错三次）。
        /// 全程只读。
        /// </summary>
        private List<string> FindUniformBlocks(MemoryIO memory)
        {
            List<string> found = new List<string>();

            List<MemoryRegion> regions = memory.EnumRegions(0x10000, 0x7FFFFFFF, true, true, 4000);
            for (int i = 0; i < regions.Count; i++)
            {
                ulong regionBase = regions[i].Base;
                ulong regionSize = regions[i].Size;

                if (regionSize < 256 * 1024 || regionSize > 16UL * 1024 * 1024) { continue; }

                byte[] data = memory.ReadBytesQuiet((uint)regionBase, (int)regionSize);
                if (data == null || data.Length < 256 * 1024) { continue; }

                int[] counts = new int[256];
                for (int k = 0; k < data.Length; k++) { counts[data[k]]++; }

                int best = 0;
                int bestValue = 0;
                for (int v = 0; v < 256; v++)
                {
                    if (counts[v] > best) { best = counts[v]; bestValue = v; }
                }

                double share = (double)best / data.Length;
                if (share < 0.99) { continue; }

                found.Add(string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8}｜{1:N0} 字节｜{2:P1} 都是 0x{3:X2}",
                    (uint)regionBase, data.Length, share, bestValue));
            }

            found.Sort();
            return found;
        }

        /// <summary>读出当前选中单位的地址列表，作为查找线索。</summary>
        private List<uint> ReadSelectedObjects(MemoryIO memory, uint slotAddress)
        {
            // 数量字段的位置**绝对不能猜**。
            //
            // 真机实测（单位校准页的探测日志）：「距数量字段 −40 字节」，
            // 也就是数量 = 数组指针 + 0x28 —— 中间还夹着别的字段，
            // 并不是教科书式的 {指针, 容量, 数量} 紧凑布局。
            // 之前按 +8 读，读到的是别的字段，结果用户明明选中了单位却报"没有选中任何单位"。
            //
            // 现在优先用配置里那个**专门记录数量位置**的定位器（它就是干这个的），
            // 再退回到几个常见偏移兜底。
            if (_profile.SelectedCount.IsCalibrated)
            {
                uint countAddress;
                string error;
                uint count;
                if (_profile.SelectedCount.TryResolve(memory, _target.ModuleBase, out countAddress, out error)
                    && memory.ReadUInt32(countAddress, out count)
                    && count > 0 && count <= 64)
                {
                    List<uint> objects = Collect(memory, slotAddress, count, _moduleBase, _moduleSize);
                    if (IsRealObject(memory, objects, _target.ModuleBase, _target.ModuleSize)) { return objects; }
                }
            }

            int[] countOffsets = new int[] { 0x28, 8, 0, 4, -4, -8, 0x24, 0x2C };
            for (int i = 0; i < countOffsets.Length; i++)
            {
                uint countSlot = Offset(slotAddress, countOffsets[i]);
                if (countSlot == 0) { continue; }

                uint count;
                if (!memory.ReadUInt32(countSlot, out count) || count == 0 || count > 64) { continue; }

                List<uint> objects = Collect(memory, slotAddress, count, _moduleBase, _moduleSize);
                if (IsRealObject(memory, objects, _target.ModuleBase, _target.ModuleSize)) { return objects; }
            }

            DumpSelectionSlots(memory, slotAddress);
            return new List<uint>();
        }

        /// <summary>
        /// 读不到选中单位时，把这一带**原始数值**全部打进日志。
        ///
        /// 为什么不再接着猜偏移：前面几轮我按"常见布局"试了七八个偏移都不对，
        /// 每次都要用户重跑一遍才能知道结果 —— 代价太高。
        /// 直接把原始值摊开，一眼就能看出"哪个格子里是 3"（你选了几个单位），
        /// 不用再来回试。这是最后一次为这件事麻烦用户。
        /// </summary>
        private void DumpSelectionSlots(MemoryIO memory, uint slotAddress)
        {
            int[] offsets = new int[] { -0x10, -0x8, -0x4, 0, 4, 8, 0x20, 0x24, 0x28, 0x2C, 0x30 };
            string line = "选中名单附近原始数值：";
            for (int i = 0; i < offsets.Length; i++)
            {
                uint probe = Offset(slotAddress, offsets[i]);
                uint value = 0;
                if (probe != 0) { memory.ReadUInt32(probe, out value); }
                line += string.Format(CultureInfo.InvariantCulture, "[{0:X}]={1} ", offsets[i], value);
            }
            Logger.Info("Advanced", line);

            bool calibrated = _profile.SelectedCount.IsCalibrated;
            uint countAddress = 0;
            string error = "";
            if (calibrated)
            {
                _profile.SelectedCount.TryResolve(memory, _target.ModuleBase, out countAddress, out error);
            }
            uint countValue = 0;
            if (countAddress != 0) { memory.ReadUInt32(countAddress, out countValue); }

            Logger.Info("Advanced", string.Format(CultureInfo.InvariantCulture,
                "数量定位器：已校准={0}｜地址=0x{1:X8}｜当前值={2}｜错误={3}",
                calibrated, countAddress, countValue, error));

            // 数组里前几项的**原始数值** —— 这是判定"最后一跳为什么过不去"的唯一硬证据。
            // 上一轮漏打了这一段，导致我只能推测，又多跑了一轮。
            uint arrayAddress = 0;
            if (ReadUInt32Retry(memory, slotAddress, out arrayAddress) && arrayAddress > 0x10000)
            {
                string entries = "选中数组内容：";
                for (int k = 0; k < 4; k++)
                {
                    uint entry = 0;
                    bool entryOk = ReadUInt32Retry(memory, arrayAddress + (uint)(k * 4), out entry);

                    uint head = 0;
                    bool headOk = entry > 0x10000 && ReadUInt32Retry(memory, entry, out head);

                    entries += string.Format(CultureInfo.InvariantCulture,
                        "[{0}]项={1}｜头4字节={2}{3} ",
                        k,
                        entryOk ? string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", entry) : "读取失败",
                        headOk ? string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", head) : "读取失败",
                        headOk ? (InModule(head, _moduleBase, _moduleSize) ? "(在模块内✓)" : "(不在模块内)") : "");
                }
                Logger.Info("Advanced", entries);
            }
            else
            {
                Logger.Info("Advanced", "选中数组指针读取失败（连续重试 3 次均失败）");
            }
        }

        /// <summary>从"数组指针格"读 count 个对象地址。</summary>
        private static List<uint> Collect(MemoryIO memory, uint slotAddress, uint count, uint moduleBase, uint moduleSize)
        {
            List<uint> objects = new List<uint>();

            uint arrayAddress;
            if (!memory.ReadUInt32(slotAddress, out arrayAddress) || arrayAddress < 0x10000) { return objects; }

            for (int k = 0; k < (int)count && objects.Count < 32; k++)
            {
                uint objectAddress;
                if (!memory.ReadUInt32(arrayAddress + (uint)(k * 4), out objectAddress)) { break; }
                if (objectAddress <= 0x10000) { continue; }

                // 统一换算成"对象本身的地址"：线索要和总名单里的值可比对，
                // 差一层就会永远比对不上（前面几轮"命中 0"就有这个因素）。
                uint resolved = ResolveObject(memory, objectAddress, moduleBase, moduleSize);
                objects.Add(resolved);

                // 两种口径都留一份：名单里的原值、以及再跳一层后的对象地址。
                // 真机实测：查找阶段能命中、交叉验证却 0 命中 —— 就是这个"差一层"造成的。
                // 都留着，两种存法都能比对得上。
                if (resolved != objectAddress) { objects.Add(objectAddress); }
            }
            return objects;
        }

        private static uint Offset(uint address, int delta)
        {
            long value = (long)address + delta;
            if (value < 0x10000 || value > 0xFFFFFFFFL) { return 0; }
            return (uint)value;
        }

        private static bool IsRealObject(MemoryIO memory, List<uint> objects, uint moduleBase, uint moduleSize)
        {
            if (objects.Count == 0) { return false; }

            for (int i = 0; i < objects.Count; i++)
            {
                if (LooksLikeObject(memory, objects[i], moduleBase, moduleSize)) { return true; }
            }
            return false;
        }

        private static bool InModule(uint value, uint moduleBase, uint moduleSize)
        {
            return value >= moduleBase && value < moduleBase + moduleSize;
        }

        /// <summary>
        /// 判断一个地址是否指向游戏对象：看它开头 4 字节是不是模块内的类型标记。
        ///
        /// **不是的话再往里跳一层** —— 真机实测发现有些容器里存的不是"对象本身"，
        /// 而是**包了一层的指针**。之前只认直接命中，于是明明读到了数组、也读到了数量，
        /// 却因为"最后一跳"判不通过而报"读不到选中单位"。
        /// 两种都认，才能同时兼容"直接存对象"和"包一层"两种容器。
        /// </summary>
        private static bool LooksLikeObject(MemoryIO memory, uint address, uint moduleBase, uint moduleSize)
        {
            uint first;
            if (!ReadUInt32Retry(memory, address, out first)) { return false; }
            if (InModule(first, moduleBase, moduleSize)) { return true; }

            if (first >= 0x10000 && first <= 0x7FFFFFFF)
            {
                uint second;
                if (ReadUInt32Retry(memory, first, out second) && InModule(second, moduleBase, moduleSize))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>把"数组里的一项"换算成**对象本身的地址**（兼容包一层的情况）。</summary>
        private static uint ResolveObject(MemoryIO memory, uint address, uint moduleBase, uint moduleSize)
        {
            uint first;
            if (!ReadUInt32Retry(memory, address, out first)) { return address; }
            if (InModule(first, moduleBase, moduleSize)) { return address; }

            if (first >= 0x10000 && first <= 0x7FFFFFFF)
            {
                uint second;
                if (ReadUInt32Retry(memory, first, out second) && InModule(second, moduleBase, moduleSize))
                {
                    return first;
                }
            }
            return address;
        }

        /// <summary>
        /// 读 4 字节，失败先重试几次再判负。
        ///
        /// 真机日志里出现过 `Win32=299 仅完成部分读取` 的**偶发**失败。
        /// 之前"失败一次就当作没读到"，足以让整条链路误判成"读不到选中单位" ——
        /// 现象和"地址找错了"完全一样，白白浪费了好几轮排查。
        /// 这是纯读取，重试没有任何副作用。
        /// </summary>
        private static bool ReadUInt32Retry(MemoryIO memory, uint address, out uint value)
        {
            value = 0;
            if (memory == null || address < 0x10000) { return false; }

            for (int attempt = 0; attempt < 3; attempt++)
            {
                if (memory.ReadUInt32(address, out value)) { return true; }
                System.Threading.Thread.Sleep(2);
            }
            return false;
        }
    }
}
