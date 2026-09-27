// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    // Native changed-path report, following the history dialog list and footer conventions.
    internal sealed class ChangesetComparisonForm : Form
    {
        private readonly PlasticClient client;
        private readonly string workspacePath;
        private readonly string expectedRoot;
        private readonly string expectedRepository;
        private readonly long fromChangeset;
        private readonly long toChangeset;
        private readonly ListView files = new ListView();
        private readonly TextBox filter = new TextBox();
        private readonly Label status = new Label();
        private readonly Button refresh = DialogStyle.Button("刷新(&R)");
        private readonly Button cancel = DialogStyle.Button("取消加载");
        private readonly Button open = DialogStyle.Button("比较 / 导出…");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource request;
        private PlasticChangesetComparison comparison;
        private bool busy;

        public ChangesetComparisonForm(PlasticClient client, string workspacePath, string repository, long fromChangeset, long toChangeset)
        {
            this.client = client; this.workspacePath = workspacePath;
            this.fromChangeset = fromChangeset; this.toChangeset = toChangeset;
            var workspace = client.DiscoverWorkspace(workspacePath);
            if (workspace == null || workspace.Repository != repository)
                throw new InvalidOperationException("工作区仓库已改变，请重新打开历史窗口。");
            expectedRoot = workspace.RootPath; expectedRepository = repository;
            DialogStyle.Apply(this);
            Text = "变更集差异 - TortoiseSCM";
            Size = new Size(1000, 650); MinimumSize = new Size(780, 440);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(8), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = "整个仓库：" + repository + "    cs:" + fromChangeset + " → cs:" + toChangeset,
                Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            var search = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            search.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 70)); search.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            search.Controls.Add(new Label { Text = "筛选(&F)：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            filter.Dock = DockStyle.Fill; filter.AccessibleName = "筛选变更集差异的状态、路径、原路径或类型";
            filter.TextChanged += delegate { RenderFiles(); }; search.Controls.Add(filter, 1, 0); layout.Controls.Add(search, 0, 1);
            files.Dock = DockStyle.Fill; files.View = View.Details; files.MultiSelect = false; files.AccessibleName = "两个变更集之间的完整仓库差异";
            files.Columns.Add("操作", 85); files.Columns.Add("路径", 430); files.Columns.Add("原路径", 300); files.Columns.Add("类型", 70);
            DialogStyle.ApplyList(files); files.SelectedIndexChanged += delegate { UpdateActions(); };
            files.DoubleClick += delegate { OpenFile(); };
            files.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.C && !e.Alt && !e.Shift)
                { CopyPath(false); e.Handled = e.SuppressKeyPress = true; }
            };
            var menu = new ContextMenuStrip();
            var inspect = menu.Items.Add("比较 / 导出历史文件…", null, delegate { OpenFile(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("复制仓库路径", null, delegate { CopyPath(false); });
            var oldPath = menu.Items.Add("复制原仓库路径", null, delegate { CopyPath(true); });
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                var selected = SelectedFile(); e.Cancel = busy || selected == null;
                inspect.Enabled = open.Enabled; oldPath.Enabled = selected != null && !String.IsNullOrEmpty(selected.OldPath);
            };
            files.ContextMenuStrip = menu; layout.Controls.Add(files, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(status, 0, 3);
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            refresh.Click += async delegate { await LoadAsync(); };
            cancel.Enabled = false; cancel.Click += delegate { if (request != null) request.Cancel(); };
            navigation.Controls.Add(refresh); navigation.Controls.Add(cancel); footer.Controls.Add(navigation, 0, 0);
            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            open.Width = 125; open.Enabled = false; open.Click += delegate { OpenFile(); };
            close.Click += delegate { Close(); }; CancelButton = close;
            actions.Controls.Add(open); actions.Controls.Add(close); footer.Controls.Add(actions, 1, 0);
            layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
            Shown += async delegate { await LoadAsync(); };
            FormClosing += delegate { lifetime.Cancel(); if (request != null) request.Cancel(); };
        }

        private async Task LoadAsync()
        {
            if (busy || lifetime.IsCancellationRequested) return;
            busy = true; comparison = null; files.Items.Clear(); UpdateActions();
            refresh.Enabled = filter.Enabled = false; cancel.Enabled = true;
            status.Text = "正在比较整个仓库的两个快照…";
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); request = cancellation;
            try
            {
                ValidateContext();
                var result = await client.GetChangesetComparisonAsync(workspacePath, fromChangeset, toChangeset, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested(); ValidateContext();
                if (result.Repository != expectedRepository || !SameRoot(result.RootPath, expectedRoot))
                    throw new InvalidOperationException("工作区或仓库已改变，请重新打开比较窗口。");
                comparison = result;
            }
            catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) status.Text = "已取消比较。点击刷新重试。"; }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested) status.Text = "比较失败：" + ex.Message; }
            finally
            {
                request = null; cancellation.Dispose(); busy = false;
                if (!lifetime.IsCancellationRequested)
                {
                    refresh.Enabled = filter.Enabled = true; cancel.Enabled = false;
                    if (comparison != null) RenderFiles(); else UpdateActions();
                }
            }
        }

        private void RenderFiles()
        {
            files.BeginUpdate();
            try
            {
                files.Items.Clear();
                if (busy || comparison == null) return;
                string text = filter.Text.Trim();
                foreach (var file in comparison.Files)
                {
                    var values = new[] { file.Status, file.Path, file.OldPath ?? "", file.ItemType };
                    if (text.Length > 0 && !values.Any(value => (value ?? "").IndexOf(text, StringComparison.CurrentCultureIgnoreCase) >= 0)) continue;
                    files.Items.Add(new ListViewItem(values) { Tag = file });
                }
                status.Text = files.Items.Count + " / " + comparison.Files.Count + " 项差异；A 新增，C 修改，D 删除，M 移动。目录、链接和挂载项仅列出。";
            }
            finally { files.EndUpdate(); UpdateActions(); }
        }

        private PlasticChangesetFile SelectedFile()
        { return files.SelectedItems.Count == 1 ? files.SelectedItems[0].Tag as PlasticChangesetFile : null; }

        private void UpdateActions()
        {
            var file = SelectedFile();
            open.Enabled = !busy && comparison != null && file != null && (file.ItemType == "F" || file.ItemType == "B");
        }

        private HistoricalFileForm CreateFileDialog()
        {
            ValidateContext();
            if (!open.Enabled) throw new InvalidOperationException("请选择可读取的历史文件。");
            return new HistoricalFileForm(client, workspacePath, comparison, SelectedFile());
        }

        private void OpenFile()
        {
            if (!open.Enabled) return;
            try { using (var dialog = CreateFileDialog()) dialog.ShowDialog(this); }
            catch (Exception ex) { status.Text = "无法打开历史文件：" + ex.Message; }
        }

        private void CopyPath(bool original)
        {
            var file = SelectedFile(); if (busy || file == null) return;
            string text = original ? file.OldPath : file.Path; if (String.IsNullOrEmpty(text)) return;
            try { Clipboard.SetText(text); }
            catch (System.Runtime.InteropServices.ExternalException) { status.Text = "剪贴板暂时不可用，请稍后重试。"; }
            catch (System.Threading.ThreadStateException) { status.Text = "当前线程无法访问剪贴板。"; }
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(workspacePath);
            if (workspace == null || workspace.Repository != expectedRepository || !SameRoot(workspace.RootPath, expectedRoot))
                throw new InvalidOperationException("工作区或仓库已改变，请关闭并重新打开比较窗口。");
        }

        private static bool SameRoot(string first, string second)
        { return first != null && second != null && first.TrimEnd('\\', '/').Equals(second.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F5) { if (refresh.Enabled) refresh.PerformClick(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
