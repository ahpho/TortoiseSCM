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
    // A small confirmation surface for one native file operation. Keeping this
    // separate from MainForm prevents checkout/add/undo from opening the
    // check-in editor and makes the command's destructive boundary explicit.
    internal sealed class OperationForm : Form
    {
        private readonly PlasticClient client;
        private readonly string commandName;
        private readonly List<string> selectedPaths;
        private readonly Label scope = new Label();
        private readonly TextBox paths = new TextBox();
        private readonly TextBox output = new TextBox();
        private readonly Label status = new Label();
        private readonly Button execute = DialogStyle.Button("执行");
        private readonly Button refresh = DialogStyle.Button("刷新范围");
        private readonly Button close = DialogStyle.Button("关闭");
        private bool busy;
        private bool ready;

        internal OperationForm(PlasticClient client, string command, IList<string> paths)
        {
            this.client = client; commandName = command; selectedPaths = new List<string>(paths);
            Text = Title(command); DialogStyle.Apply(this); Size = new Size(720, 500); MinimumSize = new Size(580, 380);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            scope.Dock = DockStyle.Fill; scope.UseMnemonic = false; layout.Controls.Add(scope, 0, 0);
            Configure(this.paths, "操作范围"); layout.Controls.Add(this.paths, 0, 1);
            Configure(output, "操作记录"); layout.Controls.Add(output, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            execute.Text = Title(command, true); buttons.Controls.Add(close); buttons.Controls.Add(execute); buttons.Controls.Add(refresh);
            layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
            execute.Click += async delegate { await ExecuteAsync(); }; refresh.Click += async delegate { await LoadScopeAsync(); };
            close.Click += delegate { Close(); }; CancelButton = close; AcceptButton = close;
            Shown += async delegate { await LoadScopeAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        }

        private static void Configure(TextBox box, string name)
        { box.Dock = DockStyle.Fill; box.Multiline = true; box.ReadOnly = true; box.ScrollBars = ScrollBars.Both; box.WordWrap = false; box.BackColor = SystemColors.Window; box.AccessibleName = name; }
        private static string Title(string command, bool action = false)
        {
            string name = command == "add" ? "添加" : command == "checkout" ? "签出" : "撤销更改";
            return (action ? name : name) + " - TortoiseSCM";
        }
        private async Task LoadScopeAsync()
        {
            if (busy) return; busy = true; ready = false; SetButtons(); status.Text = "正在识别工作区…"; paths.Clear();
            try
            {
                var workspace = await client.GetWorkspaceAsync(selectedPaths[0], CancellationToken.None);
                if (workspace == null) throw new InvalidOperationException("所选路径不属于 Plastic SCM 工作区。");
                foreach (string path in selectedPaths)
                    if (client.DiscoverWorkspace(path) == null || !String.Equals(client.DiscoverWorkspace(path).RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("请一次只选择同一工作区内的路径。");
                scope.Text = "工作区：" + workspace.RootPath + "\r\n范围：" + selectedPaths.Count + " 个所选项；目录操作包含其子项。";
                paths.Lines = selectedPaths.ToArray(); ready = true; status.Text = "请核对范围后点击“" + CommandLabel(commandName) + "”。";
            }
            catch (Exception ex) { output.AppendText(ex.Message + Environment.NewLine); status.Text = "无法识别操作范围。"; }
            finally { busy = false; SetButtons(); }
        }
        private async Task ExecuteAsync()
        {
            if (busy || !ready) return;
            string label = CommandLabel(commandName);
            string warning = commandName == "undo" ? "所选项的本地更改将丢失。\r\n" : "";
            if (selectedPaths.Any(Directory.Exists))
                warning += "目录操作会包含其全部子项，包括没有单独勾选的子项。\r\n";
            if (MessageBox.Show(this, warning + String.Join("\r\n", selectedPaths.Take(12).ToArray()) +
                (selectedPaths.Count > 12 ? "\r\n… 共 " + selectedPaths.Count + " 项" : "") + "\r\n\r\n继续" + label + "？",
                "TortoiseSCM — " + label, MessageBoxButtons.OKCancel,
                commandName == "undo" ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            busy = true; ready = false; SetButtons(); status.Text = "正在" + label + "…";
            try
            {
                var request = new PlasticCommandRequest { Command = Parse(commandName), WorkingDirectory = client.DiscoverWorkspace(selectedPaths[0]).RootPath,
                    Paths = new List<string>(selectedPaths), Recursive = true };
                output.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + label + Environment.NewLine + String.Join(Environment.NewLine, selectedPaths.ToArray()) + Environment.NewLine);
                var result = await client.RunAsync(request, CancellationToken.None);
                output.AppendText(result.Output + Environment.NewLine + result.Error + Environment.NewLine + "退出码：" + result.ExitCode + Environment.NewLine);
                status.Text = result.Succeeded ? label + "完成。" : label + "未成功，请刷新范围核对。";
                if (!result.Succeeded) MessageBox.Show(this, "操作未成功，请查看操作记录。", "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch (Exception ex) { output.AppendText(ex.Message + Environment.NewLine); status.Text = label + "未完成。"; }
            finally { busy = false; SetButtons(); }
        }
        private void SetButtons() { execute.Enabled = !busy && ready; refresh.Enabled = close.Enabled = !busy; }
        private static PlasticCommand Parse(string command) { return (PlasticCommand)Enum.Parse(typeof(PlasticCommand), command, true); }
        private static string CommandLabel(string command) { return command == "add" ? "添加" : command == "checkout" ? "签出" : "撤销更改"; }
    }
}
