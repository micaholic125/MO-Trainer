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

        /// <summary>"你选中的是哪些单位"的可靠读法（金钱锚定 + 自动更正）——
        /// 与引擎 / 采集器共用同一套实现，见 `SelectionResolver` 的类注释。</summary>
        private readonly SelectionResolver _selection;

        /// <summary>
        /// 本次会话里最后一次**成功建立**的我方锚点（阵营字段偏移 + 我方 House 地址）。
        ///
        /// 为什么需要它（2026-10-08 晚）：**心灵终结里中立单位无法和友军单位同时选中**
        /// （用户实测反馈）——"选中立建筑单独看一眼会不会被秒杀"时，选中的东西里
        /// 一个本方单位都没有，锚点**当场建不出来**。此时沿用这份旧锚点，
        /// 但必须先用 `TechnoOwnership.TryVerifyCachedAnchor` 核对"它还指着同一个房子"，
        /// 再用"名单里还有没有我方对象"复核一次（见 `OnKillPreviewClick`）。
        /// </summary>
        private uint _cachedOwnerOffset;
        private uint _cachedOwnerHouse;

        /// <summary>
        /// 游戏主模块的地址区间。用来判断"指针指向的是不是游戏对象"。
        ///
        /// **必须每次现取，绝不能在构造时缓存成字段。** 真机教训（2026-10-08）：
        /// 本页在**修改器启动时**就构造好了，而玩家常常"先开修改器、后开游戏"——
        /// 那一刻还没附加，基址是 0；缓存下来的 0 会让本页所有"模块基址 + 偏移"的解析
        /// 全部算错，界面上的表现却是"读不到金钱值"这类**看起来像数据问题**的报错，
        /// 极难归因（当天"预演"按钮就是这样失败的）。`_target` 每次读到的都是当前值。
        /// </summary>
        private uint LiveModuleBase { get { return _target == null ? 0 : _target.ModuleBase; } }

        private uint LiveModuleSize { get { return _target == null ? 0 : _target.ModuleSize; } }

        /// <summary>页面留白与按钮间距（位置计算用；不是控件的绝对坐标）。</summary>
        private const int PagePadding = 24;
        private const int ButtonGap = 10;
        private const int ButtonRowGap = 8;

        private readonly Label _heading;
        private readonly Label _subtitle;
        private readonly Label _hintTop;
        private readonly Button _scanButton;
        private readonly Button _findButton;
        private readonly Button _previewButton;
        private readonly Button _markAllyButton;
        private readonly Button _shroudButton;
        private readonly Button _harvestButton;
        private readonly Label _harvestStatus;
        private readonly Timer _harvestTimer;
        private HarvestSession _harvest;
        private readonly TextBox _output;

        public AdvancedPage(ICalibrationTarget target, CalibrationProfile profile, ProfileStore store)
        {
            _target = target;
            _profile = profile == null ? new CalibrationProfile() : profile;
            _store = store;
            _selection = target == null ? null : new SelectionResolver(target);

            // 把模块区间交给「单位校准」的探测逻辑，让它多一条硬判定：
            // 名单里每一项的头 4 字节必须是模块内的类型标记 —— 这条能把
            // "游戏对象的方法表冒充名单""模块里的代码地址冒充对象"当场拒掉（都真机踩过）。
            //
            // 但**这里只是初值**：本页在修改器启动时构造，那一刻游戏往往还没连上（基址为 0）。
            // 真正保证它一直有效的是 `MainForm.TickRefresh` 每轮重灌一次，
            // 以及各调用点（`HarvestSession` / `UnitPage`）在探测前自带一份。
            SelectionProbe.SetModuleRange(LiveModuleBase, LiveModuleSize);

            BackColor = Theme.Bg0;
            AutoScroll = true;

            // ---- 头部：位置一律由 `RelayoutAll()` 按**实测宽度**排（不写死坐标）----
            //
            // 为什么改成这样（2026-10-08 真机截图）：此前这里全是写死的坐标
            //（按钮 x=24/184/344/556、提示 y=130、采集 y=156）。用户显示器有 DPI 缩放，
            // 字号放大 → 按钮跟着变宽 → 新加的「标记友军」压住提示文字、「开始采集」与提示叠在一起。
            // 这正是 `CLAUDE.md` 7.2 记的那一类坑（**像素不跟着缩放走**），治法是"位置按实测尺寸算"。
            _heading = new Label();
            _heading.AutoSize = false;
            _heading.Font = Theme.Heading;
            _heading.ForeColor = Theme.Text0;
            _heading.Text = "高级功能";
            Controls.Add(_heading);

            _subtitle = MakeHintLabel();
            _subtitle.Font = Theme.Body;
            // 副标题刻意短：页面高度只有 484 像素（窗口 660 高时），文案一长就把内容顶出竖向滚动条，
            // 而竖向条会连锁引出横向条（见 `RelayoutAll` 的注释）。
            _subtitle.Text = "秒杀 / 瞬间建造 / 全图视野 —— 本页只读不写。";
            Controls.Add(_subtitle);

            // 第一组：名单与秒杀（四个按钮挨着放，提示文字单独占一行在它们下面）
            _scanButton = UiFactory.MakePrimaryButton("扫描全对象数组", 150);
            _scanButton.Click += OnScanClick;
            Controls.Add(_scanButton);

            _findButton = UiFactory.MakePrimaryButton("自动查找总名单", 150);
            _findButton.Click += OnFindClick;
            Controls.Add(_findButton);

            // 预演按钮：**只计算、不写入**。
            // 秒杀敌人是所有功能里唯一可能"误伤自家部队"的一项，
            // 所以先给用户一个"会杀谁、不杀谁"的清单，确认无误后再放开真正的执行。
            _previewButton = UiFactory.MakeButton("预演：秒杀敌人（不写入）", 200);
            _previewButton.Click += OnKillPreviewClick;
            Controls.Add(_previewButton);

            // 「标记友军」：把选中的东西所属的阵营记入"永不秒杀"名单。
            // 为什么必须由用户点一下：秒杀只认"不是你 + 阵营有钱"，
            // **不知道"同盟"** —— AI 队友 / 局域网人类队友恰好符合"不是你 + 有钱"，
            // 会被一起清掉，而且不可逆（2026-10-08 用户提问时发现，见 `AllyGuard` 的注释）。
            _markAllyButton = UiFactory.MakePrimaryButton("标记友军（不会杀）", 180);
            _markAllyButton.Click += OnMarkAllyClick;
            Controls.Add(_markAllyButton);

            _hintTop = MakeHintLabel();
            _hintTop.Text = "用法：先在游戏里选中一个自己的单位，再点「自动查找总名单」；然后点「预演」看它准备杀谁、放过谁。\n"
                + "有队友的局：点选队友的一个单位（或建筑）→ 点「标记友军」，该阵营就永远不会被秒杀。";
            Controls.Add(_hintTop);

            // 第二组：全图视野 + 一键采集。
            //
            // 「开始采集」存在的意义：本项目的瓶颈是"每要一个数据就得让用户开一局游戏"。
            // 有了它，用户只操作一次（开局 → 点开始 → 随便玩几分钟 → 点停止），
            // 期间程序自己按秒采样，把后面几轮要用的数据一次收齐。
            // 它是**校准辅助**，不属于面向玩家的产品功能；S8 交付前应连同本页的其它
            // 调试按钮一起评估去留（见 docs/04 的 S8）。
            //
            // 全图视野：一个按钮、两次点击。第 1 次记录"迷雾快照"，用户去游戏里揭开一小块地图，
            // 第 2 次对比并清除。原理：迷雾是一大片连续位图，揭开地图时**只有它那部分字节在变** ——
            // 对比前后就能把它定位出来，再分片清零。
            _shroudButton = UiFactory.MakePrimaryButton("全图视野（点两次）", 168);
            _shroudButton.Click += OnShroudClick;
            Controls.Add(_shroudButton);

            _harvestButton = UiFactory.MakePrimaryButton("开始采集（3 分钟）", 180);
            _harvestButton.Click += OnHarvestClick;
            Controls.Add(_harvestButton);

            _harvestStatus = MakeHintLabel();
            _harvestStatus.Text = "采集期间你只需要在游戏里正常玩：选几个单位、造点东西、让单位挨点打。程序自己记录。";
            Controls.Add(_harvestStatus);

            // 每秒刷新一次状态文字。用界面线程的 Timer，避免后台线程碰控件。
            _harvestTimer = new Timer();
            _harvestTimer.Interval = 1000;
            _harvestTimer.Tick += OnHarvestTick;

            _output = new TextBox();
            _output.Multiline = true;
            _output.ReadOnly = true;
            _output.ScrollBars = ScrollBars.Both;
            _output.WordWrap = false;
            _output.BackColor = Theme.Bg1;
            _output.ForeColor = Theme.Text0;
            _output.Font = Theme.Mono;
            _output.BorderStyle = BorderStyle.FixedSingle;
            _output.Anchor = AnchorStyles.Top | AnchorStyles.Left;   // 位置与大小都由 RelayoutAll 算
            Controls.Add(_output);

            // 两个事件都要挂：`SizeChanged` 管窗口缩放；`ClientSizeChanged` 管**滚动条出现/消失**
            //（竖向滚动条一出现，可用宽就少 ~17 像素，但控件的 Size 没变 —— 只挂前者会漏掉这一步，
            //  横向滚动条就是这么冒出来的，见 `RelayoutAll` 里的注释）。
            SizeChanged += delegate { RelayoutAll(); };
            ClientSizeChanged += delegate { RelayoutAll(); };
            RelayoutAll();
            RefreshUi();
            UpdateHarvestUi();

            // 离线提示只在构造时给一次。放进 RefreshUi 会让它每秒重写输出框，
            // 把用户正在看的内容冲掉。
            if (_target == null || !_target.IsReady)
            {
                SetOutput("游戏未连接。请先启动游戏并让修改器附加成功，再点扫描。");
            }
        }

        /// <summary>页面被销毁时收尾：采集线程仍在跑的话，先把报告落盘再退出，别让用户白等一场。</summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_harvestTimer != null)
                {
                    try { _harvestTimer.Stop(); } catch { }
                    try { _harvestTimer.Dispose(); } catch { }
                }
                if (_harvest != null)
                {
                    try { _harvest.Stop(); } catch { }
                    try { _harvest.Dispose(); } catch { }
                    _harvest = null;
                }
            }
            base.Dispose(disposing);
        }

        /// <summary>
        /// 重排本页头部与输出框 —— **位置按控件的实测宽度算，不写死坐标**。
        ///
        /// 为什么（2026-10-08 真机截图）：此前按钮与提示用的是写死的 x/y。用户显示器有 DPI 缩放，
        /// 字号放大后按钮跟着变宽，于是「标记友军」压住了提示文字、「开始采集」和提示又叠在一起，
        /// 窄窗口下按钮还会互相重叠。全部改由"实测宽度 + 放不下就换行"决定后，
        /// **任何缩放、任何窗口宽度都不会互相压**（这正是 `CLAUDE.md` 7.2 记的那类坑的通用治法）。
        /// 提示文字一律**单独占一行**放在它所属的那排按钮下面，不再与按钮争位置。
        /// </summary>
        private void RelayoutAll()
        {
            int width = ClientSize.Width;
            if (width < 320 || _output == null) { return; }   // 还没成形，等下一次尺寸事件

            int left = PagePadding;
            int rightLimit = width - PagePadding;
            int y = 20;

            SetBoundsIfChanged(_heading, new Point(left, y),
                new Size(rightLimit - left, _heading.Font.Height + 10));
            y += _heading.Height + 6;

            y = LayoutHint(_subtitle, y, rightLimit - left) + 10;

            y = LayoutButtonRow(y, new Control[] { _scanButton, _findButton, _previewButton, _markAllyButton },
                left, rightLimit) + 6;
            y = LayoutHint(_hintTop, y, rightLimit - left) + 14;

            y = LayoutButtonRow(y, new Control[] { _shroudButton, _harvestButton }, left, rightLimit) + 6;
            y = LayoutHint(_harvestStatus, y, rightLimit - left) + 14;

            int boxHeight = ClientSize.Height - y - 20;
            // 空间不够时**不硬撑**：从前写死 120 的下限，内容一高就把页面顶出竖向滚动条，
            // 而竖向条一出现，可用宽就从 820 缩到 ~794 —— 按 820 排好的控件瞬间超出，
            // 于是**横向滚动条也冒出来**（真机 2026-10-08 23:38 报「横条=有」就是这么来的）。
            if (boxHeight < 60) { boxHeight = 60; }
            SetBoundsIfChanged(_output, new Point(left, y), new Size(rightLimit - left, boxHeight));

            // 排完强制结算一次：滚动条的去留由它重新判定 ——
            // 否则"曾经超出过"留下的横向条会一直挂着（上面的用例就是：控件早已装得下，条却还在）。
            PerformLayout();
        }

        /// <summary>一排按钮：从左到右摆，放不下就换行。返回最后一行按钮的底边 y。</summary>
        private int LayoutButtonRow(int y, Control[] items, int left, int rightLimit)
        {
            int x = left;
            int rowHeight = 0;
            for (int i = 0; i < items.Length; i++)
            {
                Control control = items[i];
                if (control == null) { continue; }

                if (x > left && x + control.Width > rightLimit)
                {
                    x = left;
                    y += rowHeight + ButtonRowGap;
                    rowHeight = 0;
                }

                SetBoundsIfChanged(control, new Point(x, y), control.Size);
                x += control.Width + ButtonGap;
                if (control.Height > rowHeight) { rowHeight = control.Height; }
            }
            return y + rowHeight;
        }

        /// <summary>
        /// 一行提示文字：宽度撑满页面可用宽，**高度按实测折行结果**定（不写死 20 像素 ——
        /// 高 DPI 下字变大会只显示上半截，见 `CLAUDE.md` 7.2）。返回控件底边 y。
        /// </summary>
        private int LayoutHint(Label label, int y, int width)
        {
            if (label == null) { return y; }
            if (width < 120) { width = 120; }

            Size measured = TextRenderer.MeasureText(label.Text, label.Font,
                new Size(width, 0), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            int height = measured.Height + 4;

            SetBoundsIfChanged(label, new Point(PagePadding, y), new Size(width, height));
            return y + height;
        }

        /// <summary>只在真的变了时才赋值 —— 否则每秒一次的刷新会把界面刷得发抖。</summary>
        private static void SetBoundsIfChanged(Control control, Point location, Size size)
        {
            if (control == null) { return; }
            if (control.Location != location) { control.Location = location; }
            if (control.Size != size) { control.Size = size; }
        }

        private static Label MakeHintLabel()
        {
            Label label = new Label();
            label.AutoSize = false;     // 固定尺寸：宽高由 RelayoutAll 按实测文字算
            label.Font = Theme.Small;
            label.ForeColor = Theme.Text2;
            label.BackColor = Color.Transparent;
            return label;
        }

        // ============================================================ 一键采集

        private void OnHarvestClick(object sender, EventArgs e)
        {
            if (_harvest != null && _harvest.IsRunning)
            {
                string path = _harvest.Stop();
                _harvestTimer.Stop();
                SetOutput(BuildHarvestDoneText(path));
                UpdateHarvestUi();
                return;
            }

            HarvestSession session = new HarvestSession(_target, _profile, _store);
            string refusal = session.Start(180, 1000);
            if (refusal.Length > 0)
            {
                // 缺什么就说什么，绝不"按钮能点、程序悄悄什么都不做"
                session.Dispose();
                SetOutput(refusal);
                return;
            }

            _harvest = session;
            _harvestTimer.Start();
            SetOutput("采集已开始。\n\n"
                + "现在可以切回游戏，正常玩 3 分钟：\n"
                + "  1. 选中几个自己的单位（最好包含一座发电厂或兵营）\n"
                + "  2. 造点东西，让金钱发生变化\n"
                + "  3. 让某个单位挨点打\n"
                + "  4. 想取消选中就取消，程序会继续跟踪它\n\n"
                + "3 分钟到会自动结束；想提前结束就再点一次这个按钮。");
            UpdateHarvestUi();
        }

        private void OnHarvestTick(object sender, EventArgs e)
        {
            if (_harvest == null) { _harvestTimer.Stop(); return; }

            if (!_harvest.IsRunning)
            {
                // 到时间自动结束（或采样线程意外退出）：把报告落盘，一次都不落下
                _harvestTimer.Stop();
                string path = _harvest.Stop();
                SetOutput(BuildHarvestDoneText(path));
            }
            UpdateHarvestUi();
        }

        private void UpdateHarvestUi()
        {
            bool running = _harvest != null && _harvest.IsRunning;
            _harvestButton.Text = running ? "停止并保存报告" : "开始采集（3 分钟）";

            if (running)
            {
                _harvestStatus.Text = string.Format(CultureInfo.InvariantCulture,
                    "采集中… {0:0} / {1} 秒｜已采 {2} 次｜跟踪 {3} 个对象（现在可以切回游戏了）",
                    _harvest.ElapsedSeconds, _harvest.PlannedSeconds,
                    _harvest.SampleCount, _harvest.TrackedCount);
            }
            else if (_harvest != null && _harvest.ReportPath.Length > 0)
            {
                _harvestStatus.Text = "采集完成，报告已保存。";
            }

            // 按钮文字与提示文字都变过 → 宽度/高度跟着变，位置必须重算
            //（否则「停止并保存报告」比「开始采集（3 分钟）」长，会压到旁边的提示）。
            RelayoutAll();
        }

        private static string BuildHarvestDoneText(string path)
        {
            if (path.Length == 0)
            {
                return "采集已结束，但报告写入失败。请查看日志（点主窗口日志区的「导出日志」）了解原因。";
            }
            return "采集完成，报告已保存到：\n" + path + "\n\n"
                 + "把这个文件发给我即可。报告里已经包含了时间线、房子数值、\n"
                 + "全场对象名单，以及你选中过的每个对象的完整字节变化。";
        }

        private void RefreshUi()
        {
            bool ready = _target != null && _target.IsReady;
            _scanButton.Enabled = ready;
            _findButton.Enabled = ready;
            _harvestButton.Enabled = ready;
            _shroudButton.Enabled = ready;
        }

        /// <summary>
        /// 由主窗口每秒调用一次：按"游戏是否已连接"刷新按钮可用状态。
        ///
        /// **必须挂上。** 附加是异步完成的，如果只在构造时判定一次，
        /// 而用户先开修改器、后开游戏（很常见的顺序），这一页的按钮就会**永远是灰的**。
        /// `UnitPage` 真机踩过同一个坑（见 `MainForm` 里那句注释），本页当时漏了。
        /// </summary>
        public void TickRefresh()
        {
            RefreshUi();
            UpdateHarvestUi();
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
                || !_profile.TechnoArray.TryResolve(memory, LiveModuleBase, out slotAddress, out error))
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
                memory, arrayPointer, 4096, LiveModuleBase, LiveModuleSize);
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

            // 我方样本：你当前选中的单位（金钱锚定确认属于我方）
            string sampleNote;
            List<uint> myObjects = ReadSelectedObjects(out sampleNote);
            if (myObjects.Count == 0)
            {
                text.AppendLine("我方样本读取失败：" + sampleNote);
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
                    if (value >= LiveModuleBase && value < LiveModuleBase + LiveModuleSize) { moduleHits++; }

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
            // 用与查找线索**同一套**读取（金钱锚定 + 自动更正）——
            // 两边读法不同的话，"对不上"到底是谁错都说不清。
            string selectionNote;
            List<uint> selected = ReadSelectedObjects(out selectionNote);
            if (selected.Count == 0)
            {
                text.AppendLine("读不到你选中的单位（" + selectionNote + "）。");
                text.AppendLine("先在游戏里选中一个自己的单位再扫描，");
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

            // 线索 = **金钱锚定确认属于我方**的选中单位（2026-10-08 起）。
            // 以前这里用的是"按槽位直读"的弱校验读法，读出来的可能是垃圾 ——
            // 拿垃圾当线索，"找到的名单"自然也是垃圾（真机三次定位失败都栽在这条链上）。
            string hintsNote;
            List<uint> hints = ReadSelectedObjects(out hintsNote);
            if (hints.Count == 0)
            {
                text.AppendLine("【没有线索】" + hintsNote);
                text.AppendLine("「自动查找」要靠你选中的单位去反查名单，没有线索就不找、也绝不保存。");
                text.AppendLine("请：① 在游戏里选中一个自己的单位（框选 3~5 个更好）；② 确认金钱仍处于已校准状态；然后再点一次。");
                return text.ToString();
            }
            text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "线索单位 {0} 个（金钱锚定确认属于我方），第一个是 0x{1:X8}", hints.Count, hints[0]));

            // 「阵营分布」这条判据必须用**金钱锚定出来的**偏移来算（2026-10-08 晚改）。
            // 以前用的是配置里那个**人工试出的 +0xF0** —— 真机证明那里读出来的不是阵营，
            // 于是判据是在噪声上算的（16:46 那次因此选中了一张只有 60 个对象、玩家单位一个都不在里面的假名单）。
            uint money;
            uint ownerOffset = 0;
            if (_selection != null && _selection.TryReadMoney(_profile, out money))
            {
                string offsetNote;
                ownerOffset = TechnoOwnership.ResolveOwnerOffsetForAnalysis(memory, hints, money, out offsetNote);
                text.AppendLine("阵营字段：" + offsetNote);
            }
            else
            {
                text.AppendLine("阵营字段：读不到金钱值 → 本次不做「阵营分布」分析（这条判据跳过，其余判据照旧）");
            }
            text.AppendLine();

            int rejectedCode;
            List<TechnoArrayCandidate> found = TechnoArrayFinder.Find(
                memory, moduleBase, moduleSize, slotAddress, hints, ownerOffset, out rejectedCode);

            if (rejectedCode > 0)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "（按 2026-10-07 加固规则，另有 {0} 个位置因落在可执行页/只读区被排除——指针格必须位于可写数据段）",
                    rejectedCode));
                text.AppendLine();
            }

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

            // ---- 硬验收（2026-10-08 起）----
            //
            // 真机教训：只要求"含 1 个选中单位"是不够的 —— 三次找到的"名单"都过了那条线，
            // 却全都经不起换局检验（容器里装的其实是别的东西）。
            // 现在两条判据必须**同时**满足，才允许保存：
            //   ① 你选中的单位**全部**出现在名单里；
            //   ② 「阵营」字段要聚集成群（少数几种取值、每种一大群）。
            string acceptReason;
            if (!TechnoArrayFinder.MeetsAcceptance(best, hints.Count, out acceptReason))
            {
                text.AppendLine("**本次不保存** —— 硬验收没过：");
                text.AppendLine("  " + acceptReason);
                text.AppendLine();
                text.AppendLine("请确认：① 游戏里确实选中了你自己的单位（框选 3~5 个更好）；"
                    + "② 处于对局中；③ 金钱仍处于已校准状态。然后再点一次这个按钮。");
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

            MemoryRegion slotRegion = memory.Describe(best.SlotAddress);
            if (slotRegion != null)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "指针格 0x{0:X8} 所在内存：{1}", best.SlotAddress, slotRegion.ToString()));
            }

            text.AppendLine(saved
                ? "已保存到配置 —— 以后不用再找。"
                : "保存失败：" + saveError);
            text.AppendLine();

            if (saved)
            {
                text.AppendLine("**注意（2026-10-07 起的新规矩）**：这次只算「本局可用」，还不算「已固化」。");
                text.AppendLine("该定位必须在**下一次开游戏**仍能读出，才算校准成功——下次开局请留意名单是否照常读出。");
                text.AppendLine();
            }

            text.AppendLine("下一步：点「扫描全对象数组」。若阵营分布显示为「少数几个阵营、各自聚集大量单位」，就说明找对了。");
            return text.ToString();
        }

        /// <summary>
        /// 预演「秒杀敌人」：**只读不改**，列出"会杀哪些、放过哪些"，并做一次安全抽检。
        ///
        /// 这是真正开写之前的最后一道人工关卡。与 2026-10-07 之前最大的不同：
        /// 敌我判据从"人工试出的阵营偏移 + 猜它的取值含义"改成**金钱锚定**
        /// （见 `TechnoOwnership`），而且判完还要**用独立判据复核一遍**（抽检）——
        /// 因为秒杀是唯一可能"把自己人一起清空"的功能，宁可多花几十毫秒。
        /// 判定规则与引擎真正执行时完全相同（同一个锚点、同一个偏移）。
        /// </summary>
        private void OnKillPreviewClick(object sender, EventArgs e)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 预演：秒杀敌人（只读，不写入任何内存）===");
            text.AppendLine("敌我判据：金钱锚定 —— 从你选中的单位里反查「指向我方 House（装着你的金钱）的指针」，");
            text.AppendLine("用它的偏移和取值区分敌我；不依赖任何人工试出来的偏移。");
            text.AppendLine();

            MemoryIO memory = _target.Memory;
            string error;

            // ---- 名单 ----
            uint slotAddress;
            if (!_profile.TechnoArray.IsCalibrated
                || !_profile.TechnoArray.TryResolve(memory, LiveModuleBase, out slotAddress, out error))
            {
                text.AppendLine("【缺前置】还没找到对象名单。请先点「自动查找总名单」。");
                SetOutput(text.ToString());
                return;
            }

            uint arrayPointer;
            if (!ReadUInt32Retry(memory, slotAddress, out arrayPointer) || arrayPointer < 0x10000)
            {
                text.AppendLine("【缺前置】名单格子里读不到有效指针（跨会话失效？请重新「自动查找总名单」）。");
                SetOutput(text.ToString());
                return;
            }

            // ---- 金钱（锚点靠它判定）----
            uint moneyAddress;
            uint money = 0;
            if (!_profile.Money.IsCalibrated
                || !_profile.Money.TryResolve(memory, LiveModuleBase, out moneyAddress, out error)
                || !ReadUInt32Retry(memory, moneyAddress, out money) || money == 0)
            {
                text.AppendLine("【缺前置】读不到金钱值 —— 敌我锚点靠它判定（请确认金钱仍处于已校准状态）。");
                SetOutput(text.ToString());
                return;
            }

            // ---- 锚点 ----
            // 优先：用"你选中的本方单位"**现建**（最硬的判据）。
            // 退路：这次选中的东西里没有本方单位 —— 典型情形是**单独点了一座中立建筑**
            //       （心灵终结里中立单位不能和友军单位一起选中，用户 2026-10-08 实测反馈），
            //       此时沿用本次会话此前建立的锚点，前提是它仍指着同一个房子（换局即作废）。
            string sampleNote;
            List<uint> myObjects = TryReadMyObjectsQuiet(out sampleNote);

            TechnoOwnership.OwnerAnchor anchor = null;
            string why = "";
            bool built = myObjects.Count > 0
                && TechnoOwnership.TryBuildAnchor(memory, myObjects, money, out anchor, out why);

            if (built)
            {
                _cachedOwnerOffset = anchor.OwnerOffset;
                _cachedOwnerHouse = anchor.MyHouse;
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "我方锚点：阵营字段 +0x{0:X} = 0x{1:X8}（由 {2} 个选中单位金钱锚定）",
                    anchor.OwnerOffset, anchor.MyHouse, anchor.Samples));
            }
            else
            {
                string reuseWhy;
                bool reused = TryReuseAnchor(memory, moneyAddress, out anchor, out reuseWhy);
                if (!reused)
                {
                    text.AppendLine("【无法判定敌我】" + (myObjects.Count > 0 ? why : sampleNote));
                    text.AppendLine("（沿用旧锚点也不行：" + reuseWhy + "）");
                    text.AppendLine();
                    text.AppendLine("程序靠「你自己选中的东西」确认「我方是谁」。若这次选的是中立建筑这类");
                    text.AppendLine("不属于你的东西，请**先选中你自己的一个单位或建筑**点一次本按钮，再选中它点一次。");
                    text.AppendLine();
                    text.AppendLine("本次**没有写入任何内存**。");
                    SetOutput(text.ToString());
                    return;
                }

                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "我方锚点：阵营字段 +0x{0:X} = 0x{1:X8}（**沿用本次会话此前建立的锚点** —— {2}）",
                    anchor.OwnerOffset, anchor.MyHouse,
                    myObjects.Count > 0
                        ? "这次选中里没有可用的我方样本，没法现建"
                        : "这次选中的东西里没有你自己的单位，没法现建"));
            }
            text.AppendLine();

            // ---- 遍历全场 ----
            uint count = 0;
            bool countKnown = false;
            if (_profile.TechnoArrayCount.IsCalibrated)
            {
                uint countAddress;
                if (_profile.TechnoArrayCount.TryResolve(memory, LiveModuleBase, out countAddress, out error)
                    && memory.ReadUInt32(countAddress, out count) && count >= 64)
                {
                    countKnown = true;
                }
            }
            if (!countKnown || count > TechnoWalker.MaxObjects) { count = 4096; }

            TechnoWalkResult walk = TechnoWalker.WalkArray(
                memory, arrayPointer, count, LiveModuleBase, LiveModuleSize);
            if (!walk.Ok)
            {
                text.AppendLine("遍历失败：" + walk.Error);
                SetOutput(text.ToString());
                return;
            }

            // ---- 分类（与引擎共用同一份实现：预演看到的 = 真正开杀时用的）----
            TechnoOwnership.OwnerClassification cls = TechnoOwnership.ClassifyOwners(
                memory, walk.Objects, anchor.OwnerOffset, anchor.MyHouse, LiveModuleBase, LiveModuleSize);

            // **名单自检**：你自己的部队永远在场上 —— 整份名单里一个"我方"对象都没有，
            // 说明这份「全对象名单」不可信（真机 2026-10-08 16:46 抓到：名单里只有 60 个对象，
            // 玩家的 6 个单位一个都不在里面，而四个数字照样算得出来）。
            // 这时**照常给出「你选中的对象逐个判定」**（那一节只看选中列表与阵营字段，不依赖名单），
            // 但不给"将击杀/抽检"这些**以名单为准**的结论。
            bool rosterSuspect = cls.Mine == 0;

            if (rosterSuspect)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "⚠ **名单自检未通过**：遍历到 {0} 个对象，其中**没有一个**指向你方 House ——", walk.Count));
                text.AppendLine("你自己的部队本该在里面。这份「全对象名单」不可信，**秒杀不要开启**，");
                text.AppendLine("请回上一步重新点「自动查找总名单」。");
                text.AppendLine("（下方「你选中的对象逐个判定」不依赖这份名单，仍然有效。）");
                text.AppendLine();
            }

            // ---- 你选中的对象逐个判定（2026-10-08 新增）----
            // 用途：让你一眼看清"自己选中的东西被判成了哪边"——
            // 单独点一座中立建筑再点本按钮，就能看出它会不会被杀。
            // 放在最前面：**它不依赖名单**，名单不可信时它是唯一还有效的结论。
            AppendSelectionVerdicts(text, memory, anchor, walk.Objects, rosterSuspect);

            if (!rosterSuspect)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "遍历到 {0} 个对象", walk.Count));
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "**将击杀：{0} 个**｜**放过我方：{1} 个**｜**疑似中立（不杀）：{2} 个**"
                    + "｜**友军不杀：{3} 个**｜归属存疑（不杀）：{4} 个",
                    cls.KillableCount, cls.Mine, cls.NeutralCount, cls.Ally, cls.Unknown));

                if (cls.SingletonSkipped > 0)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  （存疑里有 {0} 个是「全场只有一个对象的阵营」—— 真机实测那类都是垃圾归属，"
                        + "一并跳过不杀）", cls.SingletonSkipped));
                }
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "血量偏移 +0x{0:X}（写入时每个目标都会先校验、写后回读）",
                    _profile.OffsetTechnoHealth));
                text.AppendLine();

                // ---- 将击杀：按阵营拆分（附每个阵营的类型构成）----
                AppendOwnerGroups(text, memory, "将击杀（按阵营拆分）", cls.KillableByGroup, anchor.OwnerOffset);

                if (cls.NeutralCount > 0)
                {
                    AppendOwnerGroups(text, memory,
                        "疑似中立 / 民用（**不会杀**）", cls.NeutralByGroup, anchor.OwnerOffset);
                    text.AppendLine("  说明：这些阵营的「金钱」读数一直是 0 —— 中立 / 民用建筑通常如此，所以默认保护。");
                    text.AppendLine();
                }

                if (cls.Ally > 0)
                {
                    AppendOwnerGroups(text, memory,
                        "友军（**你标记过的，不会杀**）", cls.AllyByGroup, anchor.OwnerOffset);
                    text.AppendLine("  说明：这些阵营是你在「标记友军」里点过的 —— 队友（AI 或人类）靠它免于误伤。");
                    text.AppendLine();
                }

                // ---- 安全抽检：把"判为敌方"的对象拿独立判据复核（与真正要写的名单一致）----
                int enemySeen;
                int checkedCount;
                uint suspect;
                string verifyWhy;
                bool verified = TechnoOwnership.VerifyEnemySample(
                    memory, cls.Killable, anchor.OwnerOffset, anchor.MyHouse, 256,
                    out enemySeen, out checkedCount, out suspect, out verifyWhy);

                if (verified)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "安全抽检：复核 {0} 个判为敌方的对象，**没有一个连着你方 House** —— 通过。",
                        checkedCount));
                }
                else
                {
                    text.AppendLine("安全抽检：**未通过！** " + verifyWhy);
                    text.AppendLine("→ 敌我分类不可靠，**绝不能开启秒杀**。请把这份结果发给开发者。");
                }
            }

            text.AppendLine();
            text.AppendLine(rosterSuspect
                ? "本次**没有写入任何内存**。名单未通过自检，**秒杀不要开启**：请重新「自动查找总名单」后再点本按钮。"
                : "本次**没有写入任何内存**。确认数字合理（特别是「放过我方」应等于你自己的东西总数）后再开启「秒杀敌人」。");
            SetOutput(text.ToString());
        }

        /// <summary>
        /// 「标记友军」：把**当前选中对象所属的阵营**记入"永不秒杀"名单（`AllyGuard`）。
        ///
        /// 为什么必须由用户点一下，而不是程序自动判断：秒杀的判据只有"不是你 + 阵营有钱"，
        /// 它**不知道"同盟"这回事**；AI 队友 / 局域网里的人类队友恰好是"不是你 + 有钱"，
        /// 会被一起清掉，而这类误伤**不可逆**。由用户点选队友的任意一个单位来标记，
        /// 是不需要猜游戏内部结构、又能当场验证的办法；误标记一个敌人的后果也只是"少杀"（方向安全）。
        ///
        /// 前置：读"阵营字段"用的偏移来自**本次会话已建立的我方锚点**（`_cachedOwnerOffset`）——
        /// 那是同一套对象布局（我方单位与队友单位是同一类对象）。没建过锚点就明确提示，不猜偏移。
        /// </summary>
        private void OnMarkAllyClick(object sender, EventArgs e)
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine("=== 标记友军（此后永不参与秒杀）===");
            text.AppendLine();

            MemoryIO memory = _target.Memory;
            if (_cachedOwnerOffset == 0)
            {
                text.AppendLine("【缺前置】还没建立「我方锚点」—— 程序要靠它知道去对象的哪个位置读阵营。");
                text.AppendLine("请先在游戏里选中**你自己的一个单位**，点一次「预演：秒杀敌人」，再回来点本按钮。");
                SetOutput(text.ToString());
                return;
            }

            List<uint> selected;
            string note = "";
            if (_selection == null
                || !_selection.TryReadSelectedObjectsLoose(_profile, 4, out selected, out note))
            {
                text.AppendLine("【读不到选中的对象】" + note);
                text.AppendLine("请在游戏里**点选队友的一个单位或建筑**（点一下就行），再点本按钮。");
                SetOutput(text.ToString());
                return;
            }

            int marked = 0;
            for (int i = 0; i < selected.Count && i < 4; i++)
            {
                uint address = selected[i];
                uint owner;
                if (!ReadUInt32Retry(memory, address + _cachedOwnerOffset, out owner) || owner < 0x10000)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8} → 归属读不出，没有标记", address));
                    continue;
                }
                if (owner == _cachedOwnerHouse)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8} → 这是**你自己的阵营**，不用标记（秒杀本来就不会碰自己人）", address));
                    continue;
                }

                uint money;
                if (!TechnoOwnership.TryReadHouseMoney(memory, owner, LiveModuleBase, LiveModuleSize, out money))
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8} → 它的归属值 0x{1:X8} 不像真阵营（读不出阵营数据），**没有标记** ——"
                        + "请确认选中的是队友的部队或建筑", address, owner));
                    continue;
                }

                if (AllyGuard.IsProtected(owner))
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8} → 阵营 0x{1:X8} **刚才已经标记过**了", address, owner));
                    continue;
                }

                if (AllyGuard.Mark(owner))
                {
                    marked++;
                    Logger.Info("Advanced", string.Format(CultureInfo.InvariantCulture,
                        "已标记友军阵营 0x{0:X8}（对象 0x{1:X8} 的归属）—— 秒杀不会再写它的任何东西",
                        owner, address));
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8} → **已标记为友军**：阵营 0x{1:X8}（金钱读数 {2}）", address, owner, money));
                }
            }

            text.AppendLine();
            if (marked > 0)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "现在共标记了 {0} 个友军阵营 —— 秒杀**不会**碰它们的任何东西（与「疑似中立」同一层保护）。",
                    AllyGuard.Count));
                text.AppendLine("**每次重新开局都要重新标记一次**（阵营地址每局都会变，程序不会跨局沿用）。");
                text.AppendLine("验证办法：再点一次「预演」，上面会多出「**友军不杀：N 个**」这一项。");
            }
            else
            {
                text.AppendLine("本次没有新增标记（原因见上）。你想保护的队友，标记一次就够了。");
            }
            SetOutput(text.ToString());
        }

        /// <summary>
        /// 「你选中的对象逐个判定」：列出当前选中（含非我方）的每个对象，以及它被判成了哪一类。
        ///
        /// 用途：**心灵终结里中立单位不能和友军单位一起选中**（2026-10-08 用户实测），
        /// 所以"选中立建筑和单位一起看"走不通 —— 改成两次点击：
        /// **先选中你自己的单位点一次（建立锚点），再单独点那座中立建筑点一次**，
        /// 就能在这里看到它的判定结果。
        ///
        /// 这里用的是**宽松读法**（不要求选中的东西属于我方，见 `TryReadSelectedObjectsLoose`）
        /// 加**名单复核**（对象必须出现在全对象名单里才判定）——
        /// 宽松判据本身不足以定论，宁可不判定，也绝不猜。
        /// `rosterSuspect`（名单自检没过）时**跳过名单复核**：那时名单本身不可信，
        /// 拿它当判据只会把正确的结论也一起挡掉（真机 2026-10-08 16:47 正是如此）。
        /// </summary>
        private void AppendSelectionVerdicts(
            StringBuilder text, MemoryIO memory, TechnoOwnership.OwnerAnchor anchor,
            IList<TechnoObject> roster, bool rosterSuspect)
        {
            if (_selection == null) { return; }

            List<uint> selected;
            string note;
            if (!_selection.TryReadSelectedObjectsLoose(_profile, 16, out selected, out note))
            {
                Logger.Info("Advanced", "读取选中对象（宽松）失败：" + note);
                uint dumpSlot;
                string dumpError;
                if (_profile.SelectedList.TryResolve(_target.Memory, LiveModuleBase, out dumpSlot, out dumpError))
                {
                    DumpSelectionSlots(_target.Memory, dumpSlot);
                }
                text.AppendLine("（当前没有读到选中的对象：" + note + "）");
                text.AppendLine();
                return;
            }

            HashSet<uint> known = TechnoOwnership.BuildAddressSet(roster);
            int inRoster = 0;
            for (int i = 0; i < selected.Count; i++)
            {
                if (known.Contains(selected[i])) { inRoster++; }
            }

            if (rosterSuspect)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "你选中的对象逐个判定（共 {0} 个；「全对象名单」自检未通过，本节**不做名单交叉核对**）：",
                    selected.Count));
            }
            else
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "你选中的对象逐个判定（共 {0} 个，其中 {1} 个能在「全对象名单」里找到）：",
                    selected.Count, inRoster));
            }

            for (int i = 0; i < selected.Count && i < 12; i++)
            {
                uint address = selected[i];
                uint type;
                ReadUInt32Retry(memory, address, out type);

                // 名单复核：宽松读法读到的地址，必须在全对象名单里出现过才敢判定。
                //（真机两次证明：名单对不对，用"选中的东西在不在里面"一眼就能看出来。）
                // 名单自检没过时跳过这一关 —— 那时名单本身不可信，拿它当判据会把正确结论也挡掉。
                if (!rosterSuspect && !known.Contains(address))
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}（类型 0x{1:X8}）→ **不在全对象名单里**，无法判定"
                        + "（要么这份名单不对，要么刚才读到的不是你真正选中的东西）",
                        address, type));
                    continue;
                }

                uint owner;
                if (!ReadUInt32Retry(memory, address + anchor.OwnerOffset, out owner) || owner == 0)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}（类型 0x{1:X8}）→ 归属读不出", address, type));
                    continue;
                }
                if (owner == anchor.MyHouse)
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}（类型 0x{1:X8}）→ **我方**", address, type));
                    continue;
                }

                if (AllyGuard.IsProtected(owner))
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}（类型 0x{1:X8}）→ **友军（你标记过，不会杀）**（归属 0x{2:X8}）",
                        address, type, owner));
                    continue;
                }

                uint money;
                if (!TechnoOwnership.TryReadHouseMoney(memory, owner, LiveModuleBase, LiveModuleSize, out money))
                {
                    text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  0x{0:X8}（类型 0x{1:X8}）→ 归属存疑（0x{2:X8} 不像真阵营，不会杀）",
                        address, type, owner));
                    continue;
                }

                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    money == 0
                        ? "  0x{0:X8}（类型 0x{1:X8}）→ **疑似中立**（归属 0x{2:X8}，金钱读数为 0，不会杀）"
                        : "  0x{0:X8}（类型 0x{1:X8}）→ 敌方（归属 0x{2:X8}，金钱 {3}，会被杀）",
                    address, type, owner, money));
            }
            text.AppendLine();
        }

        /// <summary>按阵营分组输出（阵营 → 个数，并附该阵营的类型构成）。</summary>
        private void AppendOwnerGroups(
            StringBuilder text, MemoryIO memory, string title,
            Dictionary<uint, List<TechnoObject>> groups, uint ownerOffset)
        {
            text.AppendLine(title + "：");
            if (groups.Count == 0) { text.AppendLine("  （无）"); text.AppendLine(); return; }

            List<KeyValuePair<uint, List<TechnoObject>>> ordered =
                new List<KeyValuePair<uint, List<TechnoObject>>>();
            foreach (KeyValuePair<uint, List<TechnoObject>> pair in groups) { ordered.Add(pair); }
            ordered.Sort(delegate(KeyValuePair<uint, List<TechnoObject>> a, KeyValuePair<uint, List<TechnoObject>> b)
            {
                return b.Value.Count.CompareTo(a.Value.Count);
            });

            for (int i = 0; i < ordered.Count && i < 8; i++)
            {
                uint ownerValue = ordered[i].Key;
                List<TechnoObject> list = ordered[i].Value;

                uint money;
                TechnoOwnership.TryReadHouseMoney(memory, ownerValue, LiveModuleBase, LiveModuleSize, out money);

                StringBuilder types = new StringBuilder();
                List<KeyValuePair<uint, int>> typeCounts = TechnoOwnership.CountTypes(list);
                for (int k = 0; k < typeCounts.Count && k < 4; k++)
                {
                    if (k > 0) { types.Append("、"); }
                    types.Append(string.Format(CultureInfo.InvariantCulture,
                        "0x{0:X8}×{1}", typeCounts[k].Key, typeCounts[k].Value));
                }
                if (typeCounts.Count > 4) { types.Append("…"); }

                text.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "  阵营 0x{0:X8}：{1} 个（金钱 {2}）｜类型：{3}",
                    ownerValue, list.Count, money, types.ToString()));
            }
            if (ordered.Count > 8)
            {
                text.AppendLine(string.Format(CultureInfo.InvariantCulture, "  …另有 {0} 个阵营未列出", ordered.Count - 8));
            }
            text.AppendLine();
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

        /// <summary>
        /// 沿用本次会话此前建立的我方锚点 —— 只在"这次选中的东西里没有本方单位"时才走这里
        ///（典型情形：单独点了一座中立建筑。见 `_cachedOwnerHouse` 的注释）。
        ///
        /// 核对：锚点记录的房子必须仍是**此刻金钱链指向的那个房子**
        ///（`TechnoOwnership.TryVerifyCachedAnchor`）—— 换了局就作废并清掉，宁可拒绝判定。
        /// 第二道核对（"名单里还有我方对象"）在分类之后做，见 `OnKillPreviewClick`。
        /// </summary>
        private bool TryReuseAnchor(
            MemoryIO memory, uint moneyAddress, out TechnoOwnership.OwnerAnchor anchor, out string why)
        {
            anchor = null;
            why = "";
            if (_cachedOwnerHouse == 0 || _cachedOwnerOffset == 0)
            {
                why = "本次会话里还没有建立过锚点，没法确认「我方是谁」";
                return false;
            }

            string reason;
            if (!TechnoOwnership.TryVerifyCachedAnchor(
                memory, _cachedOwnerOffset, _cachedOwnerHouse, moneyAddress, out reason))
            {
                _cachedOwnerOffset = 0;    // 已作废，别再拿它试
                _cachedOwnerHouse = 0;
                why = reason;
                return false;
            }

            anchor = new TechnoOwnership.OwnerAnchor();
            anchor.OwnerOffset = _cachedOwnerOffset;
            anchor.MyHouse = _cachedOwnerHouse;
            anchor.Samples = 0;
            return true;
        }

        /// <summary>
        /// 与 `ReadSelectedObjects` 相同，但**失败时不倾倒原始数据**。
        /// 「预演」用它：在那里"选中的东西不属于我方"是**正常情形**（单独点了一座中立建筑），
        /// 不该每次失败都往日志里倒一大片原始值。
        /// </summary>
        private List<uint> TryReadMyObjectsQuiet(out string note)
        {
            note = "";
            if (_selection == null)
            {
                note = "内部错误：选中读取器未初始化";
                return new List<uint>();
            }

            List<uint> objects;
            if (!_selection.TryReadSelectedObjects(_profile, 16, true, out objects, out note))
            {
                return new List<uint>();
            }
            if (note.Length > 0) { Logger.Info("Advanced", "选中列表：" + note); }
            return objects;
        }

        /// <summary>
        /// 读出"你选中的自己的单位"（金钱锚定确认属于我方）。
        ///
        /// **与引擎 / 采集器共用同一套"金钱锚定 + 自动更正"的读法**（2026-10-08 起）——
        /// 在此之前这里是"按配置槽位直读 + 很弱的'看着像对象'校验"，
        /// 真机 10-08 证明那样读出来的根本不是玩家选中的单位（详见 `SelectionResolver` 的类注释）。
        /// 失败时把原因带回给调用方显示，并把这一带的原始值打进日志（便于排查）。
        /// </summary>
        private List<uint> ReadSelectedObjects(out string note)
        {
            note = "";
            if (_selection == null)
            {
                note = "内部错误：选中读取器未初始化";
                return new List<uint>();
            }

            List<uint> objects;
            if (!_selection.TryReadSelectedObjects(_profile, 16, true, out objects, out note))
            {
                Logger.Info("Advanced", "读取选中单位失败：" + note);

                uint dumpSlot;
                string dumpError;
                if (_profile.SelectedList.TryResolve(_target.Memory, LiveModuleBase, out dumpSlot, out dumpError))
                {
                    DumpSelectionSlots(_target.Memory, dumpSlot);
                }
                return new List<uint>();
            }
            if (note.Length > 0) { Logger.Info("Advanced", "选中列表：" + note); }
            return objects;
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
                        headOk ? (InModule(head, LiveModuleBase, LiveModuleSize) ? "(在模块内✓)" : "(不在模块内)") : "");
                }
                Logger.Info("Advanced", entries);
            }
            else
            {
                Logger.Info("Advanced", "选中数组指针读取失败（连续重试 3 次均失败）");
            }
        }


        private static uint Offset(uint address, int delta)
        {
            long value = (long)address + delta;
            if (value < 0x10000 || value > 0xFFFFFFFFL) { return 0; }
            return (uint)value;
        }


        private static bool InModule(uint value, uint moduleBase, uint moduleSize)
        {
            return value >= moduleBase && value < moduleBase + moduleSize;
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
