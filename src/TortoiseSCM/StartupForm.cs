// GPL-2.0-or-later. Entry point for users without a workspace.
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class StartupForm : Form
    {
        private readonly Button create = DialogStyle.Button("拉取仓库…");
        private readonly Button open = DialogStyle.Button("打开已有工作区…");
        private readonly Button settings = DialogStyle.Button("设置…");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly Label status = new Label();
        private Func<string> selectExisting;
        private Func<string> createNew;
        private Func<string, CancellationToken, Task<PlasticWorkspace>> getWorkspace;
        private CancellationTokenSource cancellation;
        internal string SelectedWorkspacePath { get; private set; }

        internal StartupForm()
        {
            DialogStyle.Apply(this); Text = "TortoiseSCM"; ClientSize = new Size(620, 330); MinimumSize = new Size(550, 350); MaximizeBox = false;
            selectExisting = delegate { using (var picker = new FolderBrowserDialog { Description = "选择已有 Plastic SCM 工作区", ShowNewFolderButton = false })
                return picker.ShowDialog(this) == DialogResult.OK ? picker.SelectedPath : null; };
            createNew = delegate { using (var wizard = new WorkspaceCreationForm()) return wizard.ShowDialog(this) == DialogResult.OK ? wizard.SelectedWorkspacePath : null; };
            getWorkspace = (path, token) => new PlasticClient(PlasticClientConfig.Load()).GetWorkspaceAsync(path, token);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 5 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.Controls.Add(new Label { Text = "欢迎使用 TortoiseSCM", Dock = DockStyle.Fill, Font = new Font(Font, FontStyle.Bold), Padding = new Padding(0, 8, 0, 0) }, 0, 0);
            layout.Controls.Add(new Label { Text = "首次使用：拉取服务器上已有的仓库，创建本地工作区。\r\n已有工作区：打开本地目录，查看状态、更新、日志或提交。\r\n请先安装 Plastic / Unity Version Control 客户端并完成登录。", Dock = DockStyle.Fill }, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            create.Width = 120; open.Width = 162; settings.Width = 88; actions.Controls.Add(create); actions.Controls.Add(open); actions.Controls.Add(settings); layout.Controls.Add(actions, 0, 2);
            status.Text = "首次拉取采用 Standard 完整工作区。比较和合并统一使用 Beyond Compare。"; status.Dock = DockStyle.Fill; layout.Controls.Add(status, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft }; buttons.Controls.Add(close); layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout); AcceptButton = create; CancelButton = close;
            create.Click += delegate { var path = createNew(); if (!String.IsNullOrEmpty(path)) Finish(path); };
            open.Click += async delegate { await OpenAsync(); }; settings.Click += delegate { using (var form = new SettingsForm()) form.ShowDialog(this); }; close.Click += delegate { Close(); };
            FormClosing += delegate { if (cancellation != null) cancellation.Cancel(); };
        }

        private async Task OpenAsync()
        {
            if (cancellation != null) return;
            string path = selectExisting(); if (String.IsNullOrEmpty(path)) return;
            var source = new CancellationTokenSource(); cancellation = source;
            create.Enabled = open.Enabled = settings.Enabled = false; status.Text = "正在验证工作区…";
            try {
                var workspace = await getWorkspace(path, source.Token);
                if (IsDisposed || source.IsCancellationRequested) return;
                if (workspace == null || String.IsNullOrEmpty(workspace.RootPath)) throw new InvalidOperationException("所选目录不是有效的 Plastic SCM 工作区。");
                Finish(workspace.RootPath);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!IsDisposed) status.Text = "无法打开工作区：" + ex.Message; }
            finally { cancellation = null; source.Dispose(); if (!IsDisposed) create.Enabled = open.Enabled = settings.Enabled = true; }
        }

        private void Finish(string path) { SelectedWorkspacePath = path; DialogResult = DialogResult.OK; Close(); }
    }
}