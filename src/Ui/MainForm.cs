using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MoTrainer.Calibration;
using MoTrainer.Cheats;
using MoTrainer.Core;
using MoTrainer.Ui.Controls;
using MoTrainer.Ui.Pages;
using MoTrainer.Util;

namespace MoTrainer.Ui
{
    /// <summary>
    /// 主窗口：顶部状态条 + 左侧导航 + 右侧内容区 + 底部日志条。
    /// 使用 TableLayoutPanel 作为根容器，避免 Docking 顺序带来的布局不确定性。
    /// </summary>
    public sealed class MainForm : Form
    {
        private const int NavTopOffset = 72;
        private const int NavItemHeight = 44;

        // 热键 ID：只要在本窗口内唯一即可
        private const int HotkeyMoney = 0xA001;
        private const int HotkeyPower = 0xA002;
        private const int HotkeyInvincible = 0xA003;

        private readonly ProcessAttach _attach;
        private readonly MemoryIO _memory;
        private readonly ProfileStore _profileStore;
        private readonly CalibrationProfile _profile;
        private readonly TrainerSettings _settings = new TrainerSettings();
        private readonly LiveCalibrationTarget _target;
        private readonly CheatEngine _engine;
        private readonly FeaturesPage _featuresPage;
        private readonly List<int> _registeredHotkeys = new List<int>();

        private readonly TableLayoutPanel _root;
        private readonly StatusStripView _statusStrip;
        private readonly Panel _navHost;
        private readonly Panel _contentHost;

        /// <summary>日志区标题栏与标题，只用于布局自检时上报实际尺寸。</summary>
        private readonly Panel _logHeader;
        private readonly Label _logTitle;
        private readonly LogView _logView;
        private readonly Button _exportLogButton;
        private readonly Button _openLogDirButton;
        private readonly Timer _uiTimer;

        private readonly List<NavButton> _navButtons = new List<NavButton>();

        /// <summary>本次运行里已确认过高风险提示的功能（确认一次即不再拦，让热键可以独立开关）。</summary>
        private readonly HashSet<CheatId> _riskConfirmed = new HashSet<CheatId>();
        private readonly List<Control> _pages = new List<Control>();
        private readonly HomePage _homePage;
        private readonly CalibratePage _calibratePage;
        private readonly UnitPage _unitPage;
        private readonly AdvancedPage _advancedPage;

        public MainForm(ProcessAttach attach, MemoryIO memory)
        {
            _attach = attach;
            _memory = memory;

            Text = "MO Trainer · 心灵终结修改器";
            BackColor = Theme.Bg0;
            ForeColor = Theme.Text0;
            Font = Theme.Body;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1000, 660);
            MinimumSize = new Size(880, 580);

            // 窗口不得超出屏幕工作区。
            //
            // 真机实测（导出日志）：窗口被拉到 **2527 宽**，而显示器装不下 ——
            // 右边缘落在屏幕外，正卡在日志那两个按钮上，看起来就是"按钮的字只能看到一半"。
            // 这类"被切"根本不是布局问题，是**窗口跑到屏幕外面去了**：
            // 布局怎么调都没用，因为按钮在窗口里的位置一直是对的。
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            if (ClientSize.Width > work.Width) { ClientSize = new Size(work.Width, ClientSize.Height); }
            if (ClientSize.Height > work.Height) { ClientSize = new Size(ClientSize.Width, work.Height); }
            DoubleBuffered = true;

