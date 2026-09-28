using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 左侧导航项。自绘以获得稳定的深色扁平观感（Button 的 FlatStyle 在各系统上表现不一致）。
    /// </summary>
    public sealed class NavButton : Control
    {
        private bool _hover;
        private bool _selected;

        public NavButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg1;
            Font = Theme.Body;
            Height = 44;
            Cursor = Cursors.Hand;
        }

        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected == value) { return; }
                _selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg1);

            Color background;
            if (_selected) { background = Theme.Bg2; }
            else if (_hover) { background = Theme.Mix(Theme.Bg1, Theme.Bg2, 0.5); }
            else { background = Theme.Bg1; }

            using (SolidBrush brush = new SolidBrush(background))
            {
                g.FillRectangle(brush, ClientRectangle);
            }

            if (_selected)
            {
                using (SolidBrush brush = new SolidBrush(Theme.Primary))
                {
                    g.FillRectangle(brush, new Rectangle(0, 0, 3, Height));
                }
            }

            Color textColor = _selected ? Theme.Text0 : (_hover ? Theme.Text0 : Theme.Text1);
            using (SolidBrush brush = new SolidBrush(textColor))
            {
                using (StringFormat format = new StringFormat())
                {
                    format.LineAlignment = StringAlignment.Center;
                    format.Alignment = StringAlignment.Near;
                    g.DrawString(Text, Font, brush, new RectangleF(20f, 0f, Width - 28f, Height), format);
                }
            }

            base.OnPaint(e);
        }
    }
}
