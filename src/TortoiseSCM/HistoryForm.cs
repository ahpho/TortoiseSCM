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
        private readonly string workspaceRoot;
        private readonly bool wholeWorkspace;
        private readonly string branch;
        private readonly string branchRepository;
        private readonly ListView revisions = new ListView();
        private readonly ListView changedFiles = new ListView();
        private readonly TextBox description = new TextBox();
        private readonly Label status = new Label();
        private readonly Label historySummary = new Label();
        private readonly Button refreshHistory = new Button();
        private readonly Button cancelHistory = new Button();
        private readonly Button restore = new Button();
        private readonly Button snapshot = new Button();
        private readonly Button historicalFile = new Button();
        private readonly TextBox filter = new TextBox();
        private readonly Button close = new Button();
        private readonly List<PlasticHistoryItem> entries = new List<PlasticHistoryItem>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource detailRequest;
        private CancellationTokenSource historyRequest;
        private bool loadingHistory;
        private bool hasMoreHistory = true;
        private int scannedChangesets;
        private bool historyCompatibilityFallback;
        private string historyRepository;
        private Font notLoadedFont;
        private PlasticHistoryLocalState localState;
        private readonly Dictionary<long, bool> notLoaded = new Dictionary<long, bool>();
        private readonly Dictionary<long, ListViewItem> revisionRows = new Dictionary<long, ListViewItem>();
        private bool checkingLocalState;
        private int localStateGeneration;
        private string localStateError;
        private bool writing;
        private int generation;
        private bool filtering;
        private long? comparisonChangeset;
        private readonly ToolStripMenuItem compareMarkedFile = new ToolStripMenuItem();
        private readonly ToolStripMenuItem compareMarkedChangeset = new ToolStripMenuItem();
        private bool comparingFile;
        private Func<long, CancellationToken, Task<PlasticChangesetComparison>> getParentComparison;
        private Func<long, long, CancellationToken, Task<PlasticChangesetComparison>> getMarkedComparison;
        private Func<PlasticChangesetComparison, PlasticChangesetFile, CancellationToken, Task<PlasticCommandResult>> openFileComparison;
        private Func<PlasticChangesetComparison, string, CancellationToken, Task<PlasticCommandResult>> openUnchangedComparison;

        public HistoryForm(PlasticClient client, string path, string workspaceRoot)
            : this(client, path, workspaceRoot, null) { }

        public HistoryForm(PlasticClient client, string path, string workspaceRoot, string branch)
            : this(client, path, workspaceRoot, branch, null) { }

        internal HistoryForm(PlasticClient client, string path, string workspaceRoot, string branch, string expectedRepository)
        {
            this.client = client;
            this.path = path;
            this.workspaceRoot = workspaceRoot;
            this.branch = branch;
            getParentComparison = (cs, token) => client.GetChangesetParentComparisonAsync(workspaceRoot, cs, historyRepository, token);
            getMarkedComparison = (from, to, token) => client.GetChangesetComparisonAsync(workspaceRoot, from, to, token);
            openFileComparison = (comparison, file, token) => client.OpenChangesetFileDiffToolAsync(workspaceRoot, comparison, file, token);
            openUnchangedComparison = (comparison, file, token) => client.OpenRevisionDiffToolAsync(workspaceRoot, file, file,
                comparison.FromChangeset, comparison.ToChangeset, comparison.Repository, token);
            if (branch != null)
            {
                var workspace = client.DiscoverWorkspace(path);
                if (workspace == null) throw new InvalidOperationException("工作区不存在。");
                branchRepository = expectedRepository ?? workspace.Repository;
                ValidateHistoryContext();
            }
            wholeWorkspace = path.TrimEnd('\\', '/').Equals(workspaceRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            Text = "历史记录 - TortoiseSCM";
            Font = SystemFonts.MessageBoxFont;
            Size = new Size(1080, 740);
            MinimumSize = new Size(860, 580);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, branch == null ? 30 : 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = branch == null ? 1 : 2, Margin = Padding.Empty };
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            if (branch != null) header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 64));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 260));
            header.Controls.Add(new Label { Text = branch == null ? "范围：" + path : "分支：" + branch, Dock = DockStyle.Fill, AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Margin = new Padding(0, 0, 12, 3) }, 0, 0);
            header.Controls.Add(new Label { Text = "筛选(&F):", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 1, 0);
            filter.Dock = DockStyle.Fill;
            filter.AccessibleName = "筛选全部历史：版本、日期、作者、分支或说明";
            filter.Margin = new Padding(3, 2, 0, 4);
            filter.TextChanged += async delegate { await ApplyFilterAsync(); };
            header.Controls.Add(filter, 2, 0);
            if (branch != null)
            {
                var branchScope = new Label { Name = "branchScope", Text = "范围：" + path, Dock = DockStyle.Fill, AutoEllipsis = true,
                    TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Margin = new Padding(0, 0, 0, 3) };
                header.Controls.Add(branchScope, 0, 1); header.SetColumnSpan(branchScope, 3);
            }
            layout.Controls.Add(header, 0, 0);
            // Follow IDD_LOGMESSAGE: revision list, commit message and changed paths,
            // separated by native splitters rather than framed panels or tabs.
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Margin = Padding.Empty,
                Size = new Size(1040, 570), SplitterDistance = 270, SplitterWidth = 5, Panel1MinSize = 100, Panel2MinSize = 180 };
            ConfigureList(revisions, "提交历史", new[] { "版本", "日期", "作者", "分支", "说明" }, new[] { 70, 155, 120, 210, 440 });
            revisions.SelectedIndexChanged += async delegate { if (!filtering) await LoadDetailsAsync(); };
            revisions.KeyDown += delegate(object sender, KeyEventArgs e) { CopyListSelection(e, false); };
            var revisionMenu = new ContextMenuStrip();
            revisionMenu.Items.Add("复制变更集编号", null, delegate { CopyText(SelectedRevisionText(false)); });
            revisionMenu.Items.Add("复制提交说明", null, delegate { CopyText(SelectedRevisionText(true)); });
            revisionMenu.Items.Add("浏览此版本的完整仓库…", null, delegate { OpenRepositoryBrowser(); });
            revisionMenu.Items.Add("在此版本创建标签…", null, delegate { OpenCreateLabel(); });
            revisionMenu.Items.Add(new ToolStripSeparator());
            revisionMenu.Items.Add("标记为比较起点", null, delegate { MarkComparisonChangeset(); });
            compareMarkedChangeset.Click += delegate { OpenChangesetComparison(); };
            revisionMenu.Items.Add(compareMarkedChangeset);
            var clearComparison = revisionMenu.Items.Add("清除比较标记", null, delegate { comparisonChangeset = null; UpdateFileAction(); });
            revisionMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                e.Cancel = writing || revisions.SelectedItems.Count != 1;
                clearComparison.Enabled = comparisonChangeset.HasValue;
                UpdateFileAction();
            };
            revisions.ContextMenuStrip = revisionMenu;
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
            changedFiles.DoubleClick += async delegate { await CompareHistoricalFileAsync(false); };
            changedFiles.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyData == (Keys.Control | Keys.D))
                { e.Handled = e.SuppressKeyPress = true; await CompareHistoricalFileAsync(false); }
                else CopyListSelection(e, true);
            };
            var fileMenu = new ContextMenuStrip();
            var openFile = (ToolStripMenuItem)fileMenu.Items.Add("比较工具 比较", null, async delegate { await CompareHistoricalFileAsync(false); });
            openFile.ShortcutKeyDisplayString = "Ctrl+D";
            var exportFile = fileMenu.Items.Add("导出 / 自选版本…", null, delegate { OpenHistoricalFile(); });
            compareMarkedFile.Click += async delegate { await CompareHistoricalFileAsync(true); };
            fileMenu.Items.Add(compareMarkedFile);
            fileMenu.Items.Add("显示此路径的历史…", null, delegate { OpenSelectedPathHistory(); });
            fileMenu.Items.Add(new ToolStripSeparator());
            fileMenu.Items.Add("复制仓库路径", null, delegate { CopyText(SelectedFilePath(false)); });
            var copyOldPath = fileMenu.Items.Add("复制原仓库路径", null, delegate { CopyText(SelectedFilePath(true)); });
            fileMenu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                e.Cancel = writing || changedFiles.SelectedItems.Count != 1;
                openFile.Enabled = exportFile.Enabled = historicalFile.Enabled;
                copyOldPath.Enabled = !String.IsNullOrEmpty(SelectedFilePath(true));
                UpdateFileAction();
            };
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
            historySummary.Dock = DockStyle.Fill; historySummary.AutoEllipsis = true;
            historySummary.TextAlign = ContentAlignment.MiddleLeft; historySummary.Margin = Padding.Empty;
            var historyNavigation = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            historyNavigation.Controls.Add(historySummary);
            layout.Controls.Add(historyNavigation, 0, 2);
            layout.Controls.Add(status, 0, 3);
            restore.Text = wholeWorkspace ? "整仓回滚为待提交(&R)..." : "恢复此范围到此版本(&R)...";
            restore.Dock = DockStyle.Fill;
            restore.Margin = new Padding(3, 3, 6, 3);
            restore.Enabled = false;
            restore.Click += async delegate { await RestoreAsync(false); };
            historicalFile.Text = "比较文件 (Ctrl+D)";
            historicalFile.Dock = DockStyle.Fill; historicalFile.Enabled = false;
            historicalFile.Click += async delegate { await CompareHistoricalFileAsync(false); };
            footer.Controls.Add(historicalFile, 0, 0);
            var paging = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 164, Margin = Padding.Empty, WrapContents = false };
            refreshHistory.Text = "刷新全部"; refreshHistory.Width = 78;
            refreshHistory.Click += async delegate { await LoadHistoryPageAsync(true); };
            cancelHistory.Text = "取消加载"; cancelHistory.Width = 72; cancelHistory.Enabled = false;
            cancelHistory.Click += delegate
            {
                if (historyRequest != null) historyRequest.Cancel();
                if (detailRequest != null) detailRequest.Cancel();
                status.Text = "已取消加载；已加载历史保留，可重试。";
            };
            paging.Controls.Add(refreshHistory); paging.Controls.Add(cancelHistory);
            historyNavigation.Controls.Add(paging);
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
            layout.Controls.Add(footer, 0, 4);
            Controls.Add(layout);
            DialogStyle.Apply(this);
            notLoadedFont = new Font(revisions.Font, revisions.Font.Style | FontStyle.Bold);
            revisions.ShowItemToolTips = true;
            Shown += async delegate { await LoadHistoryPageAsync(true); };
            Activated += async delegate
            {
                if (!loadingHistory && !writing && !checkingLocalState && entries.Count > 0)
                { await ReadLocalStateAsync(lifetime.Token); await UpdateLocalRowsAsync(lifetime.Token); }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (writing) e.Cancel = true; else lifetime.Cancel(); };
            FormClosed += delegate { if (detailRequest != null) detailRequest.Cancel(); };
            Disposed += delegate { lifetime.Cancel(); if (detailRequest != null) detailRequest.Cancel(); notLoadedFont.Dispose(); };
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

        private async Task LoadHistoryPageAsync(bool reset)
        {
            if (loadingHistory || writing || checkingLocalState || lifetime.IsCancellationRequested) return;
            loadingHistory = true;
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            historyRequest = cancellation;
            refreshHistory.Enabled = false; cancelHistory.Enabled = true;
            status.Text = "正在读取全部历史；可随时取消…";
            // During refresh retain old rows alongside completed batches; on failure
            // restore the previous result. Native hints never establish completeness.
            var previous = entries.ToList();
            string previousRepository = historyRepository;
            var previewItems = new List<PlasticHistoryItem>();
            var refreshed = new List<PlasticHistoryItem>();
            var seen = new HashSet<long>();
            string repository = null;
            long? before = null;
            hasMoreHistory = true; scannedChangesets = 0; historyCompatibilityFallback = false;
            UpdateHistorySummary();
            try
            {
                string loadError = null;
                try
                {
                    await ReadLocalStateAsync(cancellation.Token);
                    if (!wholeWorkspace)
                    {
                        if (branch != null) ValidateHistoryContext();
                        var preview = await client.GetNativeHistoryPreviewAsync(path, branch, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (branch != null)
                        {
                            ValidateHistoryContext();
                            if (preview.Repository != branchRepository || preview.Branch != branch)
                                throw new InvalidOperationException("分支历史上下文已改变，请重新打开窗口。");
                        }
                        repository = preview.Repository;
                        previewItems.AddRange(preview.Items);
                        if (previewItems.Count > 0)
                        {
                            bool repositoryChanged = historyRepository != null && historyRepository != repository;
                            if (repositoryChanged) comparisonChangeset = null;
                            historyRepository = repository;
                            entries.Clear();
                            entries.AddRange(previewItems.Concat(previousRepository == repository ? previous : new List<PlasticHistoryItem>())
                                .GroupBy(item => item.Changeset).Select(group => group.First()).OrderByDescending(item => item.Changeset));
                            await ApplyFilterAsync(repositoryChanged);
                            await UpdateLocalRowsAsync(cancellation.Token);
                            UpdateHistorySummary();
                        }
                    }
                    do
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (branch != null) ValidateHistoryContext();
                        var page = branch == null ? await client.GetHistoryPageAsync(path, before, 50, cancellation.Token) :
                            await client.GetHistoryPageAsync(path, branch, before, 50, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (branch != null)
                        {
                            ValidateHistoryContext();
                            if (page.Repository != branchRepository || page.Branch != branch) throw new InvalidOperationException("分支历史上下文已改变，请重新打开窗口。");
                        }
                        if (repository != null && page.Repository != repository) throw new InvalidOperationException("工作区仓库已改变，请刷新历史。");
                        if (page.HasMore && (!page.NextBeforeChangeset.HasValue || page.NextBeforeChangeset.Value < 0 ||
                            (before.HasValue && page.NextBeforeChangeset.Value >= before.Value)))
                            throw new InvalidDataException("历史查询未返回有效的更早版本位置；未将不完整结果视为全部历史。");
                        repository = page.Repository;
                        historyCompatibilityFallback |= !String.IsNullOrEmpty(page.FallbackReason);
                        foreach (var item in page.Items) if (seen.Add(item.Changeset)) refreshed.Add(item);
                        scannedChangesets += page.ScannedChangesets;
                        before = page.NextBeforeChangeset;
                        hasMoreHistory = page.HasMore;
                        bool repositoryChanged = historyRepository != null && historyRepository != repository;
                        if (repositoryChanged) comparisonChangeset = null;
                        historyRepository = repository;
                        entries.Clear();
                        // The final list comes exclusively from the complete publication scan.
                        // Until then, newly confirmed rows can be used without hiding old rows.
                        IEnumerable<PlasticHistoryItem> visible = refreshed;
                        if (page.HasMore) visible = visible.Concat(previewItems)
                            .Concat(previousRepository == repository ? previous : new List<PlasticHistoryItem>());
                        entries.AddRange(visible.GroupBy(item => item.Changeset).Select(group => group.First()).OrderByDescending(item => item.Changeset));
                        await ApplyFilterAsync(repositoryChanged || !page.HasMore);
                        await UpdateLocalRowsAsync(cancellation.Token);
                        UpdateHistorySummary();
                        // Yield between bounded batches even when all backend tasks are cached.
                        if (page.HasMore) await Task.Yield();
                    }
                    while (hasMoreHistory);
                }
                catch (OperationCanceledException) { loadError = "已取消加载；已加载历史保留。点击“刷新全部”重试。"; }
                catch (Exception ex) { loadError = "读取失败：" + ex.Message; }
                if (loadError != null && !lifetime.IsCancellationRequested)
                {
                    hasMoreHistory = true;
                    // Restore/snapshot cancels history before starting its write. Do
                    // not change its status or desynchronize visible and stored rows.
                    if (!writing)
                    {
                        await RestoreHistoryAfterFailedRefreshAsync(previous, previousRepository);
                        status.Text = loadError;
                    }
                }
            }
            finally
            {
                historyRequest = null; cancellation.Dispose();
                if (!lifetime.IsCancellationRequested)
                {
                    refreshHistory.Enabled = !writing; cancelHistory.Enabled = false;
                    loadingHistory = false;
                    UpdateHistorySummary();
                }
                loadingHistory = false;
            }
        }

        private async Task RestoreHistoryAfterFailedRefreshAsync(List<PlasticHistoryItem> previous, string repository)
        {
            hasMoreHistory = true;
            if (previous.Count == 0 || historyRepository != repository) return;
            entries.Clear(); entries.AddRange(previous);
            await ApplyFilterAsync();
        }

        private void UpdateHistorySummary()
        {
            historySummary.Text = (localStateError == null ? "粗体：未拉取到本地；" : "本地拉取状态暂不可用；") +
                revisions.Items.Count + " / " + entries.Count + " 个已加载提交；已扫描 " + scannedChangesets +
                " 个提交；" + (hasMoreHistory ? (loadingHistory ? "正在读取全部历史，可取消" : "加载未完成，请刷新全部重试") : "已扫描全部历史") +
                (branch != null ? "（仅本分支提交，不含祖先）" :
                    (wholeWorkspace ? "" : "（路径历史，不追溯重命名前的其他路径）")) +
                (historyCompatibilityFallback ? "；已使用兼容查询" : "");
        }

        private async Task ApplyFilterAsync(bool forceDetails = false)
        {
            if (writing || lifetime.IsCancellationRequested) return;
            long? selected = revisions.SelectedItems.Count == 1 ? (long?)((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset : null;
            string query = filter.Text.Trim();
            filtering = true;
            revisions.BeginUpdate();
            try
            {
                revisions.Items.Clear();
                revisionRows.Clear();
                foreach (var entry in entries.Where(e => MatchesFilter(e, query)))
                {
                    var row = new ListViewItem(new[] { entry.Changeset.ToString(), entry.CreationDate, entry.Owner, entry.Branch,
                        (entry.Comment ?? "").Replace("\r", " ").Replace("\n", " ") }) { Tag = entry };
                    ApplyLocalRowStyle(row);
                    revisions.Items.Add(row);
                    revisionRows[entry.Changeset] = row;
                    if (selected.HasValue && entry.Changeset == selected.Value) row.Selected = true;
                }
                if (revisions.SelectedItems.Count == 0 && revisions.Items.Count > 0) revisions.Items[0].Selected = true;
            }
            finally { revisions.EndUpdate(); filtering = false; }
            status.Text = revisions.Items.Count + " / " + entries.Count + " 个提交";
            UpdateHistorySummary();
            long? current = revisions.SelectedItems.Count == 1 ? (long?)((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset : null;
            // Appending an older batch must not cancel/reload the selected immutable
            // changeset detail or interrupt a comparison launched from that selection.
            if (forceDetails || selected != current || !current.HasValue) await LoadDetailsAsync();
        }

        private static bool MatchesFilter(PlasticHistoryItem entry, string query)
        {
            return query.Length == 0 || new[] { entry.Changeset.ToString(), entry.CreationDate, entry.Owner, entry.Branch, entry.Comment }
                .Any(value => (value ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0);
        }

        private async Task ReadLocalStateAsync(CancellationToken token)
        {
            int request = ++localStateGeneration;
            checkingLocalState = true;
            localState = null; localStateError = null; notLoaded.Clear();
            foreach (ListViewItem row in revisions.Items) ApplyLocalRowStyle(row);
            try
            {
                var state = await client.GetHistoryLocalStateAsync(path, token);
                if (request == localStateGeneration && !lifetime.IsCancellationRequested) localState = state;
            }
            catch (OperationCanceledException) { if (request == localStateGeneration) localStateError = "检查已取消，请刷新重试。"; }
            catch (Exception ex) { if (request == localStateGeneration) localStateError = ex.Message; }
            finally
            {
                if (request == localStateGeneration)
                {
                    checkingLocalState = false;
                    if (!lifetime.IsCancellationRequested)
                    {
                        foreach (ListViewItem row in revisions.Items) ApplyLocalRowStyle(row);
                        UpdateHistorySummary();
                    }
                }
            }
        }

        private async Task UpdateLocalRowsAsync(CancellationToken token)
        {
            if (localState == null || checkingLocalState || lifetime.IsCancellationRequested) return;
            int request = localStateGeneration;
            var state = localState;
            checkingLocalState = true;
            try
            {
                foreach (var entry in entries.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (!notLoaded.ContainsKey(entry.Changeset))
                    {
                        bool missing = await client.IsHistoryNotLoadedAsync(state, entry, token);
                        if (request != localStateGeneration || lifetime.IsCancellationRequested) return;
                        notLoaded[entry.Changeset] = missing;
                    }
                    if (request != localStateGeneration || lifetime.IsCancellationRequested) return;
                    ListViewItem row;
                    if (revisionRows.TryGetValue(entry.Changeset, out row)) ApplyLocalRowStyle(row);
                }
            }
            catch (OperationCanceledException)
            {
                if (request == localStateGeneration && !lifetime.IsCancellationRequested)
                {
                    localStateError = "检查已取消，请刷新重试。";
                    foreach (ListViewItem row in revisions.Items) ApplyLocalRowStyle(row);
                }
            }
            catch (Exception ex)
            {
                if (request != localStateGeneration || lifetime.IsCancellationRequested) return;
                localStateError = ex.Message;
                // Unknown is distinct from loaded: do not retain stale bold rows.
                localState = null; notLoaded.Clear();
                foreach (ListViewItem row in revisions.Items) ApplyLocalRowStyle(row);
            }
            finally
            {
                if (request == localStateGeneration)
                {
                    checkingLocalState = false;
                    if (!lifetime.IsCancellationRequested) UpdateHistorySummary();
                }
            }
        }

        private void ApplyLocalRowStyle(ListViewItem row)
        {
            bool missing;
            bool known = notLoaded.TryGetValue(((PlasticHistoryItem)row.Tag).Changeset, out missing);
            row.Font = known && missing ? notLoadedFont : revisions.Font;
            row.ToolTipText = known ? (missing ? "尚未拉取到本地（当前范围）" : "已包含在本地加载版本中（当前范围）") :
                (localStateError == null ? "正在检查本地拉取状态…" : "无法判断本地拉取状态：" + localStateError);
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
                if (branch != null) ValidateHistoryContext();
                var details = await client.GetChangesetAsync(path, entry.Changeset, cancellation.Token);
                if (cancellation.IsCancellationRequested || request != generation) return;
                if (branch != null) ValidateHistoryContext();
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
            historicalFile.Enabled = !writing && !comparingFile && revisions.SelectedItems.Count == 1 && file != null &&
                (file.ItemType == "F" || file.ItemType == "B");
            compareMarkedFile.Text = comparisonChangeset.HasValue ? "与标记 cs:" + comparisonChangeset.Value + " 比较此文件…" : "与标记变更集比较此文件…";
            compareMarkedFile.Enabled = historicalFile.Enabled && comparisonChangeset.HasValue;
            compareMarkedChangeset.Text = comparisonChangeset.HasValue ? "与标记 cs:" + comparisonChangeset.Value + " 比较整个仓库…" : "与标记变更集比较整个仓库…";
            compareMarkedChangeset.Enabled = !writing && revisions.SelectedItems.Count == 1 && comparisonChangeset.HasValue;
        }

        private void OpenHistoricalFile()
        { OpenHistoricalFile(false); }

        private async Task CompareHistoricalFileAsync(bool useMarkedChangeset)
        {
            if (!historicalFile.Enabled || comparingFile || lifetime.IsCancellationRequested ||
                (useMarkedChangeset && !comparisonChangeset.HasValue)) return;
            var selected = (PlasticChangesetFile)changedFiles.SelectedItems[0].Tag;
            long target = ((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset;
            long? marked = useMarkedChangeset ? comparisonChangeset : null;
            comparingFile = true; UpdateFileAction();
            status.Text = "正在准备历史文件比较；比较工具 关闭前请勿重复打开…";
            try
            {
                ValidateHistoryContext();
                string repository = historyRepository ?? client.DiscoverWorkspace(workspaceRoot).Repository;
                var comparison = marked.HasValue ? await getMarkedComparison(marked.Value, target, lifetime.Token) :
                    await getParentComparison(target, lifetime.Token);
                lifetime.Token.ThrowIfCancellationRequested(); ValidateHistoryContext();
                if (comparison.Repository != repository || !comparison.RootPath.TrimEnd('\\', '/').Equals(workspaceRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                    comparison.ToChangeset != target || (marked.HasValue && comparison.FromChangeset != marked.Value))
                    throw new InvalidOperationException("历史比较的工作区、仓库或版本与所选项不一致。");
                // Use the tree comparison's normalized old path, including children
                // edited underneath a renamed directory. Never read the working file.
                var candidates = comparison.Files.Where(f => f.Path == selected.Path && f.ItemType == selected.ItemType &&
                    (marked.HasValue || f.Status == selected.Status)).ToList();
                PlasticCommandResult result;
                if (candidates.Count == 0 && marked.HasValue)
                    result = await openUnchangedComparison(comparison, selected.Path, lifetime.Token);
                else
                {
                    var file = candidates.FirstOrDefault(f => f.Status == selected.Status) ?? candidates.FirstOrDefault();
                    if (file == null || candidates.Count(f => f.Status == file.Status) != 1)
                        throw new InvalidOperationException("所选文件与服务器历史比较不一致，请刷新历史后重试。");
                    result = await openFileComparison(comparison, file, lifetime.Token);
                }
                if (!lifetime.IsCancellationRequested)
                    status.Text = result.Succeeded ? "比较工具 已关闭。" : "比较工具 比较失败：" + result.Error;
            }
            catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) status.Text = "已取消历史文件比较。"; }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = "无法比较历史文件：" + ex.Message; }
            finally { comparingFile = false; if (!lifetime.IsCancellationRequested) UpdateFileAction(); }
        }

        private void OpenHistoricalFile(bool useMarkedChangeset)
        {
            if (!historicalFile.Enabled || (useMarkedChangeset && !comparisonChangeset.HasValue)) return;
            try { using (var dialog = CreateHistoricalFileDialog(useMarkedChangeset)) dialog.ShowDialog(this); }
            catch (Exception ex) { status.Text = "无法打开历史文件：" + ex.Message; }
        }

        private HistoricalFileForm CreateHistoricalFileDialog(bool useMarkedChangeset)
        {
            ValidateHistoryContext();
            var file = (PlasticChangesetFile)changedFiles.SelectedItems[0].Tag;
            var entry = (PlasticHistoryItem)revisions.SelectedItems[0].Tag;
            return new HistoricalFileForm(client, path, file.Path, entry.Changeset, useMarkedChangeset ? comparisonChangeset : null);
        }

        private void MarkComparisonChangeset()
        {
            if (writing || revisions.SelectedItems.Count != 1) return;
            comparisonChangeset = ((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset;
            status.Text = "已标记 cs:" + comparisonChangeset.Value + "。选择另一提交，右键比较整个仓库；也可选择文件比较同一路径。";
            UpdateFileAction();
        }

        private ChangesetComparisonForm CreateChangesetComparison()
        {
            ValidateHistoryContext();
            if (writing || !comparisonChangeset.HasValue || revisions.SelectedItems.Count != 1)
                throw new InvalidOperationException("请先标记比较起点并选择目标提交。");
            return new ChangesetComparisonForm(client, workspaceRoot, historyRepository, comparisonChangeset.Value,
                ((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset);
        }

        private void OpenChangesetComparison()
        {
            if (!compareMarkedChangeset.Enabled) return;
            try { using (var dialog = CreateChangesetComparison()) dialog.ShowDialog(this); }
            catch (Exception ex) { status.Text = "无法打开变更集比较：" + ex.Message; }
        }

        private void OpenCreateLabel()
        {
            if (writing || revisions.SelectedItems.Count != 1) return;
            try { using (var dialog = CreateLabelDialog()) dialog.ShowDialog(this); }
            catch (Exception ex) { status.Text = "无法创建标签：" + ex.Message; }
        }

        private LabelCreateForm CreateLabelDialog()
        {
            ValidateHistoryContext();
            if (revisions.SelectedItems.Count != 1) throw new InvalidOperationException("请选择提交。");
            return new LabelCreateForm(client, workspaceRoot, historyRepository, ((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset);
        }

        private void OpenRepositoryBrowser()
        {
            if (writing || revisions.SelectedItems.Count != 1) return;
            try
            {
                using (var dialog = CreateRepositoryBrowser()) dialog.ShowDialog(this);
            }
            catch (Exception ex) { status.Text = "无法打开仓库浏览器：" + ex.Message; }
        }

        private RepositoryBrowserForm CreateRepositoryBrowser()
        {
            ValidateHistoryContext();
            return new RepositoryBrowserForm(client, workspaceRoot, ((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset, historyRepository);
        }

        private string SelectedRevisionText(bool comment)
        {
            if (revisions.SelectedItems.Count != 1) return null;
            var entry = (PlasticHistoryItem)revisions.SelectedItems[0].Tag;
            return comment ? entry.Comment : "cs:" + entry.Changeset;
        }

        private string SelectedFilePath(bool original)
        {
            if (changedFiles.SelectedItems.Count != 1) return null;
            var file = (PlasticChangesetFile)changedFiles.SelectedItems[0].Tag;
            return original ? file.OldPath : file.Path;
        }

        private void CopyListSelection(KeyEventArgs e, bool file)
        {
            if (e.KeyCode != Keys.C || !e.Control || e.Alt || e.Shift) return;
            CopyText(file ? SelectedFilePath(false) : SelectedRevisionText(false));
            e.Handled = e.SuppressKeyPress = true;
        }

        private void CopyText(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "剪贴板暂时不可用，请稍后重试复制。"; }
            catch (System.Threading.ThreadStateException) { status.Text = "当前线程无法访问剪贴板。"; }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.D) && changedFiles.ContainsFocus)
            {
                if (historicalFile.Enabled) historicalFile.PerformClick();
                return true;
            }
            if (keyData == Keys.F5)
            {
                if (refreshHistory.Enabled) refreshHistory.PerformClick();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private HistoryForm CreateSelectedPathHistory()
        {
            ValidateHistoryContext();
            string repositoryPath = SelectedFilePath(false);
            if (String.IsNullOrEmpty(repositoryPath) || repositoryPath[0] != '/' ||
                repositoryPath.IndexOfAny(new[] { '\\', ':', '#', '@' }) >= 0 ||
                repositoryPath.Substring(1).Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
                throw new ArgumentException("历史记录中的仓库路径无效。");
            string localPath = Path.Combine(workspaceRoot, repositoryPath.Substring(1).Replace('/', '\\'));
            // Reuse the backend's workspace, metadata, reparse-point and nested-workspace
            // checks; existence is deliberately not required for deleted historical paths.
            var validated = client.Build(new PlasticCommandRequest { Command = PlasticCommand.History,
                WorkingDirectory = workspaceRoot, Paths = new[] { localPath } });
            return new HistoryForm(client, validated.Arguments[1], validated.WorkingDirectory, branch, branchRepository);
        }

        private void ValidateHistoryContext()
        {
            var current = client.DiscoverWorkspace(path);
            if (current == null || !current.RootPath.TrimEnd('\\', '/').Equals(workspaceRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                (historyRepository != null && current.Repository != historyRepository) ||
                (branchRepository != null && current.Repository != branchRepository))
                throw new InvalidOperationException("工作区或仓库已改变，请关闭并重新打开历史窗口。");
        }

        private void OpenSelectedPathHistory()
        {
            if (writing || changedFiles.SelectedItems.Count != 1) return;
            try { using (var dialog = CreateSelectedPathHistory()) dialog.ShowDialog(this); }
            catch (Exception ex) { status.Text = "无法打开路径历史：" + ex.Message; }
        }

        private async Task RestoreAsync(bool switchSnapshot)
        {
            if (writing || comparingFile || revisions.SelectedItems.Count != 1) return;
            var entry = (PlasticHistoryItem)revisions.SelectedItems[0].Tag;
            string explanation = switchSnapshot ?
                "将整个工作区切换到历史快照。部分工作区只切换已加载内容，并保留加载规则。\r\n这不会创建回滚提交；存在待提交更改时操作会被拒绝。" :
                wholeWorkspace ? "在当前分支把整仓内容回滚到所选历史版本，生成待提交更改。\r\n不会自动提交；请返回提交窗口检查并提交。\r\n只支持完整工作区，要求当前分支最新且无待提交更改。" :
                "将此文件或目录恢复到历史内容，结果成为待提交更改。\r\n目录包含其后代；范围内已有待提交更改时操作会被拒绝。\r\n部分工作区不支持涉及增删、移动或未加载项的目录恢复。";
            if (MessageBox.Show(this, explanation + "\r\n\r\n" + path + "\r\n目标 cs:" + entry.Changeset + "\r\n\r\n继续？",
                "TortoiseSCM — 恢复历史版本", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            writing = true;
            if (historyRequest != null) historyRequest.Cancel();
            refreshHistory.Enabled = cancelHistory.Enabled = false;
            revisions.Enabled = restore.Enabled = snapshot.Enabled = historicalFile.Enabled = filter.Enabled = close.Enabled = false;
            status.Text = "正在恢复历史版本…";
            try
            {
                ValidateHistoryContext();
                var result = switchSnapshot ? await client.SwitchAsync(path, entry.Changeset, lifetime.Token) :
                    await client.RollbackAsync(path, entry.Changeset, lifetime.Token);
                status.Text = result.Succeeded ? "已完成。关闭历史窗口后可检查待定更改。" : "操作失败：" + result.Error;
                if (!result.Succeeded) MessageBox.Show(this, result.Output + "\r\n" + result.Error, "恢复未成功", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex) { status.Text = "恢复失败：" + ex.Message; MessageBox.Show(this, ex.Message, "恢复未成功"); }
            finally
            {
                writing = false; revisions.Enabled = restore.Enabled = snapshot.Enabled = filter.Enabled = close.Enabled = true;
                refreshHistory.Enabled = !loadingHistory; UpdateFileAction();
            }
            await ReadLocalStateAsync(lifetime.Token);
            await UpdateLocalRowsAsync(lifetime.Token);
        }
    }
}
