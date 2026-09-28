// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using TortoiseSCM.Core;

namespace TortoiseSCM
{
    /// <summary>Block-assisted merge editor. Saving is deliberately separate from Plastic conflict resolution.</summary>
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
        private readonly List<TextPaneLine>[] alignedRows;
        private readonly ListView blocks = new ListView();
        private readonly Button chooseBase, chooseLocal, chooseRemote, markReviewed;
        private readonly Button previousDifference, nextDifference;
        private readonly bool recovered;
        private bool settingResult, editedResult;
        private TextMergePlan mergePlan;
        internal bool HasBlockMapping { get { return mergePlan != null; } }
        internal int PendingBlockCount { get { return mergePlan == null ? -1 : mergePlan.UnresolvedCount; } }
        internal bool NeedsRegenerationConfirmation { get { return recovered || editedResult || mergePlan != null || Saved; } }
        public bool Saved { get; private set; }

        public TextMergeForm(string basePath, string localPath, string remotePath, string resultPath)
        {
            inputPaths = new[] { basePath, localPath, remotePath };
            this.resultPath = Path.GetFullPath(resultPath);
            baseline = TextDocument.Load(basePath); local = TextDocument.Load(localPath); remote = TextDocument.Load(remotePath);
            recovered = File.Exists(resultPath);
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
            previousDifference = EditorUi.Button("上一个差异", delegate { Navigate(-1); });
            nextDifference = EditorUi.Button("下一个差异", delegate { Navigate(1); });
            toolbar.Controls.Add(previousDifference); toolbar.Controls.Add(nextDifference);
            toolbar.Controls.Add(EditorUi.Button("自动合并", delegate { GenerateMergeFromToolbar(); }));
            toolbar.Controls.Add(EditorUi.Button("使用基线文件", delegate { UseSource(baseline, "基线"); }));
            toolbar.Controls.Add(EditorUi.Button("使用本地文件", delegate { UseSource(local, "本地"); }));
            toolbar.Controls.Add(EditorUi.Button("使用远程文件", delegate { UseSource(remote, "远程"); }));
            layout.Controls.Add(toolbar, 0, 0);
            var localComparison = TextComparison.Compare(baseline, local);
            var remoteComparison = TextComparison.Compare(baseline, remote);
            comparisonNotice = (localComparison.IsApproximate || remoteComparison.IsApproximate ? "大文件采用粗略行对齐。" : "")
                + (localComparison.HasEncodingChanges || remoteComparison.HasEncodingChanges ? "输入编码/BOM 不同。" : "")
                + (localComparison.HasLineEndingChanges || remoteComparison.HasLineEndingChanges ? "输入行尾不同。" : "");
            var aligned = Align(localComparison, remoteComparison); alignedRows = aligned;
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
            result.HideSelection = false;
            var resultArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            resultArea.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 248));
            resultArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            resultArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var blockPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 0, 6, 0) };
            blockPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            blockPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            blockPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            blockPanel.Controls.Add(new Label { Text = "合并块（核查状态仅限本次窗口）", Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
            blocks.Dock = DockStyle.Fill; blocks.View = View.Details; blocks.FullRowSelect = true;
            blocks.MultiSelect = false; blocks.HideSelection = false; blocks.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            blocks.Columns.Add("块 / 来源", 117); blocks.Columns.Add("状态", 95);
            blocks.Items.Add(new ListViewItem(new[] { "尚未生成", "手工模式" })); blocks.Enabled = false;
            blocks.SelectedIndexChanged += delegate { HighlightSelectedBlock(); UpdateBlockButtons(); };
            blockPanel.Controls.Add(blocks, 0, 1);
            var blockActions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            blockActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); blockActions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            blockActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); blockActions.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chooseBase = EditorUi.Button("采用基线块", delegate { ChooseSelectedBlock(TextMergeChoice.Base); });
            chooseLocal = EditorUi.Button("采用本地块", delegate { ChooseSelectedBlock(TextMergeChoice.Local); });
            chooseRemote = EditorUi.Button("采用远程块", delegate { ChooseSelectedBlock(TextMergeChoice.Remote); });
            markReviewed = EditorUi.Button("标记已核查", delegate { ReviewSelectedBlock(); });
            blockActions.Controls.Add(chooseBase, 0, 0); blockActions.Controls.Add(chooseLocal, 1, 0);
            blockActions.Controls.Add(chooseRemote, 0, 1); blockActions.Controls.Add(markReviewed, 1, 1);
            blockPanel.Controls.Add(blockActions, 0, 2);
            resultArea.Controls.Add(blockPanel, 0, 0); resultArea.Controls.Add(result, 1, 0); layout.Controls.Add(resultArea, 0, 3);
            UpdateBlockButtons();
            resultMetadata.Text = EditorUi.Metadata(resultDocument) + " | " + (recovered ? "已恢复现有结果。" : "结果初始使用本地内容。") + "保存沿用结果编码/BOM。" + comparisonNotice;
            resultMetadata.AutoEllipsis = true; resultMetadata.Dock = DockStyle.Fill; layout.Controls.Add(resultMetadata, 0, 4);
            status.Text = "手工模式：自动合并可生成分块草稿；重新打开需重新核查。保存结果不会解决 Plastic 冲突。";
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 5);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty };
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); };
            save = DialogStyle.Button("保存结果"); save.Click += delegate { SaveResult(); };
            footer.Controls.Add(close); footer.Controls.Add(save); layout.Controls.Add(footer, 0, 6);
            Controls.Add(layout); CancelButton = close;
            result.TextChanged += delegate { if (!settingResult) InvalidateBlockMapping(); UpdateDirty(); }; lineEndings.SelectedIndexChanged += delegate { UpdateDirty(); };
            FormClosing += OnClosing; KeyPreview = true;
            KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.S) { SaveResult(); e.SuppressKeyPress = true; }
                if (e.KeyCode == Keys.F7) { Navigate(e.Shift ? -1 : 1); e.Handled = true; }
            };
        }

        private bool Dirty { get { return !String.Equals(savedText, result.Text, StringComparison.Ordinal) || savedEnding != lineEndings.SelectedIndex; } }
        private void UpdateDirty()
        {
            Text = "合并文件" + (Dirty ? " *" : "") + " - TortoiseSCM";
            resultMetadata.Text = EditorUi.Metadata(resultDocument) + " | "
                + (Dirty ? "结果有未保存修改。" : Saved ? "结果已保存。" : recovered ? "已恢复现有结果。" : "结果初始使用本地内容。")
                + "保存沿用结果编码/BOM。" + comparisonNotice;
        }

        private void GenerateMergeFromToolbar()
        {
            bool discard = NeedsRegenerationConfirmation;
            if (discard && MessageBox.Show(this, "重新生成会替换整个当前结果（包括已恢复或手工编辑的内容），并重置本窗口的逐块核查状态。继续？\n磁盘文件仅在点击保存结果后更新。", "重新生成合并草稿", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            GenerateMerge(discard);
        }

        // Tests and the UI both use this guard: callers must explicitly allow discarding existing work.
        internal bool GenerateMerge(bool discardWork)
        {
            if (NeedsRegenerationConfirmation && !discardWork) return false;
            mergePlan = TextMergePlan.Create(baseline, local, remote);
            previousDifference.Enabled = nextDifference.Enabled = mergePlan.Blocks.Count > 0;
            SetPlannedResult();
            RefreshBlocks(mergePlan.Blocks.Count == 0 ? -1 : 0);
            UpdateMergeStatus("");
            return true;
        }

        private void SetPlannedResult()
        {
            settingResult = true;
            try { result.Text = mergePlan.ResultText; }
            finally { settingResult = false; }
            UpdateDirty();
        }

        private void InvalidateBlockMapping()
        {
            editedResult = true;
            mergePlan = null; blocks.Enabled = false;
            previousDifference.Enabled = nextDifference.Enabled = differences.Count > 0;
            foreach (ListViewItem item in blocks.Items) item.SubItems[1].Text = "映射失效";
            foreach (var pane in panes) pane.Grid.ClearSelection();
            UpdateBlockButtons(); UpdateMergeStatus("");
        }

        private int SelectedBlock { get { return blocks.SelectedIndices.Count == 0 ? -1 : blocks.SelectedIndices[0]; } }
        private void UpdateBlockButtons()
        {
            bool enabled = mergePlan != null && SelectedBlock >= 0;
            chooseBase.Enabled = chooseLocal.Enabled = chooseRemote.Enabled = markReviewed.Enabled = enabled;
        }

        private void RefreshBlocks(int selected)
        {
            blocks.BeginUpdate();
            try
            {
                blocks.Items.Clear(); blocks.Enabled = mergePlan != null;
                if (mergePlan != null)
                    for (int i = 0; i < mergePlan.Blocks.Count; i++)
                    {
                        var block = mergePlan.Blocks[i];
                        string kind = block.Kind == TextMergeKind.Local ? "本地" : block.Kind == TextMergeKind.Remote ? "远程" : block.Kind == TextMergeKind.Identical ? "双方相同" : "冲突";
                        string state = block.Status == TextMergeStatus.Reviewed ? "已核查" : block.Status == TextMergeStatus.Automatic ? "自动采用" : "待处理";
                        blocks.Items.Add(new ListViewItem(new[] { (i + 1) + " · " + kind, state }));
                    }
                if (selected >= 0 && selected < blocks.Items.Count) { blocks.Items[selected].Selected = true; blocks.Items[selected].EnsureVisible(); }
            }
            finally { blocks.EndUpdate(); }
            HighlightSelectedBlock(); UpdateBlockButtons();
        }

        internal void SelectBlock(int index)
        {
            if (mergePlan == null || index < 0 || index >= blocks.Items.Count) return;
            foreach (ListViewItem item in blocks.Items) item.Selected = false;
            blocks.Items[index].Selected = true; blocks.Items[index].EnsureVisible();
            HighlightSelectedBlock(); UpdateBlockButtons();
        }

        internal bool ChooseSelectedBlock(TextMergeChoice choice)
        {
            int index = SelectedBlock;
            if (mergePlan == null || index < 0) return false;
            mergePlan.Choose(index, choice); SetPlannedResult(); RefreshBlocks(index); UpdateMergeStatus(""); return true;
        }

        internal bool ReviewSelectedBlock()
        {
            int index = SelectedBlock;
            if (mergePlan == null || index < 0) return false;
            mergePlan.MarkReviewed(index); RefreshBlocks(index); UpdateMergeStatus(""); return true;
        }

        private void HighlightSelectedBlock()
        {
            int index = SelectedBlock;
            if (mergePlan == null || index < 0) return;
            var block = mergePlan.Blocks[index];
            int[] starts = { block.BaseStart, block.LocalStart, block.RemoteStart };
            int[] counts = { block.BaseCount, block.LocalCount, block.RemoteCount };
            int anchor = Int32.MaxValue;
            for (int p = 0; p < panes.Length; p++)
                for (int row = 0; row < alignedRows[p].Count; row++)
                    if (alignedRows[p][row].Number > starts[p] && alignedRows[p][row].Number <= starts[p] + counts[p]) { anchor = Math.Min(anchor, row); break; }
            if (anchor == Int32.MaxValue) anchor = Math.Max(0, alignedRows[0].Count - 1);
            foreach (var pane in panes) pane.GoToRow(anchor);
            for (int p = 0; p < panes.Length; p++)
            {
                panes[p].Grid.ClearSelection();
                for (int row = 0; row < alignedRows[p].Count; row++)
                    if (alignedRows[p][row].Number > starts[p] && alignedRows[p][row].Number <= starts[p] + counts[p])
                        foreach (DataGridViewCell cell in panes[p].Grid.Rows[row].Cells) cell.Selected = true;
            }
            result.Select(Math.Min(block.ResultStart, result.TextLength), Math.Min(block.ResultLength, Math.Max(0, result.TextLength - block.ResultStart)));
            result.ScrollToCaret();
        }

        private void UpdateMergeStatus(string prefix)
        {
            save.Text = mergePlan != null && mergePlan.UnresolvedCount > 0 ? "保存草稿" : "保存结果";
            if (mergePlan == null)
                status.Text = prefix + "手工模式：块映射不可用；自动合并将重建草稿并重置核查。保存不会解决 Plastic 冲突。";
            else
            {
                int automatic = 0, reviewed = 0;
                foreach (var block in mergePlan.Blocks) { if (block.Status == TextMergeStatus.Automatic) automatic++; if (block.Status == TextMergeStatus.Reviewed) reviewed++; }
                status.Text = prefix + (mergePlan.UnresolvedCount == 0 ? "无待选择冲突" : "待处理 " + mergePlan.UnresolvedCount) + " / 自动采用 " + automatic + " / 已核查 " + reviewed
                    + (mergePlan.SupportsAutomatic ? "。" : "。大文件或格式差异使用保守整块，请人工核查。")
                    + "保存仅生成结果；不会解决 Plastic 冲突。";
            }
        }

        private void UseSource(TextDocument document, string name)
        {
            if (MessageBox.Show(this, "将整个可编辑结果替换为" + name + "文件的内容？\n保存前请检查结果。", "使用" + name + "文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            InvalidateBlockMapping();
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
                resultMetadata.Text = EditorUi.Metadata(resultDocument) + " | 结果已保存；沿用结果编码/BOM。" + comparisonNotice;
                UpdateMergeStatus(mergePlan == null ? "手工结果已保存，需全文审核；" : mergePlan.UnresolvedCount > 0 ? "草稿已保存，仍有 " + mergePlan.UnresolvedCount + " 个冲突块待审核；" : "结果已保存；");
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
            if (mergePlan != null && mergePlan.Blocks.Count > 0)
            {
                int selected = SelectedBlock;
                SelectBlock(selected < 0 ? (direction < 0 ? mergePlan.Blocks.Count - 1 : 0) : (selected + direction + mergePlan.Blocks.Count) % mergePlan.Blocks.Count);
                return;
            }
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
