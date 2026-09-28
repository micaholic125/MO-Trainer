using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 胶囊开关：轨道 + 圆形滑块，切换时滑块平滑滑到位。
    ///
    /// 自绘而不用 CheckBox，一是跟整体深色扁平风格统一，
    /// 二是"开/关"状态在一屏卡片里比小方框醒目得多。
    /// 动效刻意做短（约 100ms）：修改器是工具类软件，拖沓的动画只会碍事。
    /// </summary>
    public sealed class ToggleSwitch : Control
    {
        private const int AnimIntervalMs = 15;
        private const double AnimStep = 0.22;

        private bool _checked;
        private double _progress;
        private bool _hover;
        private bool _enabled = true;
        private readonly Timer _timer;

        public event EventHandler CheckedChanged;

        public ToggleSwitch()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
                | ControlStyles.SupportsTransparentBackColor, true);
            Size = new Size(52, 26);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            TabStop = false;

            _timer = new Timer();
            _timer.Interval = AnimIntervalMs;
            _timer.Tick += OnAnimTick;
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) { return; }
                _checked = value;
                StartAnim();
                RaiseCheckedChanged();
            }
        }

        /// <summary>设置状态但不触发 CheckedChanged，用于程序内部同步（如配置回读）。</summary>
        public void SetCheckedSilently(bool value)
        {
            if (_checked == value) { return; }
            _checked = value;
            StartAnim();
        }

        public bool ReadOnly
        {
            get { return !_enabled; }
            set
            {
                _enabled = !value;
                Cursor = _enabled ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        private void StartAnim()
        {
            _timer.Start();
            Invalidate();
        }

        private void OnAnimTick(object sender, EventArgs e)
        {
            double target = _checked ? 1.0 : 0.0;
            double delta = target - _progress;
            if (Math.Abs(delta) <= AnimStep)
            {
                _progress = target;
                _timer.Stop();
            }
            else
            {
                _progress += delta > 0 ? AnimStep : -AnimStep;
            }
            Invalidate();
        }

        private void RaiseCheckedChanged()
        {
            EventHandler handler = CheckedChanged;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = _enabled;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        /// <summary>以编程方式切换一次，行为与用户点击完全一致。</summary>
        public void Toggle()
        {
            if (!_enabled) { return; }
            _checked = !_checked;
            StartAnim();
            RaiseCheckedChanged();
        }

        protected override void OnClick(EventArgs e)
        {
            Toggle();
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg1);

            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            int radius = track.Height / 2;

            // 关：暗色轨道 + 描边；开：主色轨道。中间态按进度插值，动画才连贯
            Color offFill = _hover ? Theme.Mix(Theme.Bg2, Theme.Text2, 0.25) : Theme.Bg2;
            Color onFill = _hover ? Theme.PrimaryLight : Theme.Primary;
            Color fill = Theme.Mix(offFill, onFill, _progress);
            Color border = _progress <= 0.01
                ? (_hover ? Theme.Mix(Theme.Border, Theme.Primary, 0.5) : Theme.Border)
                : fill;

            if (!_enabled)
            {
                fill = Theme.Mix(fill, Theme.Bg1, 0.55);
                border = Theme.Mix(border, Theme.Bg1, 0.55);
            }

            using (GraphicsPath path = Theme.RoundedRect(track, radius))
            {
                using (SolidBrush brush = new SolidBrush(fill)) { g.FillPath(brush, path); }
                using (Pen pen = new Pen(border, 1f)) { g.DrawPath(pen, path); }
            }

            // 滑块
            int knobSize = track.Height - 6;
            double travel = track.Width - knobSize - 6;
            int knobX = 3 + (int)Math.Round(travel * _progress);
            Rectangle knob = new Rectangle(knobX, 3, knobSize, knobSize);

            using (SolidBrush brush = new SolidBrush(_progress > 0.5 ? Color.White : Theme.Text1))
            {
                g.FillEllipse(brush, knob);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _timer != null)
            {
                _timer.Stop();
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
