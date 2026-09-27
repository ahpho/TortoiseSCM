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
    // Native log-style browser: server records above, selected record's files below.
    internal sealed class ShelvesForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string repository;
        private readonly string selector;
        private readonly ListView shelves = new ListView();
        private readonly ListView files = new ListView();
        private readonly TextBox filter = new TextBox();
        private readonly TextBox description = new TextBox();
        private readonly Label status = new Label();
        private readonly Button refresh = DialogStyle.Button("刷新(&R)");
        private readonly Button cancel = DialogStyle.Button("取消加载");
        private readonly Button apply = DialogStyle.Button("应用(&A)");
        private readonly Button delete = DialogStyle.Button("删除(&D)");
        private readonly Button compare = DialogStyle.Button("比较(&C)");
        private readonly Button export = DialogStyle.Button("导出(&E)");
        private readonly Button close = DialogStyle.Button("关闭");
        private ToolStripMenuItem applyMenu;
        private ToolStripMenuItem deleteMenu;
        private ToolStripMenuItem compareMenu;
        private ToolStripMenuItem exportMenu;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource request;
        private IList<PlasticShelve> entries = new List<PlasticShelve>();
        private readonly Func<string, CancellationToken, Task<IList<PlasticShelve>>> getShelves;
        private readonly Func<string, long, CancellationToken, Task<IList<PlasticChangesetFile>>> getChanges;
        private Func<string, long, CancellationToken, Task<PlasticCommandResult>> applyShelve;
        private Func<string, long, CancellationToken, Task<PlasticCommandResult>> deleteShelve;
        private Func<string, long, CancellationToken, Task<PlasticShelveComparison>> compareShelve;
        private Func<string, long, string, bool, CancellationToken, Task<PlasticCommandResult>> exportShelve;
        private Func<string, string, DialogResult> confirm = delegate(string message, string title) {
            return MessageBox.Show(message, title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        };
        private Action<PlasticShelveComparison> showComparison;
        private Action<string> showPreflightError;
        private IList<PlasticChangesetFile> selectedDetails = new List<PlasticChangesetFile>();
        private long? detailsShelveId;
        private bool busy;
        private bool writing;
        private bool rendering;

        internal ShelvesForm(PlasticClient client, string path)
        {
            this.client = client;
            var workspace = client.DiscoverWorkspace(path);
            if (workspace == null) throw new InvalidOperationException("请选择 Plastic 工作区。");
            root = workspace.RootPath; repository = workspace.Repository; selector = workspace.Selector;
            showComparison = comparison => { using (var dialog = new ShelveComparisonForm(comparison)) dialog.ShowDialog(this); };
            showPreflightError = message => MessageBox.Show(this, message, "暂存集操作已停止", MessageBoxButtons.OK, MessageBoxIcon.Error);
            getShelves = client.GetShelvesAsync; getChanges = client.GetShelveChangesAsync;
            applyShelve = (workspaceRoot, id, token) => client.ApplyShelveAsync(workspaceRoot, id, repository, selector, token);
            deleteShelve = (workspaceRoot, id, token) => client.DeleteShelveAsync(workspaceRoot, id, repository, selector, token);
            compareShelve = client.GetShelveComparisonAsync; exportShelve = client.ExportShelveAsync;
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
            var shelfMenu = new ContextMenuStrip();
            applyMenu = new ToolStripMenuItem("应用暂存集", null, async delegate { await ApplyAsync(); });
            deleteMenu = new ToolStripMenuItem("删除暂存集", null, async delegate { await DeleteAsync(); });
            compareMenu = new ToolStripMenuItem("比较暂存集", null, async delegate { await CompareAsync(); });
            exportMenu = new ToolStripMenuItem("导出暂存集", null, async delegate { await ExportAsync(); });
            shelfMenu.Items.AddRange(new ToolStripItem[] { applyMenu, deleteMenu, new ToolStripSeparator(), compareMenu, exportMenu });
            shelfMenu.Opening += delegate { UpdateButtons(); };
            shelves.ContextMenuStrip = shelfMenu;
            shelves.MouseUp += delegate(object sender, MouseEventArgs args) {
                if (args.Button != MouseButtons.Right) return;
                var item = shelves.GetItemAt(args.X, args.Y);
                if (item != null) { item.Selected = true; item.Focused = true; }
            };
            shelves.SelectedIndexChanged += async delegate { if (!rendering && !busy) await LoadDetailsAsync(); };
            split.Panel1.Controls.Add(shelves);
            var detail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            detail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); detail.RowStyles.Add(new RowStyle(SizeType.Absolute, 60)); detail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            description.Dock = DockStyle.Fill; description.Multiline = true; description.ReadOnly = true; description.ScrollBars = ScrollBars.Vertical;
            description.AccessibleName = "所选暂存集说明"; detail.Controls.Add(description, 0, 0);
            files.Dock = DockStyle.Fill; files.View = View.Details; DialogStyle.ApplyList(files);
            files.Columns.Add("路径", 470); files.Columns.Add("状态", 85); files.Columns.Add("原路径", 350); files.AccessibleName = "暂存集更改文件";
            detail.Controls.Add(files, 0, 1); split.Panel2.Controls.Add(detail); layout.Controls.Add(split, 0, 2);
            layout.Controls.Add(new Label { Text = "在待定更改窗口勾选文件后，可通过“操作 → 保存勾选项为暂存集”保存。\r\n应用暂存集要求 Standard 工作区干净；应用、删除和导出都会在写入前要求确认。比较会下载暂存内容进行只读预览。", Dock = DockStyle.Fill }, 0, 3);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(status, 0, 4);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            left.Controls.Add(apply); left.Controls.Add(delete); left.Controls.Add(compare); left.Controls.Add(export); left.Controls.Add(refresh); left.Controls.Add(cancel);
            footer.Controls.Add(left, 0, 0); footer.Controls.Add(close, 1, 0); layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
            refresh.Click += async delegate { await LoadAsync(); }; cancel.Click += delegate { if (request != null) request.Cancel(); };
            apply.Click += async delegate { await ApplyAsync(); }; delete.Click += async delegate { await DeleteAsync(); };
            compare.Click += async delegate { await CompareAsync(); }; export.Click += async delegate { await ExportAsync(); };
            close.Click += delegate { Close(); }; CancelButton = close;
            Shown += async delegate { await LoadAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs args) {
                if (writing) { args.Cancel = true; status.Text = "写入操作正在进行，完成前不能关闭窗口。"; return; }
                lifetime.Cancel(); if (request != null) request.Cancel();
            };
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool selected = !busy && !writing && shelves.SelectedItems.Count == 1 && detailsShelveId.HasValue;
            refresh.Enabled = filter.Enabled = shelves.Enabled = !busy && !writing;
            cancel.Enabled = busy && !writing;
            apply.Enabled = selected && selectedDetails.Count > 0;
            delete.Enabled = selected;
            compare.Enabled = selected && compareShelve != null;
            export.Enabled = selected && selectedDetails.Count > 0 && exportShelve != null;
            if (applyMenu != null) applyMenu.Enabled = apply.Enabled;
            if (deleteMenu != null) deleteMenu.Enabled = delete.Enabled;
            if (compareMenu != null) compareMenu.Enabled = compare.Enabled;
            if (exportMenu != null) exportMenu.Enabled = export.Enabled;
            close.Enabled = !writing;
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository || !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库已改变，请关闭并重新打开暂存集窗口。");
        }

        private void ValidateApplyContext()
        {
            ValidateContext();
            if (client.DiscoverWorkspace(root).Selector != selector)
                throw new InvalidOperationException("工作区分支已改变，请关闭并重新打开暂存集窗口后再应用。");
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

        private async Task<bool> LoadAsync()
        {
            if (busy) return false;
            entries = new List<PlasticShelve>(); RenderShelves(); status.Text = "正在读取暂存集…";
            bool succeeded = false;
            await WorkAsync(async token => {
                var result = await getShelves(root, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (result == null || result.Any(item => item == null || item.Repository != repository) || result.Select(item => item.ShelveId).Distinct().Count() != result.Count)
                    throw new InvalidOperationException("暂存集列表无效或仓库不匹配。");
                entries = result; RenderShelves(); succeeded = true;
            });
            return succeeded;
        }

        private void RenderShelves()
        {
            rendering = true; shelves.BeginUpdate();
            try {
                shelves.Items.Clear(); files.Items.Clear(); description.Clear();
                selectedDetails = new List<PlasticChangesetFile>(); detailsShelveId = null;
                string text = filter.Text.Trim();
                foreach (var entry in entries) {
                    string[] values = { "sh:" + entry.ShelveId, entry.Owner ?? "", entry.Date ?? "", entry.Comment ?? "" };
                    if (text.Length > 0 && !values.Any(value => value.IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0)) continue;
                    shelves.Items.Add(new ListViewItem(values) { Tag = entry });
                }
                status.Text = shelves.Items.Count + " / " + entries.Count + " 个暂存集。选择一项查看更改文件。";
            }
            finally { shelves.EndUpdate(); rendering = false; UpdateButtons(); }
        }

        private async Task LoadDetailsAsync()
        {
            if (busy || writing) return;
            files.Items.Clear(); description.Clear(); selectedDetails = new List<PlasticChangesetFile>(); detailsShelveId = null; UpdateButtons();
            if (shelves.SelectedItems.Count != 1) return;
            var selected = (PlasticShelve)shelves.SelectedItems[0].Tag;
            status.Text = "正在读取 sh:" + selected.ShelveId + "…";
            await WorkAsync(async token => {
                var details = await getChanges(root, selected.ShelveId, token); token.ThrowIfCancellationRequested(); ValidateContext();
                if (shelves.SelectedItems.Count != 1 || !Object.ReferenceEquals(shelves.SelectedItems[0].Tag, selected)) return;
                var loaded = details ?? new List<PlasticChangesetFile>();
                description.Text = selected.Comment;
                foreach (var file in loaded)
                    if (file != null) files.Items.Add(new ListViewItem(new[] { file.Path ?? "", file.Status ?? "", file.OldPath ?? "" }) { Tag = file });
                selectedDetails = loaded.Where(file => file != null).ToList(); detailsShelveId = selected.ShelveId;
                status.Text = "sh:" + selected.ShelveId + "：" + loaded.Count + " 个更改项。";
                UpdateButtons();
            });
        }

        private PlasticShelve SelectedShelve()
        {
            if (shelves.SelectedItems.Count != 1 || !detailsShelveId.HasValue)
                return null;
            var selected = shelves.SelectedItems[0].Tag as PlasticShelve;
            if (selected == null || selected.ShelveId != detailsShelveId.Value || selected.Repository != repository)
                return null;
            return selected;
        }

        private async Task ApplyAsync()
        {
            if (busy || writing) return;
            var selected = SelectedShelve();
            if (selected == null || selectedDetails.Count == 0) return;
            string identity = "sh:" + selected.ShelveId + "@" + repository;
            string message = "将暂存集 " + identity + " 应用到当前工作区。\r\n\r\n" +
                root + "\r\n" + selector + "\r\n" +
                "这要求 Standard 工作区没有待定更改；应用可能修改、新增或删除本地文件。\r\n" +
                "继续前请确认你已经检查了下方文件列表。\r\n\r\n继续应用？";
            if (confirm(message, "TortoiseSCM - 应用暂存集") != DialogResult.Yes) return;
            await MutateAsync(selected, true);
        }

        private async Task DeleteAsync()
        {
            if (busy || writing) return;
            var selected = SelectedShelve();
            if (selected == null) return;
            string identity = "sh:" + selected.ShelveId + "@" + repository;
            string message = "将从仓库永久删除暂存集 " + identity + "。\r\n\r\n" +
                "此操作不会删除或修改当前工作区文件，但删除后不能通过 TortoiseSCM 恢复。\r\n\r\n继续删除？";
            if (confirm(message, "TortoiseSCM - 删除暂存集") != DialogResult.Yes) return;
            await MutateAsync(selected, false);
        }

        private async Task MutateAsync(PlasticShelve selected, bool applyOperation)
        {
            if (selected == null || busy || writing || lifetime.IsCancellationRequested) return;
            try { if (applyOperation) ValidateApplyContext(); else ValidateContext(); }
            catch (Exception ex)
            {
                status.Text = "写入前检查失败：" + ex.Message;
                showPreflightError(ex.Message);
                return;
            }
            writing = true; UpdateButtons();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = cancellation;
            string identity = "sh:" + selected.ShelveId + "@" + repository;
            string action = applyOperation ? "应用" : "删除";
            try
            {
                status.Text = "正在" + action + " " + identity + "…";
                var operation = applyOperation ? applyShelve : deleteShelve;
                if (operation == null) throw new InvalidOperationException("当前版本没有可用的暂存集" + action + "后端接口。");
                var result = await operation(root, selected.ShelveId, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (result == null) throw new InvalidOperationException("Plastic 操作没有返回结果；请刷新并检查工作区状态。");
                if (!result.Succeeded)
                {
                    status.Text = action + "失败：" + (result.Error ?? "未知错误");
                    MessageBox.Show(this, (result.Output ?? "") + "\r\n" + (result.Error ?? ""), action + "暂存集失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                string success = action + "成功：" + identity + "。";
                bool refreshed = await LoadAsync();
                status.Text = refreshed ? success : success + " 但列表刷新失败，请点击“刷新”确认当前状态。";
            }
            catch (OperationCanceledException)
            {
                if (!lifetime.IsCancellationRequested)
                    status.Text = "已取消" + action + "请求；服务器结果不确定，请刷新并检查工作区或暂存集列表。";
            }
            catch (Exception ex)
            {
                if (!lifetime.IsCancellationRequested)
                {
                    status.Text = action + "失败：" + ex.Message;
                    MessageBox.Show(this, ex.Message, action + "暂存集失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (Object.ReferenceEquals(request, cancellation)) request = null;
                cancellation.Dispose(); writing = false;
                // A write may have partially completed. Require a fresh list and
                // file review before allowing another mutation of this row.
                detailsShelveId = null; selectedDetails = new List<PlasticChangesetFile>();
                if (!lifetime.IsCancellationRequested) UpdateButtons();
            }
        }

        private async Task CompareAsync()
        {
            if (busy || writing) return;
            var selected = SelectedShelve();
            if (selected == null || compareShelve == null) return;
            PlasticShelveComparison comparison = null;
            await WorkAsync(async token => {
                status.Text = "正在比较 sh:" + selected.ShelveId + "…";
                var loaded = await compareShelve(root, selected.ShelveId, token);
                token.ThrowIfCancellationRequested(); ValidateContext();
                if (loaded == null || loaded.Repository != repository || loaded.ShelveId != selected.ShelveId)
                    throw new InvalidOperationException("暂存集比较结果无效或仓库不匹配。");
                comparison = loaded;
                status.Text = "sh:" + selected.ShelveId + " 比较完成。";
            });
            if (comparison == null || lifetime.IsCancellationRequested) return;
            showComparison(comparison);
        }

        private async Task ExportAsync()
        {
            if (busy || writing) return;
            var selected = SelectedShelve();
            if (selected == null || selectedDetails.Count == 0 || exportShelve == null) return;
            using (var picker = new FolderBrowserDialog { Description = "选择暂存集导出目录（必须位于工作区之外）", ShowNewFolderButton = true })
            {
                if (picker.ShowDialog(this) != DialogResult.OK || String.IsNullOrWhiteSpace(picker.SelectedPath)) return;
                bool overwrite;
                try { overwrite = Directory.EnumerateFileSystemEntries(picker.SelectedPath).Any(); }
                catch (Exception ex)
                {
                    status.Text = "无法检查导出目录：" + ex.Message;
                    MessageBox.Show(this, ex.Message, "导出暂存集", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                string identity = "sh:" + selected.ShelveId + "@" + repository;
                string message = "将把暂存集 " + identity + " 导出到：\r\n" + picker.SelectedPath + "\r\n\r\n" +
                    (overwrite ? "目标目录已有内容；同名文件可能被覆盖。\r\n" : "只会创建新的导出文件。\r\n") +
                    "继续导出？";
                if (confirm(message, "TortoiseSCM - 导出暂存集") != DialogResult.Yes) return;
                await ExportAsync(selected, picker.SelectedPath, overwrite);
            }
        }

        private async Task ExportAsync(PlasticShelve selected, string destination, bool overwrite)
        {
            if (selected == null || busy || writing || lifetime.IsCancellationRequested) return;
            writing = true; UpdateButtons();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = cancellation;
            string identity = "sh:" + selected.ShelveId + "@" + repository;
            try
            {
                ValidateContext(); status.Text = "正在导出 " + identity + "…";
                var result = await exportShelve(root, selected.ShelveId, destination, overwrite, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (result == null) throw new InvalidOperationException("Plastic 导出没有返回结果；请检查导出目录。");
                if (result.Succeeded) status.Text = "导出成功：" + destination;
                else
                {
                    status.Text = "导出失败：" + (result.Error ?? "未知错误");
                    MessageBox.Show(this, (result.Output ?? "") + "\r\n" + (result.Error ?? ""), "导出暂存集失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (OperationCanceledException)
            {
                if (!lifetime.IsCancellationRequested) status.Text = "已取消导出请求；请检查导出目录后再决定是否重试。";
            }
            catch (Exception ex)
            {
                if (!lifetime.IsCancellationRequested)
                {
                    status.Text = "导出失败：" + ex.Message;
                    MessageBox.Show(this, ex.Message, "导出暂存集失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            finally
            {
                if (Object.ReferenceEquals(request, cancellation)) request = null;
                cancellation.Dispose(); writing = false;
                if (!lifetime.IsCancellationRequested) UpdateButtons();
            }
        }
    }

    internal sealed class ShelveComparisonForm : Form
    {
        private readonly ListView files = new ListView();
        private readonly TextBox preview = new TextBox();

        internal ShelveComparisonForm(PlasticShelveComparison comparison)
        {
            if (comparison == null) throw new ArgumentNullException("comparison");
            DialogStyle.Apply(this); Text = "暂存集比较 sh:" + comparison.ShelveId;
            Size = new Size(980, 680); MinimumSize = new Size(700, 480);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.Controls.Add(new Label { Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false,
                Text = "仓库：" + comparison.Repository + "\r\n基线 cs:" + comparison.ParentChangeset + " → 暂存集 sh:" + comparison.ShelveId }, 0, 0);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(940, 520), SplitterDistance = 240, Panel1MinSize = 90, Panel2MinSize = 120 };
            files.Dock = DockStyle.Fill; files.View = View.Details; files.FullRowSelect = true; files.MultiSelect = false; DialogStyle.ApplyList(files);
            files.Columns.Add("状态", 80); files.Columns.Add("路径", 540); files.Columns.Add("结果", 110);
            foreach (var item in comparison.Files ?? new List<PlasticShelveComparisonFile>())
            {
                string result = item.Diff == null ? "结构更改" : (item.Diff.IsBinary ? "二进制" : (item.Diff.HasChanges ? "有差异" : "无差异"));
                files.Items.Add(new ListViewItem(new[] { item.Status ?? "", item.Path ?? "", result }) { Tag = item });
            }
            files.SelectedIndexChanged += delegate {
                preview.Clear();
                if (files.SelectedItems.Count != 1) return;
                var item = files.SelectedItems[0].Tag as PlasticShelveComparisonFile;
                if (item == null || item.Diff == null) { preview.Text = "此项为目录、链接或删除结构；请使用 Plastic 官方客户端处理结构差异。"; return; }
                preview.Text = !item.Diff.HasChanges ? "两个版本的文件内容相同。" : (item.Diff.IsBinary ? "二进制文件有差异。" : item.Diff.DiffText);
                preview.Text = preview.Text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            };
            split.Panel1.Controls.Add(files);
            preview.Dock = DockStyle.Fill; preview.Multiline = true; preview.ReadOnly = true; preview.ScrollBars = ScrollBars.Both; preview.WordWrap = false; preview.Font = new Font(FontFamily.GenericMonospace, 9f); preview.AccessibleName = "暂存集比较预览";
            split.Panel2.Controls.Add(preview);
            layout.Controls.Add(split, 0, 1);
            var close = DialogStyle.Button("关闭"); close.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
            close.Click += delegate { Close(); }; CancelButton = close;
            layout.Controls.Add(close, 0, 2); Controls.Add(layout);
        }
    }
}
