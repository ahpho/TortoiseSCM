// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class LabelCreateForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly string repository;
        private readonly TextBox name = new TextBox();
        private readonly TextBox comment = new TextBox();
        private readonly NumericUpDown revision = new NumericUpDown();
        private readonly TextBox status = new TextBox();
        private readonly Button create = DialogStyle.Button("创建(&C)…");
        private readonly Button close = DialogStyle.Button("取消");
        private readonly bool fixedRevision;
        private bool busy;
        private bool attempted;
        private Func<string, long, string, CancellationToken, Task<PlasticCommandResult>> createLabel;
        private Func<string, DialogResult> confirm;
        internal bool Saved { get; private set; }

        internal LabelCreateForm(PlasticClient client, string root, string repository, long? changeset)
        {
            this.client = client; this.root = root; this.repository = repository; fixedRevision = changeset.HasValue;
            ValidateContext();
            createLabel = (label, cs, message, token) => client.CreateLabelAsync(root, label, cs, message, repository, token);
            confirm = message => MessageBox.Show(this, message, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
            DialogStyle.Apply(this); Text = "创建标签 - TortoiseSCM"; Size = new Size(690, 460); MinimumSize = new Size(580, 410);
            // IDD_NEW_BRANCH_TAG: name, explicit base revision, message, native bottom buttons.
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 28, 58, 60 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 28, 66, 34 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = "仓库：" + repository, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var nameGroup = new GroupBox { Text = "标签名称(&N)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            name.Dock = DockStyle.Fill; name.AccessibleName = "新标签名称"; nameGroup.Controls.Add(name); layout.Controls.Add(nameGroup, 0, 1);
            var baseGroup = new GroupBox { Text = "目标变更集(&R)", Dock = DockStyle.Fill, Padding = new Padding(8) };
            revision.Minimum = 0; revision.Maximum = Int64.MaxValue; revision.Width = 200; revision.Location = new Point(12, 24); revision.AccessibleName = "标签目标变更集";
            if (changeset.HasValue) revision.Value = changeset.Value;
            baseGroup.Controls.Add(revision); layout.Controls.Add(baseGroup, 0, 2);
            var messageGroup = new GroupBox { Text = "说明(&M)（必填）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            comment.Dock = DockStyle.Fill; comment.Multiline = true; comment.AcceptsReturn = true; comment.ScrollBars = ScrollBars.Vertical; comment.AccessibleName = "标签说明";
            messageGroup.Controls.Add(comment); layout.Controls.Add(messageGroup, 0, 3);
            layout.Controls.Add(new Label { Text = "标签指向完整仓库快照；创建后不会切换工作区。", Dock = DockStyle.Fill }, 0, 4);
            status.Dock = DockStyle.Fill; status.Multiline = true; status.ReadOnly = true; status.ScrollBars = ScrollBars.Vertical;
            status.AccessibleName = "标签创建结果（可复制）"; layout.Controls.Add(status, 0, 5);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            footer.Controls.Add(close); footer.Controls.Add(create); layout.Controls.Add(footer, 0, 6); Controls.Add(layout);
            name.TextChanged += delegate { UpdateButtons(); }; comment.TextChanged += delegate { UpdateButtons(); };
            create.Click += async delegate { await ConfirmCreateAsync(); }; close.Click += delegate { Close(); }; CancelButton = close;
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            UpdateButtons();
        }

        private void ValidateContext()
        {
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository || !String.Equals(workspace.RootPath, root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区仓库已改变，请重新打开标签窗口。");
        }

        private void UpdateButtons()
        {
            name.Enabled = comment.Enabled = !busy && !attempted; revision.Enabled = !busy && !attempted && !fixedRevision;
            create.Enabled = !busy && !attempted && !String.IsNullOrWhiteSpace(name.Text) && !String.IsNullOrWhiteSpace(comment.Text); close.Enabled = !busy;
        }

        private async Task ConfirmCreateAsync()
        {
            if (!create.Enabled) return;
            string label = name.Text; string message = comment.Text; long cs = (long)revision.Value;
            try
            {
                ValidateContext();
                PlasticClient.ValidateLabelName(label);
                if (String.IsNullOrWhiteSpace(message) || message.Any(c => Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                    throw new ArgumentException("请填写标签说明，不要包含控制字符。");
                if (confirm("在仓库 " + repository + " 中创建标签：\r\n\r\n" + label + " → cs:" + cs + "\r\n\r\n" + message + "\r\n\r\n继续创建？") != DialogResult.OK) return;
                ValidateContext(); busy = attempted = true; UpdateButtons(); status.Text = "正在创建标签，请等待完成…";
                var result = await createLabel(label, cs, message, CancellationToken.None);
                if (!result.Succeeded) throw new PlasticCommandException(result);
                Saved = true; DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { status.Text = (attempted ? "创建结果未确认，请刷新标签列表后核对：" : "无法创建标签：") + ex.Message; }
            finally { busy = false; UpdateButtons(); }
            if (Saved) Close();
        }
    }
}
