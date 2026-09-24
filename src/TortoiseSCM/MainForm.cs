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
        private readonly ListView files = new ListView();
        private readonly TextBox comment = new TextBox();
        private readonly TextBox output = new TextBox();
        private readonly Button actions = new Button();
        private readonly Label status = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Label scope = new Label();
        private readonly Button checkin = new Button();
        private readonly LinkLabel selectAll = new LinkLabel();
        private readonly LinkLabel selectNone = new LinkLabel();
        private bool busy;
        private bool loaded;
        private bool changingChecks;

        public MainForm(LaunchRequest request)
        {
            launch = request;
            Text = "TortoiseSCM — 待定更改";
            DialogStyle.Apply(this);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 580);
            Size = new Size(930, 720);
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildLayout();
            Shown += async delegate { await InitializeAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy)
                {
                    e.Cancel = true;
                    MessageBox.Show(this, "操作正在进行，请等待完成后关闭窗口。", "TortoiseSCM");
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
            messageGroup.Controls.Add(comment);
            split.Panel1.Controls.Add(messageGroup);

            var changesGroup = new GroupBox { Text = "更改的文件", Dock = DockStyle.Fill, Padding = new Padding(8, 6, 8, 8) };
            var changes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
            changes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            changes.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            changes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var selectionBar = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
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
            menu.Items.Add("显示历史 / 恢复版本", null, async delegate { await ExecuteAsync(PlasticCommand.History, HighlightedPaths()); });
            menu.Items.Add("查看差异", null, async delegate { await ExecuteAsync(PlasticCommand.Diff, HighlightedPaths()); });
            menu.Items.Add("丢弃所选行的修改…", null, async delegate { await ExecuteAsync(PlasticCommand.Undo, HighlightedPaths()); });
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
            operations.Items.Add("查看差异", null, async delegate { await ExecuteAsync(PlasticCommand.Diff); });
            operations.Items.Add("所选项历史", null, async delegate { await ExecuteAsync(PlasticCommand.History); });
            operations.Items.Add("当前范围历史 / 恢复", null, async delegate { await ShowScopeHistoryAsync(); });
            operations.Items.Add("合并变更集 / 解决冲突…", null, async delegate {
                if (busy || !loaded) return;
                using (var dialog = new MergeForm(client, workspace.RootPath)) dialog.ShowDialog(this);
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
            operations.Items.Add("设置…", null, delegate { using (var settings = new SettingsForm()) settings.ShowDialog(this); client = new PlasticClient(PlasticClientConfig.Load()); });
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
            checkin.Click += async delegate { await ExecuteAsync(PlasticCommand.Checkin); };
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
                client = new PlasticClient(PlasticClientConfig.Load());
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
                if (launch.Command == "history") await ExecuteAsync(PlasticCommand.History);
                else if (launch.Command == "diff") await ExecuteAsync(PlasticCommand.Diff);
                else if (launch.Command == "gluon") await ExecuteAsync(PlasticCommand.Gluon);
                else if (launch.Command == "checkin") comment.Focus();
                else if (launch.Command != "status")
                    status.Text = "请核对选择范围后点击“" + CommandName(ParseCommand(launch.Command)) + "”执行。";
            }
            catch (Exception ex) { SetBusy(false, "操作未成功"); ShowError(ex); }
        }

        private bool InScope(string path)
        {
            return launch.Paths.Any(p => string.Equals(path, p, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(p.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase));
        }

        private async Task RefreshAsync()
        {
            if (busy || !loaded) return;
            SetBusy(true, "正在读取工作区状态…");
            try
            {
                var checkedPaths = new HashSet<string>(files.CheckedItems.Cast<ListViewItem>().Select(i => ((PlasticStatusItem)i.Tag).Path), StringComparer.OrdinalIgnoreCase);
                var items = await client.GetStatusAsync(workspace.RootPath, CancellationToken.None);
                files.BeginUpdate();
                try
                {
                    files.Items.Clear();
                    foreach (var item in items.Where(i => InScope(i.Path)))
                    {
                        string relative = item.Path.StartsWith(workspace.RootPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                            ? item.Path.Substring(workspace.RootPath.TrimEnd('\\').Length + 1) : item.Path;
                        if (!string.IsNullOrEmpty(item.OldPath)) relative = item.OldPath + " → " + relative;
                        var row = new ListViewItem(relative) { Tag = item, Checked = checkedPaths.Contains(item.Path) };
                        row.SubItems.Add(item.IsDirectory ? "" : Path.GetExtension(item.Path));
                        row.SubItems.Add(item.StatusDescription);
                        files.Items.Add(row);
                    }
                }
                finally { files.EndUpdate(); }
            }
            catch (Exception ex) { ShowError(ex); }
            finally { SetBusy(false, ""); UpdateSelectionCount(); }
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

        private Task ExecuteAsync(PlasticCommand command) { return ExecuteAsync(command, null); }

        private async Task ExecuteAsync(PlasticCommand command, List<string> explicitPaths)
        {
            if (busy || !loaded) return;
            bool read = command == PlasticCommand.History || command == PlasticCommand.Diff || command == PlasticCommand.Gluon;
            var paths = explicitPaths ?? SelectedPaths(read, command != PlasticCommand.Checkin && command != PlasticCommand.Undo);
            if (command == PlasticCommand.Gluon) paths = new List<string> { workspace.RootPath };
            if (command == PlasticCommand.Update && !workspace.IsPartial) paths = new List<string> { workspace.RootPath };
            if (paths.Count == 0) { MessageBox.Show(this, "请先勾选要操作的项。", "TortoiseSCM"); return; }
            if ((command == PlasticCommand.History || command == PlasticCommand.Diff) && paths.Count != 1)
            { MessageBox.Show(this, "请仅选择一个文件或目录。", "TortoiseSCM"); return; }
            if (command == PlasticCommand.History)
            {
                using (var history = new HistoryForm(client, paths[0], workspace.RootPath)) history.ShowDialog(this);
                await RefreshAsync();
                return;
            }
            if (command == PlasticCommand.Checkin && string.IsNullOrWhiteSpace(comment.Text))
            { MessageBox.Show(this, "请填写签入说明。", "TortoiseSCM"); comment.Focus(); return; }
            if (command == PlasticCommand.Checkin && files.CheckedItems.Cast<ListViewItem>().Any(i => IsPrivate(((PlasticStatusItem)i.Tag).StatusCode)))
            { MessageBox.Show(this, "请先使用“添加”将私有项加入版本控制，再签入。", "TortoiseSCM"); return; }
            if (!read)
            {
                string note = command == PlasticCommand.Undo ? "所选项的本地更改将丢失。\r\n" : "";
                if (command == PlasticCommand.Update && !workspace.IsPartial)
                    note += "完整工作区需整体更新：这将更新整个工作区中的受控文件。\r\n";
                if (paths.Any(Directory.Exists) || files.CheckedItems.Cast<ListViewItem>().Any(i => ((PlasticStatusItem)i.Tag).IsDirectory))
                    note += "目录操作会包含其全部子项，包括没有单独勾选的子项。\r\n";
                if (command == PlasticCommand.Checkin) note += "更改将提交到当前 Plastic 服务器。\r\n";
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
                    Comment = command == PlasticCommand.Checkin ? comment.Text : null, Recursive = true };
                var result = command == PlasticCommand.Diff ? await client.OpenDiffToolAsync(paths[0], CancellationToken.None) :
                    await client.RunAsync(request, CancellationToken.None);
                success = result.Succeeded;
                AppendOutput(result.Output);
                AppendOutput(result.Error);
                AppendOutput(result.TimedOut ? "操作超时；请刷新核对当前工作区状态。" : "退出码：" + result.ExitCode);
                if (!result.Succeeded)
                    MessageBox.Show(this, "操作未成功。请从“操作”菜单打开“操作记录”查看详细信息。", "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                else if (command == PlasticCommand.Checkin) comment.Clear();
                if (!read)
                    foreach (string path in paths) SHChangeNotify(0x00002000, 0x0005, path, IntPtr.Zero);
            }
            catch (Exception ex) { ShowError(ex); }
            finally { SetBusy(false, success ? "操作完成" : "操作未成功"); }
            if (!read) await RefreshAsync();
        }

        private static bool IsPrivate(string state)
        { return state.IndexOf("private", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("ignored", StringComparison.OrdinalIgnoreCase) >= 0 || state == "PR" || state == "IG"; }

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
            checkin.Enabled = !value && loaded;
            files.Enabled = !value;
            comment.Enabled = !value;
            selectAll.Enabled = selectNone.Enabled = !value;
            progress.Visible = value;
            status.Text = text;
        }

        private void UpdateSelectionCount()
        { if (!busy) status.Text = files.Items.Count + " 个待定更改 · 已勾选 " + files.CheckedItems.Count + " 项"; }

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
