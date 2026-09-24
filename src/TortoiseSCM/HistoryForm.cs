// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class HistoryForm : Form
    {
        private readonly PlasticClient client;
        private readonly string path;
        private readonly bool wholeWorkspace;
        private readonly ListView revisions = new ListView();
        private readonly ListView changedFiles = new ListView();
        private readonly TextBox description = new TextBox();
        private readonly Label status = new Label();
        private readonly Button restore = new Button();
        private readonly Button snapshot = new Button();
        private readonly Button historicalFile = new Button();
        private readonly TextBox filter = new TextBox();
        private readonly Button close = new Button();
        private readonly List<PlasticHistoryItem> entries = new List<PlasticHistoryItem>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource detailRequest;
        private bool writing;
        private int generation;
        private bool filtering;

        public HistoryForm(PlasticClient client, string path, string workspaceRoot)
        {
            this.client = client;
            this.path = path;
            wholeWorkspace = path.TrimEnd('\\', '/').Equals(workspaceRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Text = "历史记录 - TortoiseSCM";
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(1080, 740);
            MinimumSize = new Size(860, 580);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            header.Controls.Add(new Label { Text = "范围：" + path, Dock = DockStyle.Fill, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Margin = new Padding(0, 0, 12, 3) }, 0, 0);
            header.Controls.Add(new Label { Text = "筛选(&F):", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
            filter.Dock = DockStyle.Fill;
            filter.AccessibleName = "筛选历史：版本、日期、作者、分支或说明";
            filter.Margin = new Padding(3, 2, 0, 4);
            filter.TextChanged += async delegate { await ApplyFilterAsync(); };
            header.Controls.Add(filter, 2, 0);
            layout.Controls.Add(header, 0, 0);
            // Follow IDD_LOGMESSAGE: revision list, commit message and changed paths,
            // separated by native splitters rather than framed panels or tabs.
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Margin = Padding.Empty,
                Size = new Size(1040, 570), SplitterDistance = 270, SplitterWidth = 5, Panel1MinSize = 100, Panel2MinSize = 180 };
            ConfigureList(revisions, "提交历史", new[] { "版本", "日期", "作者", "分支", "说明" }, new[] { 70, 155, 120, 210, 440 });
            revisions.SelectedIndexChanged += async delegate { if (!filtering) await LoadDetailsAsync(); };
            split.Panel1.Controls.Add(revisions);
            var lower = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Margin = Padding.Empty,
                Size = new Size(1040, 295), SplitterDistance = 100, SplitterWidth = 5, Panel1MinSize = 55, Panel2MinSize = 90 };
            description.Multiline = true;
            description.ReadOnly = true;
            description.ScrollBars = ScrollBars.Vertical;
            description.Dock = DockStyle.Fill;
            description.BorderStyle = BorderStyle.FixedSingle;
            description.BackColor = SystemColors.Window;
            description.AccessibleName = "提交说明";
            ConfigureList(changedFiles, "本次提交的文件", new[] { "操作", "路径", "原路径", "类型" }, new[] { 75, 570, 280, 70 });
            changedFiles.SelectedIndexChanged += delegate { UpdateFileAction(); };
            changedFiles.DoubleClick += delegate { OpenHistoricalFile(); };
            var fileMenu = new ContextMenuStrip();
            fileMenu.Items.Add("比较 / 导出历史文件…", null, delegate { OpenHistoricalFile(); });
            fileMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = !historicalFile.Enabled; };
            changedFiles.ContextMenuStrip = fileMenu;
            lower.Panel1.Controls.Add(description);
            lower.Panel2.Controls.Add(changedFiles);
            split.Panel2.Controls.Add(lower);
            layout.Controls.Add(split, 0, 1);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, wholeWorkspace ? 165 : 0));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.Margin = Padding.Empty;
            layout.Controls.Add(status, 0, 2);
            restore.Text = wholeWorkspace ? "整仓回滚为待提交(&R)..." : "恢复此范围到此版本(&R)...";
            restore.Dock = DockStyle.Fill;
            restore.Margin = new Padding(3, 3, 6, 3);
            restore.Enabled = false;
            restore.Click += async delegate { await RestoreAsync(false); };
            historicalFile.Text = "比较 / 导出文件…";
            historicalFile.Dock = DockStyle.Fill; historicalFile.Enabled = false;
            historicalFile.Click += delegate { OpenHistoricalFile(); };
            footer.Controls.Add(historicalFile, 0, 0);
            snapshot.Text = "切换历史快照…";
            snapshot.Dock = DockStyle.Fill; snapshot.Visible = wholeWorkspace; snapshot.Enabled = false;
            snapshot.Click += async delegate { await RestoreAsync(true); };
            footer.Controls.Add(snapshot, 2, 0);
            footer.Controls.Add(restore, 3, 0);
            close.Text = "关闭";
            close.Dock = DockStyle.Fill;
            close.Margin = new Padding(3, 3, 0, 3);
            close.DialogResult = DialogResult.Cancel;
            footer.Controls.Add(close, 4, 0);
            CancelButton = close;
            layout.Controls.Add(footer, 0, 3);
            Controls.Add(layout);
            DialogStyle.Apply(this);
            Shown += async delegate { await LoadHistoryAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (writing) e.Cancel = true; else lifetime.Cancel(); };
            FormClosed += delegate { if (detailRequest != null) detailRequest.Cancel(); };
        }

        private static void ConfigureList(ListView list, string name, string[] columns, int[] widths)
        {
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.AccessibleName = name;
            for (int i = 0; i < columns.Length; i++) list.Columns.Add(columns[i], widths[i]);
            DialogStyle.ApplyList(list);
        }

        private async Task LoadHistoryAsync()
        {
            status.Text = "正在读取历史…";
            try
            {
                var loadedEntries = await client.GetHistoryAsync(path, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                entries.Clear();
                entries.AddRange(loadedEntries.OrderByDescending(e => e.Changeset));
                await ApplyFilterAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = "读取失败：" + ex.Message; }
        }

        private async Task ApplyFilterAsync()
        {
            if (writing || lifetime.IsCancellationRequested) return;
            long? selected = revisions.SelectedItems.Count == 1 ? (long?)((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset : null;
            string query = filter.Text.Trim();
            filtering = true;
            revisions.BeginUpdate();
            try
            {
                revisions.Items.Clear();
                foreach (var entry in entries.Where(e => MatchesFilter(e, query)))
                {
                    var row = new ListViewItem(new[] { entry.Changeset.ToString(), entry.CreationDate, entry.Owner, entry.Branch,
                        (entry.Comment ?? "").Replace("\r", " ").Replace("\n", " ") }) { Tag = entry };
                    revisions.Items.Add(row);
                    if (selected.HasValue && entry.Changeset == selected.Value) row.Selected = true;
                }
                if (revisions.SelectedItems.Count == 0 && revisions.Items.Count > 0) revisions.Items[0].Selected = true;
            }
            finally { revisions.EndUpdate(); filtering = false; }
            status.Text = revisions.Items.Count + " / " + entries.Count + " 个提交";
            await LoadDetailsAsync();
        }

        private static bool MatchesFilter(PlasticHistoryItem entry, string query)
        {
            return query.Length == 0 || new[] { entry.Changeset.ToString(), entry.CreationDate, entry.Owner, entry.Branch, entry.Comment }
                .Any(value => (value ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        private async Task LoadDetailsAsync()
        {
            int request = ++generation;
            if (detailRequest != null) detailRequest.Cancel();
            changedFiles.Items.Clear();
            description.Clear();
            restore.Enabled = snapshot.Enabled = false;
            UpdateFileAction();
            if (revisions.SelectedItems.Count != 1 || writing) return;
            var entry = (PlasticHistoryItem)revisions.SelectedItems[0].Tag;
            description.Text = entry.Comment;
            status.Text = "正在读取 cs:" + entry.Changeset + " 的文件明细…";
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            detailRequest = cancellation;
            try
            {
                var details = await client.GetChangesetAsync(path, entry.Changeset, cancellation.Token);
                if (cancellation.IsCancellationRequested || request != generation) return;
                foreach (var file in details.Files)
                    changedFiles.Items.Add(new ListViewItem(new[] { file.Status, file.Path, file.OldPath, file.ItemType }) { Tag = file });
                status.Text = "cs:" + entry.Changeset + " · " + details.Files.Count + " 个更改项（完整提交）";
                restore.Enabled = snapshot.Enabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested && request == generation) status.Text = "明细读取失败：" + ex.Message; }
            finally { if (detailRequest == cancellation) detailRequest = null; cancellation.Dispose(); }
        }

        private void UpdateFileAction()
        {
            var file = changedFiles.SelectedItems.Count == 1 ? (PlasticChangesetFile)changedFiles.SelectedItems[0].Tag : null;
            historicalFile.Enabled = !writing && revisions.SelectedItems.Count == 1 && file != null &&
                !string.Equals(file.ItemType, "D", StringComparison.OrdinalIgnoreCase) && !string.Equals(file.ItemType, "dir", StringComparison.OrdinalIgnoreCase);
        }

        private void OpenHistoricalFile()
        {
            if (!historicalFile.Enabled) return;
            var file = (PlasticChangesetFile)changedFiles.SelectedItems[0].Tag;
            var entry = (PlasticHistoryItem)revisions.SelectedItems[0].Tag;
            using (var dialog = new HistoricalFileForm(client, path, file.Path, entry.Changeset)) dialog.ShowDialog(this);
        }

        private async Task RestoreAsync(bool switchSnapshot)
        {
            if (writing || revisions.SelectedItems.Count != 1) return;
            var entry = (PlasticHistoryItem)revisions.SelectedItems[0].Tag;
            string explanation = switchSnapshot ?
                "将整个工作区切换到历史快照。部分工作区只切换已加载内容，并保留加载规则。\r\n这不会创建回滚提交；存在待提交更改时操作会被拒绝。" :
                wholeWorkspace ? "在当前分支把整仓内容回滚到所选历史版本，生成待提交更改。\r\n不会自动提交；请返回提交窗口检查并提交。\r\n只支持完整工作区，要求当前分支最新且无待提交更改。" :
                "将此文件或目录恢复到历史内容，结果成为待提交更改。\r\n目录包含其后代；范围内已有待提交更改时操作会被拒绝。\r\n部分工作区不支持涉及增删、移动或未加载项的目录恢复。";
            if (MessageBox.Show(this, explanation + "\r\n\r\n" + path + "\r\n目标 cs:" + entry.Changeset + "\r\n\r\n继续？",
                "TortoiseSCM — 恢复历史版本", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            writing = true;
            revisions.Enabled = restore.Enabled = snapshot.Enabled = historicalFile.Enabled = filter.Enabled = close.Enabled = false;
            status.Text = "正在恢复历史版本…";
            try
            {
                var result = switchSnapshot ? await client.SwitchAsync(path, entry.Changeset, lifetime.Token) :
                    await client.RollbackAsync(path, entry.Changeset, lifetime.Token);
                status.Text = result.Succeeded ? "已完成。关闭历史窗口后可检查待定更改。" : "操作失败：" + result.Error;
                if (!result.Succeeded) MessageBox.Show(this, result.Output + "\r\n" + result.Error, "恢复未成功", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex) { status.Text = "恢复失败：" + ex.Message; MessageBox.Show(this, ex.Message, "恢复未成功"); }
            finally { writing = false; revisions.Enabled = restore.Enabled = snapshot.Enabled = filter.Enabled = close.Enabled = true; UpdateFileAction(); }
        }
    }
}
