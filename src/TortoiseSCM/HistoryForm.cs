// TortoiseSCM - GPL-2.0-or-later.
using System;
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
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource detailRequest;
        private bool writing;
        private int generation;

        public HistoryForm(PlasticClient client, string path, string workspaceRoot)
        {
            this.client = client;
            this.path = path;
            wholeWorkspace = path.TrimEnd('\\', '/').Equals(workspaceRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Text = "TortoiseSCM — 历史";
            Font = new Font("Microsoft YaHei UI", 9F);
            Size = new Size(1120, 760);
            MinimumSize = new Size(860, 600);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.Controls.Add(new Label { Text = "历史范围：" + path + "\r\n选择提交后，下方显示该次提交的完整文件明细。", Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1000, 560), SplitterDistance = 260, Panel1MinSize = 120, Panel2MinSize = 120 };
            ConfigureList(revisions, "提交历史", new[] { "版本", "日期", "作者", "分支", "说明" }, new[] { 80, 165, 130, 150, 490 });
            revisions.SelectedIndexChanged += async delegate { await LoadDetailsAsync(); };
            split.Panel1.Controls.Add(revisions);
            var lower = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            lower.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
            lower.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            description.Multiline = true;
            description.ReadOnly = true;
            description.ScrollBars = ScrollBars.Vertical;
            description.Dock = DockStyle.Fill;
            ConfigureList(changedFiles, "本次提交的文件", new[] { "状态", "路径", "原路径", "类型" }, new[] { 100, 500, 320, 85 });
            lower.Controls.Add(description, 0, 0);
            lower.Controls.Add(changedFiles, 0, 1);
            split.Panel2.Controls.Add(lower);
            layout.Controls.Add(split, 0, 1);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            restore.Text = wholeWorkspace ? "整个工作区切换到所选版本…" : "将此范围恢复到所选版本…";
            restore.Dock = DockStyle.Fill;
            restore.Enabled = false;
            restore.Click += async delegate { await RestoreAsync(); };
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(restore, 1, 0);
            layout.Controls.Add(footer, 0, 2);
            Controls.Add(layout);
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
        }

        private async Task LoadHistoryAsync()
        {
            status.Text = "正在读取历史…";
            try
            {
                var entries = await client.GetHistoryAsync(path, lifetime.Token);
                if (lifetime.IsCancellationRequested) return;
                foreach (var entry in entries.OrderByDescending(e => e.Changeset))
                {
                    var row = new ListViewItem(new[] { entry.Changeset.ToString(), entry.CreationDate, entry.Owner, entry.Branch,
                        (entry.Comment ?? "").Replace("\r", " ").Replace("\n", " ") }) { Tag = entry };
                    revisions.Items.Add(row);
                }
                status.Text = entries.Count + " 个提交";
                if (revisions.Items.Count > 0) revisions.Items[0].Selected = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = "读取失败：" + ex.Message; }
        }

        private async Task LoadDetailsAsync()
        {
            int request = ++generation;
            if (detailRequest != null) detailRequest.Cancel();
            changedFiles.Items.Clear();
            description.Clear();
            restore.Enabled = false;
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
                restore.Enabled = true;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!cancellation.IsCancellationRequested && request == generation) status.Text = "明细读取失败：" + ex.Message; }
            finally { if (detailRequest == cancellation) detailRequest = null; cancellation.Dispose(); }
        }

        private async Task RestoreAsync()
        {
            if (writing || revisions.SelectedItems.Count != 1) return;
            var entry = (PlasticHistoryItem)revisions.SelectedItems[0].Tag;
            string explanation = wholeWorkspace ?
                "将整个工作区切换到历史快照。部分工作区只切换已加载内容，并保留加载规则。\r\n这不会创建回滚提交；存在待提交更改时操作会被拒绝。" :
                "将此文件或目录恢复到历史内容，结果成为待提交更改。\r\n目录包含其后代；范围内已有待提交更改时操作会被拒绝。\r\n部分工作区不支持涉及增删、移动或未加载项的目录恢复。";
            if (MessageBox.Show(this, explanation + "\r\n\r\n" + path + "\r\n目标 cs:" + entry.Changeset + "\r\n\r\n继续？",
                "TortoiseSCM — 恢复历史版本", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            writing = true;
            revisions.Enabled = restore.Enabled = false;
            status.Text = "正在恢复历史版本…";
            try
            {
                var result = wholeWorkspace ? await client.SwitchAsync(path, entry.Changeset, lifetime.Token) :
                    await client.RollbackAsync(path, entry.Changeset, lifetime.Token);
                status.Text = result.Succeeded ? "已完成。关闭历史窗口后可检查待定更改。" : "操作失败：" + result.Error;
                if (!result.Succeeded) MessageBox.Show(this, result.Output + "\r\n" + result.Error, "恢复未成功", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex) { status.Text = "恢复失败：" + ex.Message; MessageBox.Show(this, ex.Message, "恢复未成功"); }
            finally { writing = false; revisions.Enabled = restore.Enabled = true; }
        }
    }
}
