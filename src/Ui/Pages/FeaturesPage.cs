using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MoTrainer.Calibration;
using MoTrainer.Cheats;
using MoTrainer.Ui.Controls;

namespace MoTrainer.Ui.Pages
{
    /// <summary>
    /// 功能开关页 —— 这是工具的**主界面**。
    /// 设计目标是"打开就知道能干什么、一眼看出哪里没准备好"，
    /// 所以顺序是：总体状态 → 功能卡片 → 热键说明。
    /// </summary>
    public sealed class FeaturesPage : UserControl
    {
        private readonly CheatEngine _engine;
        private readonly TrainerSettings _settings;
        private readonly CalibrationProfile _profile;
        private readonly List<FeatureCard> _cards = new List<FeatureCard>();
        private readonly Label _summary;
        private readonly Label _subtitle;
        private readonly ToolTip _toolTip = new ToolTip();

        /// <summary>
        /// 用户拨动了某个开关，携带"希望切换到的状态"。
        /// 本页**不做裁决**（是否已校准、是否高风险），一律交给主窗口统一处理 ——
        /// 否则热键与界面两条路径的安全规则迟早会走岔。
        /// </summary>
        public event Action<CheatId, bool> ToggleRequested;

        /// <summary>用户修改了目标值，需要落盘。</summary>
        public event EventHandler ValueChanged;

        public FeaturesPage(CheatEngine engine, TrainerSettings settings, CalibrationProfile profile)
        {
            if (engine == null) { throw new ArgumentNullException("engine"); }
            if (settings == null) { throw new ArgumentNullException("settings"); }

            _engine = engine;
            _settings = settings;
            _profile = profile == null ? new CalibrationProfile() : profile;

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

            Label heading = new Label();
            heading.AutoSize = true;
            heading.Font = Theme.Heading;
            heading.ForeColor = Theme.Text0;
            heading.Text = "功能开关";
            heading.Margin = new Padding(0, 0, 0, 2);
            root.Controls.Add(heading);

            _subtitle = new Label();
            _subtitle.AutoSize = true;
            _subtitle.Font = Theme.Body;
            _subtitle.ForeColor = Theme.Text2;
            _subtitle.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(_subtitle);

            _summary = new Label();
            _summary.AutoSize = true;
            _summary.Font = Theme.Body;
            _summary.Margin = new Padding(0, 0, 0, 12);
            root.Controls.Add(_summary);

            IList<CheatFeature> features = _engine.Features;
            for (int i = 0; i < features.Count; i++)
            {
                FeatureCard card = new FeatureCard();
                card.Bind(features[i]);
                // 必须显式 Dock=Fill：卡片是 Panel，默认尺寸只有 200x100，
                // 不撑满的话 TableLayoutPanel 会按内容宽度收窄整列，卡片全挤成一条
                card.Dock = DockStyle.Fill;
                card.Margin = new Padding(0, 0, 0, Theme.GapCard);
                card.Toggled += OnCardToggled;
                card.ValueCommitted += OnCardValueCommitted;
                root.Controls.Add(card);
                _cards.Add(card);

                if (!string.IsNullOrEmpty(features[i].Detail))
                {
                    _toolTip.SetToolTip(card, features[i].Detail);
                }
            }

            root.Controls.Add(BuildHotkeyCard());
            Controls.Add(root);

            RefreshAll();
        }

        private CardPanel BuildHotkeyCard()
        {
            CardPanel card = new CardPanel();
            card.Title = "全局热键";
            card.Dock = DockStyle.Fill;
            card.Margin = new Padding(0, 0, 0, 0);

            string[] lines = new string[]
            {
                HotkeyLine(CheatId.Money, "Ctrl + Alt + 1        金钱开关"),
                HotkeyLine(CheatId.PowerOutput, "Ctrl + Alt + 2        电力开关"),
                HotkeyLine(CheatId.SelectedInvincible, "Ctrl + Alt + 3        选中单位无敌开关"),
                HotkeyLine(CheatId.KillEnemies, "Ctrl + Alt + 4        秒杀敌人开关"),
                "",
                "提示：游戏若是独占全屏，Windows 可能把热键让给游戏窗口而收不到。",
                "本工具不修改任何游戏文件，只读写游戏进程的内存。"
            };
            card.Height = 34 + 8 + lines.Length * 20 + 14;

            int y = card.TitleHeight + 6;
            for (int i = 0; i < lines.Length; i++)
            {
                Label line = new Label();
                line.AutoSize = true;
                line.Font = i >= 5 ? Theme.Small : Theme.Body;
                line.ForeColor = i >= 5 ? Theme.Text2 : Theme.Text1;
                line.Text = lines[i];
                line.Location = new Point(Theme.CardPadding + 6, y);
                card.Controls.Add(line);
                y += i >= 4 ? 18 : 20;
            }
            return card;
        }

