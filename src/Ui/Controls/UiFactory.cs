using System.Drawing;
using System.Windows.Forms;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 深色扁平风格的控件工厂。
    /// 所有按钮 / 标签的外观集中在这里，避免各页面各写一套。
    /// </summary>
    public static class UiFactory
    {
        public const int ButtonHeight = 32;

        /// <summary>
        /// 给页面里所有「自动宽度」的 Label 设一个最大宽度，让长句**就地折行**
        /// 而不是一直往宽里长。
        ///
        /// 为什么必须做：页面用 `TableLayoutPanel + AutoSize + Dock=Top` 承载内容，
        /// 而 `AutoSize=true` 的 Label 有多长就占多宽 —— 一句长文案会把整张表的
        /// **期望宽度**顶大，于是 `AutoScroll` 面板冒出横向滚动条，右侧内容被滚出视野。
        /// 用户实测：1000 宽的窗口下，勾选框文字、卡片右边缘、日志按钮全被切掉；
        /// 1920 宽时装得下，所以看起来"只有小窗口才有问题"—— 实为同一原因。
        ///
        /// 关键点：按 **Label 相对页面的剩余宽度** 来限，而不是按父容器宽度 ——
        /// 父容器很可能正是那个已经被撑大的 TableLayoutPanel，按它算等于没限制。
        /// `MaximumSize.Height = 0` 表示不限高，只限宽。
        /// </summary>
        public static void ClampLabelWidths(Control page)
        {
            if (page == null || page.ClientSize.Width <= 0) { return; }
            ClampLabelWidthsCore(page, page, page.ClientSize.Width);
        }

        private static void ClampLabelWidthsCore(Control parent, Control page, int pageWidth)
        {
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                Control child = parent.Controls[i];

                // 对所有**自动尺寸**的控件统一限宽，不只是 Label。
                // 页面的期望宽度由它最宽的子孙决定，而 AutoSize 的 Panel / TableLayoutPanel
                // 同样会"有内容多宽就长多宽" —— 只治 Label 治不干净（用户实测：日志里
                // 仍然报「横条=有」，说明页面还被别的东西顶着）。
                // MaximumSize(可用宽, 0) 的语义是"只限宽、不限高"：控件照旧纵向自适应，只是不再横向溢出。
                if (child.AutoSize)
                {
                    int left = OffsetFromPage(child, page);
                    int available = pageWidth - left - child.Margin.Horizontal - 8;
                    if (available < 120) { available = 120; }

                    Size want = new Size(available, 0);
                    if (child.MaximumSize != want) { child.MaximumSize = want; }
                }

                if (child.Controls.Count > 0)
                {
                    ClampLabelWidthsCore(child, page, pageWidth);
                }
            }
        }

        private static int OffsetFromPage(Control control, Control page)
        {
            int left = 0;
            Control current = control;
            while (current != null && current != page)
            {
                left += current.Left;
                current = current.Parent;
            }
            return left;
        }

        /// <summary>次级按钮：深灰底 + 描边，用于常规操作。</summary>
        public static Button MakeButton(string text, int width)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = Theme.Body;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Theme.Border;
            button.FlatAppearance.MouseOverBackColor = Theme.Bg2;
            button.FlatAppearance.MouseDownBackColor = Theme.Bg1;
            button.ForeColor = Theme.Text0;
            button.BackColor = Theme.Bg2;

            // 宽度不得小于文字实际所需，高度不得小于字体所需。
            //
            // 真机根因：用户显示器是高 DPI 缩放（窗口宽 2560），**字号随之放大，
            // 而这里的宽度是写死的物理像素** —— 100 像素装不下放大后的「打开日志目录」，
            // 只显示成「打开日」。日志里那些坐标全都是对的，问题出在"像素不跟着缩放走"。
            //
            // 量出文字真实宽度/高度后取大值，任何 DPI 缩放下都不会切字。
            Size textSize = TextRenderer.MeasureText(text, button.Font);
            int finalWidth = width > textSize.Width + 24 ? width : textSize.Width + 24;
            int finalHeight = ButtonHeight > textSize.Height + 12 ? ButtonHeight : textSize.Height + 12;
            button.Size = new Size(finalWidth, finalHeight);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        /// <summary>主操作按钮：强调色填充。</summary>
        public static Button MakePrimaryButton(string text, int width)
        {
            Button button = MakeButton(text, width);
            button.Font = Theme.BodyBold;
            button.BackColor = Theme.Primary;
            button.ForeColor = Color.White;
            button.FlatAppearance.BorderColor = Theme.PrimaryDark;
            button.FlatAppearance.MouseOverBackColor = Theme.PrimaryLight;
            button.FlatAppearance.MouseDownBackColor = Theme.PrimaryDark;
            return button;
        }

        /// <summary>危险操作按钮：红色描边。</summary>
        public static Button MakeDangerButton(string text, int width)
        {
            Button button = MakeButton(text, width);
            button.BackColor = Theme.Bg2;
            button.ForeColor = Theme.Danger;
            button.FlatAppearance.BorderColor = Theme.Danger;
            button.FlatAppearance.MouseOverBackColor = Theme.Bg1;
            return button;
        }

        public static Label MakeLabel(string text, Font font, Color color)
        {
            Label label = new Label();
            label.AutoSize = true;
            label.Font = font;
            label.ForeColor = color;
            label.BackColor = Color.Transparent;
            label.Text = text;
            return label;
        }
    }
}
