// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class RepositoryBrowserForm : Form
    {
        private readonly PlasticClient client;
        private readonly string workspacePath;
        private readonly string expectedRepository;
        private readonly string expectedRoot;
        private readonly long? initialChangeset;
        private readonly TreeView directories = new TreeView();
        private readonly ListView files = new ListView();
        private readonly NumericUpDown revision = new NumericUpDown();
        private readonly Label location = new Label();
        private readonly Label status = new Label();
        private readonly TextBox preview = new TextBox();
        private readonly Button browse = DialogStyle.Button("浏览 (&B)");
        private readonly Button up = DialogStyle.Button("上级 (&U)");
        private readonly Button refresh = DialogStyle.Button("刷新 (&R)");
        private readonly Button cancel = DialogStyle.Button("取消读取");
        private readonly Button view = DialogStyle.Button("预览文件");
        private readonly Button export = DialogStyle.Button("导出文件…");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource request;
        private PlasticRepositoryListing listing;
        private bool busy;
        private bool exporting;
        private bool updating;
        private string requestedDirectory = "/";
        private Func<string, string, long, CancellationToken, Task<PlasticRepositoryListing>> loadDirectory;
        private Func<string, string, long, CancellationToken, Task<PlasticHistoricalFile>> readFile;

        public RepositoryBrowserForm(PlasticClient client, string workspacePath, long? changeset, string repository)
        {
            this.client = client; this.workspacePath = workspacePath; initialChangeset = changeset;
            var workspace = client.DiscoverWorkspace(workspacePath);
            if (workspace == null) throw new InvalidOperationException("找不到 Plastic 工作区。");
            expectedRepository = repository ?? workspace.Repository; expectedRoot = workspace.RootPath;
            ValidateContext();
            loadDirectory = client.GetRepositoryDirectoryAsync;
            readFile = (sourceWorkspace, path, cs, token) => client.GetHistoricalFileAsync(sourceWorkspace, path, cs, expectedRepository, token);
            DialogStyle.Apply(this);
            Text = "仓库浏览器 - TortoiseSCM"; Size = new Size(1060, 720); MinimumSize = new Size(820, 560);
            // Match IDD_REPOSITORY_BROWSER: path/revision row, native tree/list split, bottom actions.
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            location.Dock = DockStyle.Fill; location.AutoEllipsis = true; location.UseMnemonic = false;
            location.Text = expectedRepository + "  /"; layout.Controls.Add(location, 0, 0);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            toolbar.Controls.Add(new Label { Text = "快照 cs:", AutoSize = true, Padding = new Padding(0, 5, 0, 0) });
            revision.Minimum = 0; revision.Maximum = Int64.MaxValue; revision.Width = 150; revision.AccessibleName = "仓库快照变更集";
            if (changeset.HasValue) revision.Value = changeset.Value;
            revision.ValueChanged += delegate { if (!updating) { requestedDirectory = "/"; ClearListing(true); ShowRequestedLocation(); status.Text = "版本已更改，请点击浏览。"; } };
            toolbar.Controls.Add(revision); toolbar.Controls.Add(browse); toolbar.Controls.Add(up); toolbar.Controls.Add(refresh); toolbar.Controls.Add(cancel);
            browse.Click += async delegate { await LoadDirectoryAsync("/", true); };
            refresh.Click += async delegate { await LoadDirectoryAsync(requestedDirectory, false); };
            up.Click += async delegate { await LoadDirectoryAsync(ParentDirectory(requestedDirectory), false); };
            cancel.Click += delegate { if (request != null) request.Cancel(); };
            layout.Controls.Add(toolbar, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Size = new Size(1000, 520), SplitterDistance = 245, Panel1MinSize = 150, Panel2MinSize = 380 };
            directories.Dock = DockStyle.Fill; directories.HideSelection = false; directories.AccessibleName = "历史目录树";
            directories.AfterSelect += async delegate { if (!updating && !busy && directories.SelectedNode != null) await LoadDirectoryAsync((string)directories.SelectedNode.Tag, false); };
            split.Panel1.Controls.Add(directories);
            var content = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(740, 520), SplitterDistance = 315, Panel1MinSize = 100, Panel2MinSize = 70 };
            files.Dock = DockStyle.Fill; files.View = View.Details; files.MultiSelect = false; DialogStyle.ApplyList(files);
            files.AccessibleName = "历史目录内容"; files.Columns.Add("名称", 300); files.Columns.Add("类型", 100); files.Columns.Add("大小 (字节)", 120);
            files.SelectedIndexChanged += delegate { preview.Clear(); UpdateActions(); };
            files.DoubleClick += async delegate { var entry = SelectedEntry(); if (entry != null && !busy) { if (entry.IsDirectory && !entry.IsSymbolicLink) await LoadDirectoryAsync(entry.Path, false); else await PreviewAsync(); } };
            var menu = new ContextMenuStrip();
            var openItem = menu.Items.Add("打开目录 / 预览文件", null, async delegate { var entry = SelectedEntry(); if (entry == null || busy) return; if (entry.IsDirectory && !entry.IsSymbolicLink) await LoadDirectoryAsync(entry.Path, false); else await PreviewAsync(); });
            var exportItem = menu.Items.Add("导出此版本…", null, async delegate { await ExportAsync(); });
            menu.Opening += delegate { var entry = SelectedEntry(); openItem.Enabled = !busy && entry != null && !entry.IsSymbolicLink; exportItem.Enabled = export.Enabled; };
            files.ContextMenuStrip = menu; content.Panel1.Controls.Add(files);
            preview.Dock = DockStyle.Fill; preview.Multiline = true; preview.ReadOnly = true; preview.WordWrap = false; preview.ScrollBars = ScrollBars.Both;
            preview.Font = new Font("Consolas", 10F); preview.AccessibleName = "历史文件只读预览"; content.Panel2.Controls.Add(preview);
            split.Panel2.Controls.Add(content); layout.Controls.Add(split, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.UseMnemonic = false; layout.Controls.Add(status, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); }; CancelButton = close;
            view.Click += async delegate { await PreviewAsync(); }; export.Click += async delegate { await ExportAsync(); };
            footer.Controls.Add(close); footer.Controls.Add(export); footer.Controls.Add(view); layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
            Shown += async delegate { await InitializeAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (exporting) { e.Cancel = true; status.Text = "正在导出，请等待完成或先取消读取。"; return; } lifetime.Cancel(); if (request != null) request.Cancel(); };
            UpdateActions();
        }

        private async Task InitializeAsync()
        {
            if (initialChangeset.HasValue) { await LoadDirectoryAsync("/", true); return; }
            BeginRequest(); status.Text = "正在读取当前分支的最新变更集…";
            try
            {
                ValidateContext();
                var branches = await client.GetBranchesAsync(expectedRoot, request.Token);
                request.Token.ThrowIfCancellationRequested(); ValidateContext();
                var current = branches.SingleOrDefault(item => item.IsCurrent);
                if (current == null || current.Repository != expectedRepository || current.HeadChangeset < 0) throw new InvalidOperationException("无法确定当前分支快照，请输入变更集编号后浏览。");
                updating = true; revision.Value = current.HeadChangeset; updating = false;
            }
            catch (Exception ex) { if (!IsDisposed) status.Text = ex is OperationCanceledException ? "已取消。请输入快照编号后浏览。" : ex.Message; EndRequest(); return; }
            EndRequest(); await LoadDirectoryAsync("/", true);
        }

        private async Task LoadDirectoryAsync(string directory, bool resetTree)
        {
            if (busy || lifetime.IsCancellationRequested) return;
            long changeset = (long)revision.Value; requestedDirectory = directory;
            ClearListing(resetTree); ShowRequestedLocation(); BeginRequest(); status.Text = "正在读取 " + directory + " @ cs:" + changeset + "…";
            try
            {
                ValidateContext(); var result = await loadDirectory(workspacePath, directory, changeset, request.Token);
                request.Token.ThrowIfCancellationRequested(); ValidateContext();
                if (result.Repository != expectedRepository || !SameRoot(result.RootPath, expectedRoot) || result.Changeset != changeset || result.DirectoryPath != directory)
                    throw new InvalidOperationException("返回的仓库快照与请求不一致。");
                RenderListing(result);
            }
            catch (Exception ex) { if (!IsDisposed) { ClearListing(true); status.Text = ex is OperationCanceledException ? "读取已取消。" : "读取失败：" + ex.Message; } }
            finally { EndRequest(); }
        }

        private void RenderListing(PlasticRepositoryListing result)
        {
            listing = result; updating = true;
            try
            {
                files.Items.Clear();
                foreach (var entry in result.Entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase))
                    files.Items.Add(new ListViewItem(new[] { entry.Name, entry.IsSymbolicLink ? "符号链接" : entry.IsDirectory ? "目录" : "文件", entry.IsDirectory ? "" : entry.Size.ToString(CultureInfo.InvariantCulture) }) { Tag = entry });
                if (directories.Nodes.Count == 0) directories.Nodes.Add(new TreeNode("/") { Tag = "/" });
                var node = directories.Nodes[0]; string path = "";
                foreach (string part in result.DirectoryPath.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    path += "/" + part; var child = node.Nodes.Cast<TreeNode>().FirstOrDefault(n => (string)n.Tag == path);
                    if (child == null) { child = new TreeNode(part) { Tag = path }; node.Nodes.Add(child); } node = child;
                }
                node.Nodes.Clear();
                foreach (var entry in result.Entries.Where(e => e.IsDirectory && !e.IsSymbolicLink)) node.Nodes.Add(new TreeNode(entry.Name) { Tag = entry.Path });
                directories.SelectedNode = node; node.Expand(); node.EnsureVisible();
                location.Text = expectedRepository + "  " + result.DirectoryPath + "  @ cs:" + result.Changeset;
                status.Text = "cs:" + result.Changeset + " · " + result.Entries.Count + " 项（完整快照目录，与本地加载范围无关）";
            }
            finally { updating = false; UpdateActions(); }
        }

        private async Task PreviewAsync()
        {
            var entry = SelectedEntry(); if (!view.Enabled || entry == null) return;
            long changeset = listing.Changeset; preview.Clear(); BeginRequest();
            try
            {
                ValidateContext();
                if (entry.Size > 2 * 1024 * 1024) throw new InvalidOperationException("预览最多支持 2 MiB，请导出文件查看。");
                var file = await readFile(workspacePath, entry.Path, changeset, request.Token);
                request.Token.ThrowIfCancellationRequested(); ValidateContext();
                if (file.Content.Length > 2 * 1024 * 1024 || file.Content.Any(b => b == 0)) throw new InvalidOperationException("文件过大或为二进制内容，请导出文件查看。");
                string text = new UTF8Encoding(false, true).GetString(file.Content).TrimStart('\uFEFF');
                preview.Text = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                status.Text = entry.Path + " @ cs:" + changeset + "（只读 UTF-8 预览）";
            }
            catch (Exception ex) { if (!IsDisposed) { preview.Clear(); status.Text = ex is OperationCanceledException ? "预览已取消。" : "预览失败：" + ex.Message; } }
            finally { EndRequest(); }
        }

        private async Task ExportAsync()
        {
            var entry = SelectedEntry(); if (!export.Enabled || entry == null) return;
            long changeset = listing.Changeset;
            using (var picker = new SaveFileDialog { FileName = entry.Name, OverwritePrompt = true, Title = "导出 " + entry.Path + " @ cs:" + changeset })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                exporting = true; BeginRequest();
                try
                {
                    ValidateContext(); var result = await client.ExportRevisionAsync(workspacePath, entry.Path, changeset, picker.FileName, true, expectedRepository, request.Token);
                    ValidateContext();
                    if (!IsDisposed) status.Text = result.Succeeded ? "已导出 cs:" + changeset + "：" + picker.FileName : "导出失败：" + result.Error;
                }
                catch (Exception ex) { if (!IsDisposed) status.Text = "导出未完成：" + ex.Message; }
                finally { exporting = false; EndRequest(); }
            }
        }

        private void ClearListing(bool tree)
        { listing = null; files.Items.Clear(); preview.Clear(); if (tree) { updating = true; directories.Nodes.Clear(); updating = false; } UpdateActions(); }
        private void ShowRequestedLocation()
        { location.Text = expectedRepository + "  " + requestedDirectory + "  @ cs:" + (long)revision.Value + "（尚未加载）"; }
        private PlasticRepositoryEntry SelectedEntry()
        { return listing != null && files.SelectedItems.Count == 1 ? files.SelectedItems[0].Tag as PlasticRepositoryEntry : null; }
        private void BeginRequest()
        { busy = true; request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); UpdateActions(); }
        private void EndRequest()
        { if (request != null) { request.Dispose(); request = null; } busy = false; if (!IsDisposed) UpdateActions(); }
        private void UpdateActions()
        {
            browse.Enabled = refresh.Enabled = revision.Enabled = directories.Enabled = files.Enabled = !busy;
            up.Enabled = !busy && listing != null && listing.DirectoryPath != "/"; cancel.Enabled = busy;
            var entry = SelectedEntry(); view.Enabled = export.Enabled = !busy && entry != null && !entry.IsDirectory && !entry.IsSymbolicLink;
        }
        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(workspacePath);
            if (workspace == null || !SameRoot(workspace.RootPath, expectedRoot) || workspace.Repository != expectedRepository)
                throw new InvalidOperationException("工作区仓库已改变，请关闭并重新打开浏览器。");
        }
        private static bool SameRoot(string first, string second)
        { return String.Equals(first == null ? null : first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        private static string ParentDirectory(string path)
        { int index = path.LastIndexOf('/'); return index <= 0 ? "/" : path.Substring(0, index); }
    }
}
