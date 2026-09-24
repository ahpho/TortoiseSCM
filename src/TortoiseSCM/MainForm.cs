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
        private readonly ToolStrip toolbar = new ToolStrip();
        private readonly ToolStripStatusLabel status = new ToolStripStatusLabel();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly Label scope = new Label();
        private readonly TabControl tabs = new TabControl();
        private readonly Button checkin = new Button();
        private readonly Button selectAll = new Button();
        private readonly Button selectNone = new Button();
        private bool busy;
        private bool loaded;
        private bool changingChecks;

        public MainForm(LaunchRequest request)
        {
            launch = request;
            Text = "TortoiseSCM — 待定更改";
            Font = new Font("Microsoft YaHei UI", 9F);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(820, 590);
            Size = new Size(1080, 760);
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
            AddTool("刷新", async delegate { await RefreshAsync(); });
            AddTool("更新", async delegate { await ExecuteAsync(PlasticCommand.Update); });
            toolbar.Items.Add(new ToolStripSeparator());
            AddTool("添加", async delegate { await ExecuteAsync(PlasticCommand.Add); });
            AddTool("签出", async delegate { await ExecuteAsync(PlasticCommand.Checkout); });
            AddTool("撤销更改", async delegate { await ExecuteAsync(PlasticCommand.Undo); });
            toolbar.Items.Add(new ToolStripSeparator());
            AddTool("差异", async delegate { await ExecuteAsync(PlasticCommand.Diff); });
            AddTool("历史", async delegate { await ExecuteAsync(PlasticCommand.History); });
            AddTool("范围历史 / 恢复", async delegate { await ShowScopeHistoryAsync(); });
            AddTool("打开 Gluon", async delegate { await ExecuteAsync(PlasticCommand.Gluon); });
            AddTool("设置", delegate { using (var settings = new SettingsForm()) settings.ShowDialog(this); client = new PlasticClient(PlasticClientConfig.Load()); });
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Padding = new Padding(6, 7, 6, 7);
            toolbar.Dock = DockStyle.Top;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 106));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            scope.Dock = DockStyle.Fill;
            scope.AutoEllipsis = true;
            scope.Text = "正在识别工作区…";
            layout.Controls.Add(scope, 0, 0);

            tabs.Dock = DockStyle.Fill;
            var changesTab = new TabPage("待定更改");
            var outputTab = new TabPage("操作记录 / 历史");
            files.Dock = DockStyle.Fill;
            files.View = View.Details;
            files.CheckBoxes = true;
            files.FullRowSelect = true;
            files.HideSelection = false;
            files.GridLines = true;
            files.Columns.Add("状态", 160);
            files.Columns.Add("路径", 700);
            files.AccessibleName = "待定更改列表";
            files.ItemChecked += OnItemChecked;
            files.DoubleClick += async delegate { await ExecuteAsync(PlasticCommand.Diff); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("显示历史 / 恢复版本", null, async delegate { await ExecuteAsync(PlasticCommand.History, HighlightedPaths()); });
            menu.Items.Add("查看差异", null, async delegate { await ExecuteAsync(PlasticCommand.Diff, HighlightedPaths()); });
            menu.Items.Add("丢弃所选行的修改…", null, async delegate { await ExecuteAsync(PlasticCommand.Undo, HighlightedPaths()); });
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = busy || files.SelectedItems.Count == 0; };
            files.ContextMenuStrip = menu;
            changesTab.Controls.Add(files);
            output.Multiline = true;
            output.ReadOnly = true;
            output.WordWrap = false;
            output.ScrollBars = ScrollBars.Both;
            output.Dock = DockStyle.Fill;
            output.Font = new Font("Consolas", 10F);
            output.AccessibleName = "操作输出";
            outputTab.Controls.Add(output);
            tabs.TabPages.Add(changesTab);
            tabs.TabPages.Add(outputTab);
            layout.Controls.Add(tabs, 0, 1);

            var selectionBar = new FlowLayoutPanel { Dock = DockStyle.Fill };
            selectAll.Text = "全选";
            selectNone.Text = "全不选";
            selectAll.Click += delegate { foreach (ListViewItem item in files.Items) item.Checked = true; };
            selectNone.Click += delegate { foreach (ListViewItem item in files.Items) item.Checked = false; };
            selectionBar.Controls.Add(selectAll);
            selectionBar.Controls.Add(selectNone);
            selectionBar.Controls.Add(new Label { Text = "签入与撤销仅应用于勾选项；双击单个文件查看差异。", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
            layout.Controls.Add(selectionBar, 0, 2);

            var commentPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            commentPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            commentPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            commentPanel.Controls.Add(new Label { Text = "签入说明", Dock = DockStyle.Fill }, 0, 0);
            comment.Multiline = true;
            comment.ScrollBars = ScrollBars.Vertical;
            comment.Dock = DockStyle.Fill;
            comment.AccessibleName = "签入说明";
            commentPanel.Controls.Add(comment, 0, 1);
            layout.Controls.Add(commentPanel, 0, 3);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2 };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
            progress.Dock = DockStyle.Fill;
            progress.Style = ProgressBarStyle.Marquee;
            progress.Visible = false;
            progress.Margin = new Padding(0, 12, 16, 12);
            checkin.Text = "签入所选项…";
            checkin.Dock = DockStyle.Fill;
            checkin.Click += async delegate { await ExecuteAsync(PlasticCommand.Checkin); };
            footer.Controls.Add(progress, 0, 0);
            footer.Controls.Add(checkin, 1, 0);
            layout.Controls.Add(footer, 0, 4);
            var statusBar = new StatusStrip();
            status.Spring = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            statusBar.Items.Add(status);
            Controls.Add(layout);
            Controls.Add(toolbar);
            Controls.Add(statusBar);
        }

        private void AddTool(string text, EventHandler action)
        {
            var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(4) };
            button.Click += action;
            toolbar.Items.Add(button);
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
                Text = "TortoiseSCM — " + Path.GetFileName(workspace.RootPath);
                scope.Text = "工作区：" + workspace.RootPath + (workspace.IsPartial ? "  ·  Gluon / 部分工作区" : "  ·  完整工作区") +
                    "\r\n范围：" + string.Join("；", launch.Paths.ToArray());
                loaded = true;
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
                        var row = new ListViewItem(item.Status + "  " + item.StatusDescription) { Tag = item, Checked = checkedPaths.Contains(item.Path) };
                        row.SubItems.Add(relative);
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
                tabs.SelectedIndex = 1;
                if (!result.Succeeded)
                    MessageBox.Show(this, "操作未成功。请查看“操作记录 / 历史”中的详细信息。", "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
            toolbar.Enabled = !value;
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
            tabs.SelectedIndex = 1;
            status.Text = "操作未成功";
            MessageBox.Show(this, ex.Message, "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
    }
}
