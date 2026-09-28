// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class UpdateForm : Form
    {
        private readonly PlasticClient client;
        private readonly List<string> selectedPaths;
        private readonly Label scope = new Label();
        private readonly TextBox paths = new TextBox();
        private readonly TextBox output = new TextBox();
        private readonly Label status = new Label();
        private readonly Button update = DialogStyle.Button("更新(&U)");
        private readonly Button refresh = DialogStyle.Button("刷新范围(&R)");
        private readonly Button close = DialogStyle.Button("关闭");
        private Func<string, CancellationToken, Task<PlasticWorkspace>> getWorkspace;
        private Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>> run;
        private PlasticWorkspace workspace;
        private bool busy;
        private bool ready;

        internal UpdateForm(PlasticClient client, IList<string> selectedPaths)
        {
            this.client = client;
            this.selectedPaths = new List<string>(selectedPaths);
            getWorkspace = client.GetWorkspaceAsync;
            run = client.RunAsync;
            Text = "更新 - TortoiseSCM";
            DialogStyle.Apply(this);
            Size = new Size(800, 570);
            MinimumSize = new Size(640, 450);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            scope.Dock = DockStyle.Fill; scope.UseMnemonic = false;
            scope.Text = "正在识别更新范围…";
            layout.Controls.Add(scope, 0, 0);
            ConfigureText(paths, "更新范围"); layout.Controls.Add(paths, 0, 1);
            layout.Controls.Add(new Label { Text = "操作记录：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft }, 0, 2);
            ConfigureText(output, "更新操作记录"); layout.Controls.Add(output, 0, 3);
            status.Dock = DockStyle.Fill; status.UseMnemonic = false; status.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(status, 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Margin = Padding.Empty, WrapContents = false };
            buttons.Controls.Add(close); buttons.Controls.Add(update); buttons.Controls.Add(refresh);
            layout.Controls.Add(buttons, 0, 5); Controls.Add(layout);
            update.Enabled = false;
            update.Click += async delegate { await UpdateAsync(); };
            refresh.Click += async delegate { await LoadScopeAsync(); };
            close.Click += delegate { Close(); };
            CancelButton = close;
            // Enter is deliberately harmless until the user explicitly chooses Update.
            AcceptButton = close;
            Shown += async delegate { await LoadScopeAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
        }

        private static void ConfigureText(TextBox text, string name)
        {
            text.Dock = DockStyle.Fill; text.Multiline = true; text.ReadOnly = true;
            text.ScrollBars = ScrollBars.Both; text.WordWrap = false;
            text.BackColor = SystemColors.Window; text.AccessibleName = name;
        }

        private async Task LoadScopeAsync()
        {
            if (busy) return;
            ready = false; SetBusy(true); status.Text = "正在识别工作区…";
            scope.Text = "正在识别更新范围…"; paths.Clear();
            try
            {
                var local = Program.ValidateWorkspacePaths(client, selectedPaths);
                workspace = await getWorkspace(selectedPaths[0], CancellationToken.None);
                if (workspace == null) throw new InvalidOperationException("工作区不存在。");
                if (!SameIdentity(local, workspace)) throw new InvalidOperationException("工作区或分支已改变，请重新刷新范围。");
                scope.Text = "工作区：" + workspace.RootPath + "\r\n" + (workspace.IsPartial
                    ? "Gluon / 部分工作区：仅更新以下所选范围；目录包含全部子项。"
                    : "完整工作区：将整体更新工作区中的受控文件，包括所选路径以外的文件。" );
                paths.Lines = EffectivePaths().ToArray();
                ready = true; status.Text = "请核对范围，点击“更新”后才会执行。";
            }
            catch (Exception ex) { output.AppendText(ex.Message + Environment.NewLine); status.Text = "无法识别更新范围；请检查工作区后刷新范围。"; }
            finally { SetBusy(false); }
        }

        private List<string> EffectivePaths()
        { return workspace.IsPartial ? new List<string>(selectedPaths) : new List<string> { workspace.RootPath }; }

        private async Task UpdateAsync()
        {
            if (busy || !ready) return;
            ready = false; SetBusy(true); status.Text = "正在更新，请等待操作结束…";
            try
            {
                var local = Program.ValidateWorkspacePaths(client, selectedPaths);
                var current = await getWorkspace(selectedPaths[0], CancellationToken.None);
                if (current == null || !SameIdentity(local, workspace) || !SameIdentity(current, workspace) || current.IsPartial != workspace.IsPartial)
                    throw new InvalidOperationException("工作区或分支已改变。请刷新范围并重新核对后更新。");
                var request = new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = workspace.RootPath,
                    Paths = EffectivePaths(), Recursive = true };
                output.AppendText("[更新] " + DateTime.Now.ToString("HH:mm:ss") + Environment.NewLine + String.Join(Environment.NewLine, request.Paths) + Environment.NewLine);
                var result = await run(request, CancellationToken.None);
                output.AppendText(result.Output + Environment.NewLine + result.Error + Environment.NewLine + "退出码：" + result.ExitCode + Environment.NewLine);
                status.Text = result.Succeeded ? "更新完成。" : result.TimedOut
                    ? "更新超时；可能已发生部分更新。请关闭后刷新工作区核对状态，不会自动重试。"
                    : "更新未成功；请查看操作记录，关闭后刷新工作区核对状态，不会自动重试。";
                SHChangeNotify(0x00002000, 0x0005, workspace.RootPath, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                output.AppendText(ex.Message + Environment.NewLine);
                status.Text = "更新未完成；请关闭后刷新工作区核对状态，不会自动重试。";
            }
            finally { SetBusy(false); }
        }

        private void SetBusy(bool value)
        { busy = value; update.Enabled = !value && ready; refresh.Enabled = close.Enabled = !value; UseWaitCursor = value; }

        private static bool SameIdentity(PlasticWorkspace left, PlasticWorkspace right)
        { return String.Equals(left.RootPath, right.RootPath, StringComparison.OrdinalIgnoreCase) && left.Name == right.Name && left.Repository == right.Repository && left.Selector == right.Selector; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
    }
}
