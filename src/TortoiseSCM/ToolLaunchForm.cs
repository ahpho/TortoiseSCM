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
            DialogStyle.Apply(this);
            Text = "三方合并 - TortoiseSCM";
            ClientSize = new Size(700, 310);
            MinimumSize = new Size(680, 345);
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(DialogStyle.Margin), ColumnCount = 1, RowCount = 3 };
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 182));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var group = new GroupBox { Text = "选择要合并的文件", Dock = DockStyle.Fill, Padding = new Padding(10, 20, 10, 8), Margin = new Padding(0) };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
            var fields = new TextBox[4];
            string[] labels = { "共同基线", "本地文件", "远端文件", "合并结果" };
            for (int i = 0; i < 4; i++)
            {
                int index = i;
                layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
                fields[i] = new TextBox { Dock = DockStyle.Fill, AccessibleName = labels[i] };
                layout.Controls.Add(new Label { Text = labels[i], AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, i);
                layout.Controls.Add(fields[i], 1, i);
                var browse = new Button { Text = "…", Dock = DockStyle.Top, Height = DialogStyle.ButtonHeight, AccessibleName = "浏览" + labels[i] };
                browse.Click += delegate
                {
                    using (FileDialog dialog = index == 3 ? (FileDialog)new SaveFileDialog() : new OpenFileDialog())
                    { if (dialog.ShowDialog(this) == DialogResult.OK) fields[index].Text = dialog.FileName; }
                };
                layout.Controls.Add(browse, 2, i);
            }
            group.Controls.Add(layout); outer.Controls.Add(group, 0, 0);
            var result = new Label { Text = "结果写入指定文件，不会自动标记 Plastic 冲突已解决。", Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0), AutoEllipsis = true };
            outer.Controls.Add(result, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            var close = DialogStyle.Button("关闭"); close.DialogResult = DialogResult.Cancel;
            var run = DialogStyle.Button("合并");
            buttons.Controls.Add(close); buttons.Controls.Add(run); outer.Controls.Add(buttons, 0, 2);
            run.Click += async delegate
            {
                if (MessageBox.Show(this, "合并工具可能覆盖结果文件：\r\n" + fields[3].Text + "\r\n\r\n继续？", Text,
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
                running = true;
                layout.Enabled = buttons.Enabled = false;
                try
                {
                    var operation = await client.RunMergeToolAsync(fields[0].Text, fields[1].Text, fields[2].Text, fields[3].Text, CancellationToken.None);
                    result.Text = operation.Succeeded ? "合并工具已正常退出。请检查结果文件。" : "合并工具未成功：" + operation.Error;
                }
                catch (Exception ex) { result.Text = ex.Message; }
                finally { running = false; layout.Enabled = buttons.Enabled = true; }
            };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { e.Cancel = running; };
            Controls.Add(outer);
            AcceptButton = run; CancelButton = close;
        }
    }
}
