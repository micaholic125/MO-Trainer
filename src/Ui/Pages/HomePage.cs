using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using MoTrainer.Calibration;
using MoTrainer.Core;
using MoTrainer.Ui.Controls;
using MoTrainer.Util;

namespace MoTrainer.Ui.Pages
{
    /// <summary>
    /// 主控台页。S1 阶段承担"环境自检面板"职责：
    /// 实时显示连接状态、模块基址、MZ 签名校验，并可按需统计内存区域画像。
    /// 金钱 / 电力 / 无敌三张功能卡片将在 S4 阶段接入。
    /// </summary>
    public sealed class HomePage : UserControl
    {
        private readonly ProcessAttach _attach;
        private readonly MemoryIO _memory;
        private readonly CalibrationProfile _profile;
        private readonly Label _calibration;

        private readonly Label _valueProcess;
        private readonly Label _valueBase;
        private readonly Label _valueSize;
        private readonly Label _valueMz;
        private readonly Label _valueBaseCheck;
        private readonly Label _scanResult;
        private readonly Button _reAttachButton;
        private readonly Button _scanButton;

        private readonly ToolTip _toolTip = new ToolTip();

        private volatile bool _scanning;
        private string _pendingScanResult;

        public HomePage(ProcessAttach attach, MemoryIO memory, CalibrationProfile profile)
        {
            _attach = attach;
            _memory = memory;
            _profile = profile == null ? new CalibrationProfile() : profile;

            BackColor = Theme.Bg0;
            AutoScroll = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Top;
            root.AutoSize = true;
            root.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            root.ColumnCount = 1;
            root.RowCount = 6;
            // 右侧留出滚动条宽度，避免卡片边缘被滚动条压住
            root.Padding = new Padding(20, 16, 34, 18);
            root.BackColor = Color.Transparent;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));

            // ---------- 标题 ----------
            Label heading = new Label();
            heading.AutoSize = true;
            heading.Font = Theme.Heading;
            heading.ForeColor = Theme.Text0;
            heading.Text = "主控台";
            heading.Margin = new Padding(0, 0, 0, 2);
            root.Controls.Add(heading, 0, 0);

            Label sub = new Label();
            sub.AutoSize = true;
            sub.Font = Theme.Body;
            sub.ForeColor = Theme.Text2;
            sub.Text = "v0.2 开发版 · 阶段 S4：核心功能与锁定引擎（功能开关在「功能开关」页）";
            sub.Margin = new Padding(0, 0, 0, 6);
            root.Controls.Add(sub, 0, 1);

            // 校准状态：引导用户去「校准向导」完成一次性定位
            _calibration = new Label();
            _calibration.AutoSize = true;
            _calibration.Font = Theme.Body;
            _calibration.Margin = new Padding(0, 0, 0, 12);
            root.Controls.Add(_calibration, 0, 2);
            RefreshCalibration();

            // ---------- 游戏连接卡片 ----------
            CardPanel connectCard = new CardPanel();
            connectCard.Title = "游戏连接";
            connectCard.Dock = DockStyle.Fill;
            connectCard.Height = 194;
            connectCard.Margin = new Padding(0, 0, 0, 12);

            int y = connectCard.TitleHeight + 6;
            _valueProcess = AddRow(connectCard, "进程状态", ref y);
            _valueBase = AddRow(connectCard, "模块基址", ref y);
            _valueSize = AddRow(connectCard, "模块大小", ref y);
            _valueMz = AddRow(connectCard, "MZ 签名", ref y);
            _valueBaseCheck = AddRow(connectCard, "基址校验", ref y);

            _reAttachButton = MakeButton("重新附加");
            _reAttachButton.Location = new Point(Theme.CardPadding, y + 4);
            _reAttachButton.Width = 96;
            _reAttachButton.Click += OnReAttachClick;
            connectCard.Controls.Add(_reAttachButton);

            // 宽度必须按中文文案实际长度给足，否则最后一个字会被裁掉
            Button openDirButton = MakeButton("打开日志目录");
            openDirButton.Location = new Point(Theme.CardPadding + 104, y + 4);
            openDirButton.Width = 118;
            openDirButton.Click += OnOpenLogDirClick;
            connectCard.Controls.Add(openDirButton);

            root.Controls.Add(connectCard, 0, 3);

            // ---------- 内存内核自检卡片 ----------
            CardPanel scanCard = new CardPanel();
            scanCard.Title = "内存内核自检";
            scanCard.Dock = DockStyle.Fill;
            scanCard.Height = 146;
            scanCard.Margin = new Padding(0, 0, 0, 12);

            _scanButton = MakeButton("统计内存区域画像");
            _scanButton.Location = new Point(Theme.CardPadding, 44);
            _scanButton.Width = 152;
            _scanButton.Click += OnScanClick;
            scanCard.Controls.Add(_scanButton);