            _root = new TableLayoutPanel();
            _root.Dock = DockStyle.Fill;
            _root.ColumnCount = 2;
            _root.RowCount = 3;
            _root.BackColor = Theme.Bg0;
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Theme.NavWidth));
            _root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.StatusHeight));
            _root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            _root.RowStyles.Add(new RowStyle(SizeType.Absolute, Theme.LogHeight));

            // ---------- 状态条 ----------
            _statusStrip = new StatusStripView();
            _statusStrip.Margin = new Padding(0);
            _root.Controls.Add(_statusStrip, 0, 0);
            _root.SetColumnSpan(_statusStrip, 2);

            // ---------- 左侧导航 ----------
            _navHost = new Panel();
            _navHost.Dock = DockStyle.Fill;
            _navHost.BackColor = Theme.Bg1;
            _navHost.Margin = new Padding(0);
            _root.Controls.Add(_navHost, 0, 1);

            // 品牌区用绝对定位，避免与导航按钮的停靠顺序互相影响
            Label brand = new Label();
            brand.AutoSize = false;
            brand.Font = Theme.BodyBold;
            brand.ForeColor = Theme.PrimaryLight;
            brand.BackColor = Color.Transparent;
            brand.Text = "MO TRAINER";
            brand.TextAlign = ContentAlignment.MiddleLeft;
            brand.Location = new Point(20, 18);
            brand.Size = new Size(Theme.NavWidth - 24, 20);
            _navHost.Controls.Add(brand);

            Label brandSub = new Label();
            brandSub.AutoSize = false;
            brandSub.Font = Theme.Small;
            brandSub.ForeColor = Theme.Text2;
            brandSub.BackColor = Color.Transparent;
            brandSub.Text = "心灵终结修改器 v0.1";
            brandSub.TextAlign = ContentAlignment.MiddleLeft;
            brandSub.Location = new Point(20, 38);
            brandSub.Size = new Size(Theme.NavWidth - 24, 18);
            _navHost.Controls.Add(brandSub);

            // ---------- 右侧内容 ----------
            _contentHost = new Panel();
            _contentHost.Dock = DockStyle.Fill;
            _contentHost.BackColor = Theme.Bg0;
            _contentHost.Margin = new Padding(0);
            _root.Controls.Add(_contentHost, 1, 1);

            // ---------- 底部日志 ----------
            Panel logHost = new Panel();
            logHost.Dock = DockStyle.Fill;
            logHost.BackColor = Theme.Bg1;
            logHost.Margin = new Padding(0);
            logHost.Padding = new Padding(12, 6, 12, 8);

            // 日志标题栏：标题 + 导出 / 打开目录两个按钮。
            // 面板只有两百来像素高，截图放不下全部内容 —— 出问题时让用户"导出后发文件"，
            // 比反复截图省事得多（前面几轮真机反馈都吃过这个亏）。
            Panel logHeader = _logHeader = new Panel();
            logHeader.Dock = DockStyle.Top;
            // 高度由字体实测决定，不用常量 28。
            // 高 DPI 下字号被放大，而写死的高度不变 ——
            // 右停靠的按钮被父容器压扁，结果就是**按钮文字只显示上半截**（用户实测）。
            logHeader.Height = Math.Max(28, Theme.BodyBold.Height + 14);
            logHeader.BackColor = Theme.Bg1;

            // 标题不用 AutoSize：它右侧钉着两个按钮，若标题按文本宽度撑开，
            // 两者会叠在一起（小窗口下就是"按钮只剩一半"的现象）。
            // 改为填满剩余空间 + 超长省略号，宽度永远由面板决定。
            Label logTitle = _logTitle = new Label();
            logTitle.Text = "运行日志";
            logTitle.Font = Theme.BodyBold;
            logTitle.ForeColor = Theme.Text1;
            logTitle.AutoSize = false;
            logTitle.AutoEllipsis = true;
            logTitle.TextAlign = ContentAlignment.MiddleLeft;
            logTitle.Dock = DockStyle.Fill;

            _openLogDirButton = UiFactory.MakeButton("打开日志目录", 100);
            _openLogDirButton.Dock = DockStyle.Right;
            _openLogDirButton.Click += OnOpenLogDirectoryClick;
            logHeader.Controls.Add(_openLogDirButton);

            _exportLogButton = UiFactory.MakeButton("导出日志", 80);
            _exportLogButton.Dock = DockStyle.Right;
            _exportLogButton.Click += OnExportLogClick;
            logHeader.Controls.Add(_exportLogButton);

            // 标题**最后**加入并置于最前 —— 顺序不能反。
            // WinForms 的停靠按 z 序结算：先结算的控件先占位。若 Fill 的标题先去占，
            // 它会把整条宽度吃光，两个右停靠按钮只剩一个字宽
            // （用户截图里"打开日""导出日"被切掉，就是这个顺序问题，与控件宽度无关）。
            logHeader.Controls.Add(logTitle);
            logTitle.BringToFront();

            logHost.Controls.Add(logHeader);

            _logView = new LogView();
            _logView.Dock = DockStyle.Fill;
            logHost.Controls.Add(_logView);
            _logView.BringToFront();

            _root.Controls.Add(logHost, 0, 2);
            _root.SetColumnSpan(logHost, 2);

            Controls.Add(_root);

            // ---------- 校准配置 ----------
            // 文件缺失或损坏时 Load() 会返回一份空配置，界面据此显示"未校准"
            _profileStore = new ProfileStore(AppDomain.CurrentDomain.BaseDirectory);
            _profile = _profileStore.Load();

            // ---------- 功能与锁定引擎 ----------
            // 运行设置与校准配置分开存：前者是"我想开什么"，后者是"内存长什么样"
            _settings.Load(_settings.PathIn(_profileStore.ConfigFolder));
            _target = new LiveCalibrationTarget(_attach, _memory);
            _engine = new CheatEngine(_target, _settings);
            RegisterFeatures();
            _engine.BindProfile(_profile);

            // ---------- 页面 ----------
            // 功能开关放在第一页：这是玩家 90% 时间会停留的地方
            _featuresPage = new FeaturesPage(_engine, _settings, _profile);
            _featuresPage.ToggleRequested += OnToggleRequested;
            AddPage("功能开关", _featuresPage);

            _homePage = new HomePage(_attach, _memory, _profile);
            AddPage("环境信息", _homePage);

            // 「高级功能」从占位页换成真实页面：第一阶段只提供**只读**的全局对象扫描，
            // 用来验证"全对象数组"到底找没找对。秒杀/建造/视野三个开关要等这一步确认后开放 ——
            // 数组地址是错的却放出可点开关，后果是往随机内存写值。
            _advancedPage = new AdvancedPage(_target, _profile, _profileStore);
            AddPage("高级功能", _advancedPage);

            AddPage("联机模式", new PlaceholderPage(
                "联机模式", "S7",
                "三种运行模式与同步风险分级",
                new string[]
                {
                    "单人模式：全部功能开放",
                    "局域网友好模式：默认只开放低风险项",
                    "全员同步模式（实验性）：房主统一广播开关与数值",
                    "注意：公网 CnCNet 联机禁止使用本工具"
                }));

            _calibratePage = new CalibratePage(_attach, _memory, _profileStore, _profile);
            _calibratePage.CalibrationCompleted += OnCalibrationCompleted;
            AddPage("校准向导", _calibratePage);

            // 单位相关字段（选中列表 / 血量偏移）不确定性远高于金钱电力，单独一页，
            // 失败也不会污染已经验证通过的向导流程
            _unitPage = new UnitPage(_attach, _memory, _profileStore, _profile);
            _unitPage.CalibrationChanged += delegate { OnCalibrationCompleted(); };
            AddPage("单位校准", _unitPage);

            AddPage("关于", new PlaceholderPage(
                "关于", "—",
                "版本与使用须知",
                new string[]
                {
                    "MO Trainer v0.1 开发版 · 目标游戏：Mental Omega 3.3.6",
                    "适用场景：单人战役 / 遭遇战打 AI / 自建局域网对局",
                    "禁止用于公网 CnCNet 对战",
                    "本工具只读写游戏内存，不修改任何游戏文件"
                }));

            // `--smoke`：发布前"真启动一次"用的探针，启动后自己退出。
            //
            // 为什么必须有：冒烟测试要启动 exe 才能验证"打得开"，但修改器是
            // `requireAdministrator`，构建脚本的 Stop-Process 会被拒绝 ——
            // 结果是**我的测试在用户桌面上留下一个关不掉的窗口**（用户当场质疑
            // "我没有开修改器啊"）。让它自己退出，测试就不会污染用户环境。
            if (Array.IndexOf(Environment.GetCommandLineArgs(), "--smoke") >= 0)
            {
                Timer smoke = new Timer();
                smoke.Interval = 4000;
                smoke.Tick += delegate
                {
                    smoke.Stop();
                    ForceLabelClamp();   // 借退出前这一刻，把当前页面的真实尺寸记进日志

                    // `--autofind`：自动跑一遍「查找总名单 + 扫描」，结果落进日志后自己退出。
                    // 这套流程需要"游戏里保持选中一个单位"的人工前置，
                    // 让程序自己跑完，用户只要把游戏开着 —— 省掉每轮的点按钮 + 导出。
                    if (Array.IndexOf(Environment.GetCommandLineArgs(), "--autofind") >= 0
                        && _advancedPage != null)
                    {
                        try { _advancedPage.RunDiagnosticsForTest(); }
                        catch (Exception ex) { Logger.Info("Advanced", "自动测试出错：" + ex.Message); }
                    }

                    Close();
                };
                smoke.Start();
            }

            SelectPage(0);

            // ---------- 事件与定时器 ----------
            _logView.Subscribe();
            _attach.StatusChanged += OnAttachStatusChanged;

            _uiTimer = new Timer();
            _uiTimer.Interval = 1000;
            _uiTimer.Tick += OnUiTimerTick;
            _uiTimer.Start();

            // 热键不在这里注册：此刻窗口句柄还没创建，RegisterHotKey 会直接失败。
            // 统一挪到 OnHandleCreated，那里才是句柄真正就绪的时机。
            _engine.Start();

            Logger.Info("MainForm", "界面已加载，窗口 " + ClientSize.Width + "x" + ClientSize.Height);
        }

        // ============================================================ 功能登记

        /// <summary>
        /// 登记全部功能项。这里同时定下每项的**同步风险等级与理由**，
        /// 界面与热键都读同一份数据，避免出现"界面标红、热键却放行"这种不一致。
        /// </summary>
        private void RegisterFeatures()
        {
            CheatFeature money = _engine.Add(new CheatFeature(CheatId.Money, "金钱",
                "把余额持续锁定在指定数值，花掉后会立刻补满。", SynchRisk.Medium, true));
            money.MinValue = 0;
            money.MaxValue = 999999999;
            money.TargetValue = _settings.GetValue(CheatId.Money, 100000);
            money.Enabled = _settings.GetEnabled(CheatId.Money);
            money.Detail =
                "金钱本身不一定参与同步校验，但它是「中」风险而不是「低」：\n"
                + "你改高余额后发出的建造指令，会和其它客户端的资金状态对不上，\n"
                + "属于延迟性不一致 —— 具体会不会掉线，需要实测才敢下结论。\n"
                + "单人战役 / 遭遇战打 AI：完全可靠。";

            CheatFeature power = _engine.Add(new CheatFeature(CheatId.PowerOutput, "电力",
                "把电力产出持续锁定在指定数值，电力永远够用。", SynchRisk.Low, true));
            power.MinValue = 0;
            power.MaxValue = 100000;
            power.TargetValue = _settings.GetValue(CheatId.PowerOutput, 30000);
            power.Enabled = _settings.GetEnabled(CheatId.PowerOutput);
            power.Detail =
                "电力主要影响本机的界面显示与建造可用性判定，\n"
                + "一般认为它不直接进入同步校验和，因此列为低风险。\n"
                + "注意：该字段的「写入是否被游戏接受」尚未在真机验证过，\n"
                + "开启后请留意电力条是否真的不掉了。";

            CheatFeature invincible = _engine.Add(new CheatFeature(CheatId.SelectedInvincible, "选中单位无敌",
                "框选单位后开启，让选中的单位血量持续保持满血。", SynchRisk.High, true));
            invincible.MinValue = 1;
            invincible.MaxValue = 100000000;
            invincible.Detail =
                "单位血量是参与同步校验的游戏状态。\n"
                + "单方面让己方单位不掉血，其它客户端的计算结果必然与你不同，\n"
                + "很快就会出现 out of sync 掉线。\n"
                + "因此本功能仅适合单人 / 打 AI 使用。\n\n"
                + "「锁定为」的默认值是校准测出的满血值，一般不用改。";

            _settings.SetValue(CheatId.Money, money.TargetValue);
            _settings.SetValue(CheatId.PowerOutput, power.TargetValue);
        }

        // ============================================================ 热键

        private void RegisterHotkeys()
        {
            RegisterHotkey(HotkeyMoney, Keys.D1, "1");
            RegisterHotkey(HotkeyPower, Keys.D2, "2");
            RegisterHotkey(HotkeyInvincible, Keys.D3, "3");
        }

        private void RegisterHotkey(int id, Keys key, string label)
        {
            if (!IsHandleCreated)
            {
                Logger.Warn("MainForm", "窗口句柄尚未创建，跳过热键 Ctrl+Alt+" + label + " 的注册");
                return;
            }

            // MOD_NOREPEAT：按住不放时只触发一次，否则会疯狂开关
            uint modifiers = Win32Api.MOD_CONTROL | Win32Api.MOD_ALT | Win32Api.MOD_NOREPEAT;
            if (Win32Api.RegisterHotKey(Handle, id, modifiers, (uint)key))
            {
                _registeredHotkeys.Add(id);
                Logger.Info("MainForm", "热键已注册：Ctrl+Alt+" + label);
            }
            else
            {
                Logger.Warn("MainForm", "热键 Ctrl+Alt+" + label + " 注册失败（可能被其它程序占用）："
                    + Win32Api.LastErrorMessage());
            }
        }

        private void UnregisterHotkeys()
        {
            for (int i = 0; i < _registeredHotkeys.Count; i++)
            {
                try { Win32Api.UnregisterHotKey(Handle, _registeredHotkeys[i]); }
                catch (Exception) { }
            }
            _registeredHotkeys.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Win32Api.WM_HOTKEY)
            {
                OnHotkey(m.WParam.ToInt32());
            }
            base.WndProc(ref m);
        }

        private void OnHotkey(int id)
        {
            CheatId target;
            switch (id)
            {
                case HotkeyMoney: target = CheatId.Money; break;
                case HotkeyPower: target = CheatId.PowerOutput; break;
                case HotkeyInvincible: target = CheatId.SelectedInvincible; break;
                default: return;
            }

            CheatFeature feature = _engine.Find(target);
            if (feature == null) { return; }
            SetFeatureEnabled(target, !feature.Enabled, "热键");
        }

        // ============================================================ 开关裁决

        private void OnToggleRequested(CheatId id, bool wanted)
        {
            SetFeatureEnabled(id, wanted, "界面");
        }

        /// <summary>
        /// 切换功能开关的唯一入口（界面与热键都走这里）。
        /// 集中处理"未校准必须拒绝"和"高风险必须二次确认"两条约束，
        /// 这样即便以后新增入口也不会漏掉安全规则。
        /// </summary>
        public bool SetFeatureEnabled(CheatId id, bool wanted, string source)
        {
            CheatFeature feature = _engine.Find(id);
            if (feature == null) { return false; }

            if (wanted && !feature.IsCalibrated)
            {
                Logger.Warn("MainForm", feature.Name + " 尚未校准，拒绝" + source + "开启请求");
                _featuresPage.RefreshAll();      // 把界面开关弹回原位
                return false;
            }

            // 高风险功能的"知情确认"改成**卡片上的勾选框**，不再弹窗。
            //
            // 为什么：热键开启是"人还在游戏里、没看界面"时发生的动作，
            // 每次都要切屏去点弹窗非常别扭（用户反馈）。而弹窗本身就是"点一下就算读过"，
            // 拦不住任何人 —— 换成卡片下方常驻的勾选框，语义更清楚，也不打断操作。
            //
            // 判据：来自界面 = 卡片上的勾选框已经拦过一次，这里直接放行并记下已同意；
            //       来自热键 = 必须已经在界面上勾过，否则拒绝（并提示去哪里勾）。
            if (wanted && feature.Risk == SynchRisk.High && !FeatureCard.IsAgreed(id))
            {
                Logger.Warn("MainForm", feature.Name + " 尚未勾选知情确认，" + source + "请求被拒绝");
                _featuresPage.RefreshAll();
                return false;
            }

            if (feature.Enabled == wanted)
            {
                _featuresPage.RefreshAll();
                return true;
            }

            feature.Enabled = wanted;
            feature.WriteCount = 0;              // 重新计数，界面上的"已写入次数"才有意义

            Logger.Info("MainForm", string.Format(CultureInfo.InvariantCulture,
                "{0} 经{1}切换为 {2}", feature.Name, source, wanted ? "开启" : "关闭"));

            _settings.SetEnabled(id, wanted);
            _settings.Save(_settings.PathIn(_profileStore.ConfigFolder));

            _featuresPage.RefreshAll();
            return true;
        }

        private bool ConfirmHighRisk(CheatFeature feature)
        {
            string text = feature.Name + " 属于联机高风险功能。\n\n"
                + "它会改动参与同步校验的游戏状态。只要在局域网联机时开启，"
                + "其它客户端算出的结果就会和你不一致，很快出现 out of sync 掉线。\n\n"
                + "单人战役 / 遭遇战打 AI 可以放心使用。\n\n"
                + "确定要开启吗？";

            return MessageBox.Show(this, text, "高风险功能确认",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }

        /// <summary>校准配置读写入口，供校准向导（S3）使用。</summary>
        public ProfileStore ProfileStore { get { return _profileStore; } }

        /// <summary>当前生效的校准配置。</summary>
        public CalibrationProfile Profile { get { return _profile; } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.EnableDarkTitleBar(this);

            // 句柄就绪后才能注册全局热键。
            // 曾经把它放在构造函数里，那时 IsHandleCreated 还是 false，
            // 热键被静默跳过，表现就是"按了没反应、日志里也查不到原因"。
            if (_registeredHotkeys.Count == 0) { RegisterHotkeys(); }
        }

        private void AddPage(string navText, Control page)
        {
            int index = _navButtons.Count;

            NavButton button = new NavButton();
            button.Text = navText;
            button.Tag = index;
            // 绝对定位：导航项自上而下按加入顺序排列，不依赖停靠 z 序规则
            button.Location = new Point(0, NavTopOffset + index * NavItemHeight);
            button.Size = new Size(Theme.NavWidth, NavItemHeight);
            button.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            button.Click += OnNavClick;
            _navHost.Controls.Add(button);

            page.Dock = DockStyle.Fill;
            page.Visible = false;
            _contentHost.Controls.Add(page);

            _navButtons.Add(button);
            _pages.Add(page);
        }

        private void OnNavClick(object sender, EventArgs e)
        {
            NavButton button = sender as NavButton;
            if (button == null) { return; }
            int index = (int)button.Tag;
            SelectPage(index);
        }

        private void SelectPage(int index)
        {
            if (index < 0 || index >= _pages.Count) { return; }
            for (int i = 0; i < _pages.Count; i++)
            {
                bool active = i == index;
                _navButtons[i].Selected = active;
                _pages[i].Visible = active;
            }

            // 切页后重新钳一次标签宽度：新页面此前不可见，尺寸没结算过。
            ForceLabelClamp();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyLabelClamp();
        }

        private void ApplyLabelClamp()
        {
            if (_clampedWidth == ClientSize.Width) { return; }
            _clampedWidth = ClientSize.Width;
            UiFactory.ClampLabelWidths(this);
            LogLayoutDiagnostics();
        }

        /// <summary>
        /// 把关键控件的**实际几何尺寸**写进日志。
        ///
        /// 为什么要这样：界面被切这类问题，看截图只能得到"大概切了多少"，
        /// 我据此反复猜（连续两轮都猜错），用户也得反复截图。
        /// 把数字直接落到日志里 —— 谁超出了可用宽度、差多少像素，一眼可算。
        /// 用户只要点「导出日志」，不必再截图。
        /// </summary>
        private void LogLayoutDiagnostics()
        {
            // 构造函数里 `ClientSize = ...` 就会触发 OnResize，而那时日志区控件还没建出来。
            // 少了这道判断，取字段就是空引用 —— 用户实测现象正是**启动即报"未将对象引用
            // 设置到对象的实例"、根本打不开**。布局自检属于诊断功能，宁可不报也不能让程序起不来。
            if (_logHeader == null || _logTitle == null || _contentHost == null
                || _openLogDirButton == null || _exportLogButton == null)
            {
                return;
            }

            Control page = null;
            for (int i = 0; i < _pages.Count; i++)
            {
                if (_pages[i].Visible) { page = _pages[i]; break; }
            }

            string pageInfo = "无";
            if (page != null)
            {
                // 只有可滚动容器才有滚动条状态：页面都是 Panel（继承 ScrollableControl）。
                // 这一项直接回答"页面是不是还被撑出横向滚动条"——布局问题最关键的那个判据。
                ScrollableControl scrollable = page as ScrollableControl;
                pageInfo = string.Format(CultureInfo.InvariantCulture,
                    "{0} {1}x{2} 横条={3} 竖条={4}",
                    page.GetType().Name, page.Width, page.Height,
                    scrollable != null && scrollable.HorizontalScroll.Visible ? "有" : "无",
                    scrollable != null && scrollable.VerticalScroll.Visible ? "有" : "无");
            }

            Logger.Info("Layout", string.Format(CultureInfo.InvariantCulture,
                "窗口宽{0}｜内容区{1}｜日志头{2}｜日志标题宽{3}｜打开目录[{4}+{5}]｜导出日志[{6}+{7}]｜{8}",
                ClientSize.Width, _contentHost.Width, _logHeader.Width, _logTitle.Width,
                _openLogDirButton.Left, _openLogDirButton.Width,
                _exportLogButton.Left, _exportLogButton.Width, pageInfo));
        }

        /// <summary>强制重算一次（切页时用：宽度没变，但新页面的标签还没被钳过）。</summary>
        private void ForceLabelClamp()
        {
            _clampedWidth = -1;
            ApplyLabelClamp();
        }

        /// <summary>上次钳制标签宽度时用的窗口宽度，用来避免每次重排都无谓地走一遍控件树。</summary>
        private int _clampedWidth = -1;

        private void OnAttachStatusChanged()
        {
            try
            {
                if (IsHandleCreated && InvokeRequired)
                {
                    BeginInvoke(new Action(RefreshStatus));
                }
                else
                {
                    RefreshStatus();
                }
            }
            catch (Exception)
            {
                // 窗体销毁中，忽略
            }
        }

        private void OnUiTimerTick(object sender, EventArgs e)
        {
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            AttachState state = _attach.State;
            string stateText;
            switch (state)
            {
                case AttachState.Attached: stateText = "已连接"; break;
                case AttachState.Error: stateText = "连接失败"; break;
                default: stateText = "未连接"; break;
            }

            string baseText = state == AttachState.Attached
                ? string.Format(CultureInfo.InvariantCulture, "基址 0x{0:X8}", _attach.ModuleBase)
                : "基址 —";

            string calibrationText = _profile.IsCalibratedAtAll
                ? string.Format(CultureInfo.InvariantCulture, "校准 {0}/5 项", _profile.CalibratedItemCount())
                : "校准 未校准";

            _statusStrip.UpdateStatus(
                stateText, Theme.StateColor(state), baseText, calibrationText, "模式 单人");

            _homePage.TickRefresh();
            _featuresPage.TickRefresh();
            // 必须挂上：这一页的按钮可用状态取决于"游戏是否已连接"，而附加是异步完成的。
            // 漏掉它会让整页按钮在首次打开时全是灰的（真机踩过）。
            _unitPage.TickRefresh();
            // 高级功能页同理：漏掉它会让那一页的按钮在"先开修改器、后开游戏"时永远是灰的。
            _advancedPage.TickRefresh();
        }

        /// <summary>校准完成：刷新状态条，并把新的定位信息接到功能项上。</summary>
        private void OnCalibrationCompleted()
        {
            Logger.Info("MainForm", "校准完成，当前配置：" + _profile.Describe());

            // 重新绑定：刚校准出来的字段要立刻变成"可开启"，无需重启程序
            _featuresPage.Rebind();

            RefreshStatus();
            _homePage.RefreshCalibration();
        }

        /// <summary>
        /// 导出运行日志到 reports\log-*.txt。
        ///
        /// 日志面板只显示得下十几行，真机排查时用户只能截图，而截图必然截不全 ——
        /// 前几轮反馈里好几次关键数字（"找到 2 个槽位""归属校验 1/2"）都因此看不全。
        /// 导出后整段发过来即可。
        /// </summary>
        private void OnExportLogClick(object sender, EventArgs e)
        {
            try
            {
                string path = LogExporter.Export(_logView.Snapshot(), "运行日志面板");
                Logger.Info("MainForm", string.Format(CultureInfo.InvariantCulture,
                    "日志已导出，共 {0} 行：{1}", _logView.Snapshot().Count, path));

                try
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                catch (Exception)
                {
                    // 打不开资源管理器不影响导出本身，路径已经写进日志面板了
                }
            }
            catch (Exception ex)
            {
                Logger.Error("MainForm", "导出日志失败：" + ex.Message);
                MessageBox.Show(this, "导出日志失败：" + ex.Message, "MO Trainer",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnOpenLogDirectoryClick(object sender, EventArgs e)
        {
            try
            {
                string directory = Logger.LogDirectory;
                if (string.IsNullOrEmpty(directory)) { directory = AppDomain.CurrentDomain.BaseDirectory; }
                System.Diagnostics.Process.Start("explorer.exe", "\"" + directory + "\"");
            }
            catch (Exception ex)
            {
                Logger.Warn("MainForm", "打开日志目录失败：" + ex.Message);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // 先把功能全关掉再退出：留一个还在写内存的线程不合规矩
            // （虽然进程退出会连带结束线程，但显式收尾才能保证日志完整）
            StopAllFeatures();

            _settings.SetEnabled(CheatId.Money, _engine.Find(CheatId.Money) != null
                && _engine.Find(CheatId.Money).Enabled);
            _settings.Save(_settings.PathIn(_profileStore.ConfigFolder));

            _uiTimer.Stop();
            UnregisterHotkeys();
            _engine.Dispose();

            _attach.StatusChanged -= OnAttachStatusChanged;
            _logView.Unsubscribe();
            Logger.Info("MainForm", "界面关闭");
            base.OnFormClosing(e);
        }

        private void StopAllFeatures()
        {
            IList<CheatFeature> features = _engine.Features;
            for (int i = 0; i < features.Count; i++)
            {
                if (!features[i].Enabled) { continue; }
                features[i].Enabled = false;
                Logger.Info("MainForm", "退出前关闭功能：" + features[i].Name);
            }
        }
    }
}
