using System;
using System.Drawing;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;
using MoTrainer.Cheats;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 单个功能的卡片：开关 + 说明 + 目标值 + 实时状态。
    ///
    /// 设计要点：**未校准的功能必须看起来就不能用**。
    /// 修改器最让人恼火的失败模式是"开关能打开、但什么也没发生" ——
    /// 玩家会以为是游戏问题。所以未校准时开关置灰，并直接写明该去哪一步。
    /// </summary>
    public sealed class FeatureCard : CardPanel
    {
        public const int HeightWithValue = 134;
        public const int HeightPlain = 106;

        /// <summary>高风险功能多出一行"知情确认"勾选框，卡片相应加高。</summary>
        public const int RiskAgreeExtraHeight = 24;

        /// <summary>
        /// 知情确认勾选框（仅高风险功能显示）。
        ///
        /// 取代原来的弹窗：弹窗"点一下就过去了"，拦不住人，还逼玩家切屏（用户反馈）。
        /// 常驻在卡片下方更像"注册前勾选协议"—— 勾了才能开启，语义清楚且不打断操作。
        /// </summary>
        private readonly CheckBox _agree;

        private readonly ToggleSwitch _toggle;
        private readonly Label _description;
        private readonly Label _risk;
        private readonly Label _valueLabel;
        private readonly TextBox _valueInput;
        private readonly Label _status;

        private CheatFeature _feature;
        private bool _suppressEvents;
        private bool _desired;

        /// <summary>用户切换了开关。</summary>
        public event EventHandler Toggled;

        /// <summary>用户提交了新的目标值。</summary>
        public event EventHandler ValueCommitted;

        public FeatureCard()
        {
            _description = MakeLabel(Theme.Small, Theme.Text1);
            Controls.Add(_description);

            _risk = MakeLabel(Theme.Small, Theme.Text2);
            Controls.Add(_risk);

            _valueLabel = MakeLabel(Theme.Body, Theme.Text1);
            Controls.Add(_valueLabel);

            _valueInput = new TextBox();
            _valueInput.Font = Theme.Mono;
            _valueInput.BorderStyle = BorderStyle.FixedSingle;
            _valueInput.BackColor = Theme.Bg2;
            _valueInput.ForeColor = Theme.Text0;
            _valueInput.Height = 24;
            _valueInput.KeyDown += OnValueKeyDown;
            _valueInput.Leave += OnValueLeave;
            Controls.Add(_valueInput);

            _status = MakeLabel(Theme.Mono, Theme.Text2);
            Controls.Add(_status);

            _agree = new CheckBox();
            // 文案刻意短：这句话是卡片里最长的文本，一旦它把卡片撑宽，
            // 就会连带把整页顶宽、右边缘被窗口切掉（用户实测：勾选框和日志按钮都被切了一半）。
            // 完整说明放在提示气泡里，想看细节的人悬停即可。
            _agree.Text = "我已了解风险（联机不同步）";
            _agree.Font = Theme.Small;
            _agree.ForeColor = Theme.Text2;
            _agree.FlatStyle = FlatStyle.Flat;
            _agree.AutoSize = false;
            // 高度由字体决定：写死 20 像素时，高 DPI 下放大的字只显示上半截（用户实测）
            _agree.Height = Math.Max(20, Theme.Body.Height + 8);
            _agree.CheckedChanged += OnAgreeChanged;
            _agree.Visible = false;
            Controls.Add(_agree);

            _toggle = new ToggleSwitch();
            _toggle.CheckedChanged += OnToggleChanged;
            Controls.Add(_toggle);
        }

        public CheatFeature Feature { get { return _feature; } }

        /// <summary>卡片上的开关。暴露出来是为了让自检能驱动它（界面逻辑也要有回归覆盖）。</summary>
        public ToggleSwitch Toggle { get { return _toggle; } }

        private static Label MakeLabel(Font font, Color color)
        {
            Label label = new Label();
            label.AutoSize = true;
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            return label;
        }

        // ============================================================ 绑定

        public void Bind(CheatFeature feature)
        {
            _feature = feature;
            if (feature == null) { return; }

            Title = feature.Name;
            ShowRiskBar = true;
            RiskColor = RiskColorOf(feature.Risk);

            _description.Text = feature.Description;
            _risk.Text = feature.RiskText;
            _risk.ForeColor = RiskColorOf(feature.Risk);

            _valueLabel.Text = "锁定为";
            _valueLabel.Visible = feature.NeedsValue;

            _valueInput.Visible = feature.NeedsValue;
            _suppressEvents = true;
            _valueInput.Text = feature.TargetValue.ToString(CultureInfo.InvariantCulture);
            _suppressEvents = false;

            _desired = feature.Enabled;
            bool needsAgree = feature.Risk == SynchRisk.High;
            // 勾选框增高后，卡片也要相应加高，否则勾选框会被卡片底边压掉
            Height = (feature.NeedsValue ? HeightWithValue : HeightPlain)
                + (needsAgree ? Math.Max(RiskAgreeExtraHeight, _agree.Height + 8) : 0);

            _agree.Visible = needsAgree;
            _agree.Checked = Agreed;

            RefreshState();
            LayoutChildren();
        }

        public static Color RiskColorOf(SynchRisk risk)
        {
            switch (risk)
            {
                case SynchRisk.Low: return Theme.Success;
                case SynchRisk.Medium: return Theme.Warning;
                default: return Theme.Danger;
            }
        }

        // ============================================================ 布局

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        /// <summary>
        /// 手工排版而不是靠 Anchor：卡片被放进 TableLayoutPanel 且 Dock=Fill，
        /// 宽度在加入之后才会确定，那时 Anchor 的距离基准已经算错了。
        /// </summary>
        private void LayoutChildren()
        {
            if (_feature == null) { return; }

            int left = Theme.CardPadding + 6;
            int right = Width - Theme.CardPadding;

            // 开关固定在右上角，与标题同一行
            _toggle.Location = new Point(right - _toggle.Width, 8);

            // 风险标签放在说明行右端。
            // 早先放在标题行（开关左侧）时，功能名一长就会和它贴在一起，
            // 视觉上读成"金钱同步风险：中"这样一整句 —— 挪到说明行更安全，
            // 也留出至少 16px 的硬间隔。
            _description.Location = new Point(left, 46);
            _risk.Location = new Point(
                Math.Max(left + 40, right - _risk.Width), 48);

            if (_agree.Visible)
            {
                // 底边距也按勾选框实际高度算，别用常量 26
                _agree.Location = new Point(left, Height - _agree.Height - 6);
                _agree.Width = Math.Max(160, right - left);
            }

            if (_feature.NeedsValue)
            {
                _valueLabel.Location = new Point(left, 74);
                _valueInput.Location = new Point(left + _valueLabel.Width + 8, 71);
                _valueInput.Width = Math.Max(90, Math.Min(160, right - _valueInput.Left - 60));
                _status.Location = new Point(left, 106);
            }
            else
            {
                _status.Location = new Point(left, 76);
            }
        }

        // ============================================================ 状态显示

        /// <summary>
        /// 已勾选知情确认的功能（进程内共享）。
        ///
        /// 用静态集合是为了让**界面与热键走同一份状态**：主窗口判断热键请求时
        /// 不需要绕回卡片去问，避免"界面上勾了、热键却还被拒"这种割裂。
        /// </summary>
        private static readonly HashSet<CheatId> AgreedIds = new HashSet<CheatId>();

        public static bool IsAgreed(CheatId id) { return AgreedIds.Contains(id); }

        /// <summary>是否已勾选知情确认（勾过一次就保持，重建卡片也不会丢）。</summary>
        public bool Agreed
        {
            get { return _feature != null && AgreedIds.Contains(_feature.Id); }
            set
            {
                if (_feature == null) { return; }
                if (value) { AgreedIds.Add(_feature.Id); } else { AgreedIds.Remove(_feature.Id); }
            }
        }

        private void OnAgreeChanged(object sender, EventArgs e)
        {
            Agreed = _agree.Checked;
            RefreshState();
        }

        public void RefreshState()
        {
            CheatFeature feature = _feature;
            if (feature == null) { return; }

            if (!feature.IsCalibrated)
            {
                _toggle.ReadOnly = true;
                _toggle.SetCheckedSilently(false);
                _valueInput.Enabled = false;
                _status.ForeColor = Theme.Warning;
                _status.Text = "尚未校准 —— 请到「校准向导」或「单位校准」完成该字段的定位";
                return;
            }

            // 高风险功能必须先勾选知情确认才能开启 —— 关掉它等于把开关置灰。
            bool blockedByAgreement = feature.Risk == SynchRisk.High && !Agreed;
            _toggle.ReadOnly = blockedByAgreement;
            _toggle.SetCheckedSilently(!blockedByAgreement && feature.Enabled);
            _desired = feature.Enabled;
            _valueInput.Enabled = feature.Enabled;
            _valueInput.BackColor = feature.Enabled ? Theme.Bg2 : Theme.Bg1;
            _description.ForeColor = feature.Enabled ? Theme.Text0 : Theme.Text1;

            if (!feature.Enabled)
            {
                _status.ForeColor = Theme.Text2;
                _status.Text = feature.Address != 0
                    ? string.Format(CultureInfo.InvariantCulture,
                        "已关闭 · 当前游戏内 {0} · 地址 0x{1:X8}", feature.LastSeenValue, feature.Address)
                    : "已关闭 · 地址尚未解析";
                return;
            }

            if (feature.LastError.Length != 0)
            {
                _status.ForeColor = Theme.Danger;
                // 有些错误自带"该怎么做"的指引（例如秒杀敌人的"请先在游戏里选中一个自己的单位"）；
                // 只有"地址层面的故障"才补上"重新校准 / 重启"这句通用建议 ——
                // 否则两条指引打架，玩家会被引到错误的处置上去。
                bool selfExplained = feature.LastError.IndexOf("请") >= 0;
                _status.Text = "已开启，但" + feature.LastError
                    + (feature.LastError == "未连接游戏" || selfExplained
                        ? "" : "（请重新校准或重启游戏）");
                return;
            }

            if (feature.Address == 0)
            {
                _status.ForeColor = Theme.Warning;
                _status.Text = "已开启 · 正在解析地址…";
                return;
            }

            _status.ForeColor = Theme.Success;
            _status.Text = string.Format(CultureInfo.InvariantCulture,
                "已开启 · 已写入 {0:N0} 次 · 游戏内读到 {1} · 地址 0x{2:X8}",
                feature.WriteCount, feature.LastSeenValue, feature.Address);
        }

        // ============================================================ 交互

        private void OnToggleChanged(object sender, EventArgs e)
        {
            if (_suppressEvents || _feature == null) { return; }

            // 先把用户意图取下来存好，再往下走。
            //
            // 这里曾经踩过一个坑：在抛出 Toggled 之前调用了 RefreshState()，
            // 而 RefreshState 会把开关按回 feature.Enabled（此刻还是旧值），
            // 于是主窗口稍后读 DesiredEnabled 时读到的是被按回去的旧值，
            // 判定"状态没变"，开关自己弹回原位 —— 表现就是"点了没反应"。
            _desired = _toggle.Checked;

            // 未校准的开关是画灰的，这里再兜一层，防止任何路径把它打开
            if (!_feature.IsCalibrated)
            {
                _toggle.SetCheckedSilently(false);
                _desired = false;
                return;
            }

            // 注意：**不要**在这里刷新界面。
            // 开关状态的最终归宿由主窗口裁决，裁决完它会调用 RefreshAll() 统一同步。
            EventHandler handler = Toggled;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }

        /// <summary>用户希望切换到的状态（尚未被主窗口裁决）。</summary>
        public bool DesiredEnabled { get { return _desired; } }

        private void OnValueKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.Enter) { return; }
            e.SuppressKeyPress = true;
            CommitValue();
        }

        private void OnValueLeave(object sender, EventArgs e)
        {
            CommitValue();
        }

        private void CommitValue()
        {
            if (_suppressEvents || _feature == null || !_feature.NeedsValue) { return; }

            uint parsed;
            if (!uint.TryParse(_valueInput.Text.Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsed))
            {
                // 输入非法就还原成原值，并给出明确提示，不静默吞掉
                _valueInput.Text = _feature.TargetValue.ToString(CultureInfo.InvariantCulture);
                _status.ForeColor = Theme.Danger;
                _status.Text = "输入无效，请输入纯数字";
                return;
            }

            if (parsed < _feature.MinValue) { parsed = _feature.MinValue; }
            if (parsed > _feature.MaxValue) { parsed = _feature.MaxValue; }

            _valueInput.Text = parsed.ToString(CultureInfo.InvariantCulture);
            if (parsed == _feature.TargetValue) { return; }

            _feature.TargetValue = parsed;
            RefreshState();

            EventHandler handler = ValueCommitted;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }
    }
}