            // 结果分两行输出：单行过长时 GDI+ 会在卡片边缘直接截断，不给换行
            _scanResult = new Label();
            _scanResult.AutoSize = false;
            _scanResult.Font = Theme.Mono;
            _scanResult.ForeColor = Theme.Text1;
            _scanResult.Text = "尚未执行。\r\n用于验证区域枚举与可写页判定链路是否正常。";
            _scanResult.Location = new Point(Theme.CardPadding, 78);
            _scanResult.Size = new Size(700, 46);
            _scanResult.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            scanCard.Controls.Add(_scanResult);

            root.Controls.Add(scanCard, 0, 4);

            // ---------- 进度卡片 ----------
            CardPanel planCard = new CardPanel();
            planCard.Title = "阶段进度";
            planCard.Dock = DockStyle.Fill;

            string[] lines = new string[]
            {
                "S0 项目基建                          已完成",
                "S1 编译链路与内存内核                已完成",
                "S2 扫描与配置持久化                  已完成",
                "S3 校准向导                          已完成（金钱 / 电力已真机定位）",
                "S4 核心功能与锁定引擎                进行中（功能开关已可用）",
                "S5~S8 高级功能 · 联机 · 交付           未开始"
            };
            planCard.Height = 34 + 6 + lines.Length * 21 + 14;

            int ly = planCard.TitleHeight + 6;
            for (int i = 0; i < lines.Length; i++)
            {
                Label line = new Label();
                line.AutoSize = true;
                line.Font = Theme.Body;
                line.ForeColor = i == 4 ? Theme.PrimaryLight : Theme.Text1;
                line.Text = lines[i];
                line.Location = new Point(Theme.CardPadding + 6, ly);
                planCard.Controls.Add(line);
                ly += 21;
            }
            root.Controls.Add(planCard, 0, 5);

