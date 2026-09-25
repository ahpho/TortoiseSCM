// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class DirectoryConflictForm : Form
    {
        private readonly ComboBox choices = new ComboBox();
        private readonly TextBox rename = new TextBox();
        private readonly Label explanation = new Label();
        private readonly IList<string> options;
        internal string Resolution { get { return choices.SelectedIndex < 0 ? null : options[choices.SelectedIndex]; } }
        internal string Rename { get { return Resolution == "rename" ? rename.Text.Trim() : null; } }

        internal DirectoryConflictForm(string description, string sourcePath, string destinationPath, IEnumerable<string> supported)
        {
            options = supported.Where(option => option == "src" || option == "dst" || option == "rename").Distinct().ToList();
            DialogStyle.Apply(this); Text = "解决目录结构冲突 - TortoiseSCM";
            Size = new Size(740, 430); MinimumSize = new Size(640, 380);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.Controls.Add(new TextBox { ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
                Text = description + "\r\n\r\n来源（传入分支）：" + sourcePath + "\r\n目标（当前工作区）：" + destinationPath }, 0, 0);
            choices.DropDownStyle = ComboBoxStyle.DropDownList; choices.Dock = DockStyle.Fill; choices.AccessibleName = "结构冲突处理方式";
            foreach (string option in options) choices.Items.Add(option == "src" ? "采用来源的结构更改" : option == "dst" ? "保留目标的结构更改" : "重命名目标，保留双方");
            layout.Controls.Add(choices, 0, 1);
            var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120)); nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            nameRow.Controls.Add(new Label { Text = "目标的新名称：", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 0);
            rename.Dock = DockStyle.Fill; rename.Enabled = false; rename.AccessibleName = "保留目标时的新名称"; nameRow.Controls.Add(rename, 1, 0); layout.Controls.Add(nameRow, 0, 2);
            explanation.Dock = DockStyle.Fill; layout.Controls.Add(explanation, 0, 3);
            layout.Controls.Add(new Label { Text = "此处记录选择；所有结构冲突处理后，需单独点击“应用结构方案”。", Dock = DockStyle.Fill }, 0, 4);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var cancel = DialogStyle.Button("取消"); cancel.DialogResult = DialogResult.Cancel; CancelButton = cancel;
            var accept = DialogStyle.Button("记录选择"); accept.Enabled = false;
            accept.Click += delegate { if (Resolution == "rename" && String.IsNullOrWhiteSpace(Rename)) { rename.Focus(); return; } DialogResult = DialogResult.OK; Close(); };
            choices.SelectedIndexChanged += delegate {
                rename.Enabled = Resolution == "rename"; accept.Enabled = Resolution != null;
                explanation.Text = Resolution == "src" ? "发生冲突的目标结构更改将被放弃。请核对是否会删除或替换当前分支的文件。" :
                    Resolution == "dst" ? "发生冲突的来源结构更改将被放弃。其他没有冲突的来源更改仍会合并。" :
                    "仅输入新的文件或目录名称。目标项将更名，来源项保持其路径；内容冲突仍需另外解决。";
            };
            buttons.Controls.Add(cancel); buttons.Controls.Add(accept); layout.Controls.Add(buttons, 0, 5); Controls.Add(layout);
            AcceptButton = accept;
        }
    }
}
