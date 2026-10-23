using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 顶部常驻状态条：连接圆点 + 连接状态 + 模块基址 + 校准状态 + 运行模式。
    /// </summary>
    public sealed class StatusStripView : UserControl
    {
        private readonly FlowLayoutPanel _flow;
        private readonly StatusDot _dot;
        private readonly Label _stateLabel;
        private readonly Label _baseLabel;
        private readonly Label _calibLabel;
        private readonly Label _modeLabel;

        public StatusStripView()
        {
            BackColor = Theme.Bg1;
            Height = Theme.StatusHeight;
            Dock = DockStyle.Fill;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            _flow = new FlowLayoutPanel();
            _flow.Dock = DockStyle.Fill;
            _flow.FlowDirection = FlowDirection.LeftToRight;
            _flow.WrapContents = false;
            _flow.Padding = new Padding(18, 0, 12, 0);
            _flow.BackColor = Color.Transparent;

            _dot = new StatusDot();
            _dot.Margin = new Padding(0, 22, 8, 0);

            _stateLabel = MakeLabel("未连接", Theme.Text0);
            _stateLabel.Margin = new Padding(0, 17, 10, 0);

            _baseLabel = MakeLabel("基址 —", Theme.Text1);
            _calibLabel = MakeLabel("校准 未校准", Theme.Text1);
            _modeLabel = MakeLabel("单人 / 自建局域网", Theme.Text1);

            _flow.Controls.Add(_dot);
            _flow.Controls.Add(_stateLabel);
            _flow.Controls.Add(_baseLabel);
            _flow.Controls.Add(_calibLabel);
            _flow.Controls.Add(_modeLabel);
            Controls.Add(_flow);
        }

        private static Label MakeLabel(string text, Color color)
        {
            Label label = new Label();
            label.AutoSize = true;
            label.Font = Theme.Body;
            label.ForeColor = color;
            label.Text = text;
            label.BackColor = Color.Transparent;
            label.Margin = new Padding(0, 18, 8, 0);
            return label;
        }

        public void UpdateStatus(string stateText, Color stateColor, string baseText, string calibrationText, string modeText)
        {
            _dot.DotColor = stateColor;
            _stateLabel.Text = stateText;
            _stateLabel.ForeColor = stateColor;
            _baseLabel.Text = baseText;
            _calibLabel.Text = calibrationText;
            _modeLabel.Text = modeText;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(Theme.Border, 1f))
            {
                e.Graphics.DrawLine(pen, 0, Height - 1, Width, Height - 1);
            }
        }

        /// <summary>连接状态圆点。</summary>
        private sealed class StatusDot : Control
        {
            private Color _color = Theme.Warning;

            public StatusDot()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
                Size = new Size(10, 10);
                BackColor = Color.Transparent;
            }

            public Color DotColor
            {
                get { return _color; }
                set { _color = value; Invalidate(); }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

                // 外圈光晕，让状态点在深色底上更醒目
                using (SolidBrush glow = new SolidBrush(Color.FromArgb(60, _color)))
                {
                    e.Graphics.FillEllipse(glow, -3, -3, Width + 6, Height + 6);
                }
                using (SolidBrush brush = new SolidBrush(_color))
                {
                    e.Graphics.FillEllipse(brush, 0, 0, Width - 1, Height - 1);
                }
                base.OnPaint(e);
            }
        }
    }
}
