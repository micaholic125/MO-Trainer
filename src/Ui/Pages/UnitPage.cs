using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using MoTrainer.Calibration;
using MoTrainer.Core;
using MoTrainer.Ui.Controls;
using MoTrainer.Util;

namespace MoTrainer.Ui.Pages
{
    /// <summary>
    /// 单位校准页（实验性）。
    ///
    /// 这一页专门处理"数值匹配用不上"的字段。要做出「选中单位无敌」，得先知道三件事：
    ///   ① 哪块内存记着"选中了几个单位"；
    ///   ② 选中对象的指针数组在哪里；
    ///   ③ 对象内部哪个偏移是血量。
    ///
    /// 界面上都读不到精确数字，于是用两招绕过去：
    ///   · 选中数量 —— 让玩家"框选 N 个"把数字读出来，再走金钱/电力那套数值收敛；
    ///   · 血量偏移 —— 对比"受伤前后"同一对象的内存，谁变小了谁就是候选。
    ///
    /// 之所以单独一页而不是塞进校准向导：这三步的不确定性远高于金钱/电力，
    /// 需要反复试。放在独立页面里，失败也不会污染已经验证通过的向导流程。
    /// </summary>
    public sealed class UnitPage : UserControl
    {
        /// <summary>第 1 步的按钮区高度：留两排的余地，窄窗口换行时不会压到下面的内容。</summary>
        private const int CardActionAreaHeight = 78;

        private const int Card1Height = 330;
        private const int Card2Height = 240;
        private const int Card3Height = 296;
        private const int Card4Height = 112;

        /// <summary>说明文字的固定行高留白：放得下 3 行正文。</summary>
        private const int InstructionHeight = 58;

        /// <summary>以金钱字段为中心向前预留的范围（覆盖对象头部）。</summary>
        private const uint HouseWindowBack = 0x2000;

        /// <summary>以金钱字段为中心向后覆盖的范围。HouseClass 已知至少 0x53A4 大，留足余量。</summary>
        private const uint HouseWindowForward = 0x20000;

        /// <summary>第一档没覆盖到时的第二档窗口（HouseClass 的确切大小未知，先放大一圈再退全内存）。</summary>
        private const uint HouseWindowWide = 0x80000;

        /// <summary>
        /// 搜索范围档位名。**顺序即成本顺序**：越往后越贵、噪声越大。
        /// 第 3 档专门覆盖游戏模块的 .data —— 全局变量只住在那儿，
        /// 而此前的搜索区域集只含私有页，把这一大块整个漏掉了。
        /// </summary>
        private static readonly string[] ScopeNames = new string[]
        {
            "金钱对象附近（后 128 KB）",
            "金钱对象附近（后 512 KB）",
            "游戏模块的数据段 .data",
            "全内存（堆 + 模块）"
        };

        /// <summary>一个需要跟着卡片宽度重新换行的标签。</summary>
        private sealed class WideLabel
        {
            public Label Label;
            public int Height;
        }

        private readonly List<WideLabel> _wideLabels = new List<WideLabel>();

        private readonly ProcessAttach _attach;
        private readonly ProfileStore _store;
        private readonly CalibrationProfile _profile;
        private readonly LiveCalibrationTarget _target;

        private readonly Label _subtitle;
        private readonly DarkTextBox _countInput;
        private readonly Button _scanButton;
        private readonly Button _narrowButton;
        private readonly Button _changedButton;
        private readonly Button _verifyButton;
        private readonly Button _confirmCountButton;
        private readonly CandidateListView _countCandidates;
        private readonly Label _step1Status;

        private readonly Button _probeButton;
        private readonly Label _probeResult;

        private readonly Button _watchButton;
        private readonly Button _stopWatchButton;
        private readonly Button _confirmHealthButton;
        private readonly CandidateListView _healthCandidates;
        private readonly Label _step3Status;

        private readonly Button _saveButton;
        private readonly Button _resetButton;
        private readonly Label _footerStatus;

        // ---- 状态 ----
        private List<ScanCandidate> _countCandidatesData = new List<ScanCandidate>();
        private uint _firstCount;
        private bool _countScanStarted;
        private uint _countAddress;
        private int _count;
        private SelectionProbeResult _probe;

        /// <summary>本次扫描的范围说明，显示在状态行里，便于判断"没搜到"到底卡在哪。</summary>
        private string _scanNote = "";

        /// <summary>当前使用的搜索范围档位，见 <see cref="ScopeNames"/>。</summary>
        private int _scopeIndex;

        private readonly List<ObjectSnapshot> _snapshots = new List<ObjectSnapshot>();

        // ---- 持续监视状态 ----
        private readonly System.Windows.Forms.Timer _watchTimer;
        private readonly List<ObjectSnapshot> _watchSnapshots = new List<ObjectSnapshot>();
        private readonly Dictionary<uint, int> _watchSeen = new Dictionary<uint, int>();
        private bool _watching;
        private int _watchTicks;
        private int _watchChangedMax;
        private int _watchCurrentSelected;

        /// <summary>跟踪对象地址 → 是否确认归属本方。排名时优先采信本方对象上的变化。</summary>
        private readonly Dictionary<uint, bool> _watchOwnedMap = new Dictionary<uint, bool>();

        /// <summary>最多对多少个跟踪对象做归属校验（每个对象要读上百次小内存，别无限做）。</summary>
        private const int MaxOwnershipChecks = 8;
        private int _watchAlive;
        private int _watchLost;
        private string _watchLatest = "";

        /// <summary>最多同时跟踪多少个对象，防止用户全选时把采样量撑爆。</summary>
        private const int MaxTrackedObjects = 32;

        /// <summary>最多纳入多少个候选数组槽位。</summary>
        private const int MaxCandidateSlots = 6;

        private readonly List<uint> _candidateSlots = new List<uint>();
        private readonly List<uint> _watchLastObjects = new List<uint>();
        private int _watchListChanges;
        private int _watchOwned = -1;
        private bool _watchOwnershipChecked;

        /// <summary>按偏移汇总后的候选；AffectedObjects 表示有多少个选中单位在该偏移上变小了。</summary>
        private readonly List<FieldChangeSummary> _healthOffsets = new List<FieldChangeSummary>();

        /// <summary>是否已经尝试过从配置恢复（只在游戏连上后试一次）。</summary>
        private bool _restoreAttempted;
        private uint _healthOffset;
        private uint _healthMax;
        private List<FieldChange> _healthFound = new List<FieldChange>();

        private bool _busy;

        /// <summary>校准结果发生变化（已写入配置），主窗口据此重新绑定功能。</summary>
        public event EventHandler CalibrationChanged;

