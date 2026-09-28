// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using TortoiseSCM.Core;

namespace TortoiseSCM
{
    /// <summary>Manual merge editor. Saving is deliberately separate from Plastic conflict resolution.</summary>
    internal sealed class TextMergeForm : Form
    {
        private readonly TextDocument baseline;
        private readonly TextDocument local;
        private readonly TextDocument remote;
        private readonly TextDocument resultDocument;
        private readonly string[] inputPaths;
        private readonly string resultPath;
        private readonly TextBox result = new TextBox();
        private readonly ComboBox lineEndings = new ComboBox();
        private readonly Label status = new Label();
        private readonly Label resultMetadata = new Label();
        private readonly Button save;
        private readonly string comparisonNotice;
        private string savedText;
        private int savedEnding;
        private readonly List<int> differences = new List<int>();
        private readonly TextLinePane[] panes;
        private int currentDifference = -1;
        public bool Saved { get; private set; }

        public TextMergeForm(string basePath, string localPath, string remotePath, string resultPath)
        {
            inputPaths = new[] { basePath, localPath, remotePath };
            this.resultPath = Path.GetFullPath(resultPath);
            baseline = TextDocument.Load(basePath); local = TextDocument.Load(localPath); remote = TextDocument.Load(remotePath);
            bool recovered = File.Exists(resultPath);
            resultDocument = recovered ? TextDocument.Load(resultPath) : TextDocument.CreateResult(resultPath, local);
            DialogStyle.Apply(this); Text = "合并文件 - TortoiseSCM";
            Size = new Size(1240, 860); MinimumSize = new Size(940, 660);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 7 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            toolbar.Controls.Add(EditorUi.Button("上一个差异", delegate { Navigate(-1); }));
            toolbar.Controls.Add(EditorUi.Button("下一个差异", delegate { Navigate(1); }));
            toolbar.Controls.Add(EditorUi.Button("使用基线文件", delegate { UseSource(baseline, "基线"); }));
            toolbar.Controls.Add(EditorUi.Button("使用本地文件", delegate { UseSource(local, "本地"); }));
            toolbar.Controls.Add(EditorUi.Button("使用远程文件", delegate { UseSource(remote, "远程"); }));
            layout.Controls.Add(toolbar, 0, 0);
            var localComparison = TextComparison.Compare(baseline, local);
            var remoteComparison = TextComparison.Compare(baseline, remote);
            comparisonNotice = (localComparison.IsApproximate || remoteComparison.IsApproximate ? "大文件采用粗略行对齐。" : "")
                + (localComparison.HasEncodingChanges || remoteComparison.HasEncodingChanges ? "输入编码/BOM 不同。" : "")
                + (localComparison.HasLineEndingChanges || remoteComparison.HasLineEndingChanges ? "输入行尾不同。" : "");
            var aligned = Align(localComparison, remoteComparison);
            panes = new[] { new TextLinePane("基线 Base", basePath, baseline, aligned[0]), new TextLinePane("本地 Local", localPath, local, aligned[1]), new TextLinePane("远程 Remote", remotePath, remote, aligned[2]) };
            for (int i = 0; i < aligned[0].Count; i++)
            {
                bool changed = aligned[0][i].Changed || aligned[1][i].Changed || aligned[2][i].Changed;
                bool previousChanged = i != 0 && (aligned[0][i - 1].Changed || aligned[1][i - 1].Changed || aligned[2][i - 1].Changed);
                if (changed && !previousChanged) differences.Add(i);
            }
            toolbar.Controls[0].Enabled = toolbar.Controls[1].Enabled = differences.Count > 0;
            var columns = EditorUi.Columns(3); for (int i = 0; i < panes.Length; i++) columns.Controls.Add(panes[i], i, 0);
            EditorUi.Synchronize(panes); layout.Controls.Add(columns, 0, 1);
            var resultHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            resultHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            resultHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90)); resultHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            resultHeader.Controls.Add(new Label { Text = "可编辑结果：" + this.resultPath, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            resultHeader.Controls.Add(new Label { Text = "保存行尾：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 1, 0);
            lineEndings.Dock = DockStyle.Fill; lineEndings.DropDownStyle = ComboBoxStyle.DropDownList;
            lineEndings.Items.AddRange(new object[] { "保留原始行尾", "Windows (CRLF)", "Unix (LF)", "Classic Mac (CR)" });
            lineEndings.SelectedIndex = 0; resultHeader.Controls.Add(lineEndings, 2, 0); layout.Controls.Add(resultHeader, 0, 2);
            result.Dock = DockStyle.Fill; result.Multiline = true; result.AcceptsTab = true; result.AcceptsReturn = true;
            result.ScrollBars = ScrollBars.Both; result.WordWrap = false; result.Font = new Font("Consolas", 10);
            result.MaxLength = Int32.MaxValue; result.Text = resultDocument.EditorText; savedText = result.Text;
            layout.Controls.Add(result, 0, 3);
            resultMetadata.Text = EditorUi.Metadata(resultDocument) + " | " + (recovered ? "已重新打开现有结果，可继续编辑。" : "结果初始使用本地内容。") + comparisonNotice;
            resultMetadata.AutoEllipsis = true; resultMetadata.Dock = DockStyle.Fill; layout.Controls.Add(resultMetadata, 0, 4);
            status.Text = "手工合并：请检查全部差异。保存结果不会将 Plastic 冲突标记为已解决。";
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 5);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); };
            save = DialogStyle.Button("保存结果"); save.Click += delegate { SaveResult(); };
            footer.Controls.Add(close); footer.Controls.Add(save); layout.Controls.Add(footer, 0, 6);
            Controls.Add(layout); CancelButton = close;
            result.TextChanged += delegate { UpdateDirty(); }; lineEndings.SelectedIndexChanged += delegate { UpdateDirty(); };
            FormClosing += OnClosing; KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.S) { SaveResult(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.F7) { Navigate(e.Shift ? -1 : 1); e.Handled = true; }
            };
        }

        private bool Dirty { get { return !String.Equals(savedText, result.Text, StringComparison.Ordinal) || savedEnding != lineEndings.SelectedIndex; } }
        private void UpdateDirty() { Text = "合并文件" + (Dirty ? " *" : "") + " - TortoiseSCM"; }

        private void UseSource(TextDocument document, string name)
        {
            if (MessageBox.Show(this, "将整个可编辑结果替换为" + name + "文件的内容？\n保存前请检查结果。", "使用" + name + "文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            result.Text = document.EditorText;
            status.Text = "已使用" + name + "内容。请检查并保存结果；Plastic 冲突解决状态需要另行确认。";
        }

        internal bool SaveResult()
        {
            try
            {
                var endings = new[] { TextLineEnding.Preserve, TextLineEnding.CrLf, TextLineEnding.Lf, TextLineEnding.Cr };
                resultDocument.Save(result.Text, endings[lineEndings.SelectedIndex], inputPaths);
                savedText = result.Text; savedEnding = lineEndings.SelectedIndex; Saved = true; UpdateDirty();
                resultMetadata.Text = EditorUi.Metadata(resultDocument) + " | 结果已保存。" + comparisonNotice;
                status.Text = "结果已保存。尚未标记 Plastic 冲突为已解决；请返回冲突列表检查结果。";
                return true;
            }
            catch (Exception error)
            {
                MessageBox.Show(this, error.Message, "无法保存合并结果", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (!Dirty) return;
            var answer = MessageBox.Show(this, "关闭前保存合并结果的修改？\n" + resultPath, "合并结果尚未保存", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !SaveResult())) e.Cancel = true;
        }

        internal void Navigate(int direction)
        {
            if (differences.Count == 0) return;
            currentDifference = currentDifference < 0 ? (direction < 0 ? differences.Count - 1 : 0) : (currentDifference + direction + differences.Count) % differences.Count;
            foreach (var pane in panes) pane.GoToRow(differences[currentDifference]);
            status.Text = "差异 " + (currentDifference + 1) + "/" + differences.Count + "。请比较基线、本地和远程内容，并编辑下方结果。";
        }

        // Anchor both pairwise comparisons on the base. Insertions are placed before the next base line.
        private static List<TextPaneLine>[] Align(TextComparisonResult localComparison, TextComparisonResult remoteComparison)
        {
            var localRows = localComparison.Rows;
            var remoteRows = remoteComparison.Rows;
            var output = new[] { new List<TextPaneLine>(), new List<TextPaneLine>(), new List<TextPaneLine>() };
            int a = 0, b = 0;
            while (a < localRows.Count || b < remoteRows.Count)
            {
                bool insertA = a < localRows.Count && localRows[a].LeftLineNumber == 0;
                bool insertB = b < remoteRows.Count && remoteRows[b].LeftLineNumber == 0;
                if (insertA || insertB)
                {
                    output[0].Add(new TextPaneLine(0, "", true, ""));
                    output[1].Add(insertA ? new TextPaneLine(localRows[a].RightLineNumber, localRows[a].RightText, true, "+") : new TextPaneLine(0, "", false, ""));
                    output[2].Add(insertB ? new TextPaneLine(remoteRows[b].RightLineNumber, remoteRows[b].RightText, true, "+") : new TextPaneLine(0, "", false, ""));
                    if (insertA) a++; if (insertB) b++; continue;
                }
                TextDiffRow left = a < localRows.Count ? localRows[a++] : null;
                TextDiffRow right = b < remoteRows.Count ? remoteRows[b++] : null;
                TextDiffRow anchor = left ?? right;
                bool localChanged = left != null && left.Kind != TextDiffKind.Equal;
                bool remoteChanged = right != null && right.Kind != TextDiffKind.Equal;
                output[0].Add(new TextPaneLine(anchor.LeftLineNumber, anchor.LeftText, localChanged || remoteChanged, "~"));
                output[1].Add(left == null ? new TextPaneLine(0, "", false, "") : new TextPaneLine(left.RightLineNumber, left.RightText, localChanged, "~"));
                output[2].Add(right == null ? new TextPaneLine(0, "", false, "") : new TextPaneLine(right.RightLineNumber, right.RightText, remoteChanged, "~"));
            }
            return output;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) result.Font.Dispose();
            base.Dispose(disposing);
        }
    }
}
