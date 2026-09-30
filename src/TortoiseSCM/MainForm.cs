// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class MainForm : Form
    {
        private readonly LaunchRequest launch;
        private PlasticClient client;
        private PlasticWorkspace workspace;
        private readonly ListView files = new PendingChangesListView();
        private readonly TextBox comment = new TextBox();
        private readonly Button messageLibrary = DialogStyle.Button("说明历史 / 模板…");
        private readonly TextBox output = new TextBox();
        private readonly Button actions = new Button();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Label scope = new Label();
        private readonly Button checkin = new Button();
        private readonly LinkLabel selectAll = new LinkLabel();
        private readonly LinkLabel selectNone = new LinkLabel();
        private readonly CheckBox showUnversioned = new CheckBox();
        private bool busy;
        private bool loaded;
        private bool changingChecks;
        private bool submissionNeedsRefresh;
        private bool submissionUncertain;
        private string submissionNotice = "";
        private Func<string, CancellationToken, Task<IList<PlasticStatusItem>>> getPending;
        private Func<string, IList<string>, string, string, CancellationToken, Task<PlasticCheckinPreview>> prepareCheckin;
        private Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>> submitCheckin;
        private Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>> runCommand;
        private Func<PlasticCheckinPreview, string, bool, DialogResult> reviewCheckin;
        private Action<string> reportError;
        private CommitMessageStore messageStore = CommitMessageStore.CreateDefault();
        private Func<string, string, string> showMessageLibrary;
        private Func<string, bool> confirmMessageReplacement;

        public MainForm(LaunchRequest request) : this(request, true) { }

        internal MainForm(LaunchRequest request, bool initialize)
        {
            launch = request;
            getPending = (path, token) => client.GetStatusAsync(path, token);
            // The dialog has already shown and reviewed the pending list. Use
            // the same single native command as Gluon instead of re-hashing all
            // files and re-querying locks/status before dispatch.
            prepareCheckin = (root, paths, repository, selector, token) => client.PrepareCheckinFastAsync(root, paths, repository, selector, token);
            submitCheckin = (preview, message, token) => client.CheckinPreparedFastAsync(preview, message, token);
            runCommand = (commandRequest, token) => client.RunAsync(commandRequest, token);
            // The preview is already shown in the main check-in window. Submit
            // immediately after preflight; keep this delegate as a test seam so
            // UI tests can still simulate a cancelled submission without opening
            // a second confirmation dialog in production.
            reviewCheckin = (preview, message, uncertain) => DialogResult.OK;
            reportError = message => MessageBox.Show(this, message, "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            showMessageLibrary = (repository, draft) => {
                using (var dialog = new CommitMessageLibraryForm(messageStore, repository, draft))
                    return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedMessage : null;
            };
            confirmMessageReplacement = message => MessageBox.Show(this, "当前提交说明已有内容。使用所选说明替换全部现有内容？",
                "替换提交说明 - TortoiseSCM", MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                MessageBoxDefaultButton.Button2) == DialogResult.OK;
            Text = "TortoiseSCM — 待定更改";
            DialogStyle.Apply(this);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 580);
            Size = new Size(930, 720);
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildLayout();
            if (initialize) Shown += async delegate { await InitializeAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy)
                {
                    e.Cancel = true;
                    reportError("操作正在进行，请等待完成后关闭窗口。");
                }
            };
        }

        private void BuildLayout()
        {
            // Follow IDD_COMMITDLG: message above changes, selection links, bottom command row.
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            scope.Dock = DockStyle.Fill;
            scope.UseMnemonic = false;
            scope.AutoEllipsis = true;
            scope.Text = "正在识别工作区…";
            layout.Controls.Add(scope, 0, 0);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
                Size = new Size(880, 520), SplitterDistance = 155, Panel1MinSize = 100, Panel2MinSize = 160, SplitterWidth = 5 };
            split.Name = "commitSplit";
            var messageGroup = new GroupBox { Text = "提交说明 (&M)", Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 8) };
            comment.Multiline = true;
            comment.AcceptsReturn = true;
            comment.ScrollBars = ScrollBars.Vertical;
            comment.Dock = DockStyle.Fill;
            comment.AccessibleName = "签入说明";
            comment.MaxLength = CommitMessageStore.MaxMessageLength;
            var messageLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            messageLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            messageLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            messageLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var messageTools = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            messageLibrary.Width = 160; messageLibrary.Dock = DockStyle.Right; messageLibrary.Enabled = false;
            messageLibrary.Click += delegate { OpenMessageLibrary(); };
            messageTools.Controls.Add(messageLibrary);
            messageLayout.Controls.Add(messageTools, 0, 0); messageLayout.Controls.Add(comment, 0, 1);
            messageGroup.Controls.Add(messageLayout);
            split.Panel1.Controls.Add(messageGroup);

            var changesGroup = new GroupBox { Text = "更改的文件", Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 8) };
            var changes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            changes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            changes.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            changes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var selectionBar = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            showUnversioned.Text = "显示未版本控制文件";
            showUnversioned.AutoSize = true;
            showUnversioned.Margin = new Padding(0, 1, 12, 0);
            showUnversioned.CheckedChanged += async delegate { if (loaded && !busy) await RefreshAsync(); };
            selectionBar.Controls.Add(showUnversioned);
            selectionBar.Controls.Add(new Label { Text = "选择：", AutoSize = true, Margin = new Padding(0, 3, 2, 0) });
            selectAll.Text = "全部 (&A)";
            selectNone.Text = "无 (&N)";
            selectAll.AutoSize = selectNone.AutoSize = true;
            selectAll.Margin = selectNone.Margin = new Padding(0, 3, 12, 0);
            selectAll.LinkClicked += delegate { foreach (ListViewItem item in files.Items) item.Checked = true; };
            selectNone.LinkClicked += delegate { foreach (ListViewItem item in files.Items) item.Checked = false; };
            selectionBar.Controls.Add(selectAll);
            selectionBar.Controls.Add(selectNone);
            AddSelectionLink(selectionBar, "已版本控制", item => !IsPrivate(item.StatusCode));
            AddSelectionLink(selectionBar, "未版本控制", item => IsPrivate(item.StatusCode));
            changes.Controls.Add(selectionBar, 0, 0);
            files.Dock = DockStyle.Fill;
            files.View = View.Details;
            files.CheckBoxes = true;
            DialogStyle.ApplyList(files);
            files.Columns.Add("路径", 570);
            files.Columns.Add("扩展名", 80);
            files.Columns.Add("状态", 150);
            files.AccessibleName = "待定更改列表";
            files.ItemChecked += OnItemChecked;
            files.DoubleClick += async delegate { await ExecuteAsync(PlasticCommand.Diff); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Annotate / Blame", null, async delegate { await ShowBlameAsync(HighlightedPaths()); });
            menu.Items.Add("显示历史 / 恢复版本", null, async delegate { await ExecuteAsync(PlasticCommand.History, HighlightedPaths()); });
            menu.Items.Add("查看差异", null, async delegate { await ExecuteAsync(PlasticCommand.Diff, HighlightedPaths()); });
            menu.Items.Add("撤销更改…", null, async delegate { await ExecuteAsync(PlasticCommand.Undo, HighlightedPaths()); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("重命名 / 移动…", null, async delegate { await ExecuteFileOperationAsync("move", HighlightedPaths()); });
            menu.Items.Add("删除受控项…", null, async delegate { await ExecuteFileOperationAsync("remove", HighlightedPaths()); });
            menu.Items.Add("加入忽略列表", null, async delegate { await ExecuteFileOperationAsync("ignore", HighlightedPaths()); });
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = busy || files.SelectedItems.Count == 0; };
            files.ContextMenuStrip = menu;
            changes.Controls.Add(files, 0, 1);
            changesGroup.Controls.Add(changes);
            split.Panel2.Controls.Add(changesGroup);
            layout.Controls.Add(split, 0, 1);
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            status.UseMnemonic = false;
            layout.Controls.Add(status, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            var refresh = new Button { Text = "刷新 (&R)", Size = new Size(86, 26), Margin = new Padding(0, 4, 6, 0) };
            refresh.Click += async delegate { await RefreshAsync(); };
            actions.Text = "操作 (&O) ▾";
            actions.Size = new Size(94, 26);
            actions.Margin = new Padding(0, 4, 6, 0);
            var operations = new ContextMenuStrip();
            operations.Items.Add("更新…", null, async delegate { await ExecuteAsync(PlasticCommand.Update); });
            operations.Items.Add("添加…", null, async delegate { await ExecuteAsync(PlasticCommand.Add); });
            operations.Items.Add("签出…", null, async delegate { await ExecuteAsync(PlasticCommand.Checkout); });
            operations.Items.Add("撤销勾选项…", null, async delegate { await ExecuteAsync(PlasticCommand.Undo); });
            operations.Items.Add("重命名 / 移动…", null, async delegate { await ExecuteFileOperationAsync("move", null); });
            operations.Items.Add("删除受控项…", null, async delegate { await ExecuteFileOperationAsync("remove", null); });
            operations.Items.Add("加入忽略列表…", null, async delegate { await ExecuteFileOperationAsync("ignore", null); });
            operations.Items.Add(new ToolStripSeparator());
            operations.Items.Add("Annotate / Blame", null, async delegate { await ShowBlameAsync(null); });
            operations.Items.Add("查看差异", null, async delegate { await ExecuteAsync(PlasticCommand.Diff); });
            operations.Items.Add("所选项历史", null, async delegate { await ExecuteAsync(PlasticCommand.History); });
            operations.Items.Add("当前范围历史 / 恢复", null, async delegate { await ShowScopeHistoryAsync(); });
            operations.Items.Add("分支…", null, async delegate { await ShowBranchesAsync(); });
            operations.Items.Add("暂存集…", null, delegate { ShowShelves(); });
            operations.Items.Add("标签…", null, delegate { ShowLabels(); });
            operations.Items.Add("仓库浏览器…", null, delegate { ShowRepositoryBrowser(null); });
            operations.Items.Add("版本关系图…", null, delegate { ShowRevisionGraph(null); });
            operations.Items.Add("保存勾选项为暂存集…", null, async delegate { await SaveShelveAsync(); });
            operations.Items.Add("合并变更集 / 解决冲突…", null, async delegate {
                if (busy || !loaded) return;
                using (var dialog = new MergeForm(client, workspace.RootPath)) dialog.ShowDialog(this);
                await RefreshAsync();
            });
            operations.Items.Add("撤销整个合并 / 回滚…", null, async delegate {
                if (busy || !loaded) return;
                if (!client.HasSavedMergeSession(workspace.RootPath) && !File.Exists(Path.Combine(workspace.RootPath, ".plastic", "plastic.mergeprogress")))
                { MessageBox.Show(this, "当前没有合并或整仓回滚会话。", "TortoiseSCM"); return; }
                await ExecuteAsync(PlasticCommand.Undo, new List<string> { workspace.RootPath });
            });
            operations.Items.Add("Partial 传入冲突…", null, async delegate {
                if (busy || !loaded) return;
                using (var dialog = new PartialConflictForm(client, workspace.RootPath)) dialog.ShowDialog(this);
                await RefreshAsync();
            });
            operations.Items.Add("Partial 文件结构冲突…", null, async delegate {
                if (busy || !loaded) return;
                using (var dialog = new PartialStructureForm(client, workspace.RootPath)) dialog.ShowDialog(this);
                await RefreshAsync();
            });
            operations.Items.Add("Partial 目录冲突…", null, async delegate {
                if (busy || !loaded) return;
                using (var dialog = new PartialDirectoryForm(client, workspace.RootPath)) dialog.ShowDialog(this);
                await RefreshAsync();
            });
            operations.Items.Add("锁管理…", null, delegate {
                if (busy || !loaded) return;
                using (var dialog = new LocksForm(client, workspace.RootPath)) dialog.ShowDialog(this);
            });
            operations.Items.Add("刷新 Explorer 状态图标", null, async delegate {
                if (busy || !loaded) return;
                SetBusy(true, "正在刷新状态图标…");
                try { int count = await OverlayCacheHost.RefreshAsync(PlasticClientConfig.Load(), workspace.RootPath, CancellationToken.None); status.Text = "已缓存 " + count + " 个路径的状态。"; }
                catch (Exception ex) { ShowError(ex); }
                finally { SetBusy(false, status.Text); }
            });
            operations.Items.Add("操作记录…", null, delegate { ShowOutput(); });
            operations.Items.Add(new ToolStripSeparator());
            operations.Items.Add("打开 Gluon", null, async delegate { await ExecuteAsync(PlasticCommand.Gluon); });
            operations.Items.Add("拉取仓库…", null, delegate {
                if (busy) return;
                using (var wizard = new WorkspaceCreationForm())
                {
                    if (wizard.ShowDialog(this) != DialogResult.OK) return;
                    try {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Application.ExecutablePath,
                            "--path " + PlasticClient.QuoteArgument(wizard.SelectedWorkspacePath)) { UseShellExecute = false });
                    }
                    catch (Exception ex) { ShowError(new InvalidOperationException("工作区已创建，但无法打开新窗口。请从该目录右键打开 TortoiseSCM。\r\n" + wizard.SelectedWorkspacePath, ex)); }
                }
            });
            operations.Items.Add("设置…", null, delegate { using (var settings = new SettingsForm()) settings.ShowDialog(this); client = WinFormsPlasticToolHost.CreateClient(PlasticClientConfig.Load(), this); });
            operations.Items.Add("版本信息…", null, delegate { using (var version = new VersionInfoForm()) version.ShowDialog(this); });
            actions.Click += delegate { operations.Show(actions, new Point(0, actions.Height)); };
            left.Controls.Add(refresh);
            left.Controls.Add(actions);
            progress.Size = new Size(110, 16);
            progress.Style = ProgressBarStyle.Marquee;
            progress.Visible = false;
            progress.Margin = new Padding(4, 9, 0, 0);
            left.Controls.Add(progress);
            footer.Controls.Add(left, 0, 0);
            var right = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            checkin.Text = "提交 (&C)";
            checkin.Size = new Size(95, 26);
            checkin.Margin = new Padding(6, 4, 0, 0);
            checkin.Click += async delegate { await CheckinAsync(); };
            var close = new Button { Text = "关闭", Size = new Size(86, 26), Margin = new Padding(6, 4, 0, 0) };
            close.Click += delegate { Close(); };
            right.Controls.Add(checkin);
            right.Controls.Add(close);
            footer.Controls.Add(right, 1, 0);
            layout.Controls.Add(footer, 0, 3);
            Controls.Add(layout);
            CancelButton = close;
            output.Multiline = true;
            output.ReadOnly = true;
        }

        private void OpenMessageLibrary()
        {
            if (busy || !loaded) return;
            try {
                ValidatePendingContext();
                string selected = showMessageLibrary(workspace.Repository, comment.Text);
                if (selected == null) return;
                ValidatePendingContext();
                if (String.IsNullOrWhiteSpace(selected) || selected.Length > CommitMessageStore.MaxMessageLength)
                    throw new InvalidOperationException("所选提交说明无效，请重新选择。");
                if (comment.Text.Length != 0 && comment.Text != selected && !confirmMessageReplacement(selected)) return;
                ValidatePendingContext();
                comment.Text = selected; comment.Focus(); comment.SelectionStart = comment.TextLength;
            }
            catch (Exception ex) { AppendOutput(ex.Message); reportError("无法使用提交说明：" + ex.Message); }
        }

        private void AddSelectionLink(FlowLayoutPanel panel, string text, Func<PlasticStatusItem, bool> predicate)
        {
            var link = new LinkLabel { Text = text, AutoSize = true, Margin = new Padding(0, 3, 12, 0) };
            link.LinkClicked += delegate
            {
                if (busy) return;
                // Filter selection is exact: do not let a recursive directory reselect excluded children.
                changingChecks = true;
                try
                {
                    foreach (ListViewItem item in files.Items) item.Checked = predicate((PlasticStatusItem)item.Tag);
                    foreach (ListViewItem parent in files.Items)
                        if (((PlasticStatusItem)parent.Tag).IsDirectory && files.Items.Cast<ListViewItem>().Any(child =>
                            !child.Checked && ((PlasticStatusItem)child.Tag).Path.StartsWith(((PlasticStatusItem)parent.Tag).Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                            parent.Checked = false;
                }
                finally { changingChecks = false; }
                UpdateSelectionCount();
            };
            panel.Controls.Add(link);
        }

        private void ShowOutput()
        {
            using (var dialog = new Form { Text = "操作记录 - TortoiseSCM", Size = new Size(850, 520), MinimumSize = new Size(600, 360), StartPosition = FormStartPosition.CenterParent })
            {
                DialogStyle.Apply(dialog);
                var text = new TextBox { Text = output.Text, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill };
                var close = new Button { Text = "关闭", Dock = DockStyle.Bottom, Height = 28, DialogResult = DialogResult.Cancel };
                dialog.Controls.Add(text); dialog.Controls.Add(close); dialog.CancelButton = close;
                dialog.ShowDialog(this);
            }
        }

        private async Task ExecuteFileOperationAsync(string operation, List<string> explicitPaths)
        {
            if (busy || !loaded) return;
            var paths = explicitPaths ?? SelectedPaths(true, true);
            if (paths.Count != 1) { MessageBox.Show(this, "请仅选择一个文件或目录。", "TortoiseSCM"); return; }
            string path = paths[0], destination = null;
            string title = operation == "move" ? "重命名 / 移动" : operation == "remove" ? "删除受控项" : "加入忽略列表";
            if (operation == "move")
            {
                using (var dialog = new PathInputForm(path))
                { if (dialog.ShowDialog(this) != DialogResult.OK) return; destination = dialog.Destination; }
            }
            else
            {
                string note = operation == "remove" ? "从磁盘删除此受控文件或目录，生成待提交删除。目录包含后代。\r\n存在本地更改或私有项时会拒绝操作。" :
                    "将此未版本控制项的精确路径写入工作区 ignore.conf。\r\n目录规则包含后代；不会删除文件，也不会取消已受控文件的跟踪。";
                if (MessageBox.Show(this, note + "\r\n\r\n" + path + "\r\n\r\n继续？", title, MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            }
            SetBusy(true, "正在" + title + "…");
            try
            {
                PlasticCommandResult result;
                if (operation == "move") result = await client.MoveAsync(path, destination, CancellationToken.None);
                else if (operation == "remove") result = await client.RemoveAsync(path, CancellationToken.None);
                else result = await client.IgnoreAsync(path, CancellationToken.None);
                AppendOutput("[" + title + "] " + path); AppendOutput(result.Output); AppendOutput(result.Error);
                if (!result.Succeeded) MessageBox.Show(this, result.Error + "\r\n" + result.Output, "操作未成功", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SHChangeNotify(0x00002000, 0x0005, Path.GetDirectoryName(path), IntPtr.Zero);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { SetBusy(false, ""); }
            await RefreshAsync();
        }
        private async Task InitializeAsync()
        {
            SetBusy(true, "正在识别工作区…");
            try
            {
                client = WinFormsPlasticToolHost.CreateClient(PlasticClientConfig.Load(), this);
                workspace = await client.GetWorkspaceAsync(launch.Paths[0], CancellationToken.None);
                if (workspace == null) throw new InvalidOperationException("此路径不属于 Plastic SCM 工作区：" + launch.Paths[0]);
                foreach (string path in launch.Paths)
                {
                    var other = client.DiscoverWorkspace(path);
                    if (other == null || !string.Equals(other.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("请一次只选择同一 Plastic 工作区内的文件。");
                }
                Text = string.Join("; ", launch.Paths.ToArray()) + " - 提交 - TortoiseSCM";
                scope.Text = "工作区：" + workspace.RootPath + (workspace.IsPartial ? "  ·  Gluon / 部分工作区" : "  ·  完整工作区") +
                    "\r\n范围：" + string.Join("；", launch.Paths.ToArray());
                loaded = true;
                OverlayCacheHost.TrackAndStart(workspace.RootPath);
                SetBusy(false, "");
                await RefreshAsync();
                if (launch.Command == "history") await ExecuteAsync(PlasticCommand.History, new List<string>(launch.Paths));
                else if (launch.Command == "blame") await ShowBlameAsync(launch.Paths);
                else if (launch.Command == "diff") await ExecuteAsync(PlasticCommand.Diff);
                else if (launch.Command == "gluon") await ExecuteAsync(PlasticCommand.Gluon);
                else if (launch.Command == "checkin") comment.Focus();
                else if (launch.Command == "add") await ExecuteAsync(PlasticCommand.Add, new List<string>(launch.Paths));
                else if (launch.Command == "move" || launch.Command == "remove" || launch.Command == "ignore")
                    await ExecuteFileOperationAsync(launch.Command, new List<string>(launch.Paths));
                else if (launch.Command == "locks" || launch.Command == "unlock")
                {
                    // Unlocking is intentionally performed from the same lock
                    // dialog as the in-app menu; the dialog rechecks ownership
                    // immediately before issuing cm lock unlock.
                    using (var dialog = new LocksForm(client, workspace.RootPath)) dialog.ShowDialog(this);
                    await RefreshAsync();
                }
                else if (launch.Command == "branches") await ShowBranchesAsync();
                else if (launch.Command == "shelves") ShowShelves();
                else if (launch.Command == "labels") ShowLabels();
                else if (launch.Command == "repository-browser") ShowRepositoryBrowser(launch.Changeset);
                else if (launch.Command == "revision-graph") ShowRevisionGraph(launch.Before);
                else if (launch.Command == "merge")
                {
                    using (var dialog = new MergeForm(client, workspace.RootPath)) dialog.ShowDialog(this);
                    await RefreshAsync();
                }
                else if (launch.Command == "export" || launch.Command == "rollback" || launch.Command == "recover")
                {
                    // Historical export and restore share the bounded history
                    // surface.  The user picks the changeset and file there,
                    // preserving the same safety checks as the normal menu.
                    if (launch.Paths.Count != 1) throw new InvalidOperationException("历史操作需要一个文件或目录范围。");
                    using (var history = new HistoryForm(client, launch.Paths[0], workspace.RootPath)) history.ShowDialog(this);
                    await RefreshAsync();
                }
                else if (launch.Command != "status")
                    status.Text = "请核对选择范围后点击“" + CommandName(ParseCommand(launch.Command)) + "”执行。";
            }
            catch (Exception ex) { SetBusy(false, "操作未成功"); ShowError(ex); }
        }

        private async Task ShowBranchesAsync()
        {
            if (busy || !loaded) return;
            try
            {
                using (var dialog = new BranchForm(client, workspace.RootPath)) dialog.ShowDialog(this);
                workspace = await client.GetWorkspaceAsync(workspace.RootPath, CancellationToken.None);
                await RefreshAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private bool InScope(string path)
        {
            return launch.Paths.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(p.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));
        }

        private void ShowRevisionGraph(long? before)
        {
            if (busy || !loaded) return;
            try { using (var dialog = new RevisionGraphForm(client, workspace.RootPath, before)) dialog.ShowDialog(this); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ShowLabels()
        {
            if (busy || !loaded) return;
            try { using (var dialog = new LabelsForm(client, workspace.RootPath)) dialog.ShowDialog(this); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ShowShelves()
        {
            if (busy || !loaded) return;
            try { using (var dialog = new ShelvesForm(client, workspace.RootPath)) dialog.ShowDialog(this); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void ShowRepositoryBrowser(long? changeset)
        {
            if (busy || !loaded) return;
            try { using (var dialog = new RepositoryBrowserForm(client, workspace.RootPath, changeset, workspace.Repository)) dialog.ShowDialog(this); }
            catch (Exception ex) { ShowError(ex); }
        }

        private ShelveCreateForm CreateShelveDialog()
        {
            var selected = files.CheckedItems.Cast<ListViewItem>().Select(row => (PlasticStatusItem)row.Tag).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("请先勾选需要保存的受控文件。");
            if (selected.Any(item => !InScope(item.Path))) throw new InvalidOperationException("勾选项不在当前目录范围内，请刷新后重新选择。");
            if (selected.Any(item => IsPrivate(item.StatusCode) || item.StatusCode == "IG"))
                throw new InvalidOperationException("暂存集不能包含未版本控制或忽略的文件，请先添加或取消勾选这些文件。");
            if (selected.Any(item => !new[] { "CH", "CO" }.Contains(item.StatusCode, StringComparer.OrdinalIgnoreCase)))
                throw new InvalidOperationException("当前暂存集仅支持内容修改（CH/CO）。新增、删除、移动等结构更改请先单独处理。");
            if (selected.Any(item => item.IsDirectory || Directory.Exists(item.Path)))
                throw new InvalidOperationException("当前暂存集仅支持文件。请取消勾选目录，然后逐项勾选需要保存的文件。");
            return new ShelveCreateForm(client, workspace.RootPath, workspace.Repository, workspace.Selector,
                selected.Select(item => item.Path).ToArray(), comment.Text);
        }

        private async Task SaveShelveAsync()
        {
            if (busy || !loaded) return;
            try {
                using (var dialog = CreateShelveDialog()) {
                    if (dialog.ShowDialog(this) == DialogResult.OK)
                        MessageBox.Show(this, "暂存集已保存。本地修改仍然保留，可在“操作 → 暂存集”查看。", "TortoiseSCM");
                }
                await RefreshAsync();
            }
            catch (Exception ex) { ShowError(ex); }
        }

        private async Task<bool> RefreshAsync()
        {
            if (busy || !loaded) return false;
            SetBusy(true, "正在读取工作区状态…");
            bool refreshed = false;
            string failure = null;
            try
            {
                ValidatePendingContext();
                var checkedPaths = new HashSet<string>(files.CheckedItems.Cast<ListViewItem>().Select(i => ((PlasticStatusItem)i.Tag).Path), StringComparer.OrdinalIgnoreCase);
                var previousPaths = new HashSet<string>(files.Items.Cast<ListViewItem>().Select(i => ((PlasticStatusItem)i.Tag).Path), StringComparer.OrdinalIgnoreCase);
                var items = await getPending(workspace.RootPath, CancellationToken.None);
                ValidatePendingContext();
                if (items == null || items.Any(item => item == null)) throw new InvalidOperationException("待定状态返回无效，请重新刷新。");
                bool previousChangingChecks = changingChecks;
                changingChecks = true;
                files.BeginUpdate();
                try
                {
                    files.Items.Clear();
                    foreach (var item in items.Where(i => InScope(i.Path) && (showUnversioned.Checked || !IsPrivate(i.StatusCode))))
                    {
                        string relative = item.Path.StartsWith(workspace.RootPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                            ? item.Path.Substring(workspace.RootPath.TrimEnd('\\').Length + 1) : item.Path;
                        if (!string.IsNullOrEmpty(item.OldPath)) relative = item.OldPath + " → " + relative;
                        var row = new ListViewItem(relative) { Tag = item, Checked = checkedPaths.Contains(item.Path) ||
                            (!previousPaths.Contains(item.Path) && !IsPrivate(item.StatusCode)) };
                        row.SubItems.Add(item.IsDirectory ? "" : Path.GetExtension(item.Path));
                        row.SubItems.Add(item.StatusDescription);
                        files.Items.Add(row);
                    }
                }
                finally { files.EndUpdate(); changingChecks = previousChangingChecks; }
                refreshed = true;
                submissionNeedsRefresh = false;
                if (submissionUncertain) submissionNotice = "状态已刷新；请查看历史核对上次提交，再确认是否重试。";
                else submissionNotice = "";
            }
            catch (Exception ex)
            {
                failure = "刷新失败：" + ex.Message; submissionNeedsRefresh = true;
                submissionNotice = "刷新未完成；原勾选和说明已保留，请重试刷新。";
                AppendOutput(failure); reportError(failure);
            }
            finally { SetBusy(false, ""); if (failure != null) status.Text = failure; else UpdateSelectionCount(); }
            return refreshed;
        }

        private void ValidatePendingContext()
        {
            var current = client.DiscoverWorkspace(workspace.RootPath);
            if (current == null || current.Repository != workspace.Repository || current.Selector != workspace.Selector ||
                current.Name != workspace.Name || !String.Equals(current.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库或分支已改变，请关闭并重新打开待定更改窗口。");
        }

        private List<string> SelectedPaths(bool forRead, bool allowScope)
        {
            if (forRead && files.SelectedItems.Count > 0)
                return files.SelectedItems.Cast<ListViewItem>().Select(i => ((PlasticStatusItem)i.Tag).Path).ToList();
            var paths = files.CheckedItems.Cast<ListViewItem>().Select(i => ((PlasticStatusItem)i.Tag).Path).ToList();
            if (paths.Count == 0 && allowScope) paths.AddRange(launch.Paths);
            return paths;
        }

        private List<string> HighlightedPaths()
        { return files.SelectedItems.Cast<ListViewItem>().Select(i => ((PlasticStatusItem)i.Tag).Path).ToList(); }

        private void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (changingChecks || e.Item.Tag == null) return;
            changingChecks = true;
            try
            {
                var changed = (PlasticStatusItem)e.Item.Tag;
                foreach (ListViewItem row in files.Items)
                {
                    var item = (PlasticStatusItem)row.Tag;
                    if (item == null || row == e.Item) continue;
                    if (changed.IsDirectory && item.Path.StartsWith(changed.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        row.Checked = e.Item.Checked;
                    if (!e.Item.Checked && item.IsDirectory && changed.Path.StartsWith(item.Path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                        row.Checked = false;
                }
            }
            finally { changingChecks = false; UpdateSelectionCount(); }
        }

        private async Task ShowScopeHistoryAsync()
        {
            if (busy || !loaded) return;
            if (launch.Paths.Count != 1) { MessageBox.Show(this, "请以一个文件或目录作为历史范围。", "TortoiseSCM"); return; }
            await ExecuteAsync(PlasticCommand.History, new List<string>(launch.Paths));
        }

        private async Task ShowBlameAsync(List<string> explicitPaths)
        {
            if (busy || !loaded) return;
            var paths = explicitPaths ?? SelectedPaths(true, true);
            if (paths.Count != 1 || Directory.Exists(paths[0]))
            { MessageBox.Show(this, "Annotate requires exactly one file.", "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            try { using (var dialog = new BlameForm(client, paths[0], workspace.RootPath)) dialog.ShowDialog(this); }
            catch (Exception ex) { ShowError(ex); }
            await Task.CompletedTask;
        }

        private Task ExecuteAsync(PlasticCommand command) { return ExecuteAsync(command, null); }

        private async Task CheckinAsync()
        {
            if (busy || !loaded) return;
            if (submissionNeedsRefresh) { reportError("上次签入未确认，请先刷新状态并查看历史，核对服务器结果后再重试。"); return; }
            if (!workspace.IsPartial && (client.HasSavedMergeSession(workspace.RootPath) || File.Exists(Path.Combine(workspace.RootPath, ".plastic", "plastic.mergeprogress"))))
            {
                SetBusy(true, "正在检查合并状态…");
                try
                {
                    var session = await client.GetMergeSessionAsync(workspace.RootPath, CancellationToken.None);
                    if (session == null) throw new InvalidOperationException("当前合并没有本程序的会话，请在启动该合并的 Plastic 客户端完成提交。");
                    if (session.AwaitingDirectoryResolution) throw new InvalidOperationException("请先在合并窗口中处理目录结构冲突并应用结构方案。");
                    if (session.Plan.FileConflicts.Any(item => !item.Resolved) || session.Plan.DirectoryConflicts.Any(item => !item.Resolved))
                        throw new InvalidOperationException("请先在“操作 → 合并变更集 / 解决冲突”中解决所有冲突。");
                }
                catch (Exception ex) { ShowError(ex); SetBusy(false, "合并尚不能提交"); return; }
                SetBusy(false, "合并已解决，请确认整个工作区的提交范围。");
                await CheckinSelectionAsync(new List<string> { workspace.RootPath });
            }
            else await CheckinSelectionAsync(null);
        }

        private async Task CheckinSelectionAsync(List<string> explicitPaths)
        {
            if (busy || !loaded || submissionNeedsRefresh) return;
            var selectedRows = files.CheckedItems.Cast<ListViewItem>().Select(row => (PlasticStatusItem)row.Tag).ToArray();
            // Directory selection also checks private descendants for Add/Undo workflows.
            // Checkin has no --private: omit those covered descendants from its explicit
            // path list and let the review report them as excluded. Standalone private
            // selections still require an explicit Add operation.
            var coveredPrivate = new HashSet<string>(selectedRows.Where(item => IsPrivate(item.StatusCode) &&
                selectedRows.Any(parent => parent.IsDirectory && !IsPrivate(parent.StatusCode) &&
                    item.Path.StartsWith(parent.Path.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
            var privatePaths = explicitPaths == null
                ? CollapseCheckinPaths(selectedRows.Where(item => IsPrivate(item.StatusCode) && !coveredPrivate.Contains(item.Path)))
                : new List<string>();
            var checkinItems = explicitPaths == null
                ? selectedRows.Where(item => !coveredPrivate.Contains(item.Path))
                : explicitPaths.Select(path => new PlasticStatusItem { Path = path, IsDirectory = Directory.Exists(path) });
            var paths = CollapseCheckinPaths(checkinItems).ToArray();
            if (paths.Length == 0) { reportError("请先勾选要提交的项。"); return; }
            string message = comment.Text;
            string messageRepository = workspace.Repository;
            if (String.IsNullOrWhiteSpace(message)) { reportError("请填写签入说明。"); comment.Focus(); return; }
            bool dispatched = false, succeeded = false;
            string historyWarning = null;
            SetBusy(true, "正在准备提交范围与内容预览…");
            try
            {
                ValidatePendingContext();
                if (privatePaths.Count > 0)
                {
                    status.Text = "正在添加私有文件，准备同时提交…";
                    var add = await runCommand(new PlasticCommandRequest {
                        Command = PlasticCommand.Add, WorkingDirectory = workspace.RootPath,
                        Paths = privatePaths, Recursive = true }, CancellationToken.None);
                    AppendOutput("[Add private] " + String.Join("\r\n", privatePaths.ToArray()));
                    AppendOutput(add.Output); AppendOutput(add.Error);
                    if (!add.Succeeded) throw new InvalidOperationException("私有文件添加失败，未执行提交。\r\n" + add.Error);
                }
                var preview = await prepareCheckin(workspace.RootPath, paths, workspace.Repository, workspace.Selector, CancellationToken.None);
                if (reviewCheckin(preview, message, submissionUncertain) != DialogResult.OK) return;
                ValidatePendingContext();
                status.Text = "正在签入…";
                AppendOutput("\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "] 签入\r\n" + String.Join("\r\n", preview.Paths));
                dispatched = true;
                var result = await submitCheckin(preview, message, CancellationToken.None);
                AppendOutput(result.Output); AppendOutput(result.Error);
                AppendOutput(result.TimedOut ? "签入超时，服务器可能已经接受提交。" : "退出码：" + result.ExitCode);
                succeeded = result.Succeeded;
                if (!succeeded) throw new InvalidOperationException("签入未确认。说明和勾选已保留；请先刷新状态并查看历史核对服务器结果。\r\n" + result.Error);
                comment.Clear(); submissionUncertain = submissionNeedsRefresh = false;
                submissionNotice = "签入成功。";
                // Local history is optional: its failure must never turn an accepted
                // server check-in into an uncertain write or invite a duplicate retry.
                try { messageStore.RecordSuccess(messageRepository, message); }
                catch (Exception ex) {
                    historyWarning = "签入成功，但本机说明历史未保存；无需重新提交。";
                    AppendOutput(historyWarning + " " + ex.Message);
                }
                foreach (string path in paths) SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                submissionNeedsRefresh = true;
                submissionUncertain = submissionUncertain || dispatched;
                submissionNotice = dispatched ? "签入未确认；说明和勾选已保留，请刷新并查看历史。" : "提交预检未通过；请刷新后重新核对。";
                AppendOutput(submissionNotice); AppendOutput(ex.Message); reportError(ex.Message);
            }
            finally { SetBusy(false, submissionNotice); UpdateSelectionCount(); }
            // A failed/uncertain write never refreshes away the user's reviewed selection.
            if (succeeded) {
                bool refreshed = await RefreshAsync();
                if (historyWarning != null) {
                    submissionNotice = historyWarning;
                    if (refreshed) UpdateSelectionCount(); else status.Text += " " + historyWarning;
                }
            }
        }

        private async Task ExecuteAsync(PlasticCommand command, List<string> explicitPaths)
        {
            if (busy || !loaded) return;
            if (command == PlasticCommand.Checkin) { await CheckinSelectionAsync(explicitPaths); return; }
            if (command == PlasticCommand.Update)
            {
                var updatePaths = explicitPaths ?? (workspace.IsPartial ? SelectedPaths(false, true) : new List<string>(launch.Paths));
                if (updatePaths.Count == 0) { MessageBox.Show(this, "请先勾选要操作的项。", "TortoiseSCM"); return; }
                using (var update = new UpdateForm(client, updatePaths)) update.ShowDialog(this);
                await RefreshAsync();
                return;
            }
            bool read = command == PlasticCommand.History || command == PlasticCommand.Diff || command == PlasticCommand.Gluon;
            var paths = explicitPaths ?? SelectedPaths(read, command != PlasticCommand.Checkin && command != PlasticCommand.Undo);
            if (command == PlasticCommand.Gluon) paths = new List<string> { workspace.RootPath };
            if (paths.Count == 0) { MessageBox.Show(this, "请先勾选要操作的项。", "TortoiseSCM"); return; }
            if ((command == PlasticCommand.History || command == PlasticCommand.Diff) && paths.Count != 1)
            { MessageBox.Show(this, "请仅选择一个文件或目录。", "TortoiseSCM"); return; }
            if (command == PlasticCommand.History)
            {
                using (var history = new HistoryForm(client, paths[0], workspace.RootPath)) history.ShowDialog(this);
                await RefreshAsync();
                return;
            }
            if (!read)
            {
                string note = command == PlasticCommand.Undo ? "所选项的本地更改将丢失。\r\n" : "";
                if (paths.Any(Directory.Exists) || files.CheckedItems.Cast<ListViewItem>().Any(i => ((PlasticStatusItem)i.Tag).IsDirectory))
                    note += "目录操作会包含其全部子项，包括没有单独勾选的子项。\r\n";
                if (MessageBox.Show(this, note + "\r\n" + string.Join("\r\n", paths.Take(12).ToArray()) +
                    (paths.Count > 12 ? "\r\n… 共 " + paths.Count + " 项" : "") + "\r\n\r\n继续" + CommandName(command) + "？",
                    "TortoiseSCM — " + CommandName(command), MessageBoxButtons.OKCancel,
                    command == PlasticCommand.Undo ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            }
            SetBusy(true, "正在" + CommandName(command) + "…");
            bool success = false;
            try
            {
                AppendOutput("\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "] " + CommandName(command) + "\r\n" + string.Join("\r\n", paths.ToArray()));
                var request = new PlasticCommandRequest { Command = command, WorkingDirectory = workspace.RootPath, Paths = paths,
                    Recursive = true };
                var result = command == PlasticCommand.Diff ? await client.OpenDiffToolAsync(paths[0], CancellationToken.None) :
                    await client.RunAsync(request, CancellationToken.None);
                success = result.Succeeded;
                AppendOutput(result.Output);
                AppendOutput(result.Error);
                AppendOutput(result.TimedOut ? "操作超时；请刷新核对当前工作区状态。" : "退出码：" + result.ExitCode);
                if (!result.Succeeded)
                    MessageBox.Show(this, "操作未成功。请从“操作”菜单打开“操作记录”查看详细信息。", "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                if (!read)
                    foreach (string path in paths) SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { SetBusy(false, success ? "操作完成" : "操作未成功"); }
            if (!read) await RefreshAsync();
        }

        private static bool IsPrivate(string state)
        { return state.IndexOf("private", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("ignored", StringComparison.OrdinalIgnoreCase) >= 0 || state == "PR" || state == "IG"; }

        private static List<string> CollapseCheckinPaths(IEnumerable<PlasticStatusItem> items)
        {
            var ordered = items.Where(item => item != null && !String.IsNullOrWhiteSpace(item.Path))
                .GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .OrderBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase);
            var selected = new List<PlasticStatusItem>();
            foreach (var item in ordered)
            {
                if (selected.Any(parent => parent.IsDirectory && IsDescendantPath(item.Path, parent.Path))) continue;
                selected.Add(item);
            }
            return selected.Select(item => item.Path).ToList();
        }

        private static bool IsDescendantPath(string path, string parent)
        {
            return String.Equals(path, parent, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(parent.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private static PlasticCommand ParseCommand(string name)
        { return (PlasticCommand)Enum.Parse(typeof(PlasticCommand), name, true); }

        private static string CommandName(PlasticCommand command)
        {
            switch (command)
            {
                case PlasticCommand.Checkin: return "签入";
                case PlasticCommand.Update: return "更新";
                case PlasticCommand.Add: return "添加";
                case PlasticCommand.Checkout: return "签出";
                case PlasticCommand.Undo: return "撤销更改";
                case PlasticCommand.Diff: return "查看差异";
                case PlasticCommand.History: return "查看历史";
                case PlasticCommand.Gluon: return "打开 Gluon";
                default: return "刷新";
            }
        }

        private void SetBusy(bool value, string text)
        {
            busy = value;
            actions.Enabled = !value;
            checkin.Enabled = !value && loaded && !submissionNeedsRefresh;
            files.Enabled = !value;
            comment.Enabled = !value;
            messageLibrary.Enabled = !value && loaded;
            selectAll.Enabled = selectNone.Enabled = !value;
            progress.Visible = value;
            status.Text = text;
        }

        private void UpdateSelectionCount()
        { if (!busy) status.Text = (submissionNotice.Length == 0 ? "" : submissionNotice + "  ") + files.Items.Count + " 个待定更改 · 已勾选 " + files.CheckedItems.Count + " 项"; }

        private void AppendOutput(string text)
        { if (!string.IsNullOrEmpty(text)) output.AppendText(text.TrimEnd() + Environment.NewLine); }

        private void ShowError(Exception ex)
        {
            AppendOutput(ex.Message);
            status.Text = "操作未成功";
            MessageBox.Show(this, ex.Message, "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
    }
}
