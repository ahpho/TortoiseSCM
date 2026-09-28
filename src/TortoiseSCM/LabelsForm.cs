// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class LabelsForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string repository;
        private readonly ListView labels = new ListView();
        private readonly ListView files = new ListView();
        private readonly TextBox filter = new TextBox();
        private readonly TextBox description = new TextBox();
        private readonly Label status = new Label();
        private readonly Button create = DialogStyle.Button("创建(&C)…");
        private readonly Button delete = DialogStyle.Button("删除(&D)…");
        private readonly Button browse = DialogStyle.Button("浏览快照(&B)…");
        private readonly Button refresh = DialogStyle.Button("刷新(&R)");
        private readonly Button cancel = DialogStyle.Button("取消读取");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource request;
        private IList<PlasticLabel> entries = new List<PlasticLabel>();
        private Func<CancellationToken, Task<IList<PlasticLabel>>> getLabels;
        private Func<long, CancellationToken, Task<PlasticChangesetDetails>> getChanges;
        private Func<PlasticLabel, CancellationToken, Task<PlasticCommandResult>> deleteLabel;
        private Func<string, DialogResult> confirm;
        private bool busy;
        private bool writing;
        private bool rendering;

        internal LabelsForm(PlasticClient client, string path)
        {
            this.client = client;
            var workspace = client.DiscoverWorkspace(path);
            if (workspace == null) throw new InvalidOperationException("请选择 Plastic 工作区。");
            root = workspace.RootPath; repository = workspace.Repository;
            getLabels = token => client.GetLabelsAsync(root, repository, token);
            getChanges = (cs, token) => client.GetLabelChangesetAsync(root, cs, repository, token);
            deleteLabel = (label, token) => client.DeleteLabelAsync(root, label.Name, label.Id, label.Changeset, repository, token);
            confirm = message => MessageBox.Show(this, message, "删除标签 - TortoiseSCM", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            DialogStyle.Apply(this); Text = "标签 - TortoiseSCM"; Size = new Size(1020, 720); MinimumSize = new Size(800, 560);
            // IDD_BROWSE_REFS: native report list, filter row and bottom actions.
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = "仓库：" + repository + "（所有分支的标签）", Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var search = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            search.Controls.Add(new Label { Text = "筛选(&F)：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            filter.Dock = DockStyle.Fill; filter.AccessibleName = "筛选标签名称、版本、作者或说明"; filter.TextChanged += delegate { if (!busy) RenderLabels(); };
            search.Controls.Add(filter, 1, 0); layout.Controls.Add(search, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(980, 500), SplitterDistance = 235, Panel1MinSize = 90, Panel2MinSize = 120 };
            labels.Dock = DockStyle.Fill; labels.View = View.Details; labels.MultiSelect = false; DialogStyle.ApplyList(labels); labels.AccessibleName = "仓库标签列表";
            labels.Columns.Add("名称", 235); labels.Columns.Add("变更集", 85); labels.Columns.Add("分支", 180); labels.Columns.Add("作者", 120); labels.Columns.Add("日期", 155); labels.Columns.Add("说明", 250);
            labels.SelectedIndexChanged += async delegate { if (!busy && !rendering) await LoadDetailsAsync(); };
            labels.DoubleClick += delegate { OpenSnapshot(); };
            var menu = new ContextMenuStrip(); var browseItem = menu.Items.Add("浏览标签快照…", null, delegate { OpenSnapshot(); });
            var deleteItem = menu.Items.Add("删除标签…", null, async delegate { await DeleteAsync(); });
            menu.Opening += delegate { browseItem.Enabled = browse.Enabled; deleteItem.Enabled = delete.Enabled; }; labels.ContextMenuStrip = menu;
            labels.MouseUp += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Right) { var row = labels.GetItemAt(e.X, e.Y); if (row != null) { row.Selected = true; row.Focused = true; } } };
            split.Panel1.Controls.Add(labels);
            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            description.Dock = DockStyle.Fill; description.Multiline = true; description.ReadOnly = true; description.ScrollBars = ScrollBars.Vertical; description.AccessibleName = "标签与提交说明";
            detail.Controls.Add(description, 0, 0);
            files.Dock = DockStyle.Fill; files.View = View.Details; DialogStyle.ApplyList(files); files.AccessibleName = "标签目标变更集的更改文件";
            files.Columns.Add("操作", 80); files.Columns.Add("路径", 550); files.Columns.Add("原路径", 280); detail.Controls.Add(files, 0, 1); split.Panel2.Controls.Add(detail); layout.Controls.Add(split, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.UseMnemonic = false; layout.Controls.Add(status, 0, 3);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            browse.Width = 110;
            actions.Controls.Add(create); actions.Controls.Add(delete); actions.Controls.Add(browse); actions.Controls.Add(refresh); actions.Controls.Add(cancel);
            footer.Controls.Add(actions, 0, 0); footer.Controls.Add(close, 1, 0); layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
            create.Click += async delegate { await CreateAsync(); }; delete.Click += async delegate { await DeleteAsync(); }; browse.Click += delegate { OpenSnapshot(); };
            refresh.Click += async delegate { await LoadAsync(); }; cancel.Click += delegate { if (request != null) request.Cancel(); }; close.Click += delegate { Close(); }; CancelButton = close;
            Shown += async delegate { await LoadAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (writing) { e.Cancel = true; return; } lifetime.Cancel(); if (request != null) request.Cancel(); };
            UpdateButtons();
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository || !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库已改变，请关闭并重新打开标签窗口。");
        }

        private PlasticLabel SelectedLabel()
        { return labels.SelectedItems.Count == 1 ? labels.SelectedItems[0].Tag as PlasticLabel : null; }

        private void UpdateButtons()
        {
            bool available = !busy && !writing;
            labels.Enabled = filter.Enabled = refresh.Enabled = create.Enabled = available;
            delete.Enabled = browse.Enabled = available && SelectedLabel() != null;
            cancel.Enabled = busy && !writing; close.Enabled = !writing;
        }

        private async Task ReadAsync(Func<CancellationToken, Task> work)
        {
            if (busy || writing || lifetime.IsCancellationRequested) return;
            busy = true; UpdateButtons(); request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try { ValidateContext(); await work(request.Token); }
            catch (Exception ex) {
                if (!lifetime.IsCancellationRequested) {
                    ClearSelection(); status.Text = ex is OperationCanceledException ? "读取已取消。" : "读取失败：" + ex.Message;
                }
            }
            finally { request.Dispose(); request = null; busy = false; if (!lifetime.IsCancellationRequested) UpdateButtons(); }
        }

        private async Task LoadAsync()
        {
            if (busy || writing) return;
            entries = new List<PlasticLabel>(); RenderLabels(); status.Text = "正在读取仓库标签…";
            await ReadAsync(async token => {
                var result = await getLabels(token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (result == null || result.Any(item => item == null || item.Repository != repository) || result.Select(item => item.Id).Distinct().Count() != result.Count)
                    throw new InvalidOperationException("标签列表无效或仓库不匹配。");
                entries = result; RenderLabels();
            });
        }

        private void ClearSelection()
        {
            rendering = true;
            try { foreach (ListViewItem row in labels.Items) row.Selected = false; files.Items.Clear(); description.Clear(); }
            finally { rendering = false; }
        }

        private void RenderLabels()
        {
            rendering = true;
            try {
                labels.Items.Clear(); files.Items.Clear(); description.Clear();
                string text = filter.Text.Trim();
                foreach (var entry in entries) {
                    string[] values = { entry.Name, "cs:" + entry.Changeset, entry.Branch ?? "", entry.Owner ?? "", entry.Date ?? "", entry.Comment ?? "" };
                    if (text.Length > 0 && !values.Any(value => value.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0)) continue;
                    labels.Items.Add(new ListViewItem(values) { Tag = entry });
                }
                status.Text = labels.Items.Count + " / " + entries.Count + " 个标签。选择标签查看目标提交；双击浏览完整快照。";
            }
            finally { rendering = false; UpdateButtons(); }
        }

        private async Task LoadDetailsAsync()
        {
            files.Items.Clear(); description.Clear(); UpdateButtons(); var selected = SelectedLabel(); if (selected == null) return;
            status.Text = "正在读取 " + selected.Name + " → cs:" + selected.Changeset + "…";
            await ReadAsync(async token => {
                var details = await getChanges(selected.Changeset, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (!Object.ReferenceEquals(SelectedLabel(), selected)) return;
                if (details == null || details.Changeset == null || details.Changeset.Changeset != selected.Changeset || details.Files == null)
                    throw new InvalidOperationException("提交明细与标签目标不一致。");
                description.Text = ("标签：" + selected.Name + " → cs:" + selected.Changeset + "\n" + selected.Comment + "\n提交说明：" + details.Changeset.Comment).Replace("\r\n", "\n").Replace("\n", "\r\n");
                foreach (var file in details.Files) files.Items.Add(new ListViewItem(new[] { file.Status, file.Path, file.OldPath }) { Tag = file });
                status.Text = "cs:" + selected.Changeset + " · " + details.Files.Count + " 个更改项（完整提交）。浏览快照不会切换工作区。";
            });
        }

        private RepositoryBrowserForm CreateSnapshotBrowser()
        {
            ValidateContext(); var selected = SelectedLabel();
            if (selected == null || selected.Repository != repository) throw new InvalidOperationException("请重新选择标签。");
            return new RepositoryBrowserForm(client, root, selected.Changeset, repository);
        }

        private void OpenSnapshot()
        {
            if (!browse.Enabled) return;
            try { using (var dialog = CreateSnapshotBrowser()) dialog.ShowDialog(this); }
            catch (Exception ex) { status.Text = "无法打开快照：" + ex.Message; }
        }

        private async Task CreateAsync()
        {
            if (!create.Enabled) return;
            try { ValidateContext(); using (var dialog = new LabelCreateForm(client, root, repository, null)) { dialog.ShowDialog(this); } await LoadAsync(); }
            catch (Exception ex) { status.Text = "无法创建标签：" + ex.Message; }
        }

        private async Task DeleteAsync()
        {
            if (!delete.Enabled) return; var selected = SelectedLabel(); if (selected == null) return;
            // Copy identity before the modal confirmation; backend checks stable id and target again.
            var target = new PlasticLabel { Id = selected.Id, Name = selected.Name, Changeset = selected.Changeset, Repository = selected.Repository };
            bool attempted = false; bool succeeded = false;
            try {
                ValidateContext();
                PlasticClient.ValidateLabelName(target.Name);
                if (confirm("从仓库 " + repository + " 删除标签：\r\n\r\n" + target.Name + " → cs:" + target.Changeset + "（ID " + target.Id + "）\r\n\r\n仅删除标签，不删除提交或工作区文件。继续删除？") != DialogResult.Yes) return;
                ValidateContext(); writing = attempted = true; UpdateButtons(); status.Text = "正在删除标签，请等待完成…";
                var result = await deleteLabel(target, CancellationToken.None);
                if (!result.Succeeded) throw new PlasticCommandException(result); succeeded = true;
            }
            catch (Exception ex) {
                // An uncertain mutation must not remain one click away from resubmission.
                entries = new List<PlasticLabel>(); RenderLabels();
                status.Text = (attempted ? "删除结果未确认，请刷新列表核对：" : "无法删除标签：") + ex.Message;
            }
            finally { writing = false; UpdateButtons(); }
            if (succeeded) { await LoadAsync(); status.Text = "标签删除成功。" + status.Text; }
        }
    }
}
