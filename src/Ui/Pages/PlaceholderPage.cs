using System;
using System.Drawing;
using System.Windows.Forms;
using MoTrainer.Ui.Controls;

namespace MoTrainer.Ui.Pages
{
    /// <summary>
    /// 尚未实现的页面占位。明确写出"由哪个阶段实现"，避免用户误以为功能缺失。
    /// </summary>
    public sealed class PlaceholderPage : UserControl
    {
        public PlaceholderPage(string title, string stage, string summary, string[] plannedItems)
        {
            BackColor = Theme.Bg0;
            AutoScroll = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.Padding = new Padding(20, 16, 34, 18);
            root.BackColor = Color.Transparent;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Label heading = new Label();
            heading.AutoSize = true;
            heading.Font = Theme.Heading;
            heading.ForeColor = Theme.Text0;
            heading.Text = title;
            heading.Margin = new Padding(0, 0, 0, 2);
            root.Controls.Add(heading, 0, 0);

            Label sub = new Label();
            sub.AutoSize = true;
            sub.Font = Theme.Body;
            sub.ForeColor = Theme.Text2;
            sub.Text = "计划实现阶段：" + stage;
            sub.Margin = new Padding(0, 0, 0, 14);
            root.Controls.Add(sub, 0, 1);

            CardPanel card = new CardPanel();
            card.Title = summary;
            card.Dock = DockStyle.Fill;
            card.Height = 60 + plannedItems.Length * 24;

            int y = card.TitleHeight + 8;
            for (int i = 0; i < plannedItems.Length; i++)
            {
                Label item = new Label();
                item.AutoSize = true;
                item.Font = Theme.Body;
                item.ForeColor = Theme.Text1;
                item.Text = "·  " + plannedItems[i];
                item.Location = new Point(Theme.CardPadding + 6, y);
                card.Controls.Add(item);
                y += 24;
            }
            root.Controls.Add(card, 0, 2);

            Controls.Add(root);
        }
    }
}
