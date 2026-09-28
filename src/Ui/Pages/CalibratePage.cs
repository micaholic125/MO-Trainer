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
    /// 校准向导界面。逻辑全部在 <see cref="CalibrationWizard"/> 里，本页只负责呈现与线程调度。
    /// 耗时的扫描操作放到线程池执行，界面用定时器轮询进度，保证窗口不卡死。
    /// </summary>
    public sealed class CalibratePage : UserControl
    {
        private const int InstructionHeight = 72;
        private const int ActionAreaHeight = 80;
        private const int CardStepHeight = 246;
        private const int CardListHeight = 156;

        private readonly ProcessAttach _attach;
        private readonly ProfileStore _store;
        private readonly CalibrationProfile _profile;
        private readonly CalibrationWizard _wizard;
        private readonly LiveCalibrationTarget _target;

        private readonly StepIndicator _steps;
        private readonly Label _subtitle;
        private readonly Label _stepTitle;
        private readonly Label _instruction;
        private readonly Label _status;
        private readonly FlowLayoutPanel _actions;
        private readonly CandidateListView _candidates;
        private readonly Label _listHint;
        // 显式限定：本文件同时 using 了 System.Threading（ThreadPool），Timer 会歧义
        private readonly System.Windows.Forms.Timer _progressTimer;

        private readonly DarkTextBox _moneyInput;
        private readonly Button _startButton;
        private readonly Button _moneyApplyButton;
        private readonly Button _grewButton;
        private readonly Button _shrankButton;
        private readonly Button _noChangeButton;
        private readonly Button _writeProbeButton;
        private readonly Button _probeWorksButton;
        private readonly Button _probeFailedButton;
        private readonly Button _spendDoneButton;
        private readonly Button _confirmPowerButton;
        private readonly Button _skipPowerFieldButton;
        private readonly Button _openReportButton;
        private readonly Button _resetButton;

        private readonly List<ActionBinding> _bindings = new List<ActionBinding>();
        private bool _busy;
        private bool _disposed;

        public CalibratePage(ProcessAttach attach, MemoryIO memory, ProfileStore store, CalibrationProfile profile)
        {
            _attach = attach;
            _store = store;
            _profile = profile;
            _target = new LiveCalibrationTarget(attach, memory);
            _wizard = new CalibrationWizard(_target, store, profile);

            BackColor = Theme.Bg0;
            AutoScroll = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 1;
            root.RowCount = 5;
            root.Padding = new Padding(20, 16, 34, 18);
            root.BackColor = Color.Transparent;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Label heading = UiFactory.MakeLabel("校准向导", Theme.Heading, Theme.Text0);
            heading.Margin = new Padding(0, 0, 0, 2);
            root.Controls.Add(heading, 0, 0);

            _subtitle = UiFactory.MakeLabel("", Theme.Body, Theme.Text2);
            _subtitle.Margin = new Padding(0, 0, 0, 10);
            root.Controls.Add(_subtitle, 0, 1);

            _steps = new StepIndicator();
            _steps.Dock = DockStyle.Fill;
            _steps.Margin = new Padding(0, 0, 0, 12);
            _steps.SetSteps(CalibrationWizard.StepNames, 0);
            root.Controls.Add(_steps, 0, 2);

            // ---------- 当前步骤卡片 ----------
            CardPanel stepCard = new CardPanel();
            stepCard.Title = "当前步骤";
            stepCard.Dock = DockStyle.Fill;
            stepCard.Height = CardStepHeight;
            stepCard.Margin = new Padding(0, 0, 0, 12);

            _stepTitle = UiFactory.MakeLabel("准备", Theme.BodyBold, Theme.PrimaryLight);
            _stepTitle.Location = new Point(Theme.CardPadding + 6, 38);
            stepCard.Controls.Add(_stepTitle);

            _instruction = new Label();
            _instruction.AutoSize = false;
            _instruction.Font = Theme.Body;
            _instruction.ForeColor = Theme.Text1;
            _instruction.BackColor = Color.Transparent;
            _instruction.Location = new Point(Theme.CardPadding + 6, 62);
            _instruction.Size = new Size(700, InstructionHeight);
            _instruction.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            stepCard.Controls.Add(_instruction);

            _actions = new FlowLayoutPanel();
            _actions.FlowDirection = FlowDirection.LeftToRight;
            _actions.WrapContents = true;
            _actions.BackColor = Color.Transparent;
            _actions.Location = new Point(Theme.CardPadding + 2, 62 + InstructionHeight);
            _actions.Size = new Size(720, ActionAreaHeight);
            _actions.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            stepCard.Controls.Add(_actions);

            _status = new Label();
            _status.AutoSize = false;
            _status.Font = Theme.Body;
            _status.ForeColor = Theme.Text1;
            _status.BackColor = Color.Transparent;
            _status.Location = new Point(Theme.CardPadding + 6, 62 + InstructionHeight + ActionAreaHeight + 2);
            _status.Size = new Size(700, 22);
            _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            stepCard.Controls.Add(_status);

            // ---------- 输入框与按钮 ----------
            _moneyInput = new DarkTextBox();
            _moneyInput.DigitsOnly = true;
            _moneyInput.Placeholder = "例如 10000";
            _moneyInput.Size = new Size(150, UiFactory.ButtonHeight);
            _moneyInput.Margin = new Padding(0, 0, 8, 8);
            _actions.Controls.Add(_moneyInput);

            _startButton = UiFactory.MakePrimaryButton("开始校准", 112);
            _moneyApplyButton = UiFactory.MakePrimaryButton("开始搜索", 108);
            _grewButton = UiFactory.MakeButton("金钱变多了", 104);
            _shrankButton = UiFactory.MakeButton("金钱变少了", 104);
            _noChangeButton = UiFactory.MakeButton("没变化", 84);
            _writeProbeButton = UiFactory.MakePrimaryButton("写入测试值", 112);
            _probeWorksButton = UiFactory.MakePrimaryButton("游戏里确实变了", 132);
            _probeFailedButton = UiFactory.MakeDangerButton("没变化，换一个", 132);
            _spendDoneButton = UiFactory.MakePrimaryButton("已花钱，检验", 124);
            // 文案在 RefreshUi 里按当前字段动态改写（负载 / 电力），宽度按"确认XXXXX字段"留足
            _confirmPowerButton = UiFactory.MakePrimaryButton("确认字段", 124);
            _skipPowerFieldButton = UiFactory.MakeButton("跳过这一项", 108);
            _openReportButton = UiFactory.MakeButton("打开报告目录", 116);
            _resetButton = UiFactory.MakeButton("重置", 72);

            AddAction(_startButton, null, new WizardStep[] { WizardStep.NotStarted, WizardStep.Failed });
            AddAction(_moneyApplyButton, null, new WizardStep[]
            {
                WizardStep.MoneyScan, WizardStep.MoneyNarrow, WizardStep.PowerScan
            });
            AddAction(_grewButton, null, new WizardStep[] { WizardStep.MoneyNarrow });
            AddAction(_shrankButton, null, new WizardStep[] { WizardStep.MoneyNarrow });
            AddAction(_noChangeButton, null, new WizardStep[] { WizardStep.MoneyNarrow });
            AddAction(_writeProbeButton, null, new WizardStep[] { WizardStep.MoneyVerify });
            AddAction(_probeWorksButton, null, new WizardStep[] { WizardStep.MoneyVerify });
            AddAction(_probeFailedButton, null, new WizardStep[] { WizardStep.MoneyVerify });
            AddAction(_spendDoneButton, null, new WizardStep[] { WizardStep.MoneySpendTest });
            AddAction(_confirmPowerButton, null, new WizardStep[] { WizardStep.PowerVerify });
            AddAction(_skipPowerFieldButton, null, new WizardStep[] { WizardStep.PowerScan, WizardStep.PowerVerify });
            AddAction(_openReportButton, null, new WizardStep[] { WizardStep.Completed });
            AddAction(_resetButton, null, null); // 始终可见

            root.Controls.Add(stepCard, 0, 3);

            // ---------- 候选列表卡片 ----------
            CardPanel listCard = new CardPanel();
            listCard.Title = "候选列表";
            listCard.Dock = DockStyle.Fill;
            listCard.Height = CardListHeight;

            _candidates = new CandidateListView();
            _candidates.Location = new Point(Theme.CardPadding, 36);
            _candidates.Size = new Size(700, 96);
            _candidates.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            listCard.Controls.Add(_candidates);

            _listHint = new Label();
            _listHint.AutoSize = false;
            _listHint.Font = Theme.Small;
            _listHint.ForeColor = Theme.Text2;
            _listHint.BackColor = Color.Transparent;
            _listHint.Location = new Point(Theme.CardPadding, 132);
            _listHint.Size = new Size(700, 18);
            _listHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            _listHint.Text = "候选会随着你的筛选不断减少，收敛到 1 个后进入验证。";
            listCard.Controls.Add(_listHint);

            root.Controls.Add(listCard, 0, 4);

            Controls.Add(root);

            // ---------- 事件 ----------
            _wizard.Changed += OnWizardChanged;
            _startButton.Click += delegate { RunAsync(delegate { return _wizard.Start(); }); };
            _moneyApplyButton.Click += OnMoneyApplyClick;
            _grewButton.Click += delegate { RunAsync(delegate { return _wizard.NarrowByChange(ValueMatch.Increased); }); };
            _shrankButton.Click += delegate { RunAsync(delegate { return _wizard.NarrowByChange(ValueMatch.Decreased); }); };
            _noChangeButton.Click += delegate { RunAsync(delegate { return _wizard.NarrowByChange(ValueMatch.Unchanged); }); };
            _writeProbeButton.Click += delegate { RunAsync(delegate { return _wizard.WriteProbe(); }); };
            _probeWorksButton.Click += delegate { RunAsync(delegate { return _wizard.ConfirmProbe(); }); };
            _probeFailedButton.Click += delegate { RunAsync(delegate { return _wizard.RejectProbe(); }); };
            _spendDoneButton.Click += delegate { RunAsync(delegate { return _wizard.VerifySpend(); }); };
            _confirmPowerButton.Click += OnConfirmPowerClick;
            _skipPowerFieldButton.Click += delegate { RunAsync(delegate { return _wizard.SkipPowerField(); }); };
            _openReportButton.Click += OnOpenReportClick;
            _resetButton.Click += delegate { _wizard.Reset(); };

            _progressTimer = new System.Windows.Forms.Timer();
            _progressTimer.Interval = 200;
            _progressTimer.Tick += delegate { UpdateStatusLine(); };
            _progressTimer.Start();

            RefreshUi();
        }

        /// <summary>校准完成（含保存配置与生成报告）后触发，供主窗口刷新状态条。</summary>
        public event Action CalibrationCompleted;

        // ============================================================ 操作

        private void AddAction(Button button, string label, WizardStep[] steps)
        {
            button.Margin = new Padding(0, 0, 8, 8);
            _actions.Controls.Add(button);

            ActionBinding binding = new ActionBinding();
            binding.Button = button;
            binding.Steps = steps;
            _bindings.Add(binding);
        }

        private void OnMoneyApplyClick(object sender, EventArgs e)
        {
            uint value;
            if (!_moneyInput.TryGetUInt32(out value))
            {
                string what = _wizard.Step == WizardStep.PowerScan ? "电力读数" : "金额";
                MessageBox.Show(this, "请先输入游戏里显示的" + what + "（纯数字）。", "MO Trainer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                _moneyInput.SelectAllText();
                return;
            }

            uint captured = value;
            bool isPower = _wizard.Step == WizardStep.PowerScan;
            RunAsync(delegate
            {
                return isPower ? _wizard.SubmitPowerValue(captured) : _wizard.SubmitMoneyValue(captured);
            });
        }

        private void OnConfirmPowerClick(object sender, EventArgs e)
        {
            ScanCandidate selected = _candidates.SelectedItem;
            if (selected == null)
            {
                MessageBox.Show(this, "请先在下面的候选列表里选中一个候选。", "MO Trainer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            uint address = selected.Address;
            uint value = selected.PreviousValue;
            RunAsync(delegate { return _wizard.ConfirmPowerField(address, value); });
        }

        private void OnOpenReportClick(object sender, EventArgs e)
        {
            string path = _wizard.ReportPath;
            if (string.IsNullOrEmpty(path))
            {
                MessageBox.Show(this, "还没有生成校准报告。", "MO Trainer",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            catch (Exception ex)
            {
                Logger.Warn("Calibrate", "打开报告目录失败：" + ex.Message);
            }
        }

        // ============================================================ 线程调度

        private void RunAsync(Func<bool> work)
        {
            if (_busy) { return; }
            _busy = true;
            RefreshUi();

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                bool ok = false;
                try
                {
                    ok = work();
                }
                catch (Exception ex)
                {
                    Logger.Error("Calibrate", "校准操作异常", ex);
                }

                Post(delegate
                {
                    _busy = false;
                    RefreshUi();
                    if (ok && _wizard.Step == WizardStep.Completed)
                    {
                        Action handler = CalibrationCompleted;
                        if (handler != null)
                        {
                            try { handler(); }
                            catch (Exception ex) { Logger.Warn("Calibrate", "完成回调异常：" + ex.Message); }
                        }
                    }
                });
            });
        }

        private void Post(Action action)
        {
            try
            {
                if (!IsHandleCreated || _disposed) { return; }
                if (InvokeRequired) { BeginInvoke(action); }
                else { action(); }
            }
            catch (Exception)
            {
                // 页面销毁过程中可能抛 InvalidOperationException，忽略
            }
        }

        private void OnWizardChanged()
        {
            Post(RefreshUi);
        }

        // ============================================================ 刷新

        private void RefreshUi()
        {
            if (_disposed) { return; }

            _steps.CurrentIndex = _wizard.StepIndicatorIndex;
            _stepTitle.Text = "第 " + (_wizard.StepIndicatorIndex + 1) + " 步 · " + _wizard.StepTitle;
            _instruction.Text = _wizard.Instruction;

            // 副标题保持一行：详细状态留给「主控台」页展示，这里只报关键数字
            _subtitle.Text = string.Format(CultureInfo.InvariantCulture,
                "一次性定位金钱与电力字段，结果长期复用。当前已校准 {0} 项。",
                _profile.CalibratedItemCount());

            for (int i = 0; i < _bindings.Count; i++)
            {
                ActionBinding binding = _bindings[i];
                bool visible = binding.Steps == null || ContainsStep(binding.Steps, _wizard.Step);
                binding.Button.Visible = visible;
                if (visible) { binding.Button.Enabled = !_busy; }
            }

            bool showInput = _wizard.Step == WizardStep.MoneyScan || _wizard.Step == WizardStep.MoneyNarrow
                || _wizard.Step == WizardStep.PowerScan;
            _moneyInput.Visible = showInput;
            _moneyInput.Enabled = !_busy;
            if (showInput) { _moneyInput.Inner.Enabled = !_busy; }

            if (_wizard.Step == WizardStep.PowerScan)
            {
                _moneyApplyButton.Text = _wizard.PowerScanStarted ? "按值筛选" : "开始搜索";
            }
            else if (_wizard.Step == WizardStep.MoneyScan) { _moneyApplyButton.Text = "开始搜索"; }
            else if (_wizard.Step == WizardStep.MoneyNarrow) { _moneyApplyButton.Text = "按值筛选"; }

            // 确认按钮的文案必须跟着当前字段走。
            // 早先这里写死了"确认电力字段"，在校准「负载」时会告诉用户去点一个不存在的按钮。
            if (_wizard.Step == WizardStep.PowerVerify)
            {
                _confirmPowerButton.Text = "确认" + _wizard.PowerFieldName + "字段";
            }

            // 候选列表
            if (_wizard.Step == WizardStep.PowerScan || _wizard.Step == WizardStep.PowerVerify)
            {
                _candidates.SetItems(_wizard.PowerCandidates, 0);
                _listHint.Text = "「" + _wizard.PowerFieldName + "」的候选：收敛到只剩 1 个后选中它并确认。";
            }
            else
            {
                _candidates.SetItems(_wizard.Candidates, _wizard.MoneyAddress);
                _listHint.Text = "候选会随着你的筛选不断减少，收敛到 1 个后进入验证。";
            }

            _resetButton.Enabled = !_busy;
            _openReportButton.Enabled = !_busy && !string.IsNullOrEmpty(_wizard.ReportPath);

            UpdateStatusLine();
        }

        private void UpdateStatusLine()
        {
            if (_disposed) { return; }

            if (_busy)
            {
                _status.ForeColor = Theme.PrimaryLight;
                string progress = _wizard.ProgressText;
                _status.Text = string.IsNullOrEmpty(progress) ? "正在处理，请稍候…" : progress;
                return;
            }

            _status.Text = _wizard.Status;
            switch (_wizard.Step)
            {
                case WizardStep.Failed:
                    _status.ForeColor = Theme.Danger;
                    break;
                case WizardStep.Completed:
                    _status.ForeColor = Theme.Success;
                    break;
                default:
                    _status.ForeColor = string.IsNullOrEmpty(_wizard.Status) ? Theme.Text2 : Theme.Warning;
                    break;
            }
        }

        private static bool ContainsStep(WizardStep[] steps, WizardStep value)
        {
            for (int i = 0; i < steps.Length; i++)
            {
                if (steps[i] == value) { return true; }
            }
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                try { _progressTimer.Stop(); _progressTimer.Dispose(); }
                catch { }
                try { _wizard.Changed -= OnWizardChanged; }
                catch { }
                try { _wizard.Cancel(); }
                catch { }
            }
            base.Dispose(disposing);
        }

        /// <summary>步骤与按钮的显示绑定。</summary>
        private sealed class ActionBinding
        {
            public Button Button;
            public WizardStep[] Steps;
        }
    }
}
