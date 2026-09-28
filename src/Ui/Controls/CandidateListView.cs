using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using MoTrainer.Core;

namespace MoTrainer.Ui.Controls
{
    /// <summary>
    /// 候选地址列表：自绘深色行，选中行高亮。
    /// 地址用等宽字体，数值右对齐，方便一眼比对。
    /// </summary>
    public sealed class CandidateListView : UserControl
    {
        private const int RowHeight = 20;

        private readonly ListBox _list;
        private List<ScanCandidate> _items = new List<ScanCandidate>();
        private uint _highlightAddress;
        private int _highlightIndex = -1;

        public CandidateListView()
        {
            BackColor = Theme.Bg0;
            SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

            _list = new ListBox();
            _list.Dock = DockStyle.Fill;
            _list.BorderStyle = BorderStyle.None;
            _list.BackColor = Theme.Bg1;
            _list.ForeColor = Theme.Text1;
            _list.Font = Theme.Mono;
            _list.DrawMode = DrawMode.OwnerDrawFixed;
            _list.ItemHeight = RowHeight;
            _list.IntegralHeight = false;
            _list.HorizontalScrollbar = false;
            _list.DrawItem += OnDrawItem;
            _list.SelectedIndexChanged += delegate
            {
                EventHandler handler = SelectedIndexChanged;
                if (handler != null) { handler(this, EventArgs.Empty); }
            };
            Controls.Add(_list);
        }

        /// <summary>当前选中的候选下标；没有选中返回 -1。</summary>
        public int SelectedIndex
        {
            get { return _list.SelectedIndex; }
            set
            {
                if (value < 0 || value >= _list.Items.Count) { _list.ClearSelected(); return; }
                _list.SelectedIndex = value;
            }
        }

        public ScanCandidate SelectedItem
        {
            get
            {
                int index = _list.SelectedIndex;
                if (index < 0 || index >= _items.Count) { return null; }
                return _items[index];
            }
        }

        public event EventHandler SelectedIndexChanged;

        /// <summary>填入候选。highlightAddress 非 0 时该行会加一个标记。</summary>
        public void SetItems(List<ScanCandidate> items, uint highlightAddress)
        {
            _items = items == null ? new List<ScanCandidate>() : new List<ScanCandidate>(items);
            _highlightAddress = highlightAddress;

            _highlightIndex = -1;
            if (highlightAddress != 0)
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i].Address == highlightAddress) { _highlightIndex = i; break; }
                }
            }

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                for (int i = 0; i < _items.Count; i++) { _list.Items.Add(_items[i]); }
                if (_list.Items.Count > 0) { _list.SelectedIndex = 0; }
            }
            finally
            {
                _list.EndUpdate();
            }
        }

        public void ClearItems()
        {
            SetItems(null, 0);
        }

        private void OnDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= _items.Count) { return; }

            e.DrawBackground();

            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            Color background = selected ? Theme.Mix(Theme.Bg2, Theme.Primary, 0.25) : Theme.Bg1;
            using (SolidBrush brush = new SolidBrush(background))
            {
                e.Graphics.FillRectangle(brush, e.Bounds);
            }

            ScanCandidate candidate = _items[e.Index];
            bool marked = _highlightIndex == e.Index;

            Rectangle bounds = e.Bounds;
            using (SolidBrush brush = new SolidBrush(marked ? Theme.PrimaryLight : Theme.Text2))
            {
                e.Graphics.DrawString(
                    string.Format(CultureInfo.InvariantCulture, "#{0}", e.Index + 1),
                    Theme.Mono, brush, bounds.Left + 6, bounds.Top + 2);
            }

            using (SolidBrush brush = new SolidBrush(selected ? Theme.Text0 : Theme.Text1))
            {
                e.Graphics.DrawString(
                    string.Format(CultureInfo.InvariantCulture, "0x{0:X8}", candidate.Address),
                    Theme.Mono, brush, bounds.Left + 52, bounds.Top + 2);
            }

            string right = marked
                ? string.Format(CultureInfo.InvariantCulture, "当前值 {0}  ← 当前定位", candidate.PreviousValue)
                : string.Format(CultureInfo.InvariantCulture, "当前值 {0}", candidate.PreviousValue);

            using (SolidBrush brush = new SolidBrush(marked ? Theme.PrimaryLight : Theme.Text1))
            {
                e.Graphics.DrawString(right, Theme.Mono, brush, bounds.Left + 170, bounds.Top + 2);
            }

            // 备注列：对象内部字段靠"前后差异"定位，只有"当前值"没法判断谁是真字段，
            // 必须把"从多少掉到多少、掉了百分之多少"一起摆出来供人工判读。
            if (!string.IsNullOrEmpty(candidate.Note))
            {
                using (SolidBrush brush = new SolidBrush(marked ? Theme.PrimaryLight : Theme.Text2))
                {
                    e.Graphics.DrawString(candidate.Note, Theme.Mono, brush, bounds.Left + 330, bounds.Top + 2);
                }
            }
        }
    }
}
