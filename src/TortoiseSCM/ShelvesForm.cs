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
    // Native log-style browser: server records above, selected record's files below.
    internal sealed class ShelvesForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string repository;
        private readonly ListView shelves = new ListView();
        private readonly ListView files = new ListView();
        private readonly TextBox filter = new TextBox();
        private readonly TextBox description = new TextBox();
        private readonly Label status = new Label();
        private readonly Button refresh = DialogStyle.Button("刷新(&R)");
        private readonly Button cancel = DialogStyle.Button("取消加载");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource request;
        private IList<PlasticShelve> entries = new List<PlasticShelve>();
        private readonly Func<string, CancellationToken, Task<IList<PlasticShelve>>> getShelves;
        private readonly Func<string, long, CancellationToken, Task<IList<PlasticChangesetFile>>> getChanges;
        private bool busy;
        private bool rendering;

        internal ShelvesForm(PlasticClient client, string path)
        {
            this.client = client;
            var workspace = client.DiscoverWorkspace(path);
            if (workspace == null) throw new InvalidOperationException("请选择 Plastic 工作区。");
            root = workspace.RootPath; repository = workspace.Repository;
            getShelves = client.GetShelvesAsync; getChanges = client.GetShelveChangesAsync;
            DialogStyle.Apply(this); Text = "暂存集 - TortoiseSCM";
            Size = new Size(1020, 720); MinimumSize = new Size(760, 550);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = "仓库：" + repository + "\r\n显示当前仓库中的暂存集（不限于当前目录）。", Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var search = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            search.Controls.Add(new Label { Text = "筛选(&F)：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            filter.Dock = DockStyle.Fill; filter.AccessibleName = "筛选暂存集编号、作者、日期或说明";
            filter.TextChanged += delegate { if (!busy) RenderShelves(); }; search.Controls.Add(filter, 1, 0); layout.Controls.Add(search, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(980, 480), SplitterDistance = 220, Panel1MinSize = 90, Panel2MinSize = 110 };
            shelves.Dock = DockStyle.Fill; shelves.View = View.Details; shelves.MultiSelect = false; DialogStyle.ApplyList(shelves);
            shelves.Columns.Add("暂存集", 90); shelves.Columns.Add("作者", 150); shelves.Columns.Add("日期", 160); shelves.Columns.Add("说明", 480);
            shelves.AccessibleName = "仓库暂存集列表";
            shelves.SelectedIndexChanged += async delegate { if (!rendering && !busy) await LoadDetailsAsync(); };
            split.Panel1.Controls.Add(shelves);
            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            description.Dock = DockStyle.Fill; description.Multiline = true; description.ReadOnly = true; description.ScrollBars = ScrollBars.Vertical;
            description.AccessibleName = "所选暂存集说明"; detail.Controls.Add(description, 0, 0);
            files.Dock = DockStyle.Fill; files.View = View.Details; DialogStyle.ApplyList(files);
            files.Columns.Add("路径", 470); files.Columns.Add("状态", 85); files.Columns.Add("原路径", 350); files.AccessibleName = "暂存集更改文件";
            detail.Controls.Add(files, 0, 1); split.Panel2.Controls.Add(detail); layout.Controls.Add(split, 0, 2);
            layout.Controls.Add(new Label { Text = "在待定更改窗口勾选文件后，可通过“操作 → 保存勾选项为暂存集”保存。\r\n保存后保留本地修改。本窗口暂不支持应用或删除暂存集；恢复请使用官方客户端。", Dock = DockStyle.Fill }, 0, 3);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(status, 0, 4);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty }; left.Controls.Add(refresh); left.Controls.Add(cancel);
            footer.Controls.Add(left, 0, 0); footer.Controls.Add(close, 1, 0); layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
            refresh.Click += async delegate { await LoadAsync(); }; cancel.Click += delegate { if (request != null) request.Cancel(); };
            close.Click += delegate { Close(); }; CancelButton = close;
            Shown += async delegate { await LoadAsync(); };
            FormClosing += delegate { lifetime.Cancel(); if (request != null) request.Cancel(); };
            UpdateButtons();
        }

        private void UpdateButtons()
        { refresh.Enabled = filter.Enabled = shelves.Enabled = !busy; cancel.Enabled = busy; }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository || !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库已改变，请关闭并重新打开暂存集窗口。");
        }

        private async Task WorkAsync(Func<CancellationToken, Task> work)
        {
            if (busy || lifetime.IsCancellationRequested) return;
            busy = true; UpdateButtons();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = cancellation;
            try { ValidateContext(); await work(cancellation.Token); }
            catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) status.Text = "已取消加载。"; }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = "加载失败：" + ex.Message; }
            finally { request = null; cancellation.Dispose(); busy = false; if (!lifetime.IsCancellationRequested) UpdateButtons(); }
        }

        private async Task LoadAsync()
        {
            if (busy) return;
            entries = new List<PlasticShelve>(); RenderShelves(); status.Text = "正在读取暂存集…";
            await WorkAsync(async token => {
                var result = await getShelves(root, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (result == null || result.Any(item => item == null || item.Repository != repository) || result.Select(item => item.ShelveId).Distinct().Count() != result.Count)
                    throw new InvalidOperationException("暂存集列表无效或仓库不匹配。");
                entries = result; RenderShelves();
            });
        }

        private void RenderShelves()
        {
            rendering = true; shelves.BeginUpdate();
            try {
                shelves.Items.Clear(); files.Items.Clear(); description.Clear();
                string text = filter.Text.Trim();
                foreach (var entry in entries) {
                    string[] values = { "sh:" + entry.ShelveId, entry.Owner ?? "", entry.Date ?? "", entry.Comment ?? "" };
                    if (text.Length > 0 && !values.Any(value => value.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0)) continue;
                    shelves.Items.Add(new ListViewItem(values) { Tag = entry });
                }
                status.Text = shelves.Items.Count + " / " + entries.Count + " 个暂存集。选择一项查看更改文件。";
            }
            finally { shelves.EndUpdate(); rendering = false; }
        }

        private async Task LoadDetailsAsync()
        {
            if (busy) return;
            files.Items.Clear(); description.Clear();
            if (shelves.SelectedItems.Count != 1) return;
            var selected = (PlasticShelve)shelves.SelectedItems[0].Tag;
            status.Text = "正在读取 sh:" + selected.ShelveId + "…";
            await WorkAsync(async token => {
                var details = await getChanges(root, selected.ShelveId, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (shelves.SelectedItems.Count != 1 || !Object.ReferenceEquals(shelves.SelectedItems[0].Tag, selected)) return;
                description.Text = selected.Comment;
                foreach (var file in details ?? new List<PlasticChangesetFile>())
                    if (file != null) files.Items.Add(new ListViewItem(new[] { file.Path ?? "", file.Status ?? "", file.OldPath ?? "" }) { Tag = file });
                status.Text = "sh:" + selected.ShelveId + "：" + details.Count + " 个更改项。";
            });
        }
    }
}
