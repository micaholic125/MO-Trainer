using System;
using System.Drawing;
using System.Windows.Forms;
using MoTrainer.Ui.Controls;
using MoTrainer.Util;

namespace MoTrainer.Ui.Pages
{
    /// <summary>
    /// 「关于」页：版本与使用须知，外加一个「作者的话」入口。
    ///
    /// 2026-10-23 从 PlaceholderPage 改为独立页面：启动弹窗可以被勾选"不再显示"，
    /// 但那段留言**不能因此变得找不回来** —— 这里就是它固定的入口。
    /// 顺带去掉占位页那句没有意义的"计划实现阶段：—"。
    /// </summary>
    public sealed class AboutPage : UserControl
    {
        private readonly Button _authorNote;

        /// <summary>用户点了「作者的话」。由 MainForm 负责弹出对话框。</summary>
        public event EventHandler AuthorNoteRequested;

        public AboutPage()
        {
            BackColor = Theme.Bg0;
            AutoScroll = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 1;
            root.RowCount = 4;
            root.Padding = new Padding(20, 16, 34, 18);
            root.BackColor = Color.Transparent;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            Label heading = new Label();
            heading.AutoSize = true;
            heading.Font = Theme.Heading;
            heading.ForeColor = Theme.Text0;
            heading.Text = "关于";
            heading.Margin = new Padding(0, 0, 0, 2);
            root.Controls.Add(heading, 0, 0);

            Label sub = new Label();
            sub.AutoSize = true;
            sub.Font = Theme.Body;
            sub.ForeColor = Theme.Text2;
            sub.Text = "MO Trainer v1.0 · 使用须知与联系方式";
            sub.Margin = new Padding(0, 0, 0, 14);
            root.Controls.Add(sub, 0, 1);

            string[] items = new string[]
            {
                "MO Trainer v1.0 · 目标游戏：Mental Omega 3.3.6",
                "功能：金钱 / 电力 / 选中单位无敌 / 秒杀敌人 / 瞬间建造",
                "适用场景：单人战役 / 遭遇战打 AI / 自建局域网对局",
                "禁止用于公网 CnCNet 对战；局域网对局中开启高风险功能可能掉线",
                "本工具只读写游戏内存，不修改任何游戏文件",
                // 构建时间：程序会被反复重建，这一行用来确认"手里这份是不是最新的"。
                // （"跑到旧版本"真的发生过，见 Util\BuildInfo.cs 的注释）
                "构建时间：" + BuildInfo.Text
            };

            CardPanel card = new CardPanel();
            card.Title = "版本与使用须知";
            card.Dock = DockStyle.Fill;
            card.Height = 60 + items.Length * 24;

            int y = card.TitleHeight + 8;
            for (int i = 0; i < items.Length; i++)
            {
                Label item = new Label();
                item.AutoSize = true;
                item.Font = Theme.Body;
                item.ForeColor = Theme.Text1;
                item.Text = "·  " + items[i];
                item.Location = new Point(Theme.CardPadding + 6, y);
                card.Controls.Add(item);
                y += 24;
            }
            root.Controls.Add(card, 0, 2);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.FlowDirection = FlowDirection.LeftToRight;
            actions.WrapContents = false;
            actions.AutoSize = true;
            actions.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            actions.Margin = new Padding(0, 12, 0, 0);
            actions.BackColor = Color.Transparent;

            _authorNote = UiFactory.MakeButton("作者的话", 120);
            _authorNote.Click += OnAuthorNoteClick;
            actions.Controls.Add(_authorNote);

            Label hint = UiFactory.MakeLabel(
                "点这里可以重新查看启动时弹出的留言。", Theme.Small, Theme.Text2);
            // 与按钮文字基线大致对齐；高度交给内容和字体决定
            hint.Margin = new Padding(10, 10, 0, 0);
            actions.Controls.Add(hint);

            root.Controls.Add(actions, 0, 3);
            Controls.Add(root);
        }

        /// <summary>供自检确认入口确实存在（玩家找得到的最重要保证）。</summary>
        public Button AuthorNoteButton
        {
            get { return _authorNote; }
        }

        private void OnAuthorNoteClick(object sender, EventArgs e)
        {
            EventHandler handler = AuthorNoteRequested;
            if (handler != null) { handler(this, EventArgs.Empty); }
        }
    }
}