        public UnitPage(ProcessAttach attach, MemoryIO memory, ProfileStore store, CalibrationProfile profile)
        {
            _attach = attach;
            _store = store;
            _profile = profile;
            _target = new LiveCalibrationTarget(attach, memory);

            BackColor = Theme.Bg0;
            AutoScroll = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 1;
            root.Padding = new Padding(20, 16, 34, 18);
            root.BackColor = Color.Transparent;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Label heading = UiFactory.MakeLabel("单位校准", Theme.Heading, Theme.Text0);
            heading.Margin = new Padding(0, 0, 0, 2);
            root.Controls.Add(heading);

            _subtitle = UiFactory.MakeLabel("", Theme.Body, Theme.Text2);
            _subtitle.Margin = new Padding(0, 0, 0, 12);
            root.Controls.Add(_subtitle);

            // ---------- 第 1 步 ----------
            CardPanel card1 = MakeCard("第 1 步 · 找到「选中数量」字段", Card1Height);
            AddText(card1, 36,
                "框选恰好 N 个单位（只框单位，别框建筑；N 越大噪声越少，建议 8 以上），填入 N 点「开始搜索」。\n"
                + "再改成选中 M 个，填入 M 点「按值筛选」。搜索范围先只覆盖金钱所在的对象，噪声小得多。");
            // 按钮用自动换行的流式面板：第一排按钮已经有 6 个，
            // 用固定坐标的话，窗口一窄最后几个就会被卡片边缘裁掉、点都点不到。
            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = true;
            actions.BackColor = Color.Transparent;
            actions.Location = new Point(Theme.CardPadding + 2, 100);
            actions.Size = new Size(660, CardActionAreaHeight);
            actions.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card1.Controls.Add(actions);

            _countInput = new DarkTextBox();
            _countInput.DigitsOnly = true;
            _countInput.Placeholder = "例如 10";
            _countInput.Size = new Size(96, UiFactory.ButtonHeight);
            _countInput.Margin = new Padding(0, 0, 8, 6);
            actions.Controls.Add(_countInput);

            _scanButton = UiFactory.MakePrimaryButton("开始搜索", 100);
            _scanButton.Margin = new Padding(0, 0, 8, 6);
            _scanButton.Click += OnScanClick;
            actions.Controls.Add(_scanButton);

            _narrowButton = UiFactory.MakeButton("按值筛选", 100);
            _narrowButton.Margin = new Padding(0, 0, 8, 6);
            _narrowButton.Click += OnNarrowClick;
            actions.Controls.Add(_narrowButton);

            _changedButton = UiFactory.MakeButton("只看变化的", 112);
            _changedButton.Margin = new Padding(0, 0, 8, 6);
            _changedButton.Click += OnChangedClick;
            actions.Controls.Add(_changedButton);

            _verifyButton = UiFactory.MakeButton("复核选中项", 108);
            _verifyButton.Margin = new Padding(0, 0, 8, 6);
            _verifyButton.Click += OnVerifyClick;
            actions.Controls.Add(_verifyButton);

            _confirmCountButton = UiFactory.MakePrimaryButton("确认数量字段", 124);
            _confirmCountButton.Margin = new Padding(0, 0, 8, 6);
            _confirmCountButton.Click += OnConfirmCountClick;
            actions.Controls.Add(_confirmCountButton);

            _step1Status = AddStatus(card1, 100 + CardActionAreaHeight + 6, 40);
            _countCandidates = MakeCandidateList(card1, 100 + CardActionAreaHeight + 6 + 44);
            root.Controls.Add(card1);

            // ---------- 第 2 步 ----------
            CardPanel card2 = MakeCard("第 2 步 · 探测「选中列表」", Card2Height);
            AddText(card2, 36,
                "数量确定后，程序会在它附近寻找「对象指针数组」。\n"
                + "判定要求：那一串里的每个值都必须是互不相同的有效对象指针 —— 随便一段内存凑不出来。");

            _probeButton = UiFactory.MakePrimaryButton("确认并探测选中列表", 168);
            _probeButton.Location = new Point(Theme.CardPadding + 6, 104);
            _probeButton.Click += OnProbeClick;
            card2.Controls.Add(_probeButton);

            _probeResult = AddWideLabel(card2, 142, 86, Theme.Mono, Theme.Text1, "");
            root.Controls.Add(card2);

            // ---------- 第 3 步 ----------
            CardPanel card3 = MakeCard("第 3 步 · 找到单位「血量」偏移", Card3Height);
            AddText(card3, 36,
                "先**只选中将要挨打的那个单位**，点「开始监视」；然后去游戏里打它（别打死），"
                + "盯着状态行的数字有没有动，再点「停止并对比」。\n"
                + "选中过的单位会被长期跟踪，之后取消选中也照样采样 —— 所以只要它开始时被选中过一次就行。");

            _watchButton = UiFactory.MakePrimaryButton("开始监视", 124);
            _watchButton.Location = new Point(Theme.CardPadding + 6, 104);
            _watchButton.Click += OnWatchClick;
            card3.Controls.Add(_watchButton);

            _stopWatchButton = UiFactory.MakePrimaryButton("停止并对比", 140);
            _stopWatchButton.Location = new Point(Theme.CardPadding + 138, 104);
            _stopWatchButton.Click += OnStopWatchClick;
            card3.Controls.Add(_stopWatchButton);

            _watchTimer = new System.Windows.Forms.Timer();
            _watchTimer.Interval = 500;
            _watchTimer.Tick += WatchTick;

            _confirmHealthButton = UiFactory.MakePrimaryButton("确认血量字段", 124);
            _confirmHealthButton.Location = new Point(Theme.CardPadding + 286, 104);
            _confirmHealthButton.Click += OnConfirmHealthClick;
            card3.Controls.Add(_confirmHealthButton);

            _step3Status = AddStatus(card3, 140, 40);
            _healthCandidates = MakeCandidateList(card3, 188);
            root.Controls.Add(card3);

            // ---------- 保存 ----------
            CardPanel card4 = MakeCard("保存", Card4Height);
            _saveButton = UiFactory.MakePrimaryButton("保存到配置", 112);
            _saveButton.Location = new Point(Theme.CardPadding + 6, 44);
            _saveButton.Click += OnSaveClick;
            card4.Controls.Add(_saveButton);

            _resetButton = UiFactory.MakeButton("重置", 80);
            _resetButton.Location = new Point(Theme.CardPadding + 126, 44);
            _resetButton.Click += OnResetClick;
            card4.Controls.Add(_resetButton);

            _footerStatus = new Label();
            _footerStatus.AutoSize = false;
            _footerStatus.Font = Theme.Body;
            _footerStatus.ForeColor = Theme.Text1;
            _footerStatus.BackColor = Color.Transparent;
            _footerStatus.Location = new Point(Theme.CardPadding + 6, 80);
            _footerStatus.Size = new Size(700, 22);
            _footerStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card4.Controls.Add(_footerStatus);
            root.Controls.Add(card4);

            Controls.Add(root);

            RefreshUi();
        }

        // ============================================================ 构建辅助

        private static CardPanel MakeCard(string title, int height)
        {
            CardPanel card = new CardPanel();
            card.Title = title;
            card.Dock = DockStyle.Fill;
            card.Height = height;
            card.Margin = new Padding(0, 0, 0, Theme.GapCard);
            return card;
        }

        /// <summary>
        /// 说明文字一律走这里，登记进 <see cref="_wideLabels"/> 由 OnResize 统一按卡片宽度换行。
        ///
        /// 早先给的是固定 700 宽 + 40 高：卡片一窄文字就横向溢出，行数一多又被高度截断，
        /// 用户看到的就是"白色小字看不全"。
        /// </summary>
        private Label AddWideLabel(CardPanel card, int y, int height, Font font, Color color, string text)
        {
            Label label = new Label();
            label.AutoSize = false;
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.Text = text;
            label.Location = new Point(Theme.CardPadding + 6, y);
            label.Size = new Size(660, height);
            card.Controls.Add(label);

            WideLabel entry = new WideLabel();
            entry.Label = label;
            entry.Height = height;
            _wideLabels.Add(entry);
            return label;
        }

        private Label AddText(CardPanel card, int y, string text)
        {
            return AddWideLabel(card, y, InstructionHeight, Theme.Body, Theme.Text1, text);
        }

        private Label AddStatus(CardPanel card, int y, int height)
        {
            return AddWideLabel(card, y, height, Theme.Body, Theme.Text2, "");
        }

        private CandidateListView MakeCandidateList(CardPanel card, int y)
        {
            CandidateListView list = new CandidateListView();
            list.Location = new Point(Theme.CardPadding, y);
            list.Size = new Size(660, 96);
            list.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            card.Controls.Add(list);
            return list;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ResizeWideLabels();
        }

        /// <summary>把说明文字按各自卡片的当前宽度重新排版。</summary>
        private void ResizeWideLabels()
        {
            for (int i = 0; i < _wideLabels.Count; i++)
            {
                WideLabel entry = _wideLabels[i];
                Control parent = entry.Label.Parent;
                if (parent == null) { continue; }

                // 卡片有 1px 描边，再留 4px 余量，免得最后一个字贴着边
                int width = parent.ClientSize.Width - (Theme.CardPadding + 6) - Theme.CardPadding - 6;
                if (width < 160) { width = 160; }
                entry.Label.Width = width;
                entry.Label.Height = entry.Height;
            }
        }

        // ============================================================ 第 1 步

        private void OnScanClick(object sender, EventArgs e)
        {
            uint value;
            if (!_countInput.TryGetUInt32(out value) || value == 0)
            {
                _step1Status.ForeColor = Theme.Danger;
                _step1Status.Text = "请先输入当前选中的单位数量（正整数）。";
                return;
            }

            uint target = value;
            RunAsync("开始搜索 " + target, delegate { return ScanCount(target); });
        }

        private bool ScanCount(uint value)
        {
            if (!_target.IsReady)
            {
                SetStep1Status("尚未连接游戏进程，请先启动游戏。", Theme.Danger);
                return false;
            }

            uint moneyAddress = ResolveMoneyAddress();
            _scopeIndex = 0;
            return ScanFromScope(value, moneyAddress);
        }

        /// <summary>
        /// 从当前档位开始，逐档放大搜索范围，直到某一档找到候选或档位用尽。
        ///
        /// 档位按"成本低 + 噪声少"排：
        ///   ① 金钱对象附近 128KB —— 选中列表若属于玩家状态，最可能就在这儿
        ///   ② 同一圈放大到 512KB —— HouseClass 的确切大小未知，先放大一圈看看
        ///   ③ **游戏模块的数据段(.data)** —— 若"选中数量"是全局变量，它只会住在这儿。
        ///      这一档此前一直缺席：数值搜索用的区域集只含**私有页**，
        ///      而模块的 .data 是 image 类型、被排除在外，
        ///      于是全局变量**无论怎么搜都搜不到**（真机踩过：换了几轮范围都 0 命中）。
        ///   ④ 全内存（堆 + 模块）—— 最后兜底，小数字噪声极大
        /// </summary>
        private bool ScanFromScope(uint value, uint moneyAddress)
        {
            List<ScanCandidate> merged = new List<ScanCandidate>();
            string summary = "";
            string scope = "";

            while (_scopeIndex < ScopeNames.Length)
            {
                // 前两档依赖金钱地址，未校准时直接跳过
                if (_scopeIndex <= 1 && moneyAddress == 0) { _scopeIndex++; continue; }

                scope = ScopeNames[_scopeIndex];
                merged = ScanBothWidths(BuildScopeRegions(_scopeIndex, moneyAddress), value, out summary);
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "选中数量搜索 {0}（{1}）：{2}", value, scope, summary));

                if (merged.Count > 0) { break; }

                // 这一档没找到，换下一档
                _scopeIndex++;
            }

            if (merged.Count == 0)
            {
                SetStep1Status(string.Format(CultureInfo.InvariantCulture,
                    "所有范围（对象附近 / 模块数据段 / 全内存）里都没有找到数值 {0}。\n"
                    + "请确认框选的数量确实是 {0}，再试一次。", value), Theme.Danger);
                return false;
            }

            _countCandidatesData = merged;
            _firstCount = value;
            _countScanStarted = true;

            bool wide = merged.Count > 3000;
            _scanNote = scope + "：" + summary;

