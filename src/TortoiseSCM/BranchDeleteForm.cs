// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    // IDD_DELETEREMOTETAG conventions: visible server context and explicit action/close.
    internal sealed class BranchDeleteForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string selector;
        private readonly PlasticBranch expected;
        private readonly TextBox details = new TextBox();
        private readonly Label status = new Label();
        private readonly Button delete = DialogStyle.Button("删除空分支(&D)…");
        private readonly Button close = DialogStyle.Button("取消");
        private readonly Func<string, PlasticBranch, string, CancellationToken, Task<PlasticCommandResult>> deleteBranch;
        private readonly Func<string, bool> confirm;
        private bool busy;
        internal bool Attempted { get; private set; }
        internal string DeletedBranch { get; private set; }

        internal BranchDeleteForm(PlasticClient client, string root, string selector, PlasticBranch branch)
        {
            this.client = client; this.root = root; this.selector = selector;
            if (!CanDelete(branch)) throw new ArgumentException("只能删除具有完整身份信息的非当前、非根空叶分支。");
            expected = Snapshot(branch); deleteBranch = client.DeleteBranchAsync;
            confirm = message => MessageBox.Show(this, message, Text, MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;
            ValidateContext();
            DialogStyle.Apply(this); Text = "删除空分支 - TortoiseSCM";
            Size = new Size(780, 480); MinimumSize = new Size(650, 450);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 118, 66, 32 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            var group = new GroupBox { Text = "服务器分支", Dock = DockStyle.Fill, Padding = new Padding(8) };
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Both; details.WordWrap = false; details.Dock = DockStyle.Fill;
            details.TabStop = false; details.AccessibleName = "待删除分支的仓库、名称、父分支和头提交";
            details.Text = "仓库：" + expected.Repository + "\r\n分支：" + expected.Name + "\r\n父分支：" + expected.Parent +
                "\r\n头提交：cs:" + expected.HeadChangeset;
            group.Controls.Add(details); layout.Controls.Add(group, 0, 0);
            layout.Controls.Add(new Label { Text = "仅删除没有自身提交的空叶分支。执行前会重新核对分支身份及引用；\r\n有子分支或分支属性时不能删除。\r\n任何以此继承头提交为父提交的暂存项都会阻止删除（包括其他分支的暂存项）。\r\n其他用户保存的名称引用无法检查，删除后这些引用会失效。\r\n当前工作区保持原分支。删除不可撤销；失败后请刷新核对，不会自动重试。",
                Dock = DockStyle.Fill, UseMnemonic = false }, 0, 1);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.UseMnemonic = false;
            status.AccessibleName = "分支删除状态"; status.Text = "确认后将检查服务器条件；列表中的头提交可能继承自父分支。"; layout.Controls.Add(status, 0, 2);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            delete.Width = 135; footer.Controls.Add(close); footer.Controls.Add(delete); layout.Controls.Add(footer, 0, 3); Controls.Add(layout);
            delete.Click += async delegate { await ConfirmDeleteAsync(); };
            close.Click += delegate { Close(); }; CancelButton = close;
            Shown += delegate { close.Focus(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            UpdateButtons();
        }

        internal static bool CanDelete(PlasticBranch branch)
        {
            Guid identity;
            return branch != null && !branch.IsCurrent && !String.IsNullOrEmpty(branch.Parent) &&
                !String.IsNullOrEmpty(branch.Name) && branch.Name.StartsWith("/", StringComparison.Ordinal) &&
                !String.IsNullOrEmpty(branch.Repository) && (branch.Name + branch.Repository).IndexOfAny(new[] { '\'', '\\', '"' }) < 0 &&
                branch.Name.LastIndexOf('/') > 0 && branch.Name.Substring(0, branch.Name.LastIndexOf('/')) == branch.Parent &&
                branch.BranchId > 0 && Guid.TryParse(branch.Guid, out identity) && identity != Guid.Empty && branch.HeadChangeset >= 0;
        }

        private static PlasticBranch Snapshot(PlasticBranch branch)
        {
            return new PlasticBranch { Name = branch.Name, Parent = branch.Parent, Repository = branch.Repository,
                BranchId = branch.BranchId, Guid = branch.Guid, HeadChangeset = branch.HeadChangeset,
                IsCurrent = branch.IsCurrent, Owner = branch.Owner, CreationDate = branch.CreationDate, Comment = branch.Comment };
        }

        private void UpdateButtons()
        {
            delete.Enabled = !busy && !Attempted; close.Enabled = !busy;
            if (Attempted && !busy) close.Text = "关闭";
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != expected.Repository || workspace.Selector != selector ||
                !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库或分支已改变，请关闭窗口并刷新分支列表。");
        }

        private async Task ConfirmDeleteAsync()
        {
            if (!delete.Enabled) return;
            try {
                ValidateContext();
                if (!confirm("从仓库 " + expected.Repository + " 永久删除空分支：\r\n" + expected.Name +
                    "\r\n父分支：" + expected.Parent + "\r\n头提交 cs:" + expected.HeadChangeset +
                    "\r\n\r\n其他用户保存的名称引用无法检查，删除后会失效。\r\n此操作不可撤销。确认继续？")) return;
                await SubmitAsync();
            }
            catch (Exception ex) { Attempted = true; status.Text = ex.Message; UpdateButtons(); }
        }

        private async Task SubmitAsync()
        {
            if (busy || Attempted) return;
            try {
                Attempted = true; ValidateContext(); busy = true; UpdateButtons();
                status.Text = "正在核对并删除服务器空分支，请等待完成…";
                var result = await deleteBranch(root, Snapshot(expected), selector, CancellationToken.None);
                if (!result.Succeeded) throw new PlasticCommandException(result);
                ValidateContext(); DeletedBranch = expected.Name;
            }
            catch (Exception ex) { status.Text = "删除结果未确认。请关闭并刷新列表核对结果；不会自动重试或重建分支。\r\n" + ex.Message; }
            finally { busy = false; UpdateButtons(); }
            if (DeletedBranch != null) { DialogResult = DialogResult.OK; Close(); }
        }
    }
}
