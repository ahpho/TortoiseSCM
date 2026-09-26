// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class HistoricalFileForm : Form
    {
        private readonly PlasticClient client;
        private readonly string workspacePath;
        private readonly string repositoryPath;
        private readonly NumericUpDown fromRevision = new NumericUpDown();
        private readonly NumericUpDown toRevision = new NumericUpDown();
        private readonly TextBox preview = new TextBox();
        private readonly Label status = new Label();
        private readonly FlowLayoutPanel buttons = new FlowLayoutPanel();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool busy;
        private bool revisionsEdited;

        public HistoricalFileForm(PlasticClient client, string workspacePath, string repositoryPath, long changeset)
            : this(client, workspacePath, repositoryPath, changeset, null) { }

        public HistoricalFileForm(PlasticClient client, string workspacePath, string repositoryPath, long changeset, long? fromChangeset)
        {
            this.client = client; this.workspacePath = workspacePath; this.repositoryPath = repositoryPath;
            DialogStyle.Apply(this);
            Text = "历史文件 - TortoiseSCM";
            Size = new Size(920, 650); MinimumSize = new Size(750, 470);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new Label { Text = repositoryPath, UseMnemonic = false, Dock = DockStyle.Fill, AutoEllipsis = true }, 0, 0);
            var revisions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
            fromRevision.Maximum = toRevision.Maximum = Int64.MaxValue;
            fromRevision.Value = fromChangeset ?? changeset; toRevision.Value = changeset;
            revisionsEdited = fromChangeset.HasValue;
            fromRevision.ValueChanged += delegate { revisionsEdited = true; };
            toRevision.ValueChanged += delegate { revisionsEdited = true; };
            fromRevision.Width = toRevision.Width = 130;
            fromRevision.AccessibleName = "比较起始变更集"; toRevision.AccessibleName = "比较目标和导出变更集";
            revisions.Controls.Add(new Label { Text = "从 cs:", AutoSize = true, Padding = new Padding(0, 4, 0, 0) });
            revisions.Controls.Add(fromRevision);
            revisions.Controls.Add(new Label { Text = "到 cs:", AutoSize = true, Padding = new Padding(10, 4, 0, 0) });
            revisions.Controls.Add(toRevision);
            layout.Controls.Add(revisions, 0, 1);
            preview.Multiline = true; preview.ReadOnly = true; preview.WordWrap = false;
            preview.ScrollBars = ScrollBars.Both; preview.Dock = DockStyle.Fill;
            preview.Font = new Font("Consolas", 10F);
            preview.Text = "选择两个变更集以比较同一路径的历史内容。\r\n导出使用右侧“到 cs”版本；重命名前的文件请从旧路径的历史记录打开。";
            layout.Controls.Add(preview, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true;
            layout.Controls.Add(status, 0, 3);
            buttons.Dock = DockStyle.Fill; buttons.FlowDirection = FlowDirection.RightToLeft; buttons.WrapContents = false;
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); }; CancelButton = close;
            var export = DialogStyle.Button("导出版本…"); export.Width = 105;
            export.Click += async delegate { await ExportAsync(); };
            var external = DialogStyle.Button("外部工具比较"); external.Width = 115;
            external.Click += async delegate { await CompareAsync(true); };
            var compare = DialogStyle.Button("比较"); compare.Click += async delegate { await CompareAsync(false); };
            buttons.Controls.Add(close); buttons.Controls.Add(export); buttons.Controls.Add(external); buttons.Controls.Add(compare);
            layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
            Shown += async delegate { if (!revisionsEdited) await SuggestEarlierRevisionAsync(changeset); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; else lifetime.Cancel(); };
        }

        private async Task SuggestEarlierRevisionAsync(long selected)
        {
            if (revisionsEdited) return;
            try
            {
                string local = Path.Combine(client.DiscoverWorkspace(workspacePath).RootPath, repositoryPath.TrimStart('/').Replace('/', '\\'));
                var history = await client.GetHistoryAsync(local, lifetime.Token);
                if (lifetime.IsCancellationRequested || busy || revisionsEdited || fromRevision.Value != selected) return;
                var previous = history.Where(item => item.Changeset < selected).OrderByDescending(item => item.Changeset).FirstOrDefault();
                if (previous != null) { fromRevision.Value = previous.Changeset; status.Text = "已选择较早的文件版本，可修改两个编号。"; }
                else status.Text = "未找到更早的文件版本，请指定比较编号。";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!lifetime.IsCancellationRequested && !busy && !revisionsEdited) status.Text = "可手动输入变更集编号。" + ex.Message; }
        }

        private async Task CompareAsync(bool external)
        {
            if (busy) return;
            SetBusy(true); status.Text = "正在读取历史内容…";
            try
            {
                if (external)
                {
                    var result = await client.OpenRevisionDiffToolAsync(workspacePath, repositoryPath, (long)fromRevision.Value, (long)toRevision.Value, lifetime.Token);
                    status.Text = result.Succeeded ? "差异工具操作完成。" : "比较失败：" + result.Error;
                    if (!result.Succeeded) preview.Text = result.Output + "\r\n" + result.Error;
                }
                else
                {
                    var diff = await client.GetRevisionDiffAsync(workspacePath, repositoryPath, (long)fromRevision.Value, (long)toRevision.Value, lifetime.Token);
                    preview.Text = diff.HasChanges ? diff.DiffText : "两个版本的文件内容相同。";
                    status.Text = diff.IsBinary ? "二进制文件；可使用外部工具或分别导出。" : "比较完成。";
                }
            }
            catch (Exception ex) { status.Text = "比较失败。"; preview.Text = ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task ExportAsync()
        {
            if (busy) return;
            using (var picker = new SaveFileDialog { FileName = Path.GetFileName(repositoryPath), OverwritePrompt = true, Title = "导出 cs:" + toRevision.Value })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true); status.Text = "正在导出…";
                try
                {
                    var result = await client.ExportRevisionAsync(workspacePath, repositoryPath, (long)toRevision.Value, picker.FileName, true, lifetime.Token);
                    status.Text = result.Succeeded ? "已导出：" + picker.FileName : "导出失败：" + result.Error;
                }
                catch (Exception ex) { status.Text = "导出失败。"; preview.Text = ex.Message; }
                finally { SetBusy(false); }
            }
        }

        private void SetBusy(bool value)
        { busy = value; buttons.Enabled = fromRevision.Enabled = toRevision.Enabled = !value; }
    }
}
