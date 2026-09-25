// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class PartialConflictForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly ListView items = new ListView();
        private readonly TextBox details = new TextBox();
        private readonly Label status = new Label();
        private readonly Button refresh = DialogStyle.Button("预检 / 恢复会话");
        private readonly Button prepare = DialogStyle.Button("三方合并…");
        private readonly Button apply = DialogStyle.Button("确认应用结果…");
        private readonly Button cancelPreparation = DialogStyle.Button("取消未应用准备…");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly Button structure = DialogStyle.Button("结构冲突…");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Dictionary<string, PlasticMergeConflictFiles> prepared = new Dictionary<string, PlasticMergeConflictFiles>(StringComparer.Ordinal);
        private IList<PlasticPartialConflict> conflicts = new List<PlasticPartialConflict>();
        private PlasticPartialConflictSession session;
        private bool busy;
        private const string Guidance = "此窗口处理 Partial 工作区内已加载文件的传入内容冲突；新增、移动、删除冲突请点击“结构冲突”。\r\n先在三方工具中编辑独立的结果文件，再明确确认应用；加载范围保持不变，结果留作待定更改，请回主窗口单独签入。\r\n中断时请保留会话中的原始文件及审核结果，先检查备份，再明确撤销受影响文件并重新预检。";

        internal PartialConflictForm(PlasticClient client, string root)
        {
            this.client = client; this.root = root;
            DialogStyle.Apply(this); Text = "Partial 传入冲突 - TortoiseSCM";
            Size = new Size(960, 660); MinimumSize = new Size(840, 550);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = root, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            refresh.Width = 145; top.Controls.Add(refresh); structure.Width = 115; top.Controls.Add(structure);
            top.Controls.Add(new Label { Text = "保留当前加载范围；不自动签入。", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
            layout.Controls.Add(top, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(900, 450),
                SplitterDistance = 270, Panel1MinSize = 120, Panel2MinSize = 110 };
            items.Dock = DockStyle.Fill; items.View = View.Details; items.MultiSelect = false; DialogStyle.ApplyList(items);
            items.AccessibleName = "Partial 传入内容冲突";
            items.Columns.Add("路径", 375); items.Columns.Add("已加载 cs", 90); items.Columns.Add("传入 cs", 90); items.Columns.Add("状态 / 原因", 310);
            items.SelectedIndexChanged += delegate { UpdateButtons(); };
            items.DoubleClick += async delegate { await PrepareAsync(); };
            split.Panel1.Controls.Add(items);
            details.Dock = DockStyle.Fill; details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical;
            details.Text = Guidance; details.AccessibleName = "操作说明与备份恢复信息";
            split.Panel2.Controls.Add(details); layout.Controls.Add(split, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft;
            layout.Controls.Add(status, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            prepare.Width = 115; apply.Width = 135; cancelPreparation.Width = 150;
            close.Click += delegate { Close(); }; CancelButton = close;
            footer.Controls.Add(close); footer.Controls.Add(apply); footer.Controls.Add(prepare); footer.Controls.Add(cancelPreparation);
            layout.Controls.Add(footer, 0, 4); Controls.Add(layout);
            refresh.Click += async delegate { await RefreshAsync(); };
            prepare.Click += async delegate { await PrepareAsync(); };
            apply.Click += async delegate { await ApplyAsync(); };
            cancelPreparation.Click += async delegate { await CancelPreparationAsync(); };
            structure.Click += async delegate {
                if (!structure.Enabled) return;
                using (var dialog = new PartialStructureForm(client, root)) dialog.ShowDialog(this);
                await RefreshAsync();
            };
            Shown += async delegate { await RefreshAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; else lifetime.Cancel(); };
            UpdateButtons();
        }

        private PlasticPartialConflict SelectedConflict()
        { return items.SelectedItems.Count == 1 ? items.SelectedItems[0].Tag as PlasticPartialConflict : null; }

        private bool IsPrepared(PlasticPartialConflict conflict)
        {
            return conflict != null && session != null && session.Conflicts.Any(item => item.RepositoryPath == conflict.RepositoryPath && !item.Resolved &&
                item.BaseChangeset == conflict.BaseChangeset && item.IncomingChangeset == conflict.IncomingChangeset && item.ItemId == conflict.ItemId);
        }

        private void UpdateButtons()
        {
            var selected = SelectedConflict();
            bool safe = session == null || (session.Ready && !session.Applying);
            refresh.Enabled = !busy;
            structure.Enabled = !busy;
            prepare.Enabled = !busy && safe && selected != null && selected.CanResolve && !selected.Resolved;
            apply.Enabled = prepare.Enabled && IsPrepared(selected);
            cancelPreparation.Enabled = !busy && session != null && safe && session.Conflicts.Count > 0 && !session.Conflicts.Any(item => item.Resolved);
            close.Enabled = !busy;
        }

        private void RenderConflicts()
        {
            string selected = SelectedConflict() == null ? null : SelectedConflict().RepositoryPath;
            items.Items.Clear();
            foreach (var conflict in conflicts)
            {
                string state = conflict.Resolved ? "已应用，待单独签入" : !conflict.CanResolve ? "不可处理：" + conflict.Reason :
                    IsPrepared(conflict) ? "已准备，待审核结果" : "待准备三方文件";
                var row = new ListViewItem(new[] { conflict.RepositoryPath, conflict.BaseChangeset.ToString(), conflict.IncomingChangeset.ToString(), state }) { Tag = conflict };
                items.Items.Add(row); if (conflict.RepositoryPath == selected) row.Selected = true;
            }
            status.Text = conflicts.Count + " 个项目 · " + conflicts.Count(item => !item.Resolved && item.CanResolve) + " 个待处理内容冲突";
            if (session != null && (!session.Ready || session.Applying))
            {
                status.Text = "上次应用未完成。请先检查会话备份；当前禁止继续应用和签入。";
                details.Text = "会话：" + session.SessionId + "\r\n恢复备份目录：" + session.RecoveryDirectory + "\r\n" + Guidance;
            }
            else if (session != null && conflicts.Count > 0 && conflicts.All(item => item.Resolved))
                status.Text = "准备的结果均已应用。关闭窗口，检查待定更改后单独签入。";
            else if (conflicts.Count == 0) status.Text = "没有检测到已加载文件的传入内容冲突。";
            UpdateButtons();
        }

        private async Task ReadStateAsync()
        {
            session = await client.GetPartialConflictSessionAsync(root, lifetime.Token);
            if (session != null && (!session.Ready || session.Applying))
                conflicts = session.Conflicts.ToList();
            else
            {
                var incoming = await client.PreviewPartialConflictsAsync(root, lifetime.Token);
                conflicts = CombineConflicts(incoming, session);
            }
            RenderConflicts();
        }

        private static IList<PlasticPartialConflict> CombineConflicts(IList<PlasticPartialConflict> incoming, PlasticPartialConflictSession savedSession)
        {
            var combined = incoming.ToList();
            // Fresh incoming identity and support status take priority over saved
            // resolutions, including a newer revision on a previously resolved file.
            if (savedSession != null)
                foreach (var saved in savedSession.Conflicts)
                    if (!combined.Any(item => item.RepositoryPath == saved.RepositoryPath)) combined.Add(saved);
            return combined;
        }

        private async Task RefreshAsync()
        {
            await WorkAsync(async delegate { prepared.Clear(); details.Text = Guidance; await ReadStateAsync(); });
        }

        private async Task PrepareAsync()
        {
            if (!prepare.Enabled) return;
            var conflict = SelectedConflict();
            await WorkAsync(async delegate {
                var files = await client.PreparePartialConflictAsync(root, conflict.RepositoryPath, lifetime.Token);
                prepared[conflict.RepositoryPath] = files;
                await ReadStateAsync();
                details.Text = "独立结果文件：" + files.ResultPath + "\r\n原始内容与传入版本备份：" + Path.GetDirectoryName(files.BasePath) + "\r\n\r\n" + Guidance;
                var result = await client.RunMergeToolAsync(files.BasePath, files.LocalPath, files.RemotePath, files.ResultPath, lifetime.Token);
                if (!result.Succeeded) throw new InvalidOperationException(result.Error + "\r\n" + result.Output);
                status.Text = "三方工具已关闭。请审核独立结果，再点击“确认应用结果”；尚未改变工作区文件。";
            });
        }

        private async Task ApplyAsync()
        {
            if (!apply.Enabled) return;
            var conflict = SelectedConflict();
            PlasticMergeConflictFiles files; prepared.TryGetValue(conflict.RepositoryPath, out files);
            using (var picker = new OpenFileDialog { Title = "选择已审核的 Partial 合并结果", CheckFileExists = true, FileName = files == null ? "" : files.ResultPath })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show(this, "将此文件更新到已确认的传入版本，再应用审核结果：\r\n" + conflict.RepositoryPath +
                    "\r\n版本：cs:" + conflict.BaseChangeset + " → cs:" + conflict.IncomingChangeset +
                    "\r\n\r\n结果：" + picker.FileName + "\r\n\r\n原始内容保存在会话备份中。只处理此文件，结果保留为待定更改，不自动签入。继续？",
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                await WorkAsync(async delegate {
                    // Never silently replace contributors after the result was reviewed.
                    // Resolve validates the existing preparation and rejects any drift.
                    details.Text = "原始内容与审核结果备份：" + session.RecoveryDirectory + "\r\n" + Guidance;
                    var result = await client.ResolvePartialConflictAsync(root, conflict.RepositoryPath, picker.FileName, lifetime.Token);
                    if (!result.Succeeded) throw new InvalidOperationException(result.Error + "\r\n" + result.Output);
                    await ReadStateAsync();
                });
            }
        }

        private async Task CancelPreparationAsync()
        {
            if (!cancelPreparation.Enabled) return;
            if (MessageBox.Show(this, "取消本会话中尚未应用的准备？\r\n工作区文件保持原样；已准备的备份与结果文件仍保留。",
                Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            await WorkAsync(async delegate {
                await client.CancelPartialConflictPreparationAsync(root, lifetime.Token); prepared.Clear(); await ReadStateAsync();
            });
        }

        private async Task WorkAsync(Func<Task> work)
        {
            if (busy) return;
            busy = true; UpdateButtons(); status.Text = "正在处理…";
            try
            {
                Exception failure = null;
                try { await work(); }
                catch (Exception error) { failure = error; }
                if (failure != null)
                {
                    // C# 5 requires awaits outside catch blocks. Re-read the durable
                    // interrupted marker before allowing any further mutation.
                    try { session = await client.GetPartialConflictSessionAsync(root, lifetime.Token); }
                    catch
                    {
                        if (session == null) session = new PlasticPartialConflictSession();
                        session.Ready = false;
                    }
                    if (session != null && (!session.Ready || session.Applying))
                    { conflicts = session.Conflicts.ToList(); RenderConflicts(); }
                    status.Text = "操作未完成。请核对错误和会话备份。";
                    details.Text = failure.Message + "\r\n\r\n" + details.Text;
                }
            }
            finally { busy = false; UpdateButtons(); }
        }
    }
}
