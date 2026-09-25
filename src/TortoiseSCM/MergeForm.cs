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
    internal sealed class MergeForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly NumericUpDown source = new NumericUpDown();
        private readonly ListView items = new ListView();
        private readonly TextBox details = new TextBox();
        private readonly Label status = new Label();
        private readonly Button preview = DialogStyle.Button("预检");
        private readonly Button start = DialogStyle.Button("开始合并…");
        private readonly Button prepare = DialogStyle.Button("三方合并…");
        private readonly Button apply = DialogStyle.Button("确认解决…");
        private readonly Button resume = DialogStyle.Button("刷新会话");
        private readonly Button directory = DialogStyle.Button("结构冲突…");
        private readonly Button continueMerge = DialogStyle.Button("应用结构方案…");
        private readonly Button cancelPlan = DialogStyle.Button("取消结构方案");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Dictionary<string, PlasticMergeConflictFiles> prepared = new Dictionary<string, PlasticMergeConflictFiles>(StringComparer.Ordinal);
        private PlasticMergePlan plan;
        private PlasticMergeSession session;
        private bool busy;

        public MergeForm(PlasticClient client, string root)
        {
            this.client = client; this.root = root;
            DialogStyle.Apply(this); Text = "合并 - TortoiseSCM";
            Size = new Size(940, 650); MinimumSize = new Size(820, 520);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(new Label { Text = root, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            top.Controls.Add(new Label { Text = "合并来源 cs:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
            source.Maximum = Int64.MaxValue; source.Width = 140; source.AccessibleName = "合并来源变更集";
            source.ValueChanged += delegate { if (session == null) { plan = null; items.Items.Clear(); UpdateButtons(); } };
            top.Controls.Add(source); top.Controls.Add(preview); top.Controls.Add(start); top.Controls.Add(resume); cancelPlan.Width = 110; top.Controls.Add(cancelPlan);
            layout.Controls.Add(top, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(880, 430), SplitterDistance = 285, Panel1MinSize = 100, Panel2MinSize = 65 };
            items.Dock = DockStyle.Fill; items.View = View.Details; items.MultiSelect = false; DialogStyle.ApplyList(items);
            items.Columns.Add("路径", 535); items.Columns.Add("操作 / 冲突状态", 300);
            items.SelectedIndexChanged += delegate { UpdateButtons(); };
            items.DoubleClick += async delegate { if (directory.Enabled) await ResolveDirectoryAsync(); else await PrepareAsync(); };
            split.Panel1.Controls.Add(items);
            details.Dock = DockStyle.Fill; details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical;
            details.Text = "将指定变更集合并到当前分支。开始前工作区必须干净。\r\n目录结构冲突先逐项选择来源、目标或重命名，再应用结构方案。\r\n文件内容冲突需使用三方工具编辑，然后单独确认解决；完成后回到待定更改界面提交。";
            split.Panel2.Controls.Add(details); layout.Controls.Add(split, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 3);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); }; CancelButton = close;
            prepare.Width = apply.Width = continueMerge.Width = 115; directory.Width = 105;
            footer.Controls.Add(close); footer.Controls.Add(apply); footer.Controls.Add(prepare); footer.Controls.Add(continueMerge); footer.Controls.Add(directory); layout.Controls.Add(footer, 0, 4);
            Controls.Add(layout);
            preview.Click += async delegate { await WorkAsync(async delegate { plan = await client.PreviewMergeAsync(root, (long)source.Value, lifetime.Token); RenderPlan(); }); };
            start.Click += async delegate { await StartAsync(); };
            resume.Click += async delegate { await RefreshSessionAsync(); };
            prepare.Click += async delegate { await PrepareAsync(); };
            apply.Click += async delegate { await ApplyAsync(); };
            directory.Click += async delegate { await ResolveDirectoryAsync(); };
            continueMerge.Click += async delegate { await ContinueAsync(); };
            cancelPlan.Click += async delegate {
                if (!cancelPlan.Enabled) return;
                if (MessageBox.Show(this, "取消已记录的结构选择？工作区文件不会改变。", Text, MessageBoxButtons.OKCancel,
                    MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                await WorkAsync(async delegate { await client.CancelDirectoryMergeAsync(root, lifetime.Token); session = null; plan = null; RenderPlan(); });
            };
            Shown += async delegate { await RefreshSessionAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; else lifetime.Cancel(); };
            UpdateButtons();
        }

        private PlasticMergeConflict SelectedConflict()
        { return items.SelectedItems.Count == 1 ? items.SelectedItems[0].Tag as PlasticMergeConflict : null; }

        private void UpdateButtons()
        {
            var selected = SelectedConflict();
            source.Enabled = preview.Enabled = !busy && session == null;
            resume.Enabled = !busy;
            start.Enabled = !busy && session == null && plan != null && !plan.AlreadyConnected;
            bool planning = session != null && session.AwaitingDirectoryResolution;
            cancelPlan.Enabled = !busy && planning;
            var structure = items.SelectedItems.Count == 1 ? items.SelectedItems[0].Tag as PlasticDirectoryConflict : null;
            directory.Enabled = !busy && planning && structure != null && !structure.Resolved;
            continueMerge.Enabled = !busy && planning && plan.DirectoryConflicts.All(item => item.Resolved);
            prepare.Enabled = apply.Enabled = !busy && session != null && !planning && selected != null && !selected.Resolved;
        }

        private void RenderPlan()
        {
            items.Items.Clear();
            if (plan == null) { status.Text = "当前没有 TortoiseSCM 合并会话。"; UpdateButtons(); return; }
            foreach (var op in plan.Operations) items.Items.Add(new ListViewItem(new[] { op.Path + (String.IsNullOrEmpty(op.DestinationPath) ? "" : " → " + op.DestinationPath), op.Kind }));
            foreach (var conflict in plan.FileConflicts) items.Items.Add(new ListViewItem(new[] { conflict.RepositoryPath, conflict.Resolved ? "文件冲突：已确认解决" : "文件冲突：待解决" }) { Tag = conflict });
            foreach (var conflict in plan.DirectoryConflicts) items.Items.Add(new ListViewItem(new[] { conflict.SourcePath + " ↔ " + conflict.DestinationPath,
                (conflict.Resolved ? "已选 " + conflict.Resolution + (String.IsNullOrEmpty(conflict.Rename) ? "" : " → " + conflict.Rename) + "：" : "目录冲突：") + conflict.Kind + " — " + conflict.Description }) { Tag = conflict });
            status.Text = "cs:" + plan.SourceChangeset + " → cs:" + plan.DestinationChangeset + " · " + plan.FileConflicts.Count(c => !c.Resolved) + " 个待解决文件冲突";
            if (session != null && session.AwaitingDirectoryResolution) status.Text = "请逐项记录结构冲突的处理方式，然后应用结构方案。尚未更改工作区文件。";
            else if (session == null && plan.DirectoryConflicts.Count > 0) status.Text = "存在结构冲突。点击“开始合并”建立处理会话，再选择冲突处理方式。";
            else if (session != null && session.IsRollback) status.Text = "整仓回滚已准备（目标 cs:" + plan.BaseChangeset + "）。关闭窗口，检查更改后提交整个工作区。";
            else if (plan.AlreadyConnected) status.Text = "来源已合并。请检查待定更改，按需提交。";
            else if (session != null && plan.FileConflicts.All(c => c.Resolved)) status.Text = "合并处理完成。关闭此窗口，检查待定更改并提交。";
            UpdateButtons();
        }

        private async Task RefreshSessionAsync()
        {
            await WorkAsync(async delegate {
                session = await client.GetMergeSessionAsync(root, lifetime.Token);
                plan = session == null ? null : session.Plan;
                if (plan != null) source.Value = plan.SourceChangeset;
                RenderPlan();
            });
        }

        private async Task StartAsync()
        {
            if (!start.Enabled) return;
            if (MessageBox.Show(this, "将 cs:" + source.Value + " 合并到整个工作区：\r\n" + root + "\r\n\r\n这会产生待定更改。继续？", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            await WorkAsync(async delegate {
                session = await client.BeginMergeAsync(root, (long)source.Value, lifetime.Token);
                plan = session.Plan; prepared.Clear(); RenderPlan();
            });
        }

        private async Task PrepareAsync()
        {
            if (!prepare.Enabled) return;
            var conflict = SelectedConflict();
            await WorkAsync(async delegate {
                var files = await client.PrepareMergeConflictAsync(root, session.Plan.SourceChangeset, conflict.RepositoryPath, lifetime.Token);
                prepared[conflict.RepositoryPath] = files;
                details.Text = "结果文件：" + files.ResultPath + "\r\n工具关闭后请检查结果，再点击“确认解决”。";
                var result = await client.RunMergeToolAsync(files.BasePath, files.LocalPath, files.RemotePath, files.ResultPath, lifetime.Token);
                if (!result.Succeeded) throw new InvalidOperationException(result.Error + "\r\n" + result.Output);
                status.Text = "合并工具已关闭。确认结果后请点击“确认解决”；当前冲突尚未标记解决。";
            });
        }

        private async Task ResolveDirectoryAsync()
        {
            if (!directory.Enabled) return;
            var conflict = (PlasticDirectoryConflict)items.SelectedItems[0].Tag;
            using (var dialog = new DirectoryConflictForm(conflict.Description,
                conflict.SourceOperation + " " + (String.IsNullOrEmpty(conflict.SourceOriginalPath) ? "" : conflict.SourceOriginalPath + " → ") + conflict.SourcePath,
                conflict.DestinationOperation + " " + (String.IsNullOrEmpty(conflict.DestinationOriginalPath) ? "" : conflict.DestinationOriginalPath + " → ") + conflict.DestinationPath, conflict.ResolutionOptions))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                await WorkAsync(async delegate {
                    session = await client.ResolveDirectoryConflictAsync(root, session.Plan.SourceChangeset, conflict.Index, dialog.Resolution, dialog.Rename, lifetime.Token);
                    plan = session.Plan; RenderPlan();
                });
            }
        }

        private async Task ContinueAsync()
        {
            if (!continueMerge.Enabled) return;
            if (MessageBox.Show(this, "将按已记录的结构选择更新整个工作区。被舍弃一方的结构更改将不会保留。\r\n\r\n继续应用？", Text,
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            await WorkAsync(async delegate {
                session = await client.ContinueMergeAsync(root, session.Plan.SourceChangeset, lifetime.Token);
                plan = session.Plan; RenderPlan();
            });
        }

        private async Task ApplyAsync()
        {
            if (!apply.Enabled) return;
            var conflict = SelectedConflict();
            PlasticMergeConflictFiles files;
            prepared.TryGetValue(conflict.RepositoryPath, out files);
            using (var picker = new OpenFileDialog { Title = "选择已检查的合并结果", CheckFileExists = true,
                FileName = files == null ? "" : files.ResultPath })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (MessageBox.Show(this, "用此结果替换 " + conflict.RepositoryPath + " 并标记冲突已解决？\r\n" + picker.FileName,
                    Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                await WorkAsync(async delegate {
                    // Preparing establishes the native contributor state even when the result was edited elsewhere.
                    if (files == null) await client.PrepareMergeConflictAsync(root, session.Plan.SourceChangeset, conflict.RepositoryPath, lifetime.Token);
                    var result = await client.ApplyMergeFileResolutionAsync(root, session.Plan.SourceChangeset, conflict.RepositoryPath, picker.FileName, lifetime.Token);
                    if (!result.Succeeded) throw new InvalidOperationException(result.Error + "\r\n" + result.Output);
                    session = await client.GetMergeSessionAsync(root, lifetime.Token);
                    plan = session == null ? null : session.Plan; RenderPlan();
                });
            }
        }

        private async Task WorkAsync(Func<Task> work)
        {
            if (busy) return;
            busy = true; UpdateButtons(); status.Text = "正在处理…";
            try { await work(); }
            catch (Exception ex) { status.Text = "操作未完成。"; details.Text = ex.Message; }
            finally { busy = false; UpdateButtons(); }
        }
    }
}
