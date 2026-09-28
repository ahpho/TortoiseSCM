// GPL-2.0-or-later. First checkout of an existing Plastic repository.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class WorkspaceCreationForm : Form
    {
        private readonly TextBox server = new TextBox();
        private readonly ComboBox repositories = new ComboBox();
        private readonly TextBox workspaceName = new TextBox();
        private readonly TextBox directory = new TextBox();
        private readonly TextBox branch = new TextBox();
        private readonly TextBox status = new TextBox();
        private readonly Button query = DialogStyle.Button("查询仓库");
        private readonly Button cancelQuery = DialogStyle.Button("取消查询");
        private readonly Button browse = DialogStyle.Button("浏览…");
        private readonly Button create = DialogStyle.Button("拉取并打开");
        private readonly Button close = DialogStyle.Button("关闭");
        private Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>> getRepositories;
        private Func<PlasticRepositoryInfo, string, string, string, IProgress<string>, CancellationToken, Task<PlasticWorkspaceCreationResult>> createWorkspace;
        private Func<string, bool> confirm;
        private CancellationTokenSource queryCancellation;
        private int generation;
        private bool writing;
        private bool recoveryRequired;
        internal string SelectedWorkspacePath { get; private set; }

        internal WorkspaceCreationForm() : this(null) { }

        internal WorkspaceCreationForm(string initialPath)
        {
            DialogStyle.Apply(this); Text = "拉取仓库 - TortoiseSCM";
            ClientSize = new Size(740, 510); MinimumSize = new Size(650, 510);
            var client = new PlasticClient(PlasticClientConfig.Load());
            getRepositories = client.GetRepositoriesAsync; createWorkspace = client.CreateWorkspaceAsync;
            confirm = text => MessageBox.Show(this, text, "确认首次拉取", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 9 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
            foreach (int height in new[] { 60, 34, 34, 34, 34, 34, 44 }) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var introduction = new Label { Text = "从已有仓库创建 Standard 完整工作区并下载所选分支。\r\n请先在 Plastic / Unity Version Control 客户端完成登录；此处使用已有登录配置。", Dock = DockStyle.Fill };
            grid.Controls.Add(introduction, 0, 0); grid.SetColumnSpan(introduction, 3);
            AddField(grid, 1, "服务器", server); server.AccessibleName = "仓库服务器"; server.Text = "localhost:8087";
            grid.Controls.Add(query, 2, 1);
            repositories.DropDownStyle = ComboBoxStyle.DropDownList; repositories.DisplayMember = "Name";
            AddField(grid, 2, "仓库", repositories); grid.Controls.Add(cancelQuery, 2, 2);
            AddField(grid, 3, "工作区名称", workspaceName); AddField(grid, 4, "本地目录", directory); grid.Controls.Add(browse, 2, 4);
            directory.Text = initialPath ?? String.Empty;
            AddField(grid, 5, "分支", branch); branch.Text = "/main";
            var hint = new Label { Text = "服务器示例：localhost:8087 或组织名@cloud。目标目录必须为空或尚不存在。\r\n确认后将完整下载分支；创建和下载期间请等待完成。", Dock = DockStyle.Fill };
            grid.Controls.Add(hint, 0, 6); grid.SetColumnSpan(hint, 3);
            status.Multiline = true; status.ReadOnly = true; status.ScrollBars = ScrollBars.Vertical; status.Dock = DockStyle.Fill;
            status.Text = "填写服务器后，点击“查询仓库”。"; status.AccessibleName = "拉取进度和恢复信息";
            grid.Controls.Add(status, 0, 7); grid.SetColumnSpan(status, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0, 8, 0, 0) };
            create.Width = 104; buttons.Controls.Add(close); buttons.Controls.Add(create); grid.Controls.Add(buttons, 0, 8); grid.SetColumnSpan(buttons, 3);
            Controls.Add(grid); AcceptButton = create; CancelButton = close;
            query.Click += async delegate { await QueryAsync(); }; cancelQuery.Click += delegate { CancelQuery("查询已取消。可以重新查询。"); };
            server.TextChanged += delegate { CancelQuery("服务器已更改，请重新查询仓库。"); repositories.Items.Clear(); UpdateControls(); };
            repositories.SelectedIndexChanged += delegate { UpdateControls(); };
            browse.Click += delegate { using (var picker = new FolderBrowserDialog { Description = "选择空目录，或新建工作区目录", ShowNewFolderButton = true }) {
                if (picker.ShowDialog(this) == DialogResult.OK) directory.Text = picker.SelectedPath;
            } };
            create.Click += async delegate { await CreateAsync(); }; close.Click += delegate { Close(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (writing) { e.Cancel = true; return; } CancelQuery(null); };
            UpdateControls();
        }

        private static void AddField(TableLayoutPanel grid, int row, string label, Control field)
        {
            grid.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) }, 0, row);
            field.Dock = DockStyle.Fill; grid.Controls.Add(field, 1, row);
        }

        private void UpdateControls()
        {
            bool editable = !writing && !recoveryRequired;
            server.Enabled = workspaceName.Enabled = directory.Enabled = branch.Enabled = browse.Enabled = editable;
            query.Enabled = editable && queryCancellation == null;
            cancelQuery.Enabled = editable && queryCancellation != null;
            repositories.Enabled = editable && queryCancellation == null;
            create.Enabled = editable && queryCancellation == null && repositories.SelectedItem != null;
            close.Enabled = !writing;
        }

        private void CancelQuery(string message)
        {
            generation++;
            if (queryCancellation != null) { queryCancellation.Cancel(); queryCancellation = null; }
            if (message != null) status.Text = message;
            UpdateControls();
        }

        private async Task QueryAsync()
        {
            if (writing || recoveryRequired || queryCancellation != null) return;
            repositories.Items.Clear(); var source = new CancellationTokenSource(); queryCancellation = source;
            int request = ++generation; string requestedServer = server.Text.Trim();
            status.Text = "正在查询已授权仓库…"; UpdateControls();
            try {
                var result = await getRepositories(requestedServer, source.Token);
                if (IsDisposed || request != generation || source.IsCancellationRequested) return;
                foreach (var repository in result) repositories.Items.Add(repository);
                if (repositories.Items.Count > 0) repositories.SelectedIndex = 0;
                status.Text = repositories.Items.Count == 0 ? "没有找到可访问仓库。请检查服务器地址与客户端登录权限。" : "请选择仓库，并填写工作区名称与本地目录。";
            }
            catch (OperationCanceledException) { if (!IsDisposed && request == generation) status.Text = "查询已取消。"; }
            catch (Exception ex) { if (!IsDisposed && request == generation) status.Text = "查询失败：" + ex.Message + "\r\n请检查服务器地址、连接和 Plastic 客户端登录配置，然后重新查询。"; }
            finally { if (request == generation) queryCancellation = null; source.Dispose(); if (!IsDisposed) UpdateControls(); }
        }

        private async Task CreateAsync()
        {
            if (writing || recoveryRequired || queryCancellation != null || repositories.SelectedItem == null) return;
            var repository = (PlasticRepositoryInfo)repositories.SelectedItem;
            string name = workspaceName.Text.Trim(), path = directory.Text.Trim(), selectedBranch = branch.Text.Trim();
            if (name.Length == 0 || path.Length == 0 || selectedBranch.Length == 0) { status.Text = "请填写工作区名称、本地目录和分支。"; return; }
            if (!confirm("仓库：" + repository.Specification + "\r\n分支：" + selectedBranch + "\r\n工作区：" + name + "\r\n本地目录：" + path +
                "\r\n\r\n将创建 Standard 完整工作区并下载整个分支。创建与下载期间不能取消。继续？")) return;
            writing = true; UpdateControls(); status.Text = "正在检查并创建工作区…";
            try {
                var progress = new Progress<string>(message => { if (!IsDisposed && writing) status.Text = message; });
                var result = await createWorkspace(repository, name, path, selectedBranch, progress, CancellationToken.None);
                if (result.Succeeded) { SelectedWorkspacePath = result.WorkspacePath; writing = false; DialogResult = DialogResult.OK; Close(); return; }
                recoveryRequired = result.WorkspaceCreated || result.OutcomeUncertain;
                status.Text = "首次拉取未完成（" + result.Stage + "）。\r\n" + result.Error + "\r\n本地目录：" + result.WorkspacePath + "\r\n" + result.RecoveryInstructions;
                if (recoveryRequired) status.AppendText("\r\n本窗口不会重复创建或自动删除工作区。请关闭后按上述说明核查和恢复。");
            }
            catch (Exception ex) { status.Text = "拉取前检查失败：" + ex.Message + "\r\n请修正输入后重试。"; }
            finally { writing = false; if (!IsDisposed) UpdateControls(); }
        }
    }
}