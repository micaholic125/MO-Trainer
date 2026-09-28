using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 深色主题输入框：自绘圆角描边，获得焦点时描边变强调色。
    /// 系统的 TextBox 无法直接改边框颜色，因此外面套一层自绘容器。
    /// </summary>
    public sealed class DarkTextBox : UserControl
    {
        private readonly TextBox _inner;
        private bool _focused;
        private bool _digitsOnly;
        private string _placeholder = "";

        public DarkTextBox()
        {
            // 必须先把内部输入框建好：下面设置 Size 会立刻触发 OnResize，
            // 而 OnResize 需要读取 _inner，先设尺寸会直接空引用崩溃。
            _inner = new TextBox();
            _inner.BorderStyle = BorderStyle.None;
            _inner.BackColor = Theme.Bg2;
            _inner.ForeColor = Theme.Text0;
            _inner.Font = Theme.Body;
            _inner.Dock = DockStyle.Fill;
            _inner.GotFocus += delegate { _focused = true; Invalidate(); };
            _inner.LostFocus += delegate { _focused = false; Invalidate(); };
            _inner.TextChanged += delegate { OnTextChanged(EventArgs.Empty); };
            _inner.KeyPress += OnInnerKeyPress;

            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg0;
            Padding = new Padding(12, 6, 12, 6);
            Controls.Add(_inner);
            Size = new Size(160, 32);
        }

        public TextBox Inner { get { return _inner; } }

        /// <summary>只允许输入数字。</summary>
        public bool DigitsOnly
        {
            get { return _digitsOnly; }
            set { _digitsOnly = value; }
        }

        public string Placeholder
        {
            get { return _placeholder; }
            set { _placeholder = value == null ? "" : value; Invalidate(); }
        }

        public override string Text
        {
            get { return _inner.Text; }
            set { _inner.Text = value; }
        }

        public void SelectAllText()
        {
            _inner.SelectAll();
            _inner.Focus();
        }

        public void ClearText()
        {
            _inner.Text = "";
        }

        /// <summary>按整数解析当前文本；空或非法返回 false。</summary>
        public bool TryGetUInt32(out uint value)
        {
            value = 0;
            string text = _inner.Text == null ? "" : _inner.Text.Trim();
            if (text.Length == 0) { return false; }
            return uint.TryParse(text, out value);
        }

        private void OnInnerKeyPress(object sender, KeyPressEventArgs e)
        {
            if (!_digitsOnly) { return; }
            if (char.IsControl(e.KeyChar)) { return; }
            if (char.IsDigit(e.KeyChar)) { return; }
            e.Handled = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg0);

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath path = Theme.RoundedRect(bounds, 6))
            {
                using (SolidBrush brush = new SolidBrush(Theme.Bg2))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(_focused ? Theme.Primary : Theme.Border, _focused ? 1.6f : 1f))
                {
                    g.DrawPath(pen, path);
                }
            }

            base.OnPaint(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_inner == null) { return; }
            // 让内部输入框避开自绘描边，垂直居中
            int top = (Height - _inner.PreferredHeight) / 2;
            if (top < 1) { top = 1; }
            Padding = new Padding(12, top, 12, 0);
        }
    }
}
