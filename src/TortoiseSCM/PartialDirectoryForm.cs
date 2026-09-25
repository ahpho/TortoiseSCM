// GPL-2.0-or-later. Explicit recursive scope review for Partial directory conflicts.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class PartialDirectoryForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly ListView directories = new ListView();
        private readonly ListView descendants = new ListView();
        private readonly TextBox details = new TextBox();
        private readonly ComboBox resolution = new ComboBox();
        private readonly Label status = new Label();
        private readonly Button refresh = DialogStyle.Button("预检 / 恢复会话");
        private readonly Button prepare = DialogStyle.Button("准备整树备份");
        private readonly Button apply = DialogStyle.Button("应用处理…");
        private readonly Button cancel = DialogStyle.Button("取消准备");
        private readonly Button recover = DialogStyle.Button("恢复为传入版本…");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private IList<PlasticPartialDirectoryConflict> conflicts = new List<PlasticPartialDirectoryConflict>();
        private PlasticPartialDirectorySession session;
        private bool busy;
        private string scopeDetails = "";

        internal PartialDirectoryForm(PlasticClient client, string root)
        {
            this.client = client; this.root = root;
            DialogStyle.Apply(this); Text = "Partial 目录冲突 - TortoiseSCM";
            Size = new Size(1040, 760); MinimumSize = new Size(920, 650);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 7 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 28, 34 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (int height in new[] { 90, 46, 28, 34 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = root, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false }, 0, 0);
            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            refresh.Width = 145; top.Controls.Add(refresh);
            top.Controls.Add(new Label { Text = "处理范围包含整棵目录；先核对下方全部受影响项，再准备备份。", AutoSize = true, Padding = new Padding(8, 5, 0, 0) });
            layout.Controls.Add(top, 0, 1);
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, Size = new Size(970, 400), SplitterDistance = 160, Panel1MinSize = 90, Panel2MinSize = 100 };
            directories.Dock = descendants.Dock = DockStyle.Fill;
            directories.View = descendants.View = View.Details; directories.MultiSelect = false;
            DialogStyle.ApplyList(directories); DialogStyle.ApplyList(descendants);
            directories.AccessibleName = "Partial 目录冲突列表"; descendants.AccessibleName = "目录及全部后代影响清单";
            directories.Columns.Add("目录路径", 300); directories.Columns.Add("传入操作", 125); directories.Columns.Add("传入位置", 290); directories.Columns.Add("处理能力", 120);
            descendants.Columns.Add("原路径", 315); descendants.Columns.Add("处理后路径", 315); descendants.Columns.Add("类型", 70); descendants.Columns.Add("本地修改", 100);
            directories.SelectedIndexChanged += delegate { ShowScope(); UpdateButtons(); };
            split.Panel1.Controls.Add(directories); split.Panel2.Controls.Add(descendants); layout.Controls.Add(split, 0, 2);
            details.Dock = DockStyle.Fill; details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Vertical;
            layout.Controls.Add(details, 0, 3);
            var choices = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 5, 0, 0) };
            choices.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85)); choices.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            choices.Controls.Add(new Label { Text = "处理方式：", AutoSize = true, Padding = new Padding(0, 3, 0, 0) }, 0, 0);
            resolution.DropDownStyle = ComboBoxStyle.DropDownList; resolution.Dock = DockStyle.Fill; resolution.AccessibleName = "目录冲突处理方式";
            resolution.SelectedIndexChanged += delegate { ShowResolutionImpact(); UpdateButtons(); }; choices.Controls.Add(resolution, 1, 0); layout.Controls.Add(choices, 0, 4);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 5);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            prepare.Width = 120; recover.Width = 160; cancel.Width = 100; apply.Width = 110;
            foreach (var button in new[] { close, apply, prepare, cancel, recover }) footer.Controls.Add(button);
            layout.Controls.Add(footer, 0, 6); Controls.Add(layout); CancelButton = close;
            close.Click += delegate { Close(); };
            refresh.Click += async delegate { await WorkAsync(ReadStateAsync); };
            prepare.Click += async delegate {
                if (!prepare.Enabled) return; var selected = Selected();
                await WorkAsync(async delegate { session = await client.PreparePartialDirectoryAsync(root, selected.RepositoryPath, lifetime.Token); Render(); });
            };
            apply.Click += async delegate {
                if (!apply.Enabled) return;
                string choice = ((Choice)resolution.SelectedItem).Value;
                if (!Confirm("处理下列目录及清单中的全部后代？\r\n" + session.Conflict.RepositoryPath + "\r\n" + resolution.Text + "\r\n" + ResolutionDescription(session.Conflict, choice) + "\r\n\r\n备份：" + session.RecoveryDirectory + "\r\n处理不会自动提交。")) return;
                await WorkAsync(async delegate { CheckResult(await client.ResolvePartialDirectoryAsync(root, choice, lifetime.Token)); await ReadStateAsync(); });
            };
            cancel.Click += async delegate {
                if (!cancel.Enabled || !Confirm("取消尚未应用的目录准备？工作区不变，整树备份保留。")) return;
                await WorkAsync(async delegate { await client.CancelPartialDirectoryAsync(root, lifetime.Token); await ReadStateAsync(); });
            };
            recover.Click += async delegate {
                if (!recover.Enabled || !Confirm("将未完成的目录处理恢复为会话中的传入版本？\r\n" + (session.Conflict.Kind == "incoming-directory-delete" ? "服务器已删除此目录，恢复会移除本次重新创建的目录树。\r\n" : "") + "已知路径的新编辑会先另存备份；新出现的其他文件会阻止恢复。\r\n\r\n备份：" + session.RecoveryDirectory)) return;
                await WorkAsync(async delegate { CheckResult(await client.RecoverPartialDirectoryAsync(root, lifetime.Token)); await ReadStateAsync(); });
            };
            Shown += async delegate { await WorkAsync(ReadStateAsync); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; else lifetime.Cancel(); };
            UpdateButtons();
        }
        private bool Confirm(string text) { return MessageBox.Show(this, text, Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK; }
        private PlasticPartialDirectoryConflict Selected() { return directories.SelectedItems.Count == 1 ? directories.SelectedItems[0].Tag as PlasticPartialDirectoryConflict : null; }
        private PlasticPartialDirectoryConflict Current() { return session == null ? Selected() : session.Conflict; }
        private sealed class Choice { internal string Value, Text; public override string ToString() { return Text; } }
        private void ShowScope()
        {
            scopeDetails = ""; descendants.Items.Clear(); resolution.Items.Clear(); resolution.SelectedIndex = -1;
            var conflict = Current();
            if (conflict == null) { details.Text = "请选择目录查看完整影响清单。"; return; }
            foreach (var item in conflict.Items)
                descendants.Items.Add(new ListViewItem(new[] { item.RepositoryPath, String.IsNullOrEmpty(item.IncomingPath) ? "（删除）" : item.IncomingPath, item.IsDirectory ? "目录" : "文件", item.HasLocalChanges ? "有修改" : "" }));
            foreach (string option in conflict.ResolutionOptions)
                resolution.Items.Add(new Choice { Value = option, Text = option == "take-incoming" ? (conflict.Kind == "incoming-directory-delete" ? "采用服务器删除（本地内容保留在备份）" : "采用服务器目录与内容（本地内容保留在备份）") :
                    conflict.Kind == "incoming-directory-move" ? "跟随服务器目录位置，保留本地已修改文件的内容" : "在原路径重新添加本地目录树，作为新项待提交" });
            scopeDetails = "传入 cs:" + conflict.IncomingChangeset + "；范围内 " + conflict.Items.Count + " 项，含 " + conflict.Items.Count(item => item.HasLocalChanges) + " 项本地修改。\r\n" + conflict.Reason +
                (session == null ? "\r\n准备不会修改工作区。" : "\r\n恢复备份：" + session.RecoveryDirectory);
            ShowResolutionImpact();
        }
        private static string ResolutionDescription(PlasticPartialDirectoryConflict conflict, string choice)
        {
            if (conflict.Kind == "incoming-directory-delete")
                return choice == "keep-local" ? "按备份在原路径重新添加全部文件和空目录，使用新的版本控制身份；旧历史仍属已删除项。请在待提交界面检查后另行提交。" :
                    "采用服务器删除，移除目录树；本地原始内容保留在备份中。";
            return choice == "keep-local" ? "跟随服务器位置；本地已修改文件使用备份内容，未修改文件采用传入版本。" : "采用服务器目录位置和传入内容。";
        }
        private void ShowResolutionImpact()
        {
            var conflict = Current(); if (conflict == null) return;
            var selected = resolution.SelectedItem as Choice;
            bool keepDeleted = selected != null && selected.Value == "keep-local" && conflict.Kind == "incoming-directory-delete";
            descendants.Columns[1].Text = selected == null ? "服务器传入路径" : "处理后路径";
            for (int index = 0; index < descendants.Items.Count && index < conflict.Items.Count; index++)
            {
                var item = conflict.Items[index];
                descendants.Items[index].SubItems[1].Text = keepDeleted ? item.RepositoryPath + "（新添加）" : String.IsNullOrEmpty(item.IncomingPath) ? "（删除）" : item.IncomingPath;
            }
            details.Text = scopeDetails + (selected == null ? "" : "\r\n" + ResolutionDescription(conflict, selected.Value));
        }
        private void UpdateButtons()
        {
            var selected = Selected(); refresh.Enabled = close.Enabled = !busy;
            prepare.Enabled = !busy && session == null && selected != null && selected.ResolutionOptions.Count > 0;
            bool ready = !busy && session != null && session.Ready && !session.Applying;
            cancel.Enabled = resolution.Enabled = ready; apply.Enabled = ready && resolution.SelectedItem != null;
            recover.Enabled = !busy && session != null && (!session.Ready || session.Applying) && session.Conflict != null;
        }
        private void Render()
        {
            string selected = Selected() == null ? null : Selected().RepositoryPath;
            directories.Items.Clear();
            foreach (var conflict in session == null ? conflicts : new[] { session.Conflict })
            {
                if (conflict == null) continue;
                var row = new ListViewItem(new[] { conflict.RepositoryPath, conflict.Kind == "incoming-directory-move" ? "服务器移动" : "服务器删除", conflict.IncomingPath,
                    conflict.ResolutionOptions.Count > 0 ? "可处理" : "暂不支持" }) { Tag = conflict };
                directories.Items.Add(row); row.Selected = session != null || conflict.RepositoryPath == selected;
            }
            status.Text = session == null ? "发现 " + conflicts.Count + " 个目录变化；只处理已核验的完整加载目录。" : session.Ready && !session.Applying ? "整树备份已准备；请明确选择处理方式。" : "上次处理未完成；其他修改操作已阻止，请先恢复。";
            ShowScope(); UpdateButtons();
        }
        private async Task ReadStateAsync()
        {
            session = await client.GetPartialDirectorySessionAsync(root, lifetime.Token);
            if (session == null) conflicts = await client.PreviewPartialDirectoriesAsync(root, lifetime.Token);
            Render();
        }
        private static void CheckResult(PlasticCommandResult result) { if (!result.Succeeded) throw new InvalidOperationException(result.Error + "\r\n" + result.Output); }
        private async Task WorkAsync(Func<Task> work)
        {
            if (busy) return; busy = true; UpdateButtons(); status.Text = "正在核对目录…";
            try
            {
                Exception failure = null; try { await work(); } catch (Exception error) { failure = error; }
                if (failure != null)
                {
                    try { session = await client.GetPartialDirectorySessionAsync(root, lifetime.Token); }
                    catch { if (session == null) session = new PlasticPartialDirectorySession(); session.Ready = false; }
                    Render(); details.Text = failure.Message + "\r\n\r\n" + details.Text; status.Text = "操作未完成，请检查说明和备份。";
                }
            }
            finally { busy = false; UpdateButtons(); }
        }
    }
}