            int count = merged.Count;
            Post(delegate
            {
                _countCandidates.SetItems(_countCandidatesData, 0);
                if (count == 1)
                {
                    SetStep1Status("只找到 1 个候选。建议再改变一次选中数量并按值筛选复核，确认无误后再确认。", Theme.Success);
                }
                else
                {
                    SetStep1Status(string.Format(CultureInfo.InvariantCulture,
                        "找到 {0:N0} 个候选（{1}）。\n"
                        + "现在改变选中数量（比如换成 7 个），把新数字填进来点「按值筛选」。{2}",
                        count, _scanNote,
                        wide ? "候选偏多，多筛一两轮就能收敛。" : ""), Theme.Warning);
                }
                RefreshUi();
            });
            return true;
        }

        private void OnNarrowClick(object sender, EventArgs e)
        {
            uint value;
            if (!_countInput.TryGetUInt32(out value) || value == 0)
            {
                _step1Status.ForeColor = Theme.Danger;
                _step1Status.Text = "请先输入新的选中数量（正整数）。";
                return;
            }
            if (!_countScanStarted)
            {
                _step1Status.ForeColor = Theme.Danger;
                _step1Status.Text = "还没有做过首轮搜索，请先点「开始搜索」。";
                return;
            }

            uint target = value;
            RunAsync("按值筛选 " + target, delegate { return NarrowCount(target); });
        }

        private bool NarrowCount(uint value)
        {
            if (value == _firstCount)
            {
                SetStep1Status("新数量和上次一样，筛选不会收敛。请先在游戏里改变选中数量。", Theme.Danger);
                return false;
            }

            ValueScanner scanner = new ValueScanner(_target.Memory);
            int kept = scanner.Refilter(_countCandidatesData, ValueMatch.EqualTo, value);
            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "选中数量按值筛选 {0} → 剩余 {1} 个候选", value, kept));

            if (kept == 0)
            {
                // 这一档里没有目标字段。**自动换到更大的范围，用新读数重新搜一遍** ——
                // 和电力那套"编码逐档尝试"是同一个思路：一次失败不等于目标不存在，
                // 很可能只是我们看错了地方。这样用户不用重来一遍。
                if (_scopeIndex + 1 < ScopeNames.Length)
                {
                    _scopeIndex++;
                    _countScanStarted = false;
                    _countCandidatesData = new List<ScanCandidate>();
                    Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                        "按 {0} 筛选后 0 个候选，判定该范围不含目标字段，换到「{1}」重新搜索",
                        value, ScopeNames[_scopeIndex]));

                    Post(delegate
                    {
                        _countCandidates.SetItems(_countCandidatesData, 0);
                        SetStep1Status(string.Format(CultureInfo.InvariantCulture,
                            "「{0}」这一档里没有选中数量，已自动换到更大的范围「{1}」重新搜索…",
                            ScopeNames[_scopeIndex - 1], ScopeNames[_scopeIndex]), Theme.Warning);
                    });

                    return ScanFromScope(value, ResolveMoneyAddress());
                }

                SetStep1Status(string.Format(CultureInfo.InvariantCulture,
                    "四种范围全试过了，按 {0} 筛选后依然一个都不剩。请依次确认：\n"
                    + "① 框选后游戏里显示的数量确实是 {0}；② 两次框选的数量确实不同；\n"
                    + "③ 两次操作之间没有单位增减（被打死或刚造好都会改变数量）。\n"
                    + "也可以点「只看变化的」换个思路收窄 —— 它不要求数字读得准。",
                    value), Theme.Danger);
                return false;
            }

            _firstCount = value;
            Post(delegate
            {
                _countCandidates.SetItems(_countCandidatesData, 0);
                SetStep1Status(kept == 1
                    ? "已经收敛到唯一候选。请在列表里选中它，然后点「确认数量字段」。"
                    : string.Format(CultureInfo.InvariantCulture,
                        "还剩 {0:N0} 个候选，请再改变一次选中数量并按值筛选。", kept),
                    kept == 1 ? Theme.Success : Theme.Warning);
                RefreshUi();
            });
            return true;
        }

        /// <summary>
        /// 不依赖"数字读得准"的收窄方式：只要该字段的值**变了**就留下。
        ///
        /// 存在的意义：框选数量很容易读错（多选到一个单位、或期间有单位被打死）。
        /// 按值筛选一旦对不上就全灭，而这一条只看"变没变"，容错高得多。
        /// 收窄几轮后，玩家可以直接在候选列表里找「当前值」等于自己选中数量的那一行。
        /// </summary>
        private void OnChangedClick(object sender, EventArgs e)
        {
            if (!_countScanStarted)
            {
                _step1Status.ForeColor = Theme.Danger;
                _step1Status.Text = "还没有做过首轮搜索，请先点「开始搜索」。";
                return;
            }
            RunAsync("只看变化的", delegate { return NarrowByChange(); });
        }

        private bool NarrowByChange()
        {
            ValueScanner scanner = new ValueScanner(_target.Memory);
            int kept = scanner.Refilter(_countCandidatesData, ValueMatch.Changed, 0);
            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "选中数量按「变化」筛选 → 剩余 {0:N0} 个候选", kept));

            Post(delegate
            {
                _countCandidates.SetItems(_countCandidatesData, 0);
                if (kept == 0)
                {
                    SetStep1Status("这一步没有发现任何变化 —— 请先在游戏里改变选中数量，然后再点一次。", Theme.Warning);
                }
                else
                {
                    SetStep1Status(string.Format(CultureInfo.InvariantCulture,
                        "已收窄到 {0:N0} 个候选。请在列表里找「当前值」等于这次选中数量的那一行。\n"
                        + "注意：游戏在跑，计时器、坐标这类字段本来就会不停变化，所以这条路容错高但收得慢，"
                        + "优先用「按值筛选」。", kept),
                        kept <= 60 ? Theme.Success : Theme.Warning);
                }
                RefreshUi();
            });
            return true;
        }

        /// <summary>
        /// 复核选中的候选：重新读一次它的当前值。
        ///
        /// 为什么必须有这一步：收敛到 1 个候选靠的只是"一次数值变化"，
        /// 理论上仍可能是巧合（比如某个计数器恰好在同一时刻从 6 变成 5）。
        /// 让玩家在游戏里改一下选中数量、再点一次复核，就能得到**决定性证据**：
        /// 数字跟着变 = 找对了；数字不动 = 找错了，别在上面继续往下做。
        /// </summary>
        private void OnVerifyClick(object sender, EventArgs e)
        {
            ScanCandidate selected = _countCandidates.SelectedItem;
            if (selected == null)
            {
                _step1Status.ForeColor = Theme.Danger;
                _step1Status.Text = "请先在候选列表里选中一项，再点复核。";
                return;
            }
            RunAsync("复核选中项", delegate { return VerifyCandidate(selected); });
        }

        private bool VerifyCandidate(ScanCandidate candidate)
        {
            uint now;
            if (!_target.Memory.ReadUInt32(candidate.Address, out now))
            {
                SetStep1Status(string.Format(CultureInfo.InvariantCulture,
                    "读取 0x{0:X8} 失败，请确认游戏仍在运行。", candidate.Address), Theme.Danger);
                return false;
            }

            uint before = candidate.PreviousValue;
            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "复核 0x{0:X8}：上次读到 {1}，现在读到 {2}", candidate.Address, before, now));

            if (now == before)
            {
                _step1Status.ForeColor = Theme.Warning;
                _step1Status.Text = string.Format(CultureInfo.InvariantCulture,
                    "复核 0x{0:X8}：还是 {1}，没有变化。\n"
                    + "如果你刚在游戏里改过选中数量，说明这个候选**不是**选中数量。",
                    candidate.Address, now);
                return true;
            }

            _step1Status.ForeColor = Theme.Success;
            _step1Status.Text = string.Format(CultureInfo.InvariantCulture,
                "复核 0x{0:X8}：从 {1} 变成了 {2}。\n"
                + "请核对：{2} 是否正好等于你此刻在游戏里选中的单位数量？是就点「确认数量字段」。",
                candidate.Address, before, now);
            return true;
        }

        private void OnConfirmCountClick(object sender, EventArgs e)
        {
            ScanCandidate selected = _countCandidates.SelectedItem;
            if (selected == null)
            {
                _step1Status.ForeColor = Theme.Danger;
                _step1Status.Text = "请先在候选列表里选中一项。";
                return;
            }

            _countAddress = selected.Address;
            _count = (int)selected.PreviousValue;

            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "确认为「选中数量」：0x{0:X8}，本次读到 {1}", _countAddress, _count));

            SetStep1Status(string.Format(CultureInfo.InvariantCulture,
                "已确定数量字段：0x{0:X8}（本次读到 {1}）。接着做第 2 步。", _countAddress, _count), Theme.Success);
            RefreshUi();
        }

        // ============================================================ 第 2 步

        private void OnProbeClick(object sender, EventArgs e)
        {
            if (_countAddress == 0)
            {
                _probeResult.ForeColor = Theme.Danger;
                _probeResult.Text = "请先完成第 1 步（确定数量字段）。";
                return;
            }
            RunAsync("探测选中列表", delegate { return ProbeSelection(); });
        }

        private bool ProbeSelection()
        {
            if (!_target.IsReady)
            {
                Post(delegate
                {
                    _probeResult.ForeColor = Theme.Danger;
                    _probeResult.Text = "尚未连接游戏进程。";
                });
                return false;
            }

            // 以"当前真实数量"为准重新读一次，避免用界面上可能已经过期的数字
            uint live;
            if (!_target.Memory.ReadUInt32(_countAddress, out live) || live == 0)
            {
                Post(delegate
                {
                    _probeResult.ForeColor = Theme.Warning;
                    _probeResult.Text = "现在读到的选中数量是 0 —— 请先在游戏里框选几个单位，再点这个按钮。";
                });
                return false;
            }

            int count = (int)live;

            // 探测前必须灌一次模块区间：缺了它，"对象头必须是模块内类型表"这条硬校验会被跳过，
            // 结果就是把模块里的代码地址当成选中对象（真机踩过，混进来 8 个）。
            // 不依赖主窗口有没有刷过，这里自带一份。
            SelectionProbe.SetModuleRange(_target.ModuleBase, _target.ModuleSize);

            SelectionProbeResult probe = SelectionProbe.ProbeWithEscalation(_target.Memory, _countAddress, count);
            _probe = probe;

            for (int i = 0; i < probe.Trace.Count; i++)
            {
                Logger.Info("Unit", "选中列表探测：" + probe.Trace[i]);
            }

            Post(delegate
            {
                if (probe.Found)
                {
                    _probeResult.ForeColor = Theme.Success;
                    _probeResult.Text = string.Format(CultureInfo.InvariantCulture,
                        "找到选中列表！数量 {0}\r\n数组指针存放位置 0x{1:X8} → 数组 0x{2:X8}\r\n第一个对象 0x{3:X8}",
                        probe.Count, probe.ArraySlotAddress, probe.ArrayAddress, probe.ObjectAddresses[0]);
                }
                else
                {
                    _probeResult.ForeColor = Theme.Warning;
                    _probeResult.Text = (probe.Error.Length == 0 ? "未找到" : probe.Error)
                        + "\r\n可能原因：数量字段和数组不在同一个对象里。换一种数量再确认一次，或把结果发给我。";
                }
                RefreshUi();
            });
            return true;
        }

        // ============================================================ 第 3 步

        /// <summary>
        /// 现读当前选中列表。
        ///
        /// **必须每次现读，不能用第 2 步探测时记下的地址。**
        /// 真机上要让单位挨打，玩家得先取消选中、换别的单位强制攻击、再把被打的单位选回来，
        /// 这期间选中列表的内容一直在变（用户实测反馈）。
        /// 早先把第 2 步那一刻的第一个对象记死，结果第 3 步对比的是个没挨打的对象，
        /// 8KB 内存一个字节都没变 —— 看着像"没找到候选"，实际是"盯错了人"。
        /// </summary>
        private bool ReadCurrentSelection(out List<uint> objects, out string error)
        {
            objects = new List<uint>();
            error = "";

            uint count;
            if (!_target.Memory.ReadUInt32(_countAddress, out count))
            {
                error = "读取选中数量失败，请确认游戏仍在运行。";
                return false;
            }
            if (count == 0)
            {
                error = "当前读到的选中数量是 0 —— 请先在游戏里选中要测试的单位。";
                return false;
            }
            if (count > SelectionProbe.MaxCount)
            {
                error = string.Format(CultureInfo.InvariantCulture,
                    "选中数量异常（读到 {0}），已跳过以免读错内存。", count);
                return false;
            }

            uint arrayAddress;
            if (!_target.Memory.ReadUInt32(_probe.ArraySlotAddress, out arrayAddress)
                || arrayAddress < 0x10000)
            {
                error = "选中列表指针无效。";
                return false;
            }

            for (int i = 0; i < (int)count; i++)
            {
                uint objectAddress;
                if (!_target.Memory.ReadUInt32(arrayAddress + (uint)(i * 4), out objectAddress)) { break; }
                if (objectAddress < 0x10000) { continue; }
                objects.Add(objectAddress);
            }

            if (objects.Count == 0)
            {
                error = "没能从选中列表里读出任何对象。";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 判断这些指针指向的是不是**真正的游戏对象**：对象头 4 字节是虚表指针，
        /// 必然落在游戏模块的地址区间内。这比"能读出来"强得多 ——
        /// 数组指针万一找错，读出来的只是一堆恰好可读的地址，靠这一条能当场识破。
        /// </summary>
        private int CountGameObjectLike(List<uint> objects)
        {
            int count = 0;
            for (int i = 0; i < objects.Count; i++)
            {
                uint vtable;
                if (!_target.Memory.ReadUInt32(objects[i], out vtable)) { continue; }
                if (vtable >= _target.ModuleBase
                    && vtable < _target.ModuleBase + _target.ModuleSize) { count++; }
            }
            return count;
        }

        /// <summary>
        /// 开始持续监视：立刻采一次基准，之后每 500ms 采一次。
        ///
        /// 为什么改成"持续监视"而不是"手点两次快照"：
        /// 真机上让单位挨打必须先把选中切走（玩家反馈），手点快照极容易在错误的时机定格。
        /// 实测连续两次都拿到"3 个单位、8KB 内存一个字节都没变"，
        /// 而界面根本无法区分是"没挨打"还是"采错了对象"。
        /// 改成持续监视后：目标单位只要在**受伤前后各自被选中过一次**，
        /// 程序就自动拿到两边数据；状态行实时显示"有没有采到变化"，
        /// 打中的那一刻当场就能看到，不必等一整套流程走完才发现失败。
        /// </summary>
        private void OnWatchClick(object sender, EventArgs e)
        {
            if (_probe == null || !_probe.Found || _countAddress == 0)
            {
                _step3Status.ForeColor = Theme.Danger;
                _step3Status.Text = "请先完成第 2 步（探测到选中列表）。";
                return;
            }

            _watchSnapshots.Clear();
            _watchSeen.Clear();
            _watchTicks = 0;
            _watchChangedMax = 0;
            _watchCurrentSelected = 0;
            _watchAlive = 0;
            _watchLost = 0;
            _watchListChanges = 0;
            _watchOwned = -1;
            _watchOwnershipChecked = false;
            _watchLatest = "";
            _candidateSlots.Clear();
            _watchLastObjects.Clear();
            _watchOwnedMap.Clear();
            _healthOffsets.Clear();
            _healthCandidates.SetItems(new List<ScanCandidate>(), 0);

            _watching = true;
            WatchTick(null, null);          // 立刻采一次，作为"满血"基准
            if (_watchSnapshots.Count == 0)
            {
                _watching = false;
                SetStep3Status("没读到任何选中单位的数据 —— 请先在游戏里选中要测试的单位。",
                    Theme.Danger);
                return;
            }

            _watchTimer.Start();
            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "开始持续监视选中单位，首批 {0} 个", _watchSnapshots.Count));

            // 把内部状态一并写进日志：日志面板只有几十像素高，截图必然截不全，
            // 而这些细节（槽位地址、对象地址）恰恰是排查时最需要的。
            List<string> slots = new List<string>();
            for (int i = 0; i < _candidateSlots.Count; i++)
            {
                slots.Add(string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", _candidateSlots[i]));
            }
            Logger.Info("Unit", "候选数组槽位：" + (slots.Count == 0 ? "(无)" : string.Join("、", slots.ToArray())));

            List<string> objects = new List<string>();
            for (int i = 0; i < _watchSnapshots.Count && i < 16; i++)
            {
                objects.Add(string.Format(CultureInfo.InvariantCulture,
                    "0x{0:X8}", _watchSnapshots[i].BaseAddress));
            }
            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "已跟踪对象（{0} 个）：{1}", _watchSnapshots.Count, string.Join("、", objects.ToArray())));
            RefreshUi();
        }

        private void OnStopWatchClick(object sender, EventArgs e)
        {
            if (!_watching)
            {
                _step3Status.ForeColor = Theme.Danger;
                _step3Status.Text = "当前没有在监视。请先选中要测试的单位并点「开始监视」。";
                return;
            }

            _watchTimer.Stop();
            _watching = false;

            _snapshots.Clear();
            _snapshots.AddRange(_watchSnapshots);

            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "停止监视：采样 {0} 次｜累计跟踪 {1} 个单位（消失 {2}）｜候选偏移 {3} 个｜归属校验 {4}/{5}",
                _watchTicks, _snapshots.Count, _watchLost, _healthOffsets.Count,
                _watchOwned, _watchOwnedMap.Count));

            // 候选明细必须落进日志。
            // 之前只记了"找到 N 个候选"，明细只存在界面上 ——
            // 结果用户截图只截得下几行，关键行（动员兵 125 → 17）几次都被截掉，
            // 白绕了好几轮。凡是"人眼要判读的数据"，日志里都要有一份完整的。
            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "候选偏移明细（共 {0} 个，按 本方命中数 → 同时变化数 → 掉幅 排序）：",
                _healthOffsets.Count));

            int listLimit = _healthOffsets.Count < 24 ? _healthOffsets.Count : 24;
            for (int i = 0; i < listLimit; i++)
            {
                FieldChangeSummary detail = _healthOffsets[i];
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "  候选#{0}  对象+0x{1:X}  {2} → {3}（-{4}%×{5}个·本方{6}）",
                    i + 1, detail.Offset, detail.Before, detail.After,
                    detail.DropPermille / 10, detail.AffectedObjects, detail.OwnedObjects));
            }
            if (_healthOffsets.Count > listLimit)
            {
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "  ……另有 {0} 个候选未列出", _healthOffsets.Count - listLimit));
            }

            if (_healthOffsets.Count == 0)
            {
                _healthCandidates.SetItems(new List<ScanCandidate>(), 0);
                _step3Status.ForeColor = Theme.Warning;

                // 把内部状态如实摊开：这两种诊断能直接把"没挨打"和"跟错对象"分开
                string listNote = _watchListChanges == 0
                    ? "数组内容一次都没变过 —— 说明它不跟着你的选中变化，**不是选中列表**。"
                    : string.Format(CultureInfo.InvariantCulture,
                        "数组内容变过 {0} 次，说明确实跟着选中走。", _watchListChanges);

                string ownNote;
                if (_watchOwned < 0) { ownNote = "归属校验没做（金钱未校准）。"; }
                else if (_watchOwned == 0)
                {
                    ownNote = "**没有**一个跟踪对象引用你的阵营对象 —— 它们根本不是你的单位。";
                }
                else
                {
                    ownNote = string.Format(CultureInfo.InvariantCulture,
                        "{0} 个跟踪对象确实引用你的阵营对象，是本单位；"
                        + "那问题就是被打的那个当时不在跟踪集合里。", _watchOwned);
                }

                _step3Status.Text = string.Format(CultureInfo.InvariantCulture,
                    "监视 {0} 次采样、跟踪 {1} 个对象，一个字节都没变小。\r\n"
                    + "诊断：{2}{3}",
                    _watchTicks, _snapshots.Count, listNote, ownNote);
                return;
            }

            List<ScanCandidate> items = new List<ScanCandidate>();
            for (int i = 0; i < _healthOffsets.Count; i++)
            {
                FieldChangeSummary item = _healthOffsets[i];
                ScanCandidate candidate = new ScanCandidate(
                    _snapshots[0].BaseAddress + item.Offset, item.Before);
                candidate.Note = string.Format(CultureInfo.InvariantCulture,
                    "→ {0}（-{1}%×{2}个·本方{3}）", item.After, item.DropPermille / 10,
                    item.AffectedObjects, item.OwnedObjects);
                items.Add(candidate);
            }
            _healthCandidates.SetItems(items, 0);

            FieldChangeSummary top = _healthOffsets[0];
            _step3Status.ForeColor = Theme.Success;
            _step3Status.Text = string.Format(CultureInfo.InvariantCulture,
                "监视到 {0} 个候选偏移（跟踪 {1} 个单位，其中 {2} 个确认归属本方）。"
                + "排第一的是 +0x{3:X}：{4} 个本方单位从 {5} 掉到 {6}（-{7}%）。\r\n"
                + "挑「本方命中多、原始值等于该单位满血值」的那一行（备注里写着变化详情）。选中后点「确认血量字段」。",
                _healthOffsets.Count, _snapshots.Count, _watchOwned, top.Offset,
                top.OwnedObjects, top.Before, top.After, top.DropPermille / 10);
            RefreshUi();
        }

        /// <summary>
        /// 在"选中数量"字段附近，找出所有**看起来是对象指针数组**的槽位。
        ///
        /// 判定刻意收紧到：数组里 `count` 个元素互不相同，且**每个元素指向的对象，
        /// 头 4 字节都是落在游戏模块区间内的虚表**。这比早先只校验
        /// "可读 + 互不相同"硬得多 —— 要凑巧满足这一条，基本上就真的是一组游戏对象。
        ///
        /// 为什么要找**一批**而不是一个：早先只认死"数量字段 − 8"这一个槽位
        /// （按 `DynamicVectorClass` 的 {数组指针, 容量, 数量} 布局推出来的）。
        /// 万一数量字段和那个数组指针并不属于同一个容器，就会一直盯着无关对象 ——
        /// 现象正是"连续几轮、8KB 内存一个字节都不变"。
        /// 与其赌一个，不如把附近所有合格槽位都纳入跟踪：真的那个在里面，变化就一定采得到。
        /// </summary>
        private List<uint> FindCandidateSlots(uint count)
        {
            List<uint> slots = new List<uint>();
            if (_countAddress == 0 || count == 0) { return slots; }

            uint start = _countAddress > 0x40 ? _countAddress - 0x40 : 0;
            uint end = _countAddress + 0x40;
            for (uint slot = start; slot <= end && slots.Count < MaxCandidateSlots; slot += 4)
            {
                uint arrayAddress;
                if (!_target.Memory.ReadUInt32(slot, out arrayAddress)) { continue; }
                if (arrayAddress < 0x10000) { continue; }

                List<uint> probe;
                if (!TryReadObjectArray(arrayAddress, count, out probe)) { continue; }
                if (!Contains(slots, slot)) { slots.Add(slot); }
            }
            return slots;
        }

        /// <summary>
        /// 从某个数组地址读出 `count` 个指针，校验它们是不是**一组真正的游戏对象**。
        /// 校验：数量对得上、互不相同、都在堆区间、且虚表落在模块内。
        /// </summary>
        private bool TryReadObjectArray(uint arrayAddress, uint count, out List<uint> objects)
        {
            objects = new List<uint>();
            if (count == 0 || count > SelectionProbe.MaxCount) { return false; }

            for (uint i = 0; i < count; i++)
            {
                uint objectAddress;
                if (!_target.Memory.ReadUInt32(arrayAddress + i * 4, out objectAddress)) { return false; }
                if (objectAddress < 0x10000) { return false; }
                if (Contains(objects, objectAddress)) { return false; }

                // 虚表必须在模块区间内 —— 这是"它真的是游戏对象"最硬的一条证据
                uint vtable;
                if (!_target.Memory.ReadUInt32(objectAddress, out vtable)) { return false; }
                if (vtable < _target.ModuleBase
                    || vtable >= _target.ModuleBase + _target.ModuleSize) { return false; }

                objects.Add(objectAddress);
            }
            return objects.Count > 0;
        }

        private static bool Contains(List<uint> list, uint value)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == value) { return true; }
            }
            return false;
        }

        private static bool SameList(List<uint> a, List<uint> b)
        {
            if (a.Count != b.Count) { return false; }
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i]) { return false; }
            }
            return true;
        }

        /// <summary>
        /// 统计有多少个对象引用了"持有你金钱的那个对象"（也就是你的 HouseClass）。
        ///
        /// 不硬编码任何偏移：在对象里挑出所有看起来像堆指针的字段，
        /// 读它指向的内存，看里面有没有出现**当前金钱值**。
        /// 全局只有 HouseClass 持有金钱，所以命中即说明这个对象归属本方。
        /// </summary>
        private int CheckOwnership(List<ObjectSnapshot> snapshots, Dictionary<uint, bool> owned)
        {
            if (!_profile.Money.IsCalibrated) { return -1; }

            uint moneyAddress;
            string error;
            if (!_profile.Money.TryResolve(
                _target.Memory, _target.ModuleBase, out moneyAddress, out error)) { return -1; }

            uint money;
            if (!_target.Memory.ReadUInt32(moneyAddress, out money) || money == 0) { return -1; }

            int limit = snapshots.Count < MaxOwnershipChecks ? snapshots.Count : MaxOwnershipChecks;
            for (int i = 0; i < limit; i++)
            {
                uint address = snapshots[i].BaseAddress;
                if (owned.ContainsKey(address)) { continue; }
                owned[address] = ReferencesMoney(_target.Memory, snapshots[i], money);
            }

            int ownedCount = 0;
            foreach (bool value in owned.Values)
            {
                if (value) { ownedCount++; }
            }
            return ownedCount;
        }

        private static bool ReferencesMoney(MemoryIO memory, ObjectSnapshot snapshot, uint money)
        {
            if (snapshot == null || !snapshot.IsValid) { return false; }

            int limit = snapshot.Length < 0x1000 ? snapshot.Length : 0x1000;
            for (int offset = 0; offset + 4 <= limit; offset += 4)
            {
                uint pointer;
                if (!snapshot.TryGet(offset, out pointer)) { break; }
                if (pointer < 0x10000 || pointer > 0x7FFFFFFF) { continue; }

                byte[] block = memory.ReadBytes(pointer, 0x800);
                if (block == null) { continue; }

                for (int k = 0; k + 4 <= block.Length; k += 4)
                {
                    if (HexUtil.ReadUInt32(block, k) == money) { return true; }
                }
            }
            return false;
        }

        /// <summary>
        /// 监视心跳：每 500ms 采一次当前选中单位，各自与"首次见到它时"的状态对比。
        ///
        /// 新出现的对象会立刻补记一份基准 —— 这正是这套机制能容忍玩家操作顺序的原因：
        /// 单位被重新选回来时若还没记过基准，就当场记一份；
        /// 记过基准的则一直与那份基准比，于是"受伤前 / 受伤后"两侧的数据都拿得到。
        /// </summary>
        private void WatchTick(object sender, EventArgs e)
        {
            if (!_watching) { return; }
            if (_attach == null || !_attach.IsReady) { return; }

            // 每拍重新读"选中数量"；读不到（为 0 或越界）就跳过这一拍。
            uint count;
            if (!_target.Memory.ReadUInt32(_countAddress, out count)) { return; }
            if (count == 0 || (uint)count > SelectionProbe.MaxCount) { return; }

            _watchTicks++;
            _watchCurrentSelected = (int)count;

            if (_watchTicks == 1)
            {
                // 在数量字段附近，把所有"看起来是对象指针数组"的槽位都找出来。
                // 之前只认死一个槽位（数量字段 − 8），万一那不是同一个容器，
                // 后面无论怎么采都采不到真正的单位 —— 这是连续几轮失败的怀疑重点。
                _candidateSlots.Clear();
                _candidateSlots.AddRange(FindCandidateSlots(count));
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "监视起始：选中数量 {0}，附近找到 {1} 个疑似对象数组槽位",
                    count, _candidateSlots.Count));
            }

            // 1) 逐个候选槽位读对象数组，全部纳入跟踪集合。
            //
            //    **纳入之后长期跟踪，不再要求它保持选中** —— 这是关键。
            //    真机上要让单位挨打必须先取消选中（玩家反馈），
            //    若只在"被选中"时采样，受害者一取消选中就丢出视野，
            //    再怎么打都采不到（实测：58 次采样里跟踪的始终是攻击单位）。
            int newObjects = 0;
            List<uint> tickObjects = new List<uint>();
            for (int s = 0; s < _candidateSlots.Count; s++)
            {
                uint arrayAddress;
                if (!_target.Memory.ReadUInt32(_candidateSlots[s], out arrayAddress)) { continue; }

                List<uint> objects;
                if (!TryReadObjectArray(arrayAddress, count, out objects)) { continue; }

                for (int i = 0; i < objects.Count; i++)
                {
                    if (!Contains(tickObjects, objects[i])) { tickObjects.Add(objects[i]); }

                    if (_watchSeen.ContainsKey(objects[i])) { continue; }
                    if (_watchSnapshots.Count >= MaxTrackedObjects) { break; }

                    ObjectSnapshot snapshot = ObjectFieldHunter.Snapshot(
                        _target.Memory, objects[i], ObjectFieldHunter.DefaultSnapshotLength);
                    if (!snapshot.IsValid) { continue; }

                    _watchSnapshots.Add(snapshot);
                    _watchSeen[objects[i]] = _watchSnapshots.Count - 1;
                    newObjects++;
                }
            }
            if (newObjects > 0)
            {
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "监视中新增 {0} 个跟踪对象，累计 {1} 个", newObjects, _watchSnapshots.Count));
            }

            // 数组内容有没有跟着选中变？这是判定"这个数组到底是不是选中列表"最直接的证据：
            // 玩家换过选中的单位，这里却一直是 0，说明这些槽位指的根本不是选中列表。
            if (_watchTicks > 1 && !SameList(tickObjects, _watchLastObjects)) { _watchListChanges++; }
            _watchLastObjects.Clear();
            _watchLastObjects.AddRange(tickObjects);

            // 2) 采样**全部**已跟踪对象（不只当前选中的那几个）
            List<List<FieldChange>> perObject = new List<List<FieldChange>>();
            int changed = 0;
            int alive = 0;
            int lost = 0;
            for (int i = 0; i < _watchSnapshots.Count; i++)
            {
                ObjectSnapshot before = _watchSnapshots[i];

                // 先确认对象还活着：虚表必须仍在模块区间内。
                // 单位被打死后内存会被回收或复用，靠这一条把它剔掉，
                // 免得拿旧基准去比新对象、凭空造出一堆"变小"字段。
                uint vtable;
                if (!_target.Memory.ReadUInt32(before.BaseAddress, out vtable)
                    || vtable < _target.ModuleBase
                    || vtable >= _target.ModuleBase + _target.ModuleSize)
                {
                    lost++;
                    perObject.Add(new List<FieldChange>());
                    continue;
                }

                ObjectSnapshot now = ObjectFieldHunter.Snapshot(
                    _target.Memory, before.BaseAddress, before.Length);
                if (!now.IsValid) { lost++; perObject.Add(new List<FieldChange>()); continue; }

                alive++;

                int c;
                int u;
                ObjectFieldHunter.Summarize(before, now, out c, out u);
                changed += c;

                List<FieldChange> changes = ObjectFieldHunter.FindDecreased(
                    before, now, 1, 2000000, 1, 2000000, 60);

                // 打上归属标记：排名与展示优先采信"本方对象"上的变化。
                // 实测教训：不注意这一点，非本方结构里 6312 → 170（-97%）这种大幅跳变
                // 会把真正的血量（动员兵 125 → 23，-82%）挤到后面。
                bool owned = false;
                _watchOwnedMap.TryGetValue(before.BaseAddress, out owned);
                for (int k = 0; k < changes.Count; k++) { changes[k].Owned = owned; }

                if (changes.Count > 0 && (owned || !_watchOwnershipChecked))
                {
                    _watchLatest = string.Format(CultureInfo.InvariantCulture,
                        "{0}0x{1:X8} +0x{2:X} 从 {3} 掉到 {4}",
                        owned ? "本方 " : "",
                        before.BaseAddress, changes[0].Offset,
                        changes[0].Before, changes[0].After);
                }
                perObject.Add(changes);
            }
            _watchAlive = alive;
            _watchLost = lost;

            // 3) 对象归属校验：这些对象里有没有指向"持有你金钱的那个对象"的指针？
            //    单位对象必然带一个 Owner 指针指向自己的 HouseClass，而全局只有 House 持有金钱。
            //    所以命中就说明"它确实是你的单位"，不命中就说明跟踪对象根本不是单位 ——
            //    这条能把"没挨打"和"跟错对象"彻底分开，不必再靠猜。
            if (_watchTicks >= 3 && _watchSnapshots.Count > _watchOwnedMap.Count)
            {
                _watchOwned = CheckOwnership(_watchSnapshots, _watchOwnedMap);
                _watchOwnershipChecked = true;
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "对象归属校验：{0} 个跟踪对象里有 {1} 个引用了你的阵营对象",
                    _watchSnapshots.Count, _watchOwned));

                // 逐对象写明结果：排查时"哪个对象不是自己的"往往就是问题所在
                List<string> ownedText = new List<string>();
                for (int i = 0; i < _watchSnapshots.Count; i++)
                {
                    uint objectAddress = _watchSnapshots[i].BaseAddress;
                    bool value;
                    if (!_watchOwnedMap.TryGetValue(objectAddress, out value)) { continue; }
                    ownedText.Add(string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8}={1}", objectAddress, value ? "本方" : "非本方"));
                }
                Logger.Info("Unit", "归属明细：" + string.Join("、", ownedText.ToArray()));
            }

            if (changed > _watchChangedMax) { _watchChangedMax = changed; }

            List<FieldChangeSummary> merged = ObjectFieldHunter.MergeByOffset(perObject);
            int previousCount = _healthOffsets.Count;
            _healthOffsets.Clear();
            _healthOffsets.AddRange(merged);

            bool hit = merged.Count > 0;

            string ownership;
            if (!_watchOwnershipChecked) { ownership = "归属校验 中"; }
            else if (_watchOwned < 0) { ownership = "归属校验 跳过"; }
            else
            {
                ownership = string.Format(CultureInfo.InvariantCulture, "归属校验 {0}/{1}",
                    _watchOwned, _watchSnapshots.Count > 4 ? 4 : _watchSnapshots.Count);
            }

            _step3Status.ForeColor = hit ? Theme.Success : Theme.Text2;
            _step3Status.Text = string.Format(CultureInfo.InvariantCulture,
                "监视中｜采样{0}次｜槽位{1}个｜跟踪{2}个（选中{3}/存活{4}）｜列表变化{5}次｜{6}\r\n"
                + "变化字段{7}｜变小候选{8}　{9}{10}",
                _watchTicks, _candidateSlots.Count, _watchSnapshots.Count, _watchCurrentSelected,
                _watchAlive, _watchListChanges, ownership, _watchChangedMax, merged.Count,
                hit ? "已采到变化，可以「停止并对比」了。"
                    : "还没采到变小字段。去游戏里打它（取消选中也没关系）。",
                (hit && _watchLatest.Length > 0) ? "（最新：" + _watchLatest + "）" : "");

            if (merged.Count != previousCount) { RefreshUi(); }
        }

        // 注：原先的"手点两次快照再对比"（TakeSnapshot / CompareSnapshot）
        // 已被"持续监视"（OnWatchClick / WatchTick / OnStopWatchClick）取代。
        // 原因见 OnWatchClick 的注释。

        private void OnConfirmHealthClick(object sender, EventArgs e)
        {
            ScanCandidate selected = _healthCandidates.SelectedItem;
            if (selected == null)
            {
                _step3Status.ForeColor = Theme.Danger;
                _step3Status.Text = "请先在候选列表里选中一项。";
                return;
            }

            if (_snapshots.Count == 0)
            {
                _step3Status.ForeColor = Theme.Danger;
                _step3Status.Text = "快照状态已丢失，请重新记录快照再对比。";
                return;
            }

            _healthOffset = selected.Address - _snapshots[0].BaseAddress;

            FieldChangeSummary match = null;
            for (int i = 0; i < _healthOffsets.Count; i++)
            {
                if (_healthOffsets[i].Offset == _healthOffset) { match = _healthOffsets[i]; break; }
            }
            if (match == null)
            {
                _step3Status.ForeColor = Theme.Danger;
                _step3Status.Text = "选中的这一行已经不在候选里了，请重新对比一次。";
                return;
            }

            _healthMax = match.Before;

            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "确认为「单位血量」：对象 +0x{0:X}，满血 {1}，受伤后 {2}，吻合 {3} 个单位",
                _healthOffset, match.Before, match.After, match.AffectedObjects));

            _step3Status.ForeColor = Theme.Success;
            _step3Status.Text = string.Format(CultureInfo.InvariantCulture,
                "已确定血量字段：对象 +0x{0:X}（满血 {1} → 受伤后 {2}，{3} 个单位吻合）。"
                + "最后点「保存到配置」。",
                _healthOffset, match.Before, match.After, match.AffectedObjects);
            RefreshUi();
        }

        private void SetStep3Status(string text, Color color)
        {
            Post(delegate
            {
                _step3Status.ForeColor = color;
                _step3Status.Text = text;
                RefreshUi();
            });
        }

        // ============================================================ 保存 / 重置

        private void OnSaveClick(object sender, EventArgs e)
        {
            if (_countAddress == 0)
            {
                _footerStatus.ForeColor = Theme.Danger;
                _footerStatus.Text = "还没有可保存的内容 —— 至少要先完成第 1 步。";
                return;
            }
            if (_probe != null && _probe.Found && _healthOffset == 0)
            {
                _footerStatus.ForeColor = Theme.Warning;
                _footerStatus.Text = "已探测到选中列表，但血量偏移还没确定 —— 无敌功能还差这一步，确定只保存前半部分吗？";
                // 不阻塞：允许用户先保存选中列表，血量下次再补
            }
            RunAsync("保存到配置", delegate { return SaveAll(); });
        }

        private bool SaveAll()
        {
            List<string> saved = new List<string>();

            // 数量字段
            if (_countAddress != 0)
            {
                string note;
                _profile.SelectedCount = BuildLocator(_countAddress, "选中数量", out note);
                _profile.SelectedCount.Note = "由「单位校准」页定位。数量字段的数值等于当前选中的单位个数。" + note;
                if (_profile.SelectedCount.IsCalibrated) { saved.Add("选中数量"); }
            }

            // 选中列表（数组指针的存放位置）
            if (_probe != null && _probe.Found)
            {
                string note;
                _profile.SelectedList = BuildLocator(_probe.ArraySlotAddress, "选中列表", out note);
                _profile.SelectedList.Note = string.Format(CultureInfo.InvariantCulture,
                    "由「单位校准」页定位。该位置存放的是「选中对象指针数组」的起始地址；"
                    + "引擎会按上面「选中数量」读到的个数，从数组里依次取出对象指针。{0}", note);
                if (_profile.SelectedList.IsCalibrated) { saved.Add("选中列表"); }
            }

            // 血量偏移 + 满血值
            if (_healthOffset != 0)
            {
                _profile.OffsetTechnoHealth = _healthOffset;
                _profile.TechnoMaxHealth = _healthMax;
                saved.Add(string.Format(CultureInfo.InvariantCulture,
                    "血量偏移 +0x{0:X}（满血 {1}）", _healthOffset, _healthMax));
            }

            string error;
            bool ok = _store.Save(_profile, out error);

            Post(delegate
            {
                if (!ok)
                {
                    _footerStatus.ForeColor = Theme.Danger;
                    _footerStatus.Text = "保存失败：" + error;
                    return;
                }

                _footerStatus.ForeColor = Theme.Success;
                _footerStatus.Text = "已保存：" + string.Join("｜", saved.ToArray())
                    + "（下次启动自动加载，无需重新校准）";
                Logger.Info("Unit", "单位校准已保存：" + string.Join("｜", saved.ToArray()));

                EventHandler handler = CalibrationChanged;
                if (handler != null) { handler(this, EventArgs.Empty); }
                RefreshUi();
            });
            return true;
        }

        /// <summary>
        /// 为一个地址生成可持久化的指针链。
        /// 先直接回溯；失败就用"金钱链 + 相对偏移"推导 —— 对象内偏移超过回溯上限（0x400）时只能靠这条。
        /// </summary>
        private FieldLocator BuildLocator(uint address, string fieldName, out string note)
        {
            note = "";
            FieldLocator locator = new FieldLocator();
            locator.ValueWidth = 4;
            locator.ValueScale = 1;

            // 目标落在游戏模块内 → 它就是个**全局变量**，地址固定、跨会话恒定。
            // 这种情况绝不能去套指针链：那会解引用这个地址，读到的变成"变量内容"而不是变量本身。
            // 真机实测「选中数量」正是这一类（0x00B08EC8 落在模块 .data 里）。
            if (address >= _target.ModuleBase
                && address < _target.ModuleBase + _target.ModuleSize)
            {
                locator.SetStaticAddress(address - _target.ModuleBase);
                note = string.Format(CultureInfo.InvariantCulture,
                    "（目标 0x{0:X8} 位于游戏模块内，判定为全局变量：直接用「模块基址 + 偏移 0x{1:X}」定位，"
                    + "跨会话恒定，无需指针链）",
                    address, locator.StaticOffset);
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "{0} 判定为全局变量：0x{1:X8} = 模块基址 + 0x{2:X}",
                    fieldName, address, locator.StaticOffset));
                return locator;
            }

            PointerScanner scanner = new PointerScanner(_target.Memory);
            PointerScanOptions options = new PointerScanOptions();
            PointerScanReport report = scanner.Scan(address, _target.ModuleBase, _target.ModuleSize,
                _target.PointerRegions, options, CancellationToken.None, delegate(string s) { });

            if (report.Paths.Count > 0)
            {
                locator.SetFromPaths(report.Paths);
                note = string.Format(CultureInfo.InvariantCulture,
                    "（直接指针回溯成功，静态根 0x{0:X8}，保留 {1} 条候选链）",
                    report.Paths[0].StaticAddress, locator.ChainCount);
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "{0} 指针回溯成功：{1}（共 {2} 条候选链）",
                    fieldName, report.Paths[0].Format(_target.ModuleBase), locator.ChainCount));
                return locator;
            }

            // 回溯失败：用金钱链推导
            uint moneyAddress = _profile.Money.IsCalibrated
                ? _profile.Money.ResolveOrZero(_target.Memory, _target.ModuleBase)
                : 0;

            long delta;
            List<PointerChain> derived = ChainDerivation.Derive(
                _profile.Money, moneyAddress, address, out delta);

            for (int i = 0; i < derived.Count; i++) { locator.AddChain(derived[i]); }

            if (locator.ChainCount > 0)
            {
                note = string.Format(CultureInfo.InvariantCulture,
                    "（直接回溯 0 命中，改由金钱链加相对偏移 0x{0:X} 推导，共 {1} 条候选链）",
                    delta, locator.ChainCount);
                Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                    "{0} 由金钱链推导成功：{1}（相对偏移 0x{2:X}，共 {3} 条候选链）",
                    fieldName, locator.Chain.Format(_target.ModuleBase), delta, locator.ChainCount));
            }
            else
            {
                note = "（直接回溯失败，且无法从金钱链推导 —— 该字段可能不在金钱所在的对象里）";
                Logger.Warn("Unit", fieldName + " 无法生成可持久化的指针链");
            }
            return locator;
        }

        private void OnResetClick(object sender, EventArgs e)
        {
            Logger.Info("Unit", "收到操作「重置」");

            _countCandidatesData = new List<ScanCandidate>();
            _countScanStarted = false;
            _firstCount = 0;
            _scopeIndex = 0;
            _scanNote = "";
            _countAddress = 0;
            _count = 0;
            _probe = null;
            _watchTimer.Stop();
            _watching = false;
            _watchSnapshots.Clear();
            _watchSeen.Clear();
            _watchTicks = 0;
            _watchChangedMax = 0;
            _watchCurrentSelected = 0;
            _watchAlive = 0;
            _watchLost = 0;
            _watchLatest = "";
            _snapshots.Clear();
            _healthOffsets.Clear();
            _healthOffset = 0;
            _healthMax = 0;

            _countCandidates.SetItems(_countCandidatesData, 0);
            _healthCandidates.SetItems(new List<ScanCandidate>(), 0);
            _probeResult.Text = "";
            _step1Status.Text = "";
            _step3Status.Text = "";
            _footerStatus.Text = "已重置（配置里的已保存内容未被清除）。";
            _footerStatus.ForeColor = Theme.Text2;
            RefreshUi();
        }

        // ============================================================ 辅助

        private ValueScanReport ScanValue(IList<MemoryRegion> regions, uint value, bool uint16)
        {
            ValueScanOptions options = new ValueScanOptions();
            options.FourByteAligned = true;
            options.MatchUInt16 = uint16;
            options.MaxResults = 400000;
            // 小分块：限定范围可能跨越多段不同属性的内存，小分块丢得少
            options.ChunkSize = 0x1000;

            ValueScanner scanner = new ValueScanner(_target.Memory);
            return scanner.Scan(regions, value, options, CancellationToken.None, null);
        }

        /// <summary>
        /// 一次性按 4 字节与 2 字节两种宽度搜索并合并结果。
        ///
        /// 早先的写法是"只有 4 字节搜不到时才补搜 2 字节" —— **这是错的**：
        /// 小数字（比如 5）在 4 字节下本来就有上万处噪声，于是 2 字节那一路永远不会执行；
        /// 万一真实字段是 short，它压根进不了候选，后续筛选必然 0 个（真机踩过）。
        /// 两种宽度一起收，让筛选去淘汰噪声。
        /// </summary>
        private List<ScanCandidate> ScanBothWidths(IList<MemoryRegion> regions, uint value, out string summary)
        {
            List<ScanCandidate> merged = new List<ScanCandidate>();
            HashSet<uint> seen = new HashSet<uint>();

            ValueScanReport wide = ScanValue(regions, value, false);
            MergeCandidates(merged, seen, wide.Candidates);
            int wideCount = wide.Candidates.Count;

            ValueScanReport narrow = ScanValue(regions, value, true);
            MergeCandidates(merged, seen, narrow.Candidates);
            int narrowCount = narrow.Candidates.Count;

            string note = "";
            if (wide.Truncated || narrow.Truncated) { note = "（已达候选上限，数量偏小噪声太大）"; }
            if (!wide.Ok) { note += "｜4 字节扫描异常：" + wide.Error; }

            // 把"实际扫了多大内存、比对了多少个位置"也报出来。
            // 这两个数字能把两种完全不同的失败区分开：
            //   · 位置数接近 0  → 范围根本不可读（是我的范围构造有问题）
            //   · 位置数正常但 0 命中 → 范围可读，只是里面没有这个值（字段不在这儿）
            summary = string.Format(CultureInfo.InvariantCulture,
                "4 字节 {0:N0} 处 + 2 字节 {1:N0} 处 → 合并 {2:N0} 个候选（实扫 {3:N1} MB / 比对 {4:N0} 个位置）{5}",
                wideCount, narrowCount, merged.Count,
                wide.BytesScanned / 1048576.0, wide.PositionsTested, note);
            return merged;
        }

        private static void MergeCandidates(
            List<ScanCandidate> target, HashSet<uint> seen, List<ScanCandidate> source)
        {
            for (int i = 0; i < source.Count; i++)
            {
                if (!seen.Add(source[i].Address)) { continue; }
                target.Add(source[i]);
            }
        }

        /// <summary>
        /// 以金钱字段为中心划出的搜索范围。
        ///
        /// 为什么必须有这一步：真机上直接全内存搜小数字（1 / 5 / 6），
        /// 4 字节命中 30 多万处、2 字节直接顶到 40 万上限，
        /// **真字段很可能根本没被收进候选**，更别提筛选了。
        /// 而金钱、电力都在 HouseClass 里，选中单位列表属于玩家状态、极可能同在，
        /// 所以先只看这一片：同样的数字，候选能从几十万降到几百个。
        /// </summary>
        private List<MemoryRegion> BuildHouseWindowRegions(uint moneyAddress, uint forward)
        {
            List<MemoryRegion> regions = new List<MemoryRegion>();

            uint start = moneyAddress > HouseWindowBack ? moneyAddress - HouseWindowBack : 0;
            start = start & 0xFFFFFFFCu;
            uint end = moneyAddress + forward;

            MemoryRegion region = new MemoryRegion();
            region.Base = start;
            region.Size = (ulong)(end - start);
            region.Protect = Win32Api.PAGE_READWRITE;
            region.Type = Win32Api.MEM_PRIVATE;
            region.State = Win32Api.MEM_COMMIT;
            regions.Add(region);
            return regions;
        }

        /// <summary>按档位取搜索区域。</summary>
        private List<MemoryRegion> BuildScopeRegions(int scopeIndex, uint moneyAddress)
        {
            switch (scopeIndex)
            {
                case 0:
                    return moneyAddress == 0
                        ? new List<MemoryRegion>()
                        : BuildHouseWindowRegions(moneyAddress, HouseWindowForward);
                case 1:
                    return moneyAddress == 0
                        ? new List<MemoryRegion>()
                        : BuildHouseWindowRegions(moneyAddress, HouseWindowWide);
                case 2:
                    return BuildModuleDataRegions();
                default:
                    return new List<MemoryRegion>(_target.PointerRegions);
            }
        }

        /// <summary>
        /// 游戏模块里可写的映像页 —— 也就是 .data 段，全局变量住在这里。
        ///
        /// 必须单独拿出来：普通数值搜索用的区域集只含私有页（堆），
        /// 而 .data 属于 image 类型，会被整体排除。若目标是全局变量，
        /// 不把这一档加进来就永远搜不到。
        /// </summary>
        private List<MemoryRegion> BuildModuleDataRegions()
        {
            List<MemoryRegion> regions = new List<MemoryRegion>();
            IList<MemoryRegion> all = _target.PointerRegions;

            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Type != Win32Api.MEM_IMAGE) { continue; }
                if (!all[i].Writable) { continue; }
                regions.Add(all[i]);
            }
            return regions;
        }

        private uint ResolveMoneyAddress()
        {
            if (!_profile.Money.IsCalibrated) { return 0; }
            return _profile.Money.ResolveOrZero(_target.Memory, _target.ModuleBase);
        }

        private void SetStep1Status(string text, Color color)
        {
            Post(delegate
            {
                _step1Status.ForeColor = color;
                _step1Status.Text = text;
                RefreshUi();
            });
        }

        private void RefreshUi()
        {
            // 卡片宽度要等 TableLayoutPanel 排完版才确定，而它一定晚于构造函数，
            // 所以每次状态变化时都顺带重排一次文字宽度
            ResizeWideLabels();
            RefreshEnabledState();
        }

        /// <summary>
        /// 由主窗口的 1Hz 定时器调用，只更新可用状态、不触发重排。
        ///
        /// **这里曾经漏掉过一次，代价是用户完全用不了这一页**：
        /// 按钮的可用状态取决于"游戏是否已连接"，而附加是**异步**完成的 ——
        /// 构造函数执行时还没连上，于是整页按钮被置灰；而主窗口的定时器当时只刷新了
        /// 另外两页，这一页永远不会被重新评估，只有点「重置」（它会调刷新）才能解开。
        /// 凡是"初始状态依赖异步结果"的页面，都必须挂到定时器上。
        /// </summary>
        public void TickRefresh()
        {
            RefreshEnabledState();
        }

        /// <summary>
        /// 从已保存的配置恢复第 1、2 步的状态。
        ///
        /// 为什么可以恢复：「选中数量」实测是模块 .data 里的**全局变量**，
        /// 地址 = 模块基址 + 偏移，跨会话恒定；「选中列表」是它前面 8 字节处的数组指针
        /// （`DynamicVectorClass` 的 {数组指针, 容量, 数量} 布局）。
        /// 两者都已落盘，没必要每次重开都让用户把整页最费时的两步重做一遍。
        /// 解析失败（比如游戏换了版本）就当作未校准，让用户重做。
        /// </summary>
        private void TryRestoreFromProfile()
        {
            if (_restoreAttempted || _countAddress != 0) { return; }
            if (_attach == null || !_attach.IsReady) { return; }
            _restoreAttempted = true;

            if (!_profile.SelectedCount.IsCalibrated) { return; }

            uint countAddress;
            string error;
            if (!_profile.SelectedCount.TryResolve(
                _target.Memory, _target.ModuleBase, out countAddress, out error))
            {
                Logger.Info("Unit", "配置里的选中数量无法解析，需重新校准：" + error);
                return;
            }

            _countAddress = countAddress;

            uint slot = 0;
            if (_profile.SelectedList.IsCalibrated)
            {
                _profile.SelectedList.TryResolve(_target.Memory, _target.ModuleBase, out slot, out error);
            }

            if (slot != 0)
            {
                SelectionProbeResult probe = new SelectionProbeResult();
                probe.Found = true;
                probe.CountAddress = countAddress;
                probe.ArraySlotAddress = slot;
                probe.Trace.Add("来自已保存的校准配置（未重新探测）");
                _probe = probe;
            }

            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "已从配置恢复：选中数量 0x{0:X8}{1}",
                countAddress, slot != 0 ? string.Format(CultureInfo.InvariantCulture,
                    "，选中列表槽 0x{0:X8}", slot) : "（选中列表缺失，需重做第 2 步）"));

            _step1Status.ForeColor = Theme.Text2;
            _step1Status.Text = string.Format(CultureInfo.InvariantCulture,
                "已从配置恢复选中数量字段 0x{0:X8}（全局变量，跨会话不变）。可直接做第 3 步。",
                countAddress);
            if (slot != 0)
            {
                _probeResult.ForeColor = Theme.Text2;
                _probeResult.Text = string.Format(CultureInfo.InvariantCulture,
                    "已从配置恢复选中列表槽 0x{0:X8}（来自上次校准）。", slot);
            }
        }

        private void RefreshEnabledState()
        {
            bool ready = _attach != null && _attach.IsReady;
            TryRestoreFromProfile();

            // 刻意**不**因为"没连上游戏"而把按钮变灰。
            //
            // 曾经的写法是"没连接就全部禁用"，结果用户看到的是：整页按钮全灰、
            // 又看不出为什么，只当成"工具坏了"。而"连没连上游戏"是个会随时变化的外部状态，
            // 拿它当灰化条件，界面上就永远说不清原因。
            // 现在的做法：按钮随时可点，点下去由各自的处理函数给出**具体原因**；
            // 同时副标题常驻显示连接状态，让"没连上"这件事第一眼就能看见。
            _scanButton.Enabled = !_busy;
            _narrowButton.Enabled = !_busy && _countScanStarted;
            _changedButton.Enabled = !_busy && _countScanStarted;
            _verifyButton.Enabled = !_busy && _countCandidatesData.Count > 0;
            _confirmCountButton.Enabled = !_busy && _countAddress == 0 && _countCandidatesData.Count > 0;
            _probeButton.Enabled = !_busy && _countAddress != 0;
            _watchButton.Enabled = !_busy && !_watching
                && _probe != null && _probe.Found && _countAddress != 0;
            _stopWatchButton.Enabled = !_busy && _watching;
            _confirmHealthButton.Enabled = !_busy && !_watching && _healthOffsets.Count > 0;
            _saveButton.Enabled = !_busy && _countAddress != 0;

            if (ready)
            {
                _subtitle.Text = string.Format(CultureInfo.InvariantCulture,
                    "已连接游戏（pid={0}）。为「选中单位无敌」定位选中列表与单位血量偏移。",
                    _attach.ProcessId);
                _subtitle.ForeColor = Theme.Text2;
                return;
            }

            // 未连接时把原因写清楚，而不是干巴巴一句"未连接"
            string reason = "未检测到 gamemd.exe";
            if (_attach != null && _attach.State == AttachState.Error)
            {
                reason = _attach.LastErrorWasAccessDenied
                    ? "连接被拒绝：权限不足，请以管理员身份运行本程序"
                    : "连接失败：" + ShortenForUi(_attach.LastError);
            }

            _subtitle.Text = "⚠ " + reason + "。请先启动游戏并进入一局单人遭遇战 —— 之后按钮点下去才会有实际动作。";
            _subtitle.ForeColor = Theme.Warning;
        }

        /// <summary>把长错误信息压到一行，完整内容仍在日志里。</summary>
        private static string ShortenForUi(string message)
        {
            if (string.IsNullOrEmpty(message)) { return "未知错误"; }
            int stop = message.IndexOf('。');
            if (stop > 0 && stop <= 40) { return message.Substring(0, stop); }
            if (message.Length <= 40) { return message; }
            return message.Substring(0, 40) + "…";
        }

        // ============================================================ 线程调度

        private void RunAsync(string action, Func<bool> work)
        {
            // 每个按钮都留一条点击日志：出问题时能立刻区分"点击没到达处理函数"
            // 和"到达了但因为没连上游戏而中止"，不用靠猜。
            Logger.Info("Unit", string.Format(CultureInfo.InvariantCulture,
                "收到操作「{0}」｜连接={1}｜已连接={2}",
                action, _attach == null ? "无" : _attach.State.ToString(),
                _attach != null && _attach.IsReady));

            if (_busy)
            {
                Logger.Warn("Unit", "上一次操作还在进行中，忽略本次「" + action + "」");
                return;
            }
            _busy = true;
            RefreshUi();

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                try { work(); }
                catch (Exception ex) { Logger.Error("Unit", "单位校准操作异常", ex); }

                Post(delegate
                {
                    _busy = false;
                    RefreshUi();
                });
            });
        }

        private void Post(Action action)
        {
            try
            {
                if (IsHandleCreated && InvokeRequired) { BeginInvoke(action); }
                else { action(); }
            }
            catch (Exception)
            {
                // 页面销毁中，忽略
            }
        }
    }
}
