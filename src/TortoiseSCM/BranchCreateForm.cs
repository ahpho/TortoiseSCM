// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    // Follow IDD_NEW_BRANCH_TAG: name, base revision, message and explicit action.
    internal sealed class BranchCreateForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string repository;
        private readonly string selector;
        private readonly string parent;
        private readonly TextBox name = new TextBox();
        private readonly NumericUpDown revision = new NumericUpDown();
        private readonly TextBox comment = new TextBox();
        private readonly Label fullName = new Label();
        private readonly Label status = new Label();
        private readonly Button create = DialogStyle.Button("创建(&C)…");
        private readonly Button close = DialogStyle.Button("取消");
        private readonly Func<string, string, long, string, CancellationToken, Task<PlasticCommandResult>> createBranch;
        private bool busy;
        private bool attempted;
        internal string CreatedBranch { get; private set; }

        internal BranchCreateForm(PlasticClient client, string root, string repository, string selector, string parent, long baseChangeset)
        {
            this.client = client; this.root = root; this.repository = repository; this.selector = selector; this.parent = parent;
            createBranch = (path, branch, changeset, message, token) =>
                client.CreateBranchAsync(path, branch, changeset, message, repository, selector, token);
            ValidateContext();
            DialogStyle.Apply(this); Text = "创建子分支 - TortoiseSCM";
            Size = new Size(780, 590); MinimumSize = new Size(620, 510);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 42, 124, 70 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 40, 38, 32 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = "仓库：" + repository + "\r\n工作区：" + root, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var nameGroup = new GroupBox { Text = "名称", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 3 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105)); fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 26, 28, 26 }) fields.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            fields.Controls.Add(Label("父分支："), 0, 0); fields.Controls.Add(Label(parent), 1, 0);
            var nameLabel = Label("子分支名(&N)："); nameLabel.UseMnemonic = true;
            fields.Controls.Add(nameLabel, 0, 1); name.Dock = DockStyle.Fill; name.AccessibleName = "子分支短名称"; fields.Controls.Add(name, 1, 1);
            fields.Controls.Add(Label("完整名称："), 0, 2); fullName.Dock = DockStyle.Fill; fullName.AutoEllipsis = true; fullName.UseMnemonic = false; fields.Controls.Add(fullName, 1, 2);
            nameGroup.Controls.Add(fields); layout.Controls.Add(nameGroup, 0, 1);
            var baseGroup = new GroupBox { Text = "创建起点（默认是打开时解析的父分支头提交）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            var baseRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            baseRow.Controls.Add(new Label { Text = "变更集 cs(&B)：", AutoSize = true, Margin = new Padding(0, 5, 4, 0) });
            revision.Maximum = Int64.MaxValue; revision.Value = baseChangeset; revision.Width = 150; revision.AccessibleName = "子分支起点变更集";
            baseRow.Controls.Add(revision); baseGroup.Controls.Add(baseRow); layout.Controls.Add(baseGroup, 0, 2);
            var messageGroup = new GroupBox { Text = "分支说明(&M)（必填）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            comment.Dock = DockStyle.Fill; comment.Multiline = true; comment.AcceptsReturn = true; comment.ScrollBars = ScrollBars.Vertical; comment.AccessibleName = "分支说明";
            messageGroup.Controls.Add(comment); layout.Controls.Add(messageGroup, 0, 3);
            layout.Controls.Add(new Label { Text = "仅在服务器创建分支。不切换工作区，不提交文件，也不改变当前加载规则。\r\n可指定历史起点；起点必须是此仓库中存在的变更集。", Dock = DockStyle.Fill }, 0, 4);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 5);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            footer.Controls.Add(close); footer.Controls.Add(create); layout.Controls.Add(footer, 0, 6); Controls.Add(layout);
            name.TextChanged += delegate { UpdateButtons(); }; comment.TextChanged += delegate { UpdateButtons(); };
            create.Click += async delegate { await ConfirmCreateAsync(); }; close.Click += delegate { Close(); }; CancelButton = close;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            UpdateButtons();
        }

        private static Label Label(string text)
        { return new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, UseMnemonic = false }; }

        private string NewBranchName()
        {
            string child = name.Text.Trim();
            if (child.Length == 0 || child == "." || child == ".." || child.Any(Char.IsControl) || child.IndexOfAny(new[] { '/', '\\', '@', '#', '"', ':', '?', '\'' }) >= 0)
                throw new ArgumentException("请输入子分支短名称，不能包含 /、\\、@、#、:、?、引号或控制字符。");
            if (String.IsNullOrWhiteSpace(comment.Text)) throw new ArgumentException("请填写分支说明。");
            if (comment.Text.Any(c => Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t')) throw new ArgumentException("分支说明含无效控制字符。");
            return parent.TrimEnd('/') + "/" + child;
        }

        private void UpdateButtons()
        {
            fullName.Text = parent.TrimEnd('/') + "/" + name.Text.Trim();
            bool valid = true;
            try { NewBranchName(); } catch (ArgumentException) { valid = false; }
            name.Enabled = revision.Enabled = comment.Enabled = !busy && !attempted;
            create.Enabled = !busy && !attempted && valid; close.Enabled = !busy;
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository || workspace.Selector != selector || !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库或分支已改变，请关闭窗口并重新选择父分支。");
        }

        private async Task ConfirmCreateAsync()
        {
            if (!create.Enabled) return;
            try {
                ValidateContext(); string branch = NewBranchName();
                if (MessageBox.Show(this, "在服务器创建子分支：\r\n" + branch + "\r\n起点 cs:" + revision.Value +
                    "\r\n\r\n工作区保持当前分支。继续？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                await SubmitAsync();
            }
            catch (Exception ex) { status.Text = ex.Message; }
        }

        private async Task SubmitAsync()
        {
            if (busy || attempted) return;
            try {
                ValidateContext(); string branch = NewBranchName();
                busy = true; attempted = true; UpdateButtons(); status.Text = "正在创建分支，请等待完成…";
                var result = await createBranch(root, branch, (long)revision.Value, comment.Text, CancellationToken.None);
                if (!result.Succeeded) throw new PlasticCommandException(result);
                CreatedBranch = branch; DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { status.Text = "创建未确认：" + ex.Message + " 请关闭并刷新分支列表确认结果，再决定是否重新创建。"; }
            finally { busy = false; UpdateButtons(); }
            if (CreatedBranch != null) Close();
        }
    }
}
