using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 卡片容器：圆角 + 1px 描边 + 悬停提亮，可带左侧风险色标条。
    /// 子控件建议使用绝对定位（S5 阶段再引入自适应流式布局）。
    /// 可被继承：功能卡片（FeatureCard）在它基础上加开关与数值输入。
    /// </summary>
    public class CardPanel : Panel
    {
        private bool _hover;
        private bool _showRiskBar;
        private Color _riskColor = Theme.Success;
        private string _title = "";
        private Font _titleFont;

        public CardPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            _titleFont = Theme.Subheading;
        }

        /// <summary>卡片标题。绘制在左上角，正文子控件需自行避开标题高度。</summary>
        public string Title
        {
            get { return _title; }
            set { _title = value == null ? "" : value; Invalidate(); }
        }

        /// <summary>是否显示左侧风险色标条。</summary>
        public bool ShowRiskBar
        {
            get { return _showRiskBar; }
            set { _showRiskBar = value; Invalidate(); }
        }

        public Color RiskColor
        {
            get { return _riskColor; }
            set { _riskColor = value; Invalidate(); }
        }

        /// <summary>标题所占高度，供子控件定位使用。</summary>
        public int TitleHeight
        {
            get { return string.IsNullOrEmpty(_title) ? 0 : 34; }
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
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg0);

            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            Color fill = _hover ? Theme.Mix(Theme.Bg1, Theme.Bg2, 0.75) : Theme.Bg1;
            Color border = _hover ? Theme.Mix(Theme.Border, Theme.Primary, 0.35) : Theme.Border;

            using (GraphicsPath path = Theme.RoundedRect(bounds, Theme.RadiusCard))
            {
                using (SolidBrush brush = new SolidBrush(fill))
                {
                    g.FillPath(brush, path);
                }
                using (Pen pen = new Pen(border, 1f))
                {
                    g.DrawPath(pen, path);
                }
            }

            if (_showRiskBar)
            {
                Rectangle bar = new Rectangle(1, Theme.RadiusCard / 2, 3, Height - Theme.RadiusCard);
                using (SolidBrush brush = new SolidBrush(_riskColor))
                {
                    g.FillRectangle(brush, bar);
                }
            }

            if (!string.IsNullOrEmpty(_title))
            {
                using (SolidBrush brush = new SolidBrush(Theme.Text0))
                {
                    g.DrawString(_title, _titleFont, brush, Theme.CardPadding + (_showRiskBar ? 6 : 0), 12f);
                }
            }

            base.OnPaint(e);
        }
    }
}
