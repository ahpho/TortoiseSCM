// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    // IDD_RENAME conventions: old context, a short new name, explicit action/cancel.
    internal sealed class BranchRenameForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string selector;
        private readonly PlasticBranch expected;
        private readonly TextBox name = new TextBox();
        private readonly TextBox details = new TextBox();
        private readonly TextBox fullName = new TextBox();
        private readonly Label status = new Label();
        private readonly Button rename = DialogStyle.Button("重命名(&R)…");
        private readonly Button close = DialogStyle.Button("取消");
        private readonly Func<string, PlasticBranch, string, string, CancellationToken, Task<PlasticCommandResult>> renameBranch;
        private readonly Func<string, bool> confirm;
        private bool busy;
        internal bool Attempted { get; private set; }
        internal string RenamedBranch { get; private set; }

        internal BranchRenameForm(PlasticClient client, string root, string selector, PlasticBranch branch)
        {
            this.client = client; this.root = root; this.selector = selector;
            if (!CanRename(branch)) throw new ArgumentException("只能重命名具有完整身份信息的非当前、非根叶分支。");
            expected = Snapshot(branch);
            renameBranch = client.RenameBranchAsync;
            confirm = message => MessageBox.Show(this, message, Text, MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
            ValidateContext();
            DialogStyle.Apply(this); Text = "重命名分支 - TortoiseSCM";
            Size = new Size(780, 500); MinimumSize = new Size(650, 470);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 68, 58, 44, 55, 32 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var group = new GroupBox { Text = "服务器分支", Dock = DockStyle.Fill, Padding = new Padding(8) };
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Both; details.WordWrap = false; details.Dock = DockStyle.Fill;
            details.TabStop = false;
            details.AccessibleName = "重命名分支的仓库、原名称、父分支和头提交";
            details.Text = "仓库：" + expected.Repository + "\r\n原名称：" + expected.Name + "\r\n父分支：" + expected.Parent +
                "\r\n头提交：cs:" + expected.HeadChangeset;
            group.Controls.Add(details); layout.Controls.Add(group, 0, 0);
            var input = new GroupBox { Text = "新短名称(&N)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            name.Dock = DockStyle.Top; name.AccessibleName = "分支新短名称"; input.Controls.Add(name); layout.Controls.Add(input, 0, 1);
            var preview = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            preview.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); preview.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            preview.Controls.Add(new Label { Text = "新完整名称：", Dock = DockStyle.Fill }, 0, 0);
            fullName.ReadOnly = true; fullName.Dock = DockStyle.Top; fullName.AccessibleName = "重命名后的完整分支名";
            preview.Controls.Add(fullName, 0, 1); layout.Controls.Add(preview, 0, 2);
            layout.Controls.Add(new Label { Text = "重命名修改服务器元数据，会影响其他用户按名称引用此分支。\r\n工作区保持当前分支；其他用户可能需要更新其引用。", Dock = DockStyle.Fill }, 0, 3);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.AccessibleName = "分支重命名状态"; layout.Controls.Add(status, 0, 4);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            rename.Width = 110; footer.Controls.Add(close); footer.Controls.Add(rename); layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
            name.TextChanged += delegate { UpdateButtons(); };
            Shown += delegate { name.Focus(); name.SelectAll(); };
            name.Text = expected.Name.Substring(expected.Name.LastIndexOf('/') + 1); name.SelectAll();
            rename.Click += async delegate { await ConfirmRenameAsync(); };
            close.Click += delegate { Close(); }; CancelButton = close;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            UpdateButtons();
        }

        internal static bool CanRename(PlasticBranch branch)
        {
            Guid identity;
            return branch != null && !branch.IsCurrent && !String.IsNullOrEmpty(branch.Parent) &&
                !String.IsNullOrEmpty(branch.Name) && branch.Name.StartsWith("/", StringComparison.Ordinal) &&
                branch.Name.LastIndexOf('/') > 0 && branch.Name.Substring(0, branch.Name.LastIndexOf('/')) == branch.Parent &&
                branch.BranchId > 0 && Guid.TryParse(branch.Guid, out identity) && identity != Guid.Empty && branch.HeadChangeset >= 0;
        }

        private static PlasticBranch Snapshot(PlasticBranch branch)
        {
            return new PlasticBranch { Name = branch.Name, Parent = branch.Parent, Repository = branch.Repository,
                BranchId = branch.BranchId, Guid = branch.Guid, HeadChangeset = branch.HeadChangeset,
                IsCurrent = branch.IsCurrent, Owner = branch.Owner, CreationDate = branch.CreationDate, Comment = branch.Comment };
        }

        private string NewName()
        {
            string leaf = name.Text.Trim();
            if (leaf.Length == 0 || leaf.StartsWith("-", StringComparison.Ordinal) || leaf == "." || leaf == ".." || leaf.Any(Char.IsControl) ||
                leaf.IndexOfAny(new[] { '/', '\\', '@', '#', '"', ':', '?', '\'' }) >= 0)
                throw new ArgumentException("请输入短名称，不能以 - 开头或包含路径分隔符、@、#、:、?、引号或控制字符。");
            if (String.Equals(FullName(leaf), expected.Name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("新名称必须与原名称不同（忽略大小写）。");
            return leaf;
        }

        private string FullName(string leaf)
        { return expected.Parent + "/" + leaf; }

        private void UpdateButtons()
        {
            fullName.Text = FullName(name.Text.Trim());
            bool valid = true; try { NewName(); } catch (ArgumentException) { valid = false; }
            name.Enabled = !busy && !Attempted; rename.Enabled = !busy && !Attempted && valid; close.Enabled = !busy;
            if (Attempted && !busy) close.Text = "关闭";
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != expected.Repository || workspace.Selector != selector ||
                !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库或分支已改变，请关闭窗口并刷新分支列表。");
        }

        private async Task ConfirmRenameAsync()
        {
            if (!rename.Enabled) return;
            try {
                ValidateContext(); string leaf = NewName();
                if (!confirm("在仓库 " + expected.Repository + " 重命名分支：\r\n" + expected.Name + "\r\n→ " + FullName(leaf) +
                    "\r\n父分支：" + expected.Parent + "\r\n头提交 cs:" + expected.HeadChangeset +
                    "\r\n\r\n此操作影响其他用户按名称引用此分支。确认继续？")) return;
                await SubmitAsync();
            }
            catch (Exception ex) { Attempted = true; status.Text = ex.Message; UpdateButtons(); }
        }

        private async Task SubmitAsync()
        {
            if (busy || Attempted) return;
            try {
                string leaf = NewName(); Attempted = true;
                ValidateContext(); busy = true; UpdateButtons(); status.Text = "正在重命名服务器分支，请等待完成…";
                var result = await renameBranch(root, Snapshot(expected), leaf, selector, CancellationToken.None);
                if (!result.Succeeded) throw new PlasticCommandException(result);
                ValidateContext(); RenamedBranch = FullName(leaf);
            }
            catch (Exception ex) { status.Text = "重命名结果未确认。请关闭并刷新列表核对结果；不会自动反向重命名。\r\n" + ex.Message; }
            finally { busy = false; UpdateButtons(); }
            if (RenamedBranch != null) { DialogResult = DialogResult.OK; Close(); }
        }
    }
}
