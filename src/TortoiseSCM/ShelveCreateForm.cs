// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class ShelveCreateForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string repository;
        private readonly string selector;
        private readonly IList<string> paths;
        private readonly TextBox comment = new TextBox();
        private readonly ListView files = new ListView();
        private readonly Label status = new Label();
        private readonly Button create = DialogStyle.Button("保存(&S)…");
        private readonly Button close = DialogStyle.Button("取消");
        private readonly Func<string, IList<string>, string, CancellationToken, Task<PlasticCommandResult>> createShelve;
        private bool busy;
        private bool attempted;
        internal bool Saved { get; private set; }

        internal ShelveCreateForm(PlasticClient client, string root, string repository, string selector, IList<string> paths, string message)
        {
            this.client = client; this.root = root; this.repository = repository; this.selector = selector;
            if (paths == null || paths.Count == 0) throw new ArgumentException("请勾选需要保存的受控更改。");
            this.paths = paths.ToArray();
            createShelve = (path, selected, text, token) => client.CreateShelveAsync(path, selected, text, repository, selector, token);
            ValidateContext();
            DialogStyle.Apply(this); Text = "保存暂存集 - TortoiseSCM"; Size = new Size(820, 640); MinimumSize = new Size(660, 540);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            foreach (int height in new[] { 58, 46, 32 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = "仓库：" + repository + "\r\n工作区：" + root, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var messageGroup = new GroupBox { Text = "暂存说明(&M)（必填）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            comment.Dock = DockStyle.Fill; comment.Multiline = true; comment.AcceptsReturn = true; comment.ScrollBars = ScrollBars.Vertical;
            comment.AccessibleName = "暂存集说明"; comment.Text = message ?? ""; messageGroup.Controls.Add(comment); layout.Controls.Add(messageGroup, 0, 1);
            var pathsGroup = new GroupBox { Text = "已勾选的更改（" + paths.Count + " 项）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            files.Dock = DockStyle.Fill; files.View = View.Details; DialogStyle.ApplyList(files); files.Columns.Add("路径", 720); files.AccessibleName = "保存暂存集的已确认路径";
            foreach (string path in this.paths) files.Items.Add(new ListViewItem(path));
            pathsGroup.Controls.Add(files); layout.Controls.Add(pathsGroup, 0, 2);
            layout.Controls.Add(new Label { Text = "保存到服务器后，本地修改仍然保留，不会签入或自动丢弃。\r\n本版本暂不支持应用暂存集，恢复请使用官方客户端。\r\n本阶段仅支持文件；如存在未选择的依赖项，操作将拒绝执行。", Dock = DockStyle.Fill }, 0, 3);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 4);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            footer.Controls.Add(close); footer.Controls.Add(create); layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
            comment.TextChanged += delegate { UpdateButtons(); }; create.Click += async delegate { await ConfirmCreateAsync(); };
            close.Click += delegate { Close(); }; CancelButton = close;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            UpdateButtons();
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository || workspace.Selector != selector || !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库或分支已改变，请关闭窗口并重新勾选更改。");
        }

        private bool ValidComment()
        { return !String.IsNullOrWhiteSpace(comment.Text) && !comment.Text.Any(c => Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'); }

        private void UpdateButtons()
        { comment.Enabled = !busy && !attempted; create.Enabled = !busy && !attempted && ValidComment(); close.Enabled = !busy; }

        private async Task ConfirmCreateAsync()
        {
            if (!create.Enabled) return;
            try {
                ValidateContext();
                if (MessageBox.Show(this, "将上述 " + paths.Count + " 个受控更改保存为服务器暂存集？\r\n本地修改保持不变。", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                await SubmitAsync();
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }

        private async Task SubmitAsync()
        {
            if (busy || attempted || !ValidComment()) return;
            try {
                ValidateContext(); busy = attempted = true; UpdateButtons(); status.Text = "正在保存暂存集，请等待完成…";
                var result = await createShelve(root, paths, comment.Text, CancellationToken.None);
                if (!result.Succeeded) throw new PlasticCommandException(result);
                Saved = true; DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { status.Text = "保存未确认：" + ex.Message + " 请关闭并刷新暂存集列表确认结果，再决定是否重新保存。"; }
            finally { busy = false; UpdateButtons(); }
            if (Saved) Close();
        }
    }
}
