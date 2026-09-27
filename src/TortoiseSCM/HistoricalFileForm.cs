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
        private readonly string fromRepositoryPath;
        private string expectedRepository;
        private string expectedRoot;
        private bool fixedPair;
        private bool sourceExists = true;
        private bool targetExists = true;
        private readonly Button compare = DialogStyle.Button("比较");
        private readonly Button external = DialogStyle.Button("外部工具比较");
        private readonly Button export = DialogStyle.Button("导出目标版本…");
        private readonly Button exportSource = DialogStyle.Button("导出起点版本…");
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
            this.fromRepositoryPath = repositoryPath;
            var workspace = client.DiscoverWorkspace(workspacePath);
            if (workspace == null) throw new InvalidOperationException("找不到 Plastic 工作区。");
            expectedRepository = workspace.Repository; expectedRoot = workspace.RootPath;
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
            preview.Text = "选择两个变更集以比较同一路径的历史内容，并可分别导出起点和目标版本。\r\n跨重命名比较请从历史窗口的“比较整个仓库”列表打开移动项。";
            layout.Controls.Add(preview, 0, 2);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true;
            layout.Controls.Add(status, 0, 3);
            buttons.Dock = DockStyle.Fill; buttons.FlowDirection = FlowDirection.RightToLeft; buttons.WrapContents = false;
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); }; CancelButton = close;
            export.Width = exportSource.Width = 115;
            export.Click += async delegate { await ExportAsync(false); };
            exportSource.Click += async delegate { await ExportAsync(true); };
            external.Width = 115;
            external.Click += async delegate { await CompareAsync(true); };
            compare.Click += async delegate { await CompareAsync(false); };
            buttons.Controls.Add(close); buttons.Controls.Add(export); buttons.Controls.Add(exportSource); buttons.Controls.Add(external); buttons.Controls.Add(compare);
            layout.Controls.Add(buttons, 0, 4); Controls.Add(layout);
            Shown += async delegate { if (!revisionsEdited) await SuggestEarlierRevisionAsync(changeset); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; else lifetime.Cancel(); };
        }

        public HistoricalFileForm(PlasticClient client, string workspacePath, PlasticChangesetComparison comparison, PlasticChangesetFile file)
            : this(client, workspacePath, file.Path, comparison.ToChangeset, comparison.FromChangeset)
        {
            fromRepositoryPath = String.IsNullOrEmpty(file.OldPath) ? file.Path : file.OldPath;
            expectedRepository = comparison.Repository; expectedRoot = comparison.RootPath;
            fixedPair = true; sourceExists = file.Status != "A"; targetExists = file.Status != "D";
            preview.Text = sourceExists && targetExists ?
                "起点：" + fromRepositoryPath + " @ cs:" + comparison.FromChangeset + "\r\n目标：" + repositoryPath + " @ cs:" + comparison.ToChangeset + "\r\n选择比较或分别导出两个版本。" :
                sourceExists ? "此文件在目标版本中已删除。可导出起点版本的内容。" : "此文件为新增项。可导出目标版本的内容。";
            SetBusy(false);
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
            if (busy || !sourceExists || !targetExists) return;
            SetBusy(true); status.Text = "正在读取历史内容…";
            try
            {
                ValidateContext();
                if (external)
                {
                    var result = await client.OpenRevisionDiffToolAsync(workspacePath, fromRepositoryPath, repositoryPath, (long)fromRevision.Value, (long)toRevision.Value, lifetime.Token);
                    status.Text = result.Succeeded ? "差异工具操作完成。" : "比较失败：" + result.Error;
                    if (!result.Succeeded) preview.Text = result.Output + "\r\n" + result.Error;
                }
                else
                {
                    var diff = await client.GetRevisionDiffAsync(workspacePath, fromRepositoryPath, repositoryPath, (long)fromRevision.Value, (long)toRevision.Value, lifetime.Token);
                    ValidateContext();
                    // Native EDIT controls require CRLF to render unified-diff lines.
                    preview.Text = diff.HasChanges ? diff.DiffText.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n") : "两个版本的文件内容相同。";
                    status.Text = diff.IsBinary ? "二进制文件；可使用外部工具或分别导出。" : "比较完成。";
                }
            }
            catch (Exception ex) { status.Text = "比较失败。"; preview.Text = ex.Message; }
            finally { SetBusy(false); }
        }

        private async Task ExportAsync(bool source)
        {
            if (busy || (source ? !sourceExists : !targetExists)) return;
            string exportPath = source ? fromRepositoryPath : repositoryPath;
            long revision = (long)(source ? fromRevision.Value : toRevision.Value);
            using (var picker = new SaveFileDialog { FileName = Path.GetFileName(exportPath), OverwritePrompt = true, Title = "导出 cs:" + revision })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true); status.Text = "正在导出…";
                try
                {
                    ValidateContext();
                    var result = await client.ExportRevisionAsync(workspacePath, exportPath, revision, picker.FileName, true, lifetime.Token);
                    status.Text = result.Succeeded ? "已导出：" + picker.FileName : "导出失败：" + result.Error;
                }
                catch (Exception ex) { status.Text = "导出失败。"; preview.Text = ex.Message; }
                finally { SetBusy(false); }
            }
        }

        private void SetBusy(bool value)
        {
            busy = value; buttons.Enabled = !value; fromRevision.Enabled = toRevision.Enabled = !value && !fixedPair;
            compare.Enabled = external.Enabled = !value && sourceExists && targetExists;
            export.Enabled = !value && targetExists; exportSource.Enabled = !value && sourceExists;
        }

        private void ValidateContext()
        {
            var current = client.DiscoverWorkspace(workspacePath);
            if (current == null || current.Repository != expectedRepository ||
                !current.RootPath.TrimEnd('\\', '/').Equals(expectedRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区或仓库已改变，请关闭并重新打开比较窗口。");
        }
    }
}
