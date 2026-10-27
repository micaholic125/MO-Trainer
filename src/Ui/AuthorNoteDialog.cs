using System;
using System.Drawing;
using System.Windows.Forms;
using MoTrainer.Ui.Controls;

namespace MoTrainer.Ui
{
    /// <summary>
    /// 启动时显示的「作者的话」。
    ///
    /// 2026-10-23 用户要求新增：把作者的联系方式与项目现状放在程序里，
    /// 而不是只写在 README / 发布页 —— 玩家拿到的是 exe，未必看得到那些地方。
    ///
    /// 两个入口：
    ///   ① 每次启动（除非用户勾了「不再弹窗显示」）；
    ///   ② 「关于」页的「作者的话」按钮 —— 勾掉之后仍然必须找得到，
    ///      否则这个设置就成了"一次性、找不回来"的坑。
    ///
    /// 勾选框的状态由调用方（MainForm）读写并落盘到 config\trainer.json，
    /// 本类不碰文件，只负责显示。
    /// </summary>
    public sealed class AuthorNoteDialog : Form
    {
        /// <summary>
        /// 正文**逐字**采用用户 2026-10-23 给出的原文；2026-10-27 用户指示只改
        /// 首句（项目已实际开源），其余一字未动。
        /// 不要"顺手润色"：这里写着作者的联系方式与项目状态，
        /// 改一个字都可能让玩家找到错误的信息。
        /// </summary>
        public const string NoteText =
            "作者的话：当你们读到这段话的时候，该项目已经开源了。"
            + "由于个人技术与设备缺陷，局域网联机最终未能制作并交付。"
            + "如果你们有更好的思路意见，或使用过程中遇到了bug与问题，请联系2622307388@qq.com"
            + "或在抖音号67664177780的视频下寻求帮助。"
            + "（github的信息我看的频率低，但我也会看的！）";

        private readonly CheckBox _suppress;
        private readonly Button _confirm;

        /// <param name="suppressed">当前是否已经勾选过「不再弹窗显示」，用来还原勾选框状态。</param>
        public AuthorNoteDialog(bool suppressed)
        {
            Text = "作者的话";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Theme.Bg0;
            ForeColor = Theme.Text0;
            Font = Theme.Body;
            DoubleBuffered = true;

            // 本程序是 DPI 感知的（app.manifest 里 true/pm）：字号按点自动放大，
            // 而像素尺寸不会。若启用 WinForms 的自动缩放，两者会叠加、字被放大两次。
            // 所以这里关掉自动缩放，所有像素度量自己按屏幕 DPI 换算（与 UiFactory 的思路一致）。
            AutoScaleMode = AutoScaleMode.None;

            int pad = Dpi(24);
            int contentWidth = Dpi(620);

            TableLayoutPanel root = new TableLayoutPanel();
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(pad);
            root.BackColor = Theme.Bg0;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, contentWidth));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label body = new Label();
            body.AutoSize = true;
            // 只限宽、不限高：长句就地折行，绝不会把窗口顶到屏幕外（CLAUDE.md §7.2）
            body.MaximumSize = new Size(contentWidth, 0);
            body.Font = Theme.Body;
            body.ForeColor = Theme.Text0;
            body.BackColor = Color.Transparent;
            body.Text = NoteText;
            body.Margin = new Padding(0);
            root.Controls.Add(body, 0, 0);

            _suppress = new CheckBox();
            _suppress.Text = "不再弹窗显示";
            _suppress.AutoSize = true;
            _suppress.FlatStyle = FlatStyle.Flat;
            _suppress.Font = Theme.Body;
            _suppress.ForeColor = Theme.Text1;
            _suppress.BackColor = Color.Transparent;
            _suppress.Checked = suppressed;
            _suppress.Margin = new Padding(0, Dpi(18), 0, Dpi(12));
            root.Controls.Add(_suppress, 0, 1);

            // 确认按钮钉在右下角。FlowDirection=RightToLeft 让按钮从右往左排，
            // 以后若还要加「复制邮箱」之类的按钮，顺序也不会意外反转。
            FlowLayoutPanel buttonRow = new FlowLayoutPanel();
            buttonRow.FlowDirection = FlowDirection.RightToLeft;
            buttonRow.WrapContents = false;
            buttonRow.AutoSize = false;
            buttonRow.Dock = DockStyle.Fill;
            buttonRow.Margin = new Padding(0);
            buttonRow.BackColor = Color.Transparent;

            _confirm = UiFactory.MakePrimaryButton("确认", Dpi(96));
            _confirm.Margin = new Padding(0);
            _confirm.DialogResult = DialogResult.OK;
            buttonRow.Controls.Add(_confirm);
            buttonRow.Height = _confirm.Height;
            root.Controls.Add(buttonRow, 0, 2);

            Controls.Add(root);

            // 量一次根容器的期望尺寸再定窗口大小。
            // 不用 Form.AutoSize：它与 Dock=Fill 的子容器在部分 DPI 下会互相打架，
            // 表现是窗口缩成一条或把正文切掉 —— 这正是本项目反复踩过的"字被切"。
            Size pref = root.GetPreferredSize(Size.Empty);
            ClientSize = new Size(pref.Width, pref.Height);

            AcceptButton = _confirm;
            // 只有一个"知道了"的动作，没有可取消的操作：Esc 与点「确认」等价。
            CancelButton = _confirm;
        }

        /// <summary>关闭时勾选框的状态，由调用方决定是否落盘。</summary>
        public bool SuppressChecked
        {
            get { return _suppress.Checked; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.EnableDarkTitleBar(this);
        }

        /// <summary>把 96 DPI 下的逻辑像素换算成本机像素。</summary>
        private static int Dpi(int pixels)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                return (int)Math.Round(pixels * (g.DpiX / 96f));
            }
        }
    }
}