            Controls.Add(root);
        }

        private static Label AddRow(CardPanel card, string name, ref int y)
        {
            Label key = new Label();
            key.AutoSize = true;
            key.Font = Theme.Body;
            key.ForeColor = Theme.Text1;
            key.Text = name;
            key.Location = new Point(Theme.CardPadding + 6, y);
            card.Controls.Add(key);

            Label value = new Label();
            value.AutoSize = true;
            value.Font = Theme.Mono;
            value.ForeColor = Theme.Text0;
            value.Text = "—";
            value.Location = new Point(Theme.CardPadding + 110, y);
            card.Controls.Add(value);

            y += 22;
            return value;
        }

        /// <summary>把长错误信息压到一行能显示的长度，完整内容仍可从日志与悬停提示查看。</summary>
        private static string ShortenForUi(string message)
        {
            if (string.IsNullOrEmpty(message)) { return "未知错误"; }
            int stop = message.IndexOf('。');
            if (stop > 0 && stop <= 40) { return message.Substring(0, stop); }
            if (message.Length <= 40) { return message; }
            return message.Substring(0, 40) + "…";
        }

        private static Button MakeButton(string text)
        {
            Button button = new Button();
            button.Text = text;
            button.Font = Theme.Body;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = Theme.Border;
            button.ForeColor = Theme.Text0;
            button.BackColor = Theme.Bg2;
            button.Size = new Size(100, 30);
            button.Cursor = Cursors.Hand;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        /// <summary>刷新校准状态显示（校准完成后由主窗口调用）。</summary>
        public void RefreshCalibration()
        {
            if (_profile.IsCalibratedAtAll)
            {
                _calibration.ForeColor = Theme.Success;
                _calibration.Text = string.Format(CultureInfo.InvariantCulture,
                    "校准状态：已校准 {0} 项（{1}）", _profile.CalibratedItemCount(), _profile.Describe());
            }
            else
            {
                _calibration.ForeColor = Theme.Warning;
                _calibration.Text = "校准状态：未校准 —— 请到「校准向导」完成一次性定位";
            }
        }

        /// <summary>由主窗口的 1Hz 定时器调用。只做廉价读取。</summary>
        public void TickRefresh()
        {
            AttachState state = _attach.State;
            switch (state)
            {
                case AttachState.Attached:
                    _valueProcess.Text = string.Format(CultureInfo.InvariantCulture,
                        "已连接  pid={0}  {1}", _attach.ProcessId, _attach.ModuleName);
                    _valueProcess.ForeColor = Theme.Success;
                    _toolTip.SetToolTip(_valueProcess, _attach.Describe());
                    break;
                case AttachState.Error:
                    // 完整报错写进日志；界面上只放短提示，否则长句会被卡片边缘截断
                    if (_attach.LastErrorWasAccessDenied)
                    {
                        _valueProcess.Text = "连接失败：权限不足，请以管理员身份运行本程序";
                    }
                    else
                    {
                        _valueProcess.Text = "连接失败：" + ShortenForUi(_attach.LastError);
                    }
                    _valueProcess.ForeColor = Theme.Danger;
                    _toolTip.SetToolTip(_valueProcess, _attach.LastError);
                    break;
                default:
                    _valueProcess.Text = "未检测到 gamemd.exe，等待游戏启动…";
                    _valueProcess.ForeColor = Theme.Warning;
                    break;
            }

            if (state == AttachState.Attached)
            {
                uint baseAddr = _attach.ModuleBase;
                _valueBase.Text = string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", baseAddr);
                _valueSize.Text = string.Format(CultureInfo.InvariantCulture, "0x{0:X}  ({1:N0} 字节)",
                    _attach.ModuleSize, _attach.ModuleSize);
                _valueBaseCheck.Text = _attach.ImageBaseAsExpected
                    ? "0x00400000 与预期一致"
                    : "与预期 0x00400000 不一致（见日志）";
                _valueBaseCheck.ForeColor = _attach.ImageBaseAsExpected ? Theme.Success : Theme.Warning;

                byte[] header = _memory.ReadBytes(baseAddr, 2);
                if (header != null && header[0] == (byte)'M' && header[1] == (byte)'Z')
                {
                    _valueMz.Text = "通过（读取到 MZ，映像可正常访问）";
                    _valueMz.ForeColor = Theme.Success;
                }
                else
                {
                    _valueMz.Text = "失败：无法读取映像头";
                    _valueMz.ForeColor = Theme.Danger;
                }
            }
            else
            {
                _valueBase.Text = "—";
                _valueSize.Text = "—";
                _valueMz.Text = "—";
                _valueMz.ForeColor = Theme.Text0;
                _valueBaseCheck.Text = "—";
                _valueBaseCheck.ForeColor = Theme.Text0;
            }

            string done = _pendingScanResult;
            if (done != null)
            {
                _pendingScanResult = null;
                _scanResult.Text = done;
            }
        }

        private void OnReAttachClick(object sender, EventArgs e)
        {
            Logger.Info("Home", "用户手动触发重新附加");
            _attach.TryAttach();
            TickRefresh();
        }

        private void OnOpenLogDirClick(object sender, EventArgs e)
        {
            try
            {
                string dir = Logger.LogDirectory;
                if (!string.IsNullOrEmpty(dir))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Home", "打开日志目录失败：" + ex.Message);
            }
        }

        private void OnScanClick(object sender, EventArgs e)
        {
            if (_scanning) { return; }
            if (!_attach.IsReady)
            {
                _scanResult.Text = "当前未连接游戏，无法统计。请先启动游戏。";
                return;
            }

            _scanning = true;
            _scanButton.Enabled = false;
            _scanResult.Text = "正在统计内存区域…";

            Thread worker = new Thread(new ThreadStart(ScanWorker));
            worker.IsBackground = true;
            worker.Priority = ThreadPriority.BelowNormal;
            worker.Start();
        }

        private void ScanWorker()
        {
            try
            {
                DateTime started = DateTime.Now;
                List<MemoryRegion> regions = _memory.EnumRegions(
                    0x00010000ul, Win32Api.MaxUserAddress32, true, false, 0);

                ulong writableTotal = 0;
                int writableCount = 0;
                int privateCount = 0;
                for (int i = 0; i < regions.Count; i++)
                {
                    MemoryRegion r = regions[i];
                    if (r.Writable)
                    {
                        writableTotal += r.Size;
                        writableCount++;
                    }
                    if (r.Type == Win32Api.MEM_PRIVATE) { privateCount++; }
                }

                double elapsed = (DateTime.Now - started).TotalMilliseconds;
                string text = string.Format(CultureInfo.InvariantCulture,
                    "已提交区域 {0} 个（私有 {1} 个）\r\n可写区域 {2} 个 · 合计 {3:N0} MB · 耗时 {4:N0} ms",
                    regions.Count, privateCount, writableCount,
                    writableTotal / 1048576.0, elapsed);

                Logger.Info("Home", "内存区域画像：" + text);
                _pendingScanResult = text;
            }
            catch (Exception ex)
            {
                Logger.Error("Home", "内存区域统计失败", ex);
                _pendingScanResult = "统计失败：" + ex.Message;
            }
            finally
            {
                _scanning = false;
                try
                {
                    if (IsHandleCreated)
                    {
                        BeginInvoke(new Action(OnScanCompleted));
                    }
                }
                catch
                {
                    // 页面销毁中，忽略
                }
            }
        }

        private void OnScanCompleted()
        {
            _scanButton.Enabled = true;
        }
    }
}
