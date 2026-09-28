using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using MoTrainer.Util;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 底部日志滚动区：分级着色、自动滚到底、最多保留 500 行。
    /// 订阅 Logger.OnLog，并自动切回 UI 线程。
    /// </summary>
    public sealed class LogView : UserControl
    {
        private const int MaxLines = 500;
        private const int RowHeight = 18;

        private readonly ListBox _list;
        private readonly List<LogEntry> _entries = new List<LogEntry>();
        private bool _subscribed;

        public LogView()
        {
            BackColor = Theme.Bg0;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);

            _list = new ListBox();
            _list.Dock = DockStyle.Fill;
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Theme.Bg0;
            _list.ForeColor = Theme.Text1;
            _list.Font = Theme.Mono;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            // 行高必须由字体实测决定，不能用常量。
            // 高 DPI 下字号被放大而常量不变，日志每行只显示文字上半截（用户实测）。
            // 所有者绘制的列表项高度**不会**随 DPI 自动缩放，只能自己算。
            _list.ItemHeight = Math.Max(RowHeight, _list.Font.Height + 6);
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = false;
            _list.TabStop = false;
            _list.DrawItem += OnDrawItem;
            Controls.Add(_list);
        }

        /// <summary>开始接收日志。幂等。</summary>
        public void Subscribe()
        {
            if (_subscribed) { return; }
            Logger.OnLog += OnLogReceived;
            _subscribed = true;
        }

        public void Unsubscribe()
        {
            if (!_subscribed) { return; }
            Logger.OnLog -= OnLogReceived;
            _subscribed = false;
        }

        private void OnLogReceived(LogEntry entry)
        {
            if (entry == null) { return; }
            try
            {
                if (IsHandleCreated && InvokeRequired)
                {
                    BeginInvoke(new Action<LogEntry>(Append), entry);
                }
                else
                {
                    Append(entry);
                }
            }
            catch (Exception)
            {
                // 窗体销毁过程中可能抛 InvalidOperationException，忽略即可
            }
        }

        public void Append(LogEntry entry)
        {
            _entries.Add(entry);
            _list.Items.Add(entry);

            while (_entries.Count > MaxLines)
            {
                _entries.RemoveAt(0);
                _list.Items.RemoveAt(0);
            }
            // 总是在底部，符合"日志跟随"的直觉
            if (_list.Items.Count > 0)
            {
                _list.TopIndex = _list.Items.Count - 1;
            }
        }

        /// <summary>
        /// 当前面板里保留的全部日志（复制一份）。
        /// 供"导出日志"使用 —— 面板只显示得下十几行，导出必须拿到完整内容。
        /// </summary>
        public List<LogEntry> Snapshot()
        {
            return new List<LogEntry>(_entries);
        }

        private void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _list.Items.Count) { return; }

            e.DrawBackground();

            LogEntry entry = _list.Items[e.Index] as LogEntry;
            if (entry == null) { return; }

            string text = string.Format("{0:HH:mm:ss}  {1}", entry.Time, entry.Message);
            Color color;
            switch (entry.Level)
            {
                case LogLevel.Debug: color = Theme.Text2; break;
                case LogLevel.Info: color = Theme.Text1; break;
                case LogLevel.Warn: color = Theme.Warning; break;
                default: color = Theme.Danger; break;
            }

            using (SolidBrush brush = new SolidBrush(color))
            {
                e.Graphics.DrawString(text, _list.Font, brush, e.Bounds.Left + 2, e.Bounds.Top + 2);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Unsubscribe(); }
            base.Dispose(disposing);
        }
    }
}
