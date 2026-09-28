// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using TortoiseSCM.Core;

namespace TortoiseSCM
{
    internal sealed class TextDiffForm : Form
    {
        private readonly TextComparisonResult comparison;
        private readonly TextLinePane left;
        private readonly TextLinePane right;
        private readonly Label status = new Label();
        private int currentDifference = -1;

        public TextDiffForm(string leftPath, string rightPath)
        {
            TextDocument leftDocument = TextDocument.Load(leftPath);
            TextDocument rightDocument = TextDocument.Load(rightPath);
            comparison = TextComparison.Compare(leftDocument, rightDocument);
            DialogStyle.Apply(this);
            Text = "比较文件 - TortoiseSCM";
            Size = new Size(1120, 740); MinimumSize = new Size(820, 480);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            var previous = EditorUi.Button("上一个差异", delegate { Navigate(-1); });
            var next = EditorUi.Button("下一个差异", delegate { Navigate(1); });
            previous.Enabled = next.Enabled = comparison.Hunks.Count != 0;
            toolbar.Controls.Add(previous); toolbar.Controls.Add(next);
            toolbar.Controls.Add(new Label { Text = "只读  |  - 删除   + 新增   ~ 修改", AutoSize = true, Margin = new Padding(12, 6, 0, 0) });
            layout.Controls.Add(toolbar, 0, 0);
            var columns = EditorUi.Columns(2);
            var leftRows = new List<TextPaneLine>(); var rightRows = new List<TextPaneLine>();
            foreach (TextDiffRow row in comparison.Rows)
            {
                bool changed = row.Kind != TextDiffKind.Equal;
                leftRows.Add(new TextPaneLine(row.LeftLineNumber, row.LeftText, changed, row.RightLineNumber == 0 ? "-" : "~"));
                rightRows.Add(new TextPaneLine(row.RightLineNumber, row.RightText, changed, row.LeftLineNumber == 0 ? "+" : "~"));
            }
            left = new TextLinePane("左侧 / 原始文件", leftPath, leftDocument, leftRows);
            right = new TextLinePane("右侧 / 修改文件", rightPath, rightDocument, rightRows);
            columns.Controls.Add(left, 0, 0); columns.Controls.Add(right, 1, 0);
            EditorUi.Synchronize(left, right);
            layout.Controls.Add(columns, 0, 1);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.Text = Summary(); layout.Controls.Add(status, 0, 2);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); }; footer.Controls.Add(close);
            layout.Controls.Add(footer, 0, 3); Controls.Add(layout); CancelButton = close;
            KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.F7) { Navigate(e.Shift ? -1 : 1); e.Handled = true; } };
        }

        private string Summary()
        {
            return comparison.Hunks.Count + " 个文本差异块" + (comparison.HasEncodingChanges ? " | 编码/BOM 不同" : "") + (comparison.HasLineEndingChanges ? " | 行尾不同" : "") + (comparison.IsApproximate ? " | 大文件：使用粗略行对齐" : "") + " | F7：下一个；Shift+F7：上一个";
        }

        internal void Navigate(int direction)
        {
            if (comparison.Hunks.Count == 0) return;
            currentDifference = currentDifference < 0 ? (direction < 0 ? comparison.Hunks.Count - 1 : 0) : (currentDifference + direction + comparison.Hunks.Count) % comparison.Hunks.Count;
            int row = comparison.Hunks[currentDifference].StartRow;
            left.GoToRow(row); right.GoToRow(row);
            status.Text = "差异 " + (currentDifference + 1) + "/" + comparison.Hunks.Count + " | " + Summary();
        }
    }

    internal sealed class TextPaneLine
    {
        internal readonly int Number;
        internal readonly string Text;
        internal readonly bool Changed;
        internal readonly string Marker;
        internal TextPaneLine(int number, string text, bool changed, string marker)
        { Number = number; Text = text ?? ""; Changed = changed; Marker = marker; }
    }

    /// <summary>Virtual native grid: aligned lines never require building a second giant rich-text document.</summary>
    internal sealed class TextLinePane : UserControl
    {
        internal readonly DataGridView Grid = new DataGridView();
        private readonly IList<TextPaneLine> rows;
        internal TextLinePane(string title, string path, TextDocument document, IList<TextPaneLine> rows)
        {
            this.rows = rows;
            Dock = DockStyle.Fill; Margin = new Padding(3, 0, 3, 0);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = title + "（只读；双击查看完整行）", Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
            var pathLabel = new Label { Text = path, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false };
            layout.Controls.Add(pathLabel, 0, 1);
            layout.Controls.Add(new Label { Text = EditorUi.Metadata(document), Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 2);
            Grid.Dock = DockStyle.Fill; Grid.ReadOnly = true; Grid.VirtualMode = true;
            Grid.AllowUserToAddRows = false; Grid.AllowUserToDeleteRows = false; Grid.AllowUserToResizeRows = false;
            Grid.AllowUserToOrderColumns = false; Grid.RowHeadersVisible = false; Grid.ColumnHeadersVisible = false;
            Grid.BackgroundColor = SystemColors.Window; Grid.BorderStyle = BorderStyle.Fixed3D;
            Grid.CellBorderStyle = DataGridViewCellBorderStyle.None; Grid.AutoGenerateColumns = false;
            Grid.SelectionMode = DataGridViewSelectionMode.CellSelect;
            Grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            Grid.DefaultCellStyle.Font = new Font("Consolas", 10);
            Grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            Grid.RowTemplate.Height = 21;
            Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Line", Width = 70, Frozen = true, SortMode = DataGridViewColumnSortMode.NotSortable });
            int longest = 0; foreach (var row in rows) longest = Math.Max(longest, row.Text.Length);
            Grid.Columns.Add(new DataGridViewTextBoxColumn { Name = "Content", Width = Math.Min(32768, Math.Max(600, longest * 10 + 30)), SortMode = DataGridViewColumnSortMode.NotSortable });
            Grid.RowCount = rows.Count;
            Grid.CellValueNeeded += delegate(object sender, DataGridViewCellValueEventArgs e)
            {
                var row = rows[e.RowIndex];
                e.Value = e.ColumnIndex == 0 ? (row.Number == 0 ? "" : (row.Changed ? row.Marker + " " : "") + row.Number) : row.Text;
            };
            Grid.CellFormatting += delegate(object sender, DataGridViewCellFormattingEventArgs e)
            {
                var row = rows[e.RowIndex];
                e.CellStyle.BackColor = row.Number == 0 ? SystemColors.Control : row.Changed && !SystemInformation.HighContrast ? Color.FromArgb(255, 244, 198) : SystemColors.Window;
                e.CellStyle.ForeColor = SystemColors.WindowText;
                if (e.ColumnIndex == 0) { e.CellStyle.Alignment = DataGridViewContentAlignment.MiddleRight; e.CellStyle.ForeColor = SystemColors.GrayText; }
            };
            Grid.CellDoubleClick += delegate(object sender, DataGridViewCellEventArgs e) { ShowLine(e.RowIndex); };
            Grid.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && Grid.CurrentCell != null) { e.SuppressKeyPress = true; ShowLine(Grid.CurrentCell.RowIndex); }
            };
            Grid.CellToolTipTextNeeded += delegate(object sender, DataGridViewCellToolTipTextNeededEventArgs e)
            { if (e.RowIndex >= 0) e.ToolTipText = "双击或按 Enter 查看、复制完整行内容。"; };
            layout.Controls.Add(Grid, 0, 3); Controls.Add(layout);
        }
        internal void ShowLine(int row)
        {
            if (row < 0 || row >= rows.Count || rows[row].Number == 0) return;
            using (var inspector = new TextLineInspectorForm(rows[row].Number, rows[row].Text)) inspector.ShowDialog(FindForm());
        }
        internal void GoToRow(int row)
        {
            if (row < 0 || row >= rows.Count) return;
            Grid.FirstDisplayedScrollingRowIndex = row; Grid.CurrentCell = Grid.Rows[row].Cells[1];
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) Grid.DefaultCellStyle.Font.Dispose();
            base.Dispose(disposing);
        }
    }

    // Native grids cap column width. This wrapped inspector keeps even a 2 MiB single line fully accessible.
    internal sealed class TextLineInspectorForm : Form
    {
        internal TextLineInspectorForm(int lineNumber, string text)
        {
            DialogStyle.Apply(this); Text = "第 " + lineNumber + " 行完整内容 - TortoiseSCM";
            Size = new Size(820, 540); MinimumSize = new Size(520, 320);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = "只读完整行；自动换行显示。Ctrl+A 全选，Ctrl+C 复制。", Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
            var content = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical, MaxLength = Int32.MaxValue, Text = text, Font = new Font("Consolas", 10), BackColor = SystemColors.Window };
            layout.Controls.Add(content, 0, 1);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); }; footer.Controls.Add(close); layout.Controls.Add(footer, 0, 2);
            Controls.Add(layout); CancelButton = close;
            FormClosed += delegate { content.Font.Dispose(); };
        }
    }

    internal static class EditorUi
    {
        internal static Button Button(string text, EventHandler click)
        { var button = DialogStyle.Button(text); button.AutoSize = true; button.MinimumSize = new Size(110, 26); button.Click += click; return button; }
        internal static TableLayoutPanel Columns(int count)
        {
            var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = count, RowCount = 1, Margin = Padding.Empty };
            for (int i = 0; i < count; i++) columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / count));
            columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); return columns;
        }
        internal static string Metadata(TextDocument document)
        { return document.EncodingName + (document.HasBom ? " BOM" : " 无 BOM") + " | " + document.LineEndingDescription; }
        internal static void Synchronize(params TextLinePane[] panes)
        {
            bool syncing = false;
            foreach (var pane in panes)
            {
                var source = pane;
                pane.Grid.Scroll += delegate(object sender, ScrollEventArgs e)
                {
                    if (syncing) return;
                    syncing = true;
                    try
                    {
                        foreach (var target in panes)
                        {
                            if (target == source || target.Grid.RowCount == 0) continue;
                            int first = source.Grid.FirstDisplayedScrollingRowIndex;
                            if (first >= 0) target.Grid.FirstDisplayedScrollingRowIndex = Math.Min(first, target.Grid.RowCount - 1);
                            target.Grid.HorizontalScrollingOffset = source.Grid.HorizontalScrollingOffset;
                        }
                    }
                    finally { syncing = false; }
                };
            }
        }
    }
}
