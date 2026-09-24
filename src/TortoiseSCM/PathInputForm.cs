// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class PathInputForm : Form
    {
        private readonly TextBox destination = new TextBox();
        internal string Destination { get { return destination.Text; } }
        internal PathInputForm(string source)
        {
            DialogStyle.Apply(this); Text = "重命名 / 移动 - TortoiseSCM";
            ClientSize = new Size(650, 175); MinimumSize = new Size(570, 214);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.Controls.Add(new Label { Text = "源：" + source + "\r\n在同一工作区内指定不存在的新路径；不会覆盖已有文件。", Dock = DockStyle.Fill, UseMnemonic = false }, 0, 0);
            layout.Controls.Add(new Label { Text = "目标完整路径 (&D)", Dock = DockStyle.Fill }, 0, 1);
            destination.Dock = DockStyle.Fill; destination.Text = source;
            layout.Controls.Add(destination, 0, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var cancel = DialogStyle.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
            var move = DialogStyle.Button("移动 / 重命名"); move.Width = 120;
            move.Click += delegate
            {
                if (String.IsNullOrWhiteSpace(destination.Text) || !Path.IsPathRooted(destination.Text))
                { MessageBox.Show(this, "请输入目标完整绝对路径。"); return; }
                DialogResult = DialogResult.OK;
            };
            buttons.Controls.Add(cancel); buttons.Controls.Add(move); layout.Controls.Add(buttons, 0, 3);
            Controls.Add(layout); AcceptButton = move; CancelButton = cancel;
        }
    }
}