        /// <summary>
        /// 热键说明里的一行。功能被**刻意停用**时补一句 ——
        /// 程序那边是"根本不注册这个热键"，说明里若还写着"Ctrl+Alt+2 电力开关"，
        /// 玩家就会照着按、然后以为热键坏了。
        /// </summary>
        private string HotkeyLine(CheatId id, string text)
        {
            CheatFeature feature = _engine.Find(id);
            if (feature == null || feature.IsAvailable) { return text; }
            return text + "（暂不可用，见上方卡片）";
        }

        private void OnCardToggled(object sender, EventArgs e)
        {
            FeatureCard card = sender as FeatureCard;
            if (card == null || card.Feature == null) { return; }

            Action<CheatId, bool> handler = ToggleRequested;
            if (handler != null) { handler(card.Feature.Id, card.DesiredEnabled); }
        }

        private void OnCardValueCommitted(object sender, EventArgs e)
        {
            PersistSettings();
            EventHandler handler = ValueChanged;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }

        private void PersistSettings()
        {
            string path = _settings.PathIn(GetConfigFolder());
            _settings.Save(path);
        }

        private string GetConfigFolder()
        {
            return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");
        }

        /// <summary>校准完成后由主窗口调用：重新绑定定位信息并刷新卡片。</summary>
        public void Rebind()
        {
            _engine.BindProfile(_profile);
            RefreshAll();
        }

        /// <summary>由主窗口的 1Hz 定时器调用。</summary>
        public void TickRefresh()
        {
            for (int i = 0; i < _cards.Count; i++) { _cards[i].RefreshState(); }
            RefreshSummary();
        }

        public void RefreshAll()
        {
            for (int i = 0; i < _cards.Count; i++) { _cards[i].RefreshState(); }
            RefreshSummary();
        }

        private void RefreshSummary()
        {
            int total = 0;
            int usable = 0;
            int unavailable = 0;
            for (int i = 0; i < _engine.Features.Count; i++)
            {
                CheatFeature feature = _engine.Features[i];
                total++;
                if (!feature.IsAvailable) { unavailable++; }
                else if (feature.IsCalibrated) { usable++; }
            }

            // 分母刻意**排除"刻意停用"的功能**：那些不是"还差一步校准"，
            // 而是"现在这条路走不通"。把它们算进分母，玩家会一直看到"5/6 项"，
            // 然后跑到校准页反复折腾一个根本修不好的东西。
            int expected = total - unavailable;

            _subtitle.Text = _engine.IsRunning
                ? string.Format(CultureInfo.InvariantCulture,
                    "v0.2 开发版 · 锁定线程运行中（30Hz）· 可用功能 {0}/{1} 项", usable, expected)
                : "v0.2 开发版 · 锁定线程未运行";

            if (usable == 0)
            {
                _summary.ForeColor = Theme.Warning;
                _summary.Text = "还没有任何字段完成校准 —— 请先到「校准向导」完成一次性定位。";
                return;
            }

            if (usable < expected)
            {
                _summary.ForeColor = Theme.Warning;
                _summary.Text = string.Format(CultureInfo.InvariantCulture,
                    "已校准 {0}/{1} 项。未校准的功能开关是灰的，无法开启 —— 请到「校准向导」补齐。",
                    usable, expected);
                return;
            }

            if (unavailable > 0)
            {
                // 用警告色而不是成功色：这一页上确实有一项用不了，
                // 报"全部正常"会让玩家以为是自己没找到开关。
                _summary.ForeColor = Theme.Warning;
                _summary.Text = string.Format(CultureInfo.InvariantCulture,
                    "已校准的功能全部可用；另有 {0} 项正在重做、暂不可用（卡片上写了原因）。", unavailable);
                return;
            }

            _summary.ForeColor = Theme.Success;
            _summary.Text = "全部字段已校准，所有功能可用。";
        }
    }
}
