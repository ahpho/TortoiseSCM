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
    // File operations use a Tortoise-style review list. The selected shell
    // paths describe the scope at the top; the list contains the actual items
    // that will be passed to cm.exe, so an Add dialog is useful even when a
    // directory was selected in Explorer.
    internal sealed class OperationForm : Form
    {
        private readonly PlasticClient client;
        private readonly string commandName;
        private readonly List<string> selectedPaths;
        private readonly Label scope = new Label();
        private readonly ListView files = new ListView();
        private readonly TextBox output = new TextBox();
        private readonly Label status = new Label();
        private readonly Button execute = DialogStyle.Button("执行");
        private readonly Button refresh = DialogStyle.Button("刷新范围");
        private readonly Button close = DialogStyle.Button("关闭");
        private bool busy;
        private bool ready;
        private bool changingChecks;
        private bool checkoutCancellation;

        internal OperationForm(PlasticClient client, string command, IList<string> paths)
        {
            this.client = client; commandName = command; selectedPaths = new List<string>(paths ?? new string[0]);
            Text = Title(command); DialogStyle.Apply(this); Size = new Size(780, 540); MinimumSize = new Size(620, 430);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            scope.Dock = DockStyle.Fill; scope.UseMnemonic = false; scope.AutoEllipsis = true; layout.Controls.Add(scope, 0, 0);
            files.Dock = DockStyle.Fill; files.View = View.Details; files.CheckBoxes = true; files.FullRowSelect = true;
            files.MultiSelect = true; files.HideSelection = false; files.GridLines = true; files.AllowColumnReorder = false;
            DialogStyle.ApplyList(files); files.AccessibleName = "可操作文件列表";
            files.Columns.Add("路径", 570); files.Columns.Add("状态", 150);
            files.ItemChecked += OnItemChecked;
            layout.Controls.Add(files, 0, 1);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            execute.Text = CommandLabel(command); buttons.Controls.Add(close); buttons.Controls.Add(execute); buttons.Controls.Add(refresh);
            layout.Controls.Add(buttons, 0, 3); Controls.Add(layout);
            execute.Click += async delegate { await ExecuteAsync(); }; refresh.Click += async delegate { await LoadScopeAsync(); };
            close.Click += delegate { Close(); }; CancelButton = close; AcceptButton = close;
            Shown += async delegate { await LoadScopeAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        }

        private static string Title(string command) { return CommandLabel(command) + " - TortoiseSCM"; }

        private async Task LoadScopeAsync()
        {
            if (busy) return;
            checkoutCancellation = false;
            busy = true; ready = false; SetButtons(); files.Items.Clear(); status.Text = "正在读取可操作文件…";
            try
            {
                if (selectedPaths.Count == 0) throw new ArgumentException("请选择至少一个文件或目录。");
                var workspace = await client.GetWorkspaceAsync(selectedPaths[0], CancellationToken.None);
                if (workspace == null) throw new InvalidOperationException("所选路径不属于 Plastic SCM 工作区。");
                foreach (string path in selectedPaths)
                {
                    var other = client.DiscoverWorkspace(path);
                    if (other == null || !String.Equals(other.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("请一次只选择同一工作区内的文件。");
                }
                scope.Text = "工作区：" + workspace.RootPath + "\r\n范围：" +
                    String.Join("；", selectedPaths.ToArray()) + "\r\n" +
                    (selectedPaths.Any(Directory.Exists) ? "目录操作会包含全部子项。" : "");

                var all = await client.GetStatusAsync(workspace.RootPath, CancellationToken.None);
                var candidates = BuildCandidates(all);
                if (IsCheckout()) candidates = SelectedFallback(all, "受控路径");
                else if (candidates.Count == 0 && commandName == "undo") candidates = SelectedFallback(all, "所选路径");
                checkoutCancellation = IsCheckout() && candidates.Count > 0 && candidates.All(item => item.StatusCode == "CO");
                execute.Text = EffectiveLabel();
                Text = EffectiveLabel() + " - TortoiseSCM";
                foreach (var item in candidates.OrderBy(item => item, new PlasticStatusPathComparer()))
                {
                    string relative = item.Path.StartsWith(workspace.RootPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                        ? item.Path.Substring(workspace.RootPath.TrimEnd('\\').Length + 1) : item.Path;
                    var row = new ListViewItem(relative) { Tag = item, Checked = commandName == "add" || !IsPrivate(item.StatusCode) };
                    row.SubItems.Add(String.IsNullOrWhiteSpace(item.StatusDescription) ? item.StatusCode : item.StatusDescription);
                    files.Items.Add(row);
                }
                ready = files.Items.Count != 0;
                status.Text = ready ? "请核对列表后点击“" + EffectiveLabel() + "”。" :
                    (commandName == "add" ? "当前范围没有可添加的文件。" : "当前范围没有可操作的更改。");
            }
            catch (Exception ex) { output.AppendText(ex.Message + Environment.NewLine); status.Text = "无法读取操作范围，请刷新后重试。"; }
            finally { busy = false; SetButtons(); }
        }

        private List<PlasticStatusItem> BuildCandidates(IList<PlasticStatusItem> all)
        {
            IEnumerable<PlasticStatusItem> query = all.Where(item => selectedPaths.Any(path => InScope(item.Path, path)));
            if (commandName == "add") query = query.Where(item => item.StatusCode == "PR");
            else if (commandName == "undo") query = query.Where(item => !IsPrivate(item.StatusCode));
            return query.ToList();
        }

        private List<PlasticStatusItem> SelectedFallback(IList<PlasticStatusItem> all, string description)
        {
            return selectedPaths.Select(path => all.FirstOrDefault(item => String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)) ??
                new PlasticStatusItem { Path = path, StatusCode = "", StatusDescription = description, IsDirectory = Directory.Exists(path) }).ToList();
        }

        private static bool InScope(string value, string scope)
        {
            return String.Equals(value, scope, StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(scope.TrimEnd('\\', '/') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        private async Task ExecuteAsync()
        {
            if (busy || !ready) return;
            var paths = files.CheckedItems.Cast<ListViewItem>().Select(item => ((PlasticStatusItem)item.Tag).Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (paths.Count == 0) { status.Text = "请先勾选要操作的文件。"; return; }
            string label = EffectiveLabel();
            bool undo = commandName == "undo" || checkoutCancellation;
            string warning = undo ? "所选项的本地更改将丢失。\r\n" : "";
            if (paths.Any(Directory.Exists)) warning += "目录操作会包含其全部子项，包括没有单独勾选的子项。\r\n";
            if (MessageBox.Show(this, warning + String.Join("\r\n", paths.Take(12).ToArray()) +
                (paths.Count > 12 ? "\r\n… 共 " + paths.Count + " 项" : "") + "\r\n\r\n继续" + label + "？",
                "TortoiseSCM — " + label, MessageBoxButtons.OKCancel,
                undo ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            busy = true; ready = false; SetButtons(); status.Text = "正在" + label + "…";
            try
            {
                var workspace = client.DiscoverWorkspace(paths[0]);
                if (workspace == null) throw new InvalidOperationException("工作区已改变，请刷新范围。");
                var request = new PlasticCommandRequest { Command = Parse(commandName, checkoutCancellation), WorkingDirectory = workspace.RootPath,
                    Paths = paths, Recursive = commandName == "add" || commandName == "checkout-recursive" || undo };
                output.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + label + Environment.NewLine + String.Join(Environment.NewLine, paths.ToArray()) + Environment.NewLine);
                var result = await client.RunAsync(request, CancellationToken.None);
                output.AppendText(result.Output + Environment.NewLine + result.Error + Environment.NewLine + "退出码：" + result.ExitCode + Environment.NewLine);
                status.Text = result.Succeeded ? label + "完成。" : label + "未成功，请刷新范围核对。";
                if (!result.Succeeded) MessageBox.Show(this, "操作未成功，请查看操作记录。", "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex) { output.AppendText(ex.Message + Environment.NewLine); status.Text = label + "未完成。"; }
            finally { busy = false; SetButtons(); }
        }

        private void SetButtons() { execute.Enabled = !busy && ready; refresh.Enabled = close.Enabled = !busy; }

        private void OnItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (changingChecks || e.Item.Tag == null) return;
            var changed = e.Item.Tag as PlasticStatusItem;
            if (changed == null || !changed.IsDirectory) return;

            string descendantPrefix = changed.Path.TrimEnd('\\', '/') + "\\";
            changingChecks = true;
            try
            {
                foreach (ListViewItem row in files.Items)
                {
                    if (row == e.Item || row.Tag == null) continue;
                    var item = row.Tag as PlasticStatusItem;
                    if (item != null && item.Path.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase))
                        row.Checked = e.Item.Checked;
                }
            }
            finally { changingChecks = false; }
        }

        private bool IsCheckout() { return commandName == "checkout" || commandName == "checkout-recursive"; }
        private string EffectiveLabel() { return checkoutCancellation ? "撤销签出" : CommandLabel(commandName); }
        private static bool IsPrivate(string code) { return code == "PR" || code == "IG"; }
        private static PlasticCommand Parse(string command, bool checkoutCancellation)
        {
            return checkoutCancellation ? PlasticCommand.Undo : command == "checkout-recursive" ? PlasticCommand.Checkout :
                (PlasticCommand)Enum.Parse(typeof(PlasticCommand), command, true);
        }
        private static string CommandLabel(string command)
        {
            return command == "add" ? "添加" : command == "checkout" ? "签出" :
                command == "checkout-recursive" ? "递归签出" : "撤销更改";
        }

        private sealed class PlasticStatusPathComparer : IComparer<PlasticStatusItem>
        {
            public int Compare(PlasticStatusItem left, PlasticStatusItem right)
            {
                if (ReferenceEquals(left, right)) return 0;
                if (left == null) return -1;
                if (right == null) return 1;
                string[] leftParts = SplitPath(left.Path), rightParts = SplitPath(right.Path);
                int count = Math.Min(leftParts.Length, rightParts.Length);
                for (int index = 0; index < count; ++index)
                {
                    int result = StringComparer.OrdinalIgnoreCase.Compare(leftParts[index], rightParts[index]);
                    if (result != 0) return result;
                }
                return leftParts.Length.CompareTo(rightParts.Length);
            }

            private static string[] SplitPath(string path)
            { return (path ?? "").Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries); }
        }
    }
}
