using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 横向步骤指示器：圆点 + 连接线 + 下方步骤名。
    /// 已完成步骤填强调色，当前步骤额外带一圈光晕。
    /// </summary>
    public sealed class StepIndicator : Control
    {
        private string[] _steps = new string[0];
        private int _current;

        private const int DotRadius = 7;
        private const int DotCenterY = 16;
        private const int LabelTop = 28;

        public StepIndicator()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg0;
            Height = 54;
        }

        public void SetSteps(string[] steps, int currentIndex)
        {
            _steps = steps == null ? new string[0] : steps;
            _current = currentIndex;
            Invalidate();
        }

        public int CurrentIndex
        {
            get { return _current; }
            set
            {
                if (_current == value) { return; }
                _current = value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg0);

            if (_steps.Length == 0) { return; }

            int count = _steps.Length;
            float slot = (float)Width / count;
            float[] centers = new float[count];
            for (int i = 0; i < count; i++)
            {
                centers[i] = slot * i + slot / 2f;
            }

            // 连接线：已完成段用强调色，未完成段用描边色
            for (int i = 0; i < count - 1; i++)
            {
                Color lineColor = i < _current ? Theme.Primary : Theme.Border;
                using (Pen pen = new Pen(lineColor, 2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    g.DrawLine(pen, centers[i] + DotRadius + 4, DotCenterY, centers[i + 1] - DotRadius - 4, DotCenterY);
                }
            }

            for (int i = 0; i < count; i++)
            {
                bool done = i < _current;
                bool active = i == _current;

                if (active)
                {
                    using (SolidBrush glow = new SolidBrush(Color.FromArgb(55, Theme.Primary)))
                    {
                        g.FillEllipse(glow, centers[i] - DotRadius - 5, DotCenterY - DotRadius - 5,
                            (DotRadius + 5) * 2, (DotRadius + 5) * 2);
                    }
                }

                RectangleF dot = new RectangleF(
                    centers[i] - DotRadius, DotCenterY - DotRadius, DotRadius * 2, DotRadius * 2);

                if (done || active)
                {
                    using (SolidBrush brush = new SolidBrush(active ? Theme.Primary : Theme.PrimaryDark))
                    {
                        g.FillEllipse(brush, dot);
                    }
                }
                else
                {
                    using (SolidBrush brush = new SolidBrush(Theme.Bg2))
                    {
                        g.FillEllipse(brush, dot);
                    }
                    using (Pen pen = new Pen(Theme.Border, 1.5f))
                    {
                        g.DrawEllipse(pen, dot);
                    }
                }

                // 已完成步骤画一个对勾，比纯色点更直观
                if (done)
                {
                    using (Pen pen = new Pen(Color.White, 1.8f))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        float cx = centers[i];
                        g.DrawLine(pen, cx - 3f, DotCenterY, cx - 1f, DotCenterY + 2.5f);
                        g.DrawLine(pen, cx - 1f, DotCenterY + 2.5f, cx + 3.5f, DotCenterY - 2.5f);
                    }
                }

                Color textColor = active ? Theme.Text0 : (done ? Theme.Text1 : Theme.Text2);
                using (SolidBrush brush = new SolidBrush(textColor))
                {
                    using (StringFormat format = new StringFormat())
                    {
                        format.Alignment = StringAlignment.Center;
                        format.LineAlignment = StringAlignment.Near;
                        RectangleF area = new RectangleF(centers[i] - slot / 2f, LabelTop, slot, 20f);
                        g.DrawString(_steps[i], active ? Theme.BodyBold : Theme.Small, brush, area, format);
                    }
                }
            }
        }
    }
}
