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
        private readonly TreeView branchTree = new TreeView();
        private readonly ComboBox viewMode = new ComboBox();
        private readonly Button locateCurrent = DialogStyle.Button("定位当前");
        private readonly TableLayoutPanel branchViews = new TableLayoutPanel();
        private readonly Dictionary<string, TreeNode> treeNodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
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
        private readonly Button rename = DialogStyle.Button("重命名…");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly ToolStripMenuItem createChild = new ToolStripMenuItem("创建子分支…");
        private readonly ToolStripMenuItem branchHistory = new ToolStripMenuItem("显示本分支历史…");
        private readonly ToolStripMenuItem renameBranch = new ToolStripMenuItem("重命名分支…");
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
            var search = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, Margin = Padding.Empty };
            search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            search.Controls.Add(new Label { Text = "筛选(&F)：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            filter.Dock = DockStyle.Fill; filter.AccessibleName = "筛选分支名称、作者、日期或说明";
            filter.TextChanged += delegate { if (!busy) RenderBranches(); }; search.Controls.Add(filter, 1, 0);
            viewMode.DropDownStyle = ComboBoxStyle.DropDownList; viewMode.Dock = DockStyle.Fill; viewMode.AccessibleName = "分支显示方式";
            viewMode.Items.AddRange(new object[] { "列表", "层级" }); viewMode.SelectedIndex = 0;
            viewMode.SelectedIndexChanged += delegate { ChangeView(); }; search.Controls.Add(viewMode, 2, 0);
            locateCurrent.Dock = DockStyle.Fill; locateCurrent.Click += delegate { LocateCurrent(); }; search.Controls.Add(locateCurrent, 3, 0);
            layout.Controls.Add(search, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                Size = new Size(1020, 530), SplitterDistance = 260, Panel1MinSize = 100, Panel2MinSize = 130 };
            branches.Dock = DockStyle.Fill; branches.View = View.Details; branches.MultiSelect = false; DialogStyle.ApplyList(branches);
            branches.Columns.Add("分支", 380); branches.Columns.Add("当前", 55); branches.Columns.Add("头提交", 85);
            branches.Columns.Add("作者", 110); branches.Columns.Add("创建日期", 155); branches.Columns.Add("说明", 260);
            branches.AccessibleName = "仓库分支列表";
            branches.SelectedIndexChanged += delegate { files.Items.Clear(); description.Clear(); UpdateButtons(); };
            branches.DoubleClick += async delegate { await ShowHeadAsync(); };
            var menu = new ContextMenuStrip(); menu.Items.Add(branchHistory); menu.Items.Add(createChild); menu.Items.Add(renameBranch);
            branchHistory.Click += delegate { OpenBranchHistory(); };
            createChild.Click += async delegate { await OpenCreateAsync(); };
            renameBranch.Click += async delegate { await OpenRenameAsync(); };
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { UpdateButtons(); e.Cancel = busy || SelectedBranch() == null; };
            branches.ContextMenuStrip = menu;
            branchTree.Dock = DockStyle.Fill; branchTree.HideSelection = false; branchTree.FullRowSelect = true; branchTree.ShowNodeToolTips = true;
            branchTree.BorderStyle = BorderStyle.Fixed3D; branchTree.AccessibleName = "分支父子层级";
            branchTree.AfterSelect += delegate { files.Items.Clear(); description.Clear(); UpdateButtons(); };
            branchTree.NodeMouseClick += delegate(object sender, TreeNodeMouseClickEventArgs e) { if (e.Button == MouseButtons.Right) branchTree.SelectedNode = e.Node; };
            branchTree.NodeMouseDoubleClick += async delegate { await ShowHeadAsync(); };
            branchTree.ContextMenuStrip = menu;
            branchViews.Dock = DockStyle.Fill; branchViews.ColumnCount = 1; branchViews.RowCount = 2; branchViews.Margin = Padding.Empty;
            branchViews.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            branchViews.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); branchViews.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            branchViews.Controls.Add(new Label { Text = "分支层级（父子关系；不含提交/合并关系）", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            var browser = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            browser.Controls.Add(branches); browser.Controls.Add(branchTree); branchTree.Visible = false;
            branchViews.Controls.Add(browser, 0, 1); split.Panel1.Controls.Add(branchViews);
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
            left.Controls.Add(refresh); left.Controls.Add(cancel); left.Controls.Add(rename); footer.Controls.Add(left, 0, 0);
            var right = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            head.Width = 110; merge.Width = switchBranch.Width = 120;
            right.Controls.Add(head); right.Controls.Add(merge); right.Controls.Add(switchBranch); right.Controls.Add(close); footer.Controls.Add(right, 1, 0);
            layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
            refresh.Click += async delegate { await LoadAsync(); };
            cancel.Click += delegate { if (request != null && !writing) request.Cancel(); };
            head.Click += async delegate { await ShowHeadAsync(); };
            merge.Click += async delegate { await OpenMergeAsync(); };
            switchBranch.Click += async delegate { await SwitchAsync(); };
            rename.Click += async delegate { await OpenRenameAsync(); };
            close.Click += delegate { Close(); }; CancelButton = close;
            Shown += async delegate { await LoadAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (writing) { e.Cancel = true; return; }
                lifetime.Cancel(); if (request != null) request.Cancel();
            };
            UpdateButtons();
        }

        private PlasticBranch SelectedBranch()
        { return viewMode.SelectedIndex == 1 ? (branchTree.SelectedNode == null ? null : branchTree.SelectedNode.Tag as PlasticBranch) :
                (branches.SelectedItems.Count == 1 ? branches.SelectedItems[0].Tag as PlasticBranch : null); }

        private void UpdateButtons()
        {
            var selected = SelectedBranch();
            refresh.Enabled = filter.Enabled = branches.Enabled = branchTree.Enabled = viewMode.Enabled = !busy;
            locateCurrent.Enabled = !busy && entries.Any(branch => branch.IsCurrent);
            cancel.Enabled = busy && !writing; close.Enabled = !writing;
            head.Enabled = !busy && selected != null;
            branchHistory.Enabled = createChild.Enabled = !busy && selected != null;
            merge.Enabled = switchBranch.Enabled = !busy && !partial && selected != null && !selected.IsCurrent;
            rename.Enabled = renameBranch.Enabled = !busy && BranchRenameForm.CanRename(selected) &&
                !entries.Any(branch => String.Equals(branch.Parent, selected.Name, StringComparison.OrdinalIgnoreCase));
            renameBranch.ToolTipText = rename.Enabled ? "重命名服务器分支，不切换当前工作区。" :
                "仅支持身份完整、名称位于父分支下的非当前叶分支；根分支、有子分支或特殊名称层级不可重命名。";
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
            entries = new List<PlasticBranch>(); branches.Items.Clear(); branchTree.Nodes.Clear(); treeNodes.Clear(); files.Items.Clear(); description.Clear();
            status.Text = "正在读取分支…";
            bool loaded = false;
            await WorkAsync(async token => {
                var workspace = await client.GetWorkspaceAsync(root, token); ValidateContext();
                var result = await client.GetBranchesAsync(root, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (result.Any(branch => branch.Repository != repository)) throw new InvalidOperationException("分支仓库不匹配。");
                partial = workspace.IsPartial; entries = result;
                loaded = true;
                context.Text = "仓库：" + repository + "\r\n工作区：" + root + (partial ? "（Partial：可浏览、创建及重命名分支，不能切换或合并）" : "（Standard）");
            }, false);
            if (!lifetime.IsCancellationRequested && loaded) RenderBranches();
        }

        private void RenderBranches()
        {
            files.Items.Clear(); description.Clear();
            branches.BeginUpdate();
            branchTree.BeginUpdate();
            try {
                branches.Items.Clear();
                branchTree.Nodes.Clear(); treeNodes.Clear();
                string text = filter.Text.Trim();
                var hierarchy = PlasticClient.BuildBranchHierarchy(entries, text);
                var matches = new HashSet<string>(hierarchy.Where(item => item.IsMatch).Select(item => item.Branch.Name), StringComparer.Ordinal);
                foreach (var entry in entries) {
                    var values = new[] { entry.Name, entry.IsCurrent ? "●" : "", "cs:" + entry.HeadChangeset, entry.Owner, entry.CreationDate, entry.Comment };
                    if (!matches.Contains(entry.Name)) continue;
                    branches.Items.Add(new ListViewItem(values) { Tag = entry });
                }
                foreach (var item in hierarchy)
                {
                    var entry = item.Branch;
                    string displayName = !item.ParentMissing && !String.IsNullOrEmpty(entry.Parent) && entry.Name.StartsWith(entry.Parent + "/", StringComparison.Ordinal)
                        ? entry.Name.Substring(entry.Parent.Length + 1) : entry.Name;
                    var node = new TreeNode((entry.IsCurrent ? "[当前] " : "") + displayName + "  (cs:" + entry.HeadChangeset + ")" +
                        (item.ParentMissing ? " [父分支不可见]" : "")) { Tag = entry,
                        ForeColor = item.IsMatch ? SystemColors.WindowText : SystemColors.GrayText,
                        ToolTipText = entry.Name + "\r\n父分支：" + (entry.Parent ?? "") + "\r\n直接子分支：" + item.ChildCount + "\r\n" + entry.Owner + "  " + entry.CreationDate + "\r\n" + entry.Comment +
                            (item.IsMatch ? "" : "\r\n仅为筛选结果保留的祖先分支。") + (item.ParentMissing ? "\r\n原父分支未出现在服务器列表中。" : "") };
                    TreeNode parentNode;
                    if (!item.ParentMissing && !String.IsNullOrEmpty(entry.Parent) && treeNodes.TryGetValue(entry.Parent, out parentNode)) parentNode.Nodes.Add(node);
                    else branchTree.Nodes.Add(node);
                    treeNodes.Add(entry.Name, node);
                }
                // Expand shallow roots by default; filtered ancestor context is
                // expanded iteratively so deeply nested names do not recurse here.
                foreach (var item in hierarchy)
                    if (item.Depth == 0 || !item.IsMatch) treeNodes[item.Branch.Name].Expand();
                status.Text = branches.Items.Count + " / " + entries.Count + " 个匹配分支。右键可显示本分支历史或创建子分支。";
            }
            catch (ArgumentException ex) { RejectHierarchy(ex); }
            catch (InvalidDataException ex) { RejectHierarchy(ex); }
            finally { branchTree.EndUpdate(); branches.EndUpdate(); UpdateButtons(); }
        }

        private void RejectHierarchy(Exception error)
        {
            entries = new List<PlasticBranch>();
            branches.Items.Clear(); branchTree.Nodes.Clear(); treeNodes.Clear(); files.Items.Clear(); description.Clear();
            status.Text = "无法显示分支层级：" + error.Message;
        }

        private void ChangeView()
        {
            // Read the old visible surface, since SelectedIndex is already new.
            var selected = viewMode.SelectedIndex == 1 ? (branches.SelectedItems.Count == 1 ? branches.SelectedItems[0].Tag as PlasticBranch : null) :
                (branchTree.SelectedNode == null ? null : branchTree.SelectedNode.Tag as PlasticBranch);
            bool tree = viewMode.SelectedIndex == 1;
            branches.Visible = !tree; branchTree.Visible = tree; branchViews.RowStyles[0].Height = tree ? 24 : 0;
            files.Items.Clear(); description.Clear();
            SelectBranch(selected == null ? null : selected.Name); UpdateButtons();
        }

        private void SelectBranch(string name)
        {
            if (viewMode.SelectedIndex == 1)
            {
                TreeNode node; branchTree.SelectedNode = name != null && treeNodes.TryGetValue(name, out node) ? node : null;
                if (branchTree.SelectedNode != null)
                {
                    for (var parentNode = branchTree.SelectedNode.Parent; parentNode != null; parentNode = parentNode.Parent) parentNode.Expand();
                    branchTree.SelectedNode.EnsureVisible();
                }
            }
            else
            {
                foreach (ListViewItem row in branches.Items) row.Selected = name != null && ((PlasticBranch)row.Tag).Name == name;
                if (branches.SelectedItems.Count == 1) branches.SelectedItems[0].EnsureVisible();
            }
        }

        private void LocateCurrent()
        {
            if (!locateCurrent.Enabled) return;
            var current = entries.FirstOrDefault(branch => branch.IsCurrent); if (current == null) return;
            filter.Clear(); SelectBranch(current.Name);
            if (viewMode.SelectedIndex == 1) branchTree.Focus(); else branches.Focus();
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
            SelectBranch(created);
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

        private BranchRenameForm CreateRenameDialog()
        {
            ValidateContext(); UpdateButtons();
            if (!rename.Enabled) throw new InvalidOperationException("请选择身份完整、名称位于父分支下的非当前叶分支。");
            var workspace = client.DiscoverWorkspace(root);
            return new BranchRenameForm(client, root, workspace.Selector, SelectedBranch());
        }

        private async Task OpenRenameAsync()
        {
            if (!rename.Enabled) return;
            bool attempted = false; string renamed = null;
            try {
                using (var dialog = CreateRenameDialog()) {
                    dialog.ShowDialog(this); attempted = dialog.Attempted; renamed = dialog.RenamedBranch;
                }
            }
            catch (Exception ex) {
                entries = new List<PlasticBranch>(); RenderBranches();
                status.Text = "无法重命名：" + ex.Message; return;
            }
            if (!attempted) return;
            // All cached action targets are discarded even when the server result is uncertain.
            filter.Clear(); await LoadAsync();
            if (renamed != null) SelectBranch(renamed);
            else status.Text = "重命名结果未确认；已尝试刷新列表，请核对原名称和新名称。不会自动反向重命名。 " + status.Text;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { if (refresh.Enabled) refresh.PerformClick(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
