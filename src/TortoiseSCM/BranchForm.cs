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
    internal sealed class BranchForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string repository;
        private readonly ListView branches = new ListView();
        private readonly ListView files = new ListView();
        private readonly TextBox filter = new TextBox();
        private readonly TextBox description = new TextBox();
        private readonly Label status = new Label();
        private readonly Label context = new Label();
        private readonly Button refresh = DialogStyle.Button("刷新(&R)");
        private readonly Button cancel = DialogStyle.Button("取消加载");
        private readonly Button head = DialogStyle.Button("头提交详情");
        private readonly Button merge = DialogStyle.Button("合并到当前…");
        private readonly Button switchBranch = DialogStyle.Button("切换工作区…");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly ToolStripMenuItem createChild = new ToolStripMenuItem("创建子分支…");
        private readonly ToolStripMenuItem branchHistory = new ToolStripMenuItem("显示本分支历史…");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource request;
        private IList<PlasticBranch> entries = new List<PlasticBranch>();
        private bool busy;
        private bool writing;
        private bool partial;

        public BranchForm(PlasticClient client, string path)
        {
            this.client = client;
            var workspace = client.DiscoverWorkspace(path);
            if (workspace == null) throw new InvalidOperationException("请选择 Plastic 工作区。");
            root = workspace.RootPath; repository = workspace.Repository; partial = workspace.IsPartial;
            DialogStyle.Apply(this); Text = "分支 - TortoiseSCM";
            Size = new Size(1080, 730); MinimumSize = new Size(850, 550);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            context.Dock = DockStyle.Fill; context.AutoEllipsis = true; context.UseMnemonic = false;
            context.Text = "仓库：" + repository + "\r\n工作区：" + root; layout.Controls.Add(context, 0, 0);
            var search = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            search.Controls.Add(new Label { Text = "筛选(&F)：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            filter.Dock = DockStyle.Fill; filter.AccessibleName = "筛选分支名称、作者、日期或说明";
            filter.TextChanged += delegate { if (!busy) RenderBranches(); }; search.Controls.Add(filter, 1, 0); layout.Controls.Add(search, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                Size = new Size(1020, 530), SplitterDistance = 260, Panel1MinSize = 100, Panel2MinSize = 130 };
            branches.Dock = DockStyle.Fill; branches.View = View.Details; branches.MultiSelect = false; DialogStyle.ApplyList(branches);
            branches.Columns.Add("分支", 380); branches.Columns.Add("当前", 55); branches.Columns.Add("头提交", 85);
            branches.Columns.Add("作者", 110); branches.Columns.Add("创建日期", 155); branches.Columns.Add("说明", 260);
            branches.AccessibleName = "仓库分支列表";
            branches.SelectedIndexChanged += delegate { files.Items.Clear(); description.Clear(); UpdateButtons(); };
            branches.DoubleClick += async delegate { await ShowHeadAsync(); };
            var menu = new ContextMenuStrip(); menu.Items.Add(branchHistory); menu.Items.Add(createChild);
            branchHistory.Click += delegate { OpenBranchHistory(); };
            createChild.Click += async delegate { await OpenCreateAsync(); };
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { UpdateButtons(); e.Cancel = busy || SelectedBranch() == null; };
            branches.ContextMenuStrip = menu;
            split.Panel1.Controls.Add(branches);
            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 66)); detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            description.Dock = DockStyle.Fill; description.Multiline = true; description.ReadOnly = true; description.ScrollBars = ScrollBars.Vertical;
            description.AccessibleName = "所选分支头提交说明"; detail.Controls.Add(description, 0, 0);
            files.Dock = DockStyle.Fill; files.View = View.Details; DialogStyle.ApplyList(files);
            files.Columns.Add("路径", 510); files.Columns.Add("状态", 65); files.Columns.Add("原路径", 360);
            files.AccessibleName = "分支头提交修改的文件"; detail.Controls.Add(files, 0, 1); split.Panel2.Controls.Add(detail); layout.Controls.Add(split, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(status, 0, 3);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            left.Controls.Add(refresh); left.Controls.Add(cancel); footer.Controls.Add(left, 0, 0);
            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            head.Width = 110; merge.Width = switchBranch.Width = 120;
            right.Controls.Add(head); right.Controls.Add(merge); right.Controls.Add(switchBranch); right.Controls.Add(close); footer.Controls.Add(right, 1, 0);
            layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
            refresh.Click += async delegate { await LoadAsync(); };
            cancel.Click += delegate { if (request != null && !writing) request.Cancel(); };
            head.Click += async delegate { await ShowHeadAsync(); };
            merge.Click += async delegate { await OpenMergeAsync(); };
            switchBranch.Click += async delegate { await SwitchAsync(); };
            close.Click += delegate { Close(); }; CancelButton = close;
            Shown += async delegate { await LoadAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (writing) { e.Cancel = true; return; }
                lifetime.Cancel(); if (request != null) request.Cancel();
            };
            UpdateButtons();
        }

        private PlasticBranch SelectedBranch()
        { return branches.SelectedItems.Count == 1 ? branches.SelectedItems[0].Tag as PlasticBranch : null; }

        private void UpdateButtons()
        {
            var selected = SelectedBranch();
            refresh.Enabled = filter.Enabled = branches.Enabled = !busy;
            cancel.Enabled = busy && !writing; close.Enabled = !writing;
            head.Enabled = !busy && selected != null;
            branchHistory.Enabled = createChild.Enabled = !busy && selected != null;
            merge.Enabled = switchBranch.Enabled = !busy && !partial && selected != null && !selected.IsCurrent;
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository || !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库已改变，请关闭并重新打开分支窗口。");
        }

        private async Task WorkAsync(Func<CancellationToken, Task> work, bool write)
        {
            if (busy || lifetime.IsCancellationRequested) return;
            busy = true; writing = write; UpdateButtons();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = cancellation;
            try { ValidateContext(); await work(cancellation.Token); cancellation.Token.ThrowIfCancellationRequested(); ValidateContext(); }
            catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) status.Text = "已取消加载。"; }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = "操作失败：" + ex.Message; }
            finally { request = null; cancellation.Dispose(); busy = writing = false; if (!lifetime.IsCancellationRequested) UpdateButtons(); }
        }

        private async Task LoadAsync()
        {
            if (busy) return;
            entries = new List<PlasticBranch>(); branches.Items.Clear(); files.Items.Clear(); description.Clear();
            status.Text = "正在读取分支…";
            bool loaded = false;
            await WorkAsync(async token => {
                var workspace = await client.GetWorkspaceAsync(root, token); ValidateContext();
                var result = await client.GetBranchesAsync(root, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (result.Any(branch => branch.Repository != repository)) throw new InvalidOperationException("分支仓库不匹配。");
                partial = workspace.IsPartial; entries = result;
                loaded = true;
                context.Text = "仓库：" + repository + "\r\n工作区：" + root + (partial ? "（Partial：可浏览及创建分支，不能切换或合并）" : "（Standard）");
            }, false);
            if (!lifetime.IsCancellationRequested && loaded) RenderBranches();
        }

        private void RenderBranches()
        {
            files.Items.Clear(); description.Clear();
            branches.BeginUpdate();
            try {
                branches.Items.Clear();
                string text = filter.Text.Trim();
                foreach (var entry in entries) {
                    var values = new[] { entry.Name, entry.IsCurrent ? "●" : "", "cs:" + entry.HeadChangeset, entry.Owner, entry.CreationDate, entry.Comment };
                    if (text.Length > 0 && !values.Any(value => (value ?? "").IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0)) continue;
                    branches.Items.Add(new ListViewItem(values) { Tag = entry });
                }
                status.Text = branches.Items.Count + " / " + entries.Count + " 个分支。右键可显示本分支历史或创建子分支。";
            }
            finally { branches.EndUpdate(); UpdateButtons(); }
        }

        private async Task ShowHeadAsync()
        {
            if (!head.Enabled) return;
            var selected = SelectedBranch(); files.Items.Clear(); description.Clear(); status.Text = "正在读取分支头提交…";
            await WorkAsync(async token => {
                long changeset = await client.ResolveBranchHeadAsync(root, selected.Name, token); ValidateContext();
                var details = await client.GetChangesetAsync(root, changeset, token); token.ThrowIfCancellationRequested(); ValidateContext();
                description.Text = selected.Name + "  ·  cs:" + changeset + "  ·  " + details.Changeset.Owner + "  ·  " + details.Changeset.CreationDate + "\r\n" + details.Changeset.Comment;
                foreach (var file in details.Files) files.Items.Add(new ListViewItem(new[] { file.Path, file.Status, file.OldPath ?? "" }) { Tag = file });
                status.Text = "头提交 cs:" + changeset + "：" + details.Files.Count + " 项修改。此列表显示该提交的修改，并非分支的全部历史。";
            }, false);
        }

        private async Task OpenMergeAsync()
        {
            if (!merge.Enabled) return;
            var selected = SelectedBranch(); MergeForm dialog = null;
            await WorkAsync(async token => {
                var workspace = await client.GetWorkspaceAsync(root, token); ValidateContext();
                if (workspace.IsPartial) throw new InvalidOperationException("Partial 工作区不能从此处合并。");
                if (client.HasSavedMergeSession(root) || File.Exists(Path.Combine(root, ".plastic", "plastic.mergeprogress")))
                    throw new InvalidOperationException("请先从合并菜单完成或撤销当前合并会话。");
                dialog = await CreateMergeDialogAsync(workspace, selected, token, client.ResolveBranchHeadAsync);
            }, false);
            if (dialog == null) return;
            using (dialog) { if (lifetime.IsCancellationRequested) return; dialog.ShowDialog(this); }
            await LoadAsync();
        }

        private async Task<MergeForm> CreateMergeDialogAsync(PlasticWorkspace destination, PlasticBranch selected,
            CancellationToken token, Func<string, string, CancellationToken, Task<long>> resolveHead)
        {
            // Keep the destination captured before the asynchronous head lookup. A
            // same-repository external branch switch must not silently retarget it.
            string expectedRepository = destination.Repository, expectedSelector = destination.Selector;
            ValidateContext();
            long changeset = await resolveHead(root, selected.Name, token);
            token.ThrowIfCancellationRequested(); ValidateContext();
            var current = client.DiscoverWorkspace(root);
            if (current == null || current.Repository != expectedRepository || current.Selector != expectedSelector)
                throw new InvalidOperationException("合并目标工作区或分支已改变，请刷新分支列表后重试。");
            // The constructor checks the same expected values again, covering a
            // selector change between the preceding check and dialog construction.
            return new MergeForm(client, root, changeset, selected.Name, expectedRepository, expectedSelector);
        }

        private HistoryForm CreateBranchHistoryDialog()
        {
            ValidateContext(); var selected = SelectedBranch();
            if (busy || selected == null) throw new InvalidOperationException("请先选择分支。");
            return new HistoryForm(client, root, root, selected.Name, repository);
        }

        private void OpenBranchHistory()
        {
            if (!branchHistory.Enabled) return;
            try { using (var dialog = CreateBranchHistoryDialog()) dialog.ShowDialog(this); }
            catch (Exception ex) { status.Text = "无法打开分支历史：" + ex.Message; }
        }

        private async Task OpenCreateAsync()
        {
            if (!createChild.Enabled) return;
            var selected = SelectedBranch(); BranchCreateForm dialog = null;
            await WorkAsync(async token => {
                var workspace = await client.GetWorkspaceAsync(root, token); ValidateContext();
                dialog = await CreateChildDialogAsync(workspace, selected, token, client.ResolveBranchHeadAsync);
            }, false);
            if (dialog == null) return;
            string created = null;
            using (dialog) {
                if (lifetime.IsCancellationRequested) return;
                if (dialog.ShowDialog(this) == DialogResult.OK) created = dialog.CreatedBranch;
            }
            if (created == null) return;
            filter.Clear(); await LoadAsync();
            foreach (ListViewItem row in branches.Items)
                if (((PlasticBranch)row.Tag).Name == created) { row.Selected = true; row.EnsureVisible(); break; }
        }

        private async Task<BranchCreateForm> CreateChildDialogAsync(PlasticWorkspace destination, PlasticBranch selected,
            CancellationToken token, Func<string, string, CancellationToken, Task<long>> resolveHead)
        {
            string expectedRepository = destination.Repository, expectedSelector = destination.Selector;
            ValidateContext();
            long changeset = await resolveHead(root, selected.Name, token);
            token.ThrowIfCancellationRequested(); ValidateContext();
            // Constructor compares the pre-resolution context again rather than
            // capturing whatever workspace happens to be current after the await.
            return new BranchCreateForm(client, root, expectedRepository, expectedSelector, selected.Name, changeset);
        }

        private async Task SwitchAsync()
        {
            if (!switchBranch.Enabled) return;
            var selected = SelectedBranch();
            if (MessageBox.Show(this, "将整个工作区切换到分支：\r\n" + selected.Name + "\r\n\r\n" + root +
                "\r\n\r\n此操作会更新整个工作区，要求没有待定更改及合并会话。继续？", Text, MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            status.Text = "正在切换整个工作区，请等待完成…";
            bool success = false;
            await WorkAsync(async token => {
                var result = await client.SwitchBranchAsync(root, selected.Name, token);
                if (result.ExitCode != 0) throw new PlasticCommandException(result);
                success = true;
            }, true);
            if (success) { OverlayCacheHost.TrackAndStart(root); await LoadAsync(); }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { if (refresh.Enabled) refresh.PerformClick(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
