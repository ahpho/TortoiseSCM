// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class LocksForm : Form
    {
        private readonly PlasticClient client;
        private readonly string root;
        private readonly ListView items = new ListView();
        private readonly Label status = new Label();
        private readonly Button refresh = DialogStyle.Button("刷新");
        private readonly Button unlock = DialogStyle.Button("释放自己的锁…");
        private bool busy;

        public LocksForm(PlasticClient client, string root)
        {
            this.client = client; this.root = root;
            DialogStyle.Apply(this); Text = "锁管理 - TortoiseSCM";
            Size = new Size(920, 560); MinimumSize = new Size(740, 400);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.Controls.Add(new Label { Text = "锁属于当前仓库。文件签出时，仅在匹配服务器锁规则的情况下申请锁。\r\n只能释放当前用户在当前工作区持有的锁。", Dock = DockStyle.Fill }, 0, 0);
            items.Dock = DockStyle.Fill; items.View = View.Details; items.MultiSelect = false; DialogStyle.ApplyList(items);
            items.Columns.Add("路径", 330); items.Columns.Add("持有人", 155); items.Columns.Add("工作区", 170); items.Columns.Add("状态", 120);
            items.SelectedIndexChanged += delegate { UpdateButtons(); }; layout.Controls.Add(items, 0, 1);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 2);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var close = DialogStyle.Button("关闭"); close.Click += delegate { Close(); }; CancelButton = close;
            unlock.Width = 135; footer.Controls.Add(close); footer.Controls.Add(unlock); footer.Controls.Add(refresh);
            layout.Controls.Add(footer, 0, 3); Controls.Add(layout);
            refresh.Click += async delegate { await RefreshAsync(); };
            unlock.Click += async delegate { await UnlockAsync(); };
            Shown += async delegate { await RefreshAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) e.Cancel = true; };
            UpdateButtons();
        }

        private void UpdateButtons()
        { refresh.Enabled = !busy; unlock.Enabled = !busy && items.SelectedItems.Count == 1 && ((PlasticLockItem)items.SelectedItems[0].Tag).CanUnlock; }

        private async Task RefreshAsync()
        {
            if (busy) return;
            busy = true; UpdateButtons(); status.Text = "正在读取锁…";
            try { await LoadAsync(); }
            catch (Exception ex) { items.Items.Clear(); status.Text = ex.Message; }
            finally { busy = false; UpdateButtons(); }
        }

        private async Task LoadAsync()
        {
            var locks = await client.GetLocksAsync(root, CancellationToken.None);
            items.Items.Clear();
            foreach (var item in locks) items.Items.Add(new ListViewItem(new[] { item.Path, item.Owner, item.Workspace, item.Status }) { Tag = item });
            status.Text = locks.Count + " 个锁 · " + root;
        }

        private async Task UnlockAsync()
        {
            if (!unlock.Enabled) return;
            var item = (PlasticLockItem)items.SelectedItems[0].Tag;
            if (MessageBox.Show(this, "释放当前用户、当前工作区的锁？\r\n" + item.Path, Text, MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            busy = true; UpdateButtons();
            try
            {
                var result = await client.UnlockOwnAsync(root, item.LockId, CancellationToken.None);
                if (!result.Succeeded) throw new InvalidOperationException(result.Error + "\r\n" + result.Output);
                await LoadAsync();
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; UpdateButtons(); }
        }
    }
}
