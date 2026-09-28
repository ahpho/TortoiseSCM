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
    internal sealed class RevisionGraphForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root, repository;
        private readonly RevisionGraphControl graph = new RevisionGraphControl();
        private readonly ListView revisions = new ListView(), files = new ListView();
        private readonly TextBox description = new TextBox();
        private readonly Label status = new Label();
        private readonly NumericUpDown before = new NumericUpDown();
        private readonly Button jump = DialogStyle.Button("跳转(&G)");
        private readonly Button latest = DialogStyle.Button("最新(&L)");
        private readonly Button previous = DialogStyle.Button("返回(&B)");
        private readonly Button older = DialogStyle.Button("更早(&O)");
        private readonly Button refresh = DialogStyle.Button("刷新(&R)");
        private readonly Button cancel = DialogStyle.Button("取消读取");
        private readonly Button browse = DialogStyle.Button("浏览快照…");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly Stack<long?> cursors = new Stack<long?>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource request;
        private Func<long?, CancellationToken, Task<PlasticRevisionGraphPage>> getGraph;
        private Func<long, CancellationToken, Task<PlasticChangesetDetails>> getChanges;
        private PlasticRevisionGraphPage page;
        private long? currentBefore;
        private bool busy, rendering;

        internal RevisionGraphForm(PlasticClient client, string path, long? initialBefore)
        {
            this.client = client; var workspace = client.DiscoverWorkspace(path);
            if (workspace == null) throw new InvalidOperationException("请选择 Plastic 工作区。");
            root = workspace.RootPath; repository = workspace.Repository; currentBefore = initialBefore;
            getGraph = (cursor, token) => client.GetRevisionGraphAsync(root, cursor, 100, repository, token);
            getChanges = (cs, token) => client.GetGraphChangesetAsync(root, cs, repository, token);
            DialogStyle.Apply(this); Text = "版本关系图 - TortoiseSCM"; Size = new Size(1140, 800); MinimumSize = new Size(900, 640);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 25, 34, 24 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(new Label { Text = "仓库：" + repository + "（所有分支，每页最多 100 个提交）", Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            navigation.Controls.Add(new Label { Text = "变更集小于：", AutoSize = true, Margin = new Padding(0, 5, 3, 0) });
            before.Maximum = Int64.MaxValue; before.Minimum = 0; before.Width = 150; before.Value = initialBefore ?? 0; before.AccessibleName = "跳转到变更集编号之前，不包含该编号";
            navigation.Controls.Add(before); foreach (var button in new[] { jump, latest, previous, older, refresh, cancel }) navigation.Controls.Add(button); layout.Controls.Add(navigation, 0, 1);
            layout.Controls.Add(new Label { Text = "实线：父提交  ·  虚线：merge  ·  点划线：其他原生关系（类型见明细）  ·  箭头指向目标提交", Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 2);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1100, 620), SplitterDistance = 335, Panel1MinSize = 150, Panel2MinSize = 150 };
            var top = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1100, 330), SplitterDistance = 440, Panel1MinSize = 250, Panel2MinSize = 250 };
            graph.Dock = DockStyle.Fill; top.Panel1.Controls.Add(graph);
            SetupList(revisions, "提交列表；选择提交查看父提交及合并关系", new[] { "变更集", "分支", "作者", "日期", "说明" }, new[] { 80, 180, 100, 145, 300 }); top.Panel2.Controls.Add(revisions);
            revisions.SelectedIndexChanged += async delegate { if (!rendering && !busy) await LoadDetailsAsync(); };
            revisions.DoubleClick += delegate { OpenSnapshot(); };
            revisions.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { OpenSnapshot(); e.Handled = true; } };
            graph.NodeSelected += delegate(long cs) { if (busy) return; foreach (ListViewItem row in revisions.Items) { row.Selected = ((PlasticGraphNode)row.Tag).Changeset == cs; if (row.Selected) { row.Focused = true; row.EnsureVisible(); } } };
            var menu = new ContextMenuStrip(); menu.Items.Add("浏览此版本的完整仓库…", null, delegate { OpenSnapshot(); }); menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = !browse.Enabled; }; revisions.ContextMenuStrip = menu;
            split.Panel1.Controls.Add(top);
            var lower = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(1100, 280), SplitterDistance = 125, Panel1MinSize = 65, Panel2MinSize = 65 };
            description.Dock = DockStyle.Fill; description.ReadOnly = true; description.Multiline = true; description.ScrollBars = ScrollBars.Both; description.WordWrap = false; description.BackColor = SystemColors.Window; description.AccessibleName = "提交说明与准确的父提交、合并及基线关系"; lower.Panel1.Controls.Add(description);
            SetupList(files, "选定提交的更改文件", new[] { "操作", "路径", "原路径" }, new[] { 90, 600, 300 }); lower.Panel2.Controls.Add(files); split.Panel2.Controls.Add(lower); layout.Controls.Add(split, 0, 3);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.UseMnemonic = false; layout.Controls.Add(status, 0, 4);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty }; footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); footer.Controls.Add(browse, 0, 0); footer.Controls.Add(close, 1, 0); browse.Width = 120; layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
            jump.Click += async delegate { await NavigateAsync((long)before.Value, false); };
            latest.Click += async delegate { await NavigateAsync(null, false); };
            older.Click += async delegate { if (page != null && page.HasMore) await NavigateAsync(page.NextBeforeChangeset, false); };
            previous.Click += async delegate { if (cursors.Count > 0) await NavigateAsync(cursors.Peek(), true); };
            refresh.Click += async delegate { await LoadPageAsync(currentBefore); };
            cancel.Click += delegate { if (request != null) request.Cancel(); };
            browse.Click += delegate { OpenSnapshot(); }; close.Click += delegate { Close(); }; CancelButton = close;
            Shown += async delegate { await LoadPageAsync(currentBefore); };
            FormClosing += delegate { lifetime.Cancel(); if (request != null) request.Cancel(); };
            UpdateButtons();
        }

        private static void SetupList(ListView list, string name, string[] columns, int[] widths)
        { list.Dock = DockStyle.Fill; list.View = View.Details; list.MultiSelect = false; list.AccessibleName = name; DialogStyle.ApplyList(list); for (int i = 0; i < columns.Length; i++) list.Columns.Add(columns[i], widths[i]); }
        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository || !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("工作区仓库已改变，请关闭并重新打开版本关系图。");
        }
        private PlasticGraphNode SelectedNode() { return revisions.SelectedItems.Count == 1 ? revisions.SelectedItems[0].Tag as PlasticGraphNode : null; }
        private void UpdateButtons()
        {
            jump.Enabled = latest.Enabled = refresh.Enabled = before.Enabled = revisions.Enabled = !busy;
            previous.Enabled = !busy && cursors.Count > 0; older.Enabled = !busy && page != null && page.HasMore && page.NextBeforeChangeset.HasValue;
            browse.Enabled = !busy && SelectedNode() != null; cancel.Enabled = busy;
        }
        private void ClearDetails()
        { rendering = true; try { foreach (ListViewItem row in revisions.Items) row.Selected = false; graph.SelectNode(null); files.Items.Clear(); description.Clear(); } finally { rendering = false; } }
        private void ClearPage()
        { ClearDetails(); page = null; revisions.Items.Clear(); graph.SetPage(null); }
        private async Task<bool> ReadAsync(Func<CancellationToken, Task> work)
        {
            if (busy || lifetime.IsCancellationRequested) return false;
            busy = true; UpdateButtons(); request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try { ValidateContext(); await work(request.Token); request.Token.ThrowIfCancellationRequested(); ValidateContext(); return true; }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) { ClearPage(); status.Text = ex is OperationCanceledException ? "读取已取消，请刷新。" : "读取失败：" + ex.Message; } return false; }
            finally { request.Dispose(); request = null; busy = false; if (!lifetime.IsCancellationRequested) UpdateButtons(); }
        }
        private async Task NavigateAsync(long? cursor, bool back)
        {
            if (busy) return; long? previousCursor = currentBefore;
            if (await LoadPageAsync(cursor)) { if (back) cursors.Pop(); else if (previousCursor != cursor) cursors.Push(previousCursor); UpdateButtons(); }
        }
        private async Task<bool> LoadPageAsync(long? cursor)
        {
            if (busy) return false; ClearPage(); status.Text = "正在读取提交关系…";
            return await ReadAsync(async token => {
                var result = await getGraph(cursor, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (result == null || result.Repository != repository || result.Nodes == null || result.Edges == null || result.Nodes.Count > 100 || result.Nodes.Any(n => n == null || n.Repository != repository) || result.Nodes.Select(n => n.Changeset).Distinct().Count() != result.Nodes.Count || (result.HasMore && !result.NextBeforeChangeset.HasValue)) throw new InvalidOperationException("提交关系页面无效或仓库不匹配。");
                page = result; currentBefore = cursor; if (cursor.HasValue) before.Value = cursor.Value;
                rendering = true;
                try { foreach (var node in page.Nodes) revisions.Items.Add(new ListViewItem(new[] { "cs:" + node.Changeset, node.Branch, node.Owner, node.Date, node.Comment }) { Tag = node }); graph.SetPage(page); }
                finally { rendering = false; }
                status.Text = (cursor.HasValue ? "cs:" + cursor + " 之前" : "最新页面") + " · " + page.Nodes.Count + " 个提交，" + page.Edges.Count + " 条真实关系。页面外端点标记为未加载；基线不是父子关系。";
            });
        }
        private string RelationText(PlasticGraphNode node)
        {
            var lines = new List<string> { "cs:" + node.Changeset + " · " + node.Branch + " · " + node.Owner, node.Comment ?? "", "父提交：" + (node.ParentChangeset.HasValue ? "cs:" + node.ParentChangeset : "无") };
            foreach (var edge in page.Edges.Where(e => e.SourceChangeset == node.Changeset || e.DestinationChangeset == node.Changeset))
                lines.Add(edge.Kind + ": cs:" + edge.SourceChangeset + (edge.SourceLoaded ? "" : "（未加载）") + " → cs:" + edge.DestinationChangeset + (edge.BaseChangeset.HasValue ? "；基线 cs:" + edge.BaseChangeset + (edge.BaseLoaded ? "" : "（未加载）") : ""));
            return String.Join("\r\n", lines);
        }
        private async Task LoadDetailsAsync()
        {
            var selected = SelectedNode(); files.Items.Clear(); description.Clear(); graph.SelectNode(selected == null ? (long?)null : selected.Changeset); UpdateButtons(); if (selected == null) return;
            description.Text = RelationText(selected); status.Text = "正在读取 cs:" + selected.Changeset + " 的文件明细…";
            await ReadAsync(async token => {
                var details = await getChanges(selected.Changeset, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (!Object.ReferenceEquals(selected, SelectedNode())) return;
                if (details == null || details.Changeset == null || details.Changeset.Changeset != selected.Changeset || details.Files == null) throw new InvalidOperationException("提交明细与所选版本不一致。");
                foreach (var file in details.Files) files.Items.Add(new ListViewItem(new[] { file.Status, file.Path, file.OldPath }));
                status.Text = "cs:" + selected.Changeset + " · " + details.Files.Count + " 个更改项。图中仅包含当前页面的真实关系，基线仅作为明细显示。";
            });
        }
        private RepositoryBrowserForm CreateSnapshotBrowser()
        { ValidateContext(); var selected = SelectedNode(); if (selected == null || selected.Repository != repository) throw new InvalidOperationException("请重新选择提交。"); return new RepositoryBrowserForm(client, root, selected.Changeset, repository); }
        private void OpenSnapshot()
        { if (!browse.Enabled) return; try { using (var dialog = CreateSnapshotBrowser()) dialog.ShowDialog(this); } catch (Exception ex) { ClearPage(); UpdateButtons(); status.Text = ex.Message; } }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        { if (keyData == Keys.F5) { refresh.PerformClick(); return true; } return base.ProcessCmdKey(ref msg, keyData); }
    }
}
