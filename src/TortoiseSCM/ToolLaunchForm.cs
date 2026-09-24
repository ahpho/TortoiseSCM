// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class ToolLaunchForm : Form
    {
        private bool running;
        public ToolLaunchForm(PlasticClient client)
        {
            Text = "TortoiseSCM — 外部三方合并";
            Font = new Font("Microsoft YaHei UI", 9F);
            Size = new Size(780, 370);
            MinimumSize = new Size(700, 370);
            StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 3, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
            var fields = new TextBox[4];
            string[] labels = { "共同基线", "本地文件", "远端文件", "合并结果" };
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
                fields[i] = new TextBox { Dock = DockStyle.Fill, AccessibleName = labels[i] };
                layout.Controls.Add(new Label { Text = labels[i], AutoSize = true }, 0, i);
                layout.Controls.Add(fields[i], 1, i);
                var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill };
                browse.Click += delegate
                {
                    using (FileDialog dialog = index == 3 ? (FileDialog)new SaveFileDialog() : new OpenFileDialog())
                    { if (dialog.ShowDialog(this) == DialogResult.OK) fields[index].Text = dialog.FileName; }
                };
                layout.Controls.Add(browse, 2, i);
            }
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var result = new Label { Text = "启动配置的合并工具；结果写入指定文件。此操作不自动标记 Plastic 冲突已解决。", Dock = DockStyle.Fill };
            layout.Controls.Add(result, 0, 4);
            layout.SetColumnSpan(result, 3);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            var run = new Button { Text = "开始合并…", Dock = DockStyle.Fill };
            layout.Controls.Add(run, 1, 5);
            run.Click += async delegate
            {
                if (MessageBox.Show(this, "合并工具可能覆盖结果文件：\r\n" + fields[3].Text + "\r\n\r\n继续？", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                running = true;
                layout.Enabled = false;
                try
                {
                    var operation = await client.RunMergeToolAsync(fields[0].Text, fields[1].Text, fields[2].Text, fields[3].Text, CancellationToken.None);
                    result.Text = operation.Succeeded ? "合并工具已正常退出。请检查结果文件。" : "合并工具未成功：" + operation.Error;
                }
                catch (Exception ex) { result.Text = ex.Message; }
                finally { running = false; layout.Enabled = true; }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { e.Cancel = running; };
            Controls.Add(layout);
        }
    }
}
