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
    internal sealed class PartialStructureForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly ListView items = new ListView();
        private readonly TextBox details = new TextBox();
        private readonly Label status = new Label();
        private readonly Button refresh = DialogStyle.Button("预检 / 恢复会话");
        private readonly Button prepare = DialogStyle.Button("准备备份");
        private readonly Button apply = DialogStyle.Button("选择处理方式…");
        private readonly Button cancel = DialogStyle.Button("取消准备");
        private readonly Button recover = DialogStyle.Button("恢复为传入版本…");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private IList<PlasticPartialStructureConflict> conflicts = new List<PlasticPartialStructureConflict>();
        private PlasticPartialStructureSession session;
        private bool busy;

        internal PartialStructureForm(PlasticClient client, string root)
        {
            this.client = client; this.root = root;
            DialogStyle.Apply(this); Text = "Partial 文件结构冲突 - TortoiseSCM";
            Size = new Size(1000, 680); MinimumSize = new Size(870, 550);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = root, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            refresh.Width = 145; top.Controls.Add(refresh);
            top.Controls.Add(new Label { Text = "先准备备份，再选择处理方式；处理后单独提交。", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
            layout.Controls.Add(top, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(930, 460), SplitterDistance = 280, Panel1MinSize = 110, Panel2MinSize = 100 };
            items.Dock = DockStyle.Fill; items.View = View.Details; items.MultiSelect = false; DialogStyle.ApplyList(items);
            items.Columns.Add("本地路径", 330); items.Columns.Add("冲突类型", 165); items.Columns.Add("传入路径", 320); items.Columns.Add("处理能力", 110);
            items.AccessibleName = "Partial 文件结构冲突列表";
            items.SelectedIndexChanged += delegate { ShowDetails(); UpdateButtons(); };
            split.Panel1.Controls.Add(items);
            details.Dock = DockStyle.Fill; details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical;
            split.Panel2.Controls.Add(details); layout.Controls.Add(split, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            prepare.Width = 100; apply.Width = 135; cancel.Width = 100; recover.Width = 160;
            footer.Controls.Add(close); footer.Controls.Add(apply); footer.Controls.Add(prepare); footer.Controls.Add(cancel); footer.Controls.Add(recover);
            layout.Controls.Add(footer, 0, 4); Controls.Add(layout); CancelButton = close;
            close.Click += delegate { Close(); };
            refresh.Click += async delegate { await WorkAsync(ReadStateAsync); };
            prepare.Click += async delegate {
                if (!prepare.Enabled) return; var selected = Selected();
                await WorkAsync(async delegate { session = await client.PreparePartialStructureAsync(root, selected.RepositoryPath, lifetime.Token); Render(); });
            };
            apply.Click += async delegate { await ApplyAsync(); };
            cancel.Click += async delegate {
                if (!cancel.Enabled) return;
                if (MessageBox.Show(this, "取消尚未应用的结构处理准备？工作区文件不会改变，备份仍保留。", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                await WorkAsync(async delegate { await client.CancelPartialStructureAsync(root, lifetime.Token); await ReadStateAsync(); });
            };
            recover.Click += async delegate {
                if (!recover.Enabled) return;
                if (MessageBox.Show(this, "放弃本次未完成的处理，将受影响文件恢复为会话固定的传入版本。\r\n\r\n本地原始内容和处理备份仍保留在：\r\n" + session.RecoveryDirectory +
                    "\r\n\r\n请先保存之后手动编辑的文件。继续恢复？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                await WorkAsync(async delegate { var result = await client.RecoverPartialStructureAsync(root, lifetime.Token); CheckResult(result); await ReadStateAsync(); });
            };
            Shown += async delegate { await WorkAsync(ReadStateAsync); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; else lifetime.Cancel(); };
            UpdateButtons();
        }

        private PlasticPartialStructureConflict Selected() { return items.SelectedItems.Count == 1 ? items.SelectedItems[0].Tag as PlasticPartialStructureConflict : null; }
        private static string KindText(string kind)
        {
            switch (kind) { case "add-add": return "双方新增同名项"; case "incoming-delete": return "传入删除 / 本地修改"; case "incoming-move": return "传入移动 / 本地修改";
                case "local-delete": return "本地删除 / 传入修改"; case "local-move": return "本地移动 / 传入修改"; default: return kind; }
        }
        private void UpdateButtons()
        {
            var selected = Selected(); refresh.Enabled = close.Enabled = !busy;
            prepare.Enabled = !busy && session == null && selected != null && selected.ResolutionOptions != null && selected.ResolutionOptions.Count > 0;
            apply.Enabled = cancel.Enabled = !busy && session != null && session.Ready && !session.Applying;
            recover.Enabled = !busy && session != null && (!session.Ready || session.Applying) && session.Conflict != null;
        }
        private void ShowDetails()
        {
            var conflict = session == null ? Selected() : session.Conflict;
            details.Text = conflict == null ? "请选择一个结构冲突。准备步骤保存本地备份，不修改工作区。" : Describe(conflict);
            if (session != null) details.Text = "恢复备份目录：" + session.RecoveryDirectory + "\r\n\r\n" + details.Text;
        }
        private static string Describe(PlasticPartialStructureConflict conflict)
        {
            return KindText(conflict.Kind) + "\r\n本地：" + conflict.RepositoryPath + "\r\n基础路径：" + conflict.OriginalPath + "\r\n传入：" + conflict.IncomingPath +
                "\r\n基础 cs:" + conflict.BaseChangeset + " → 服务器 cs:" + conflict.IncomingChangeset + "\r\n" + conflict.Reason;
        }
        private void Render()
        {
            string selected = Selected() == null ? null : Selected().RepositoryPath;
            items.Items.Clear();
            foreach (var conflict in session == null ? conflicts : new[] { session.Conflict })
            {
                if (conflict == null) continue;
                var row = new ListViewItem(new[] { conflict.RepositoryPath, KindText(conflict.Kind), conflict.IncomingPath,
                    conflict.ResolutionOptions != null && conflict.ResolutionOptions.Count > 0 ? "可处理" : "暂不支持" }) { Tag = conflict };
                items.Items.Add(row); row.Selected = session != null || conflict.RepositoryPath == selected;
            }
            status.Text = session == null ? "发现 " + conflicts.Count + " 个文件结构冲突；不扫描目录级冲突。" : session.Ready && !session.Applying ? "备份已准备；请选择处理方式。" : "上次处理未完成，已阻止提交。请查看备份并恢复。";
            ShowDetails(); UpdateButtons();
        }
        private async Task ReadStateAsync()
        {
            session = await client.GetPartialStructureSessionAsync(root, lifetime.Token);
            if (session == null) conflicts = await client.PreviewPartialStructureAsync(root, lifetime.Token);
            Render();
        }
        private async Task ApplyAsync()
        {
            if (!apply.Enabled) return;
            using (var choice = new PartialStructureChoiceForm(Describe(session.Conflict) + "\r\n\r\n备份：" + session.RecoveryDirectory, session.Conflict.ResolutionOptions, session.Conflict.Kind))
            {
                if (choice.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show(this, "按已选方式处理以下文件？\r\n" + session.Conflict.RepositoryPath + "\r\n" + choice.ChoiceText +
                    (choice.Rename == null ? "" : " → " + choice.Rename) + "\r\n\r\n原始内容保留在备份中，不自动提交。", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                await WorkAsync(async delegate { var result = await client.ResolvePartialStructureAsync(root, choice.Resolution, choice.Rename, lifetime.Token); CheckResult(result); await ReadStateAsync(); });
            }
        }
        private static void CheckResult(PlasticCommandResult result) { if (!result.Succeeded) throw new InvalidOperationException(result.Error + "\r\n" + result.Output); }
        private async Task WorkAsync(Func<Task> work)
        {
            if (busy) return; busy = true; UpdateButtons(); status.Text = "正在处理…";
            try
            {
                Exception failure = null; try { await work(); } catch (Exception ex) { failure = ex; }
                if (failure != null)
                {
                    try { session = await client.GetPartialStructureSessionAsync(root, lifetime.Token); }
                    catch { if (session == null) session = new PlasticPartialStructureSession(); session.Ready = false; }
                    Render(); details.Text = failure.Message + "\r\n\r\n" + details.Text; status.Text = "操作未完成，请检查错误和备份。";
                }
            }
            finally { busy = false; UpdateButtons(); }
        }
    }
}
