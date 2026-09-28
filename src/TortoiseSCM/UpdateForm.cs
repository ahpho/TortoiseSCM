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
        private readonly Button conflicts = DialogStyle.Button("处理传入冲突…");
        private readonly Button pending = DialogStyle.Button("检查 / 丢弃修改…");
        private Func<string, CancellationToken, Task<PlasticWorkspace>> getWorkspace;
        private Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>> run;
        private Func<string, CancellationToken, Task<IList<PlasticPartialConflict>>> previewConflicts;
        private Action showConflicts;
        private Action showPending;
        private PlasticWorkspace workspace;
        private bool busy;
        private bool ready;

        internal UpdateForm(PlasticClient client, IList<string> selectedPaths)
        {
            this.client = client;
            this.selectedPaths = new List<string>(selectedPaths);
            getWorkspace = client.GetWorkspaceAsync;
            run = client.RunAsync;
            previewConflicts = client.PreviewPartialConflictsAsync;
            showConflicts = delegate { using (var dialog = new PartialConflictForm(client, workspace.RootPath, EffectivePaths())) dialog.ShowDialog(this); };
            showPending = delegate {
                var request = new LaunchRequest(); request.Paths.AddRange(EffectivePaths());
                using (var dialog = new MainForm(request)) dialog.ShowDialog(this);
            };
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
            conflicts.Width = 118; pending.Width = 132;
            conflicts.Visible = pending.Visible = false;
            buttons.Controls.Add(conflicts); buttons.Controls.Add(pending);
            conflicts.Click += async delegate { await OpenResolutionAsync(true); };
            pending.Click += async delegate { await OpenResolutionAsync(false); };
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
                scope.Text = (workspace.IsPartial
                    ? "Gluon / 部分工作区：仅更新以下所选范围；目录包含全部子项。"
                    : "完整工作区：将整体更新工作区中的受控文件，包括所选路径以外的文件。") + "\r\n工作区：" + workspace.RootPath;
                paths.Lines = EffectivePaths().ToArray();
                conflicts.Visible = pending.Visible = workspace.IsPartial;
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
                if (workspace.IsPartial)
                {
                    status.Text = "正在预检所选范围内的传入冲突…";
                    var incoming = await previewConflicts(workspace.RootPath, CancellationToken.None);
                    var blocked = incoming.Where(item => PartialConflictForm.InSelectedScope(workspace.RootPath, selectedPaths, item.RepositoryPath)).ToList();
                    if (blocked.Count != 0)
                    {
                        output.AppendText("[预检] 本次尚未执行更新。以下项目有传入冲突：" + Environment.NewLine +
                            String.Join(Environment.NewLine, blocked.Select(item => item.RepositoryPath + (item.IsBinary ? " [二进制]" : "") +
                                (item.CanResolve ? "" : " — " + item.Reason))) + Environment.NewLine +
                            "保留修改：点击“处理传入冲突”，文本可用 Beyond Compare 合并，二进制选择本地或服务器版本。" + Environment.NewLine +
                            "丢弃修改：点击“检查 / 丢弃修改”，仅选择确实不需要的文件，确认撤销后返回刷新范围并再次更新。" + Environment.NewLine);
                        status.Text = "发现传入冲突，尚未更新。请先处理或明确丢弃，再刷新范围。";
                        return;
                    }
                    // Preview is not a native transaction. Revalidate identity after the awaited read.
                    current = await getWorkspace(selectedPaths[0], CancellationToken.None);
                    if (current == null || !SameIdentity(current, workspace) || current.IsPartial != workspace.IsPartial)
                        throw new InvalidOperationException("预检期间工作区已改变，请重新刷新范围。");
                }
                var request = new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = workspace.RootPath,
                    Paths = EffectivePaths(), Recursive = true };
                output.AppendText("[更新] " + DateTime.Now.ToString("HH:mm:ss") + Environment.NewLine + String.Join(Environment.NewLine, request.Paths) + Environment.NewLine);
                var result = await run(request, CancellationToken.None);
                output.AppendText(result.Output + Environment.NewLine + result.Error + Environment.NewLine + "退出码：" + result.ExitCode + Environment.NewLine);
                status.Text = result.Succeeded ? "更新完成。" : result.TimedOut
                    ? "更新超时；可能已发生部分更新。请关闭后刷新工作区核对状态，不会自动重试。"
                    : "更新未成功；可能已部分更新。请核对操作记录与本地修改，不会自动重试。";
                SHChangeNotify(0x00002000, 0x0005, workspace.RootPath, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                output.AppendText(ex.Message + Environment.NewLine);
                status.Text = "更新未完成；请关闭后刷新工作区核对状态，不会自动重试。";
            }
            finally { SetBusy(false); }
        }

        private async Task OpenResolutionAsync(bool incoming)
        {
            if (busy || workspace == null || !workspace.IsPartial) return;
            SetBusy(true);
            try
            {
                var current = await getWorkspace(selectedPaths[0], CancellationToken.None);
                if (current == null || !SameIdentity(current, workspace) || !current.IsPartial)
                    throw new InvalidOperationException("工作区已改变，请先刷新范围。");
                if (incoming) showConflicts(); else showPending();
            }
            catch (Exception ex) { output.AppendText(ex.Message + Environment.NewLine); }
            finally { SetBusy(false); }
            await LoadScopeAsync(); // Never resume an update automatically after a modal decision.
        }

        private void SetBusy(bool value)
        { busy = value; update.Enabled = !value && ready; conflicts.Enabled = pending.Enabled = !value && workspace != null && workspace.IsPartial; refresh.Enabled = close.Enabled = !value; UseWaitCursor = value; }

        private static bool SameIdentity(PlasticWorkspace left, PlasticWorkspace right)
        { return String.Equals(left.RootPath, right.RootPath, StringComparison.OrdinalIgnoreCase) && left.Name == right.Name && left.Repository == right.Repository && left.Selector == right.Selector; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern void SHChangeNotify(uint eventId, uint flags, string item1, IntPtr item2);
    }
}
