// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class PartialStructureChoiceForm : Form
    {
        private readonly ComboBox choices = new ComboBox();
        private readonly TextBox rename = new TextBox();
        private readonly Label explanation = new Label();
        private readonly IList<string> options;
        internal string Resolution { get { return choices.SelectedIndex < 0 ? null : options[choices.SelectedIndex]; } }
        internal string Rename { get { return Resolution == "rename" ? rename.Text.Trim() : null; } }
        internal string ChoiceText { get { return choices.SelectedIndex < 0 ? "" : choices.SelectedItem.ToString(); } }

        internal PartialStructureChoiceForm(string description, IEnumerable<string> supported, string kind = "")
        {
            options = supported.Where(value => value == "keep-local" || value == "take-incoming" || value == "rename").Distinct().ToList();
            DialogStyle.Apply(this); Text = "Partial 结构冲突处理方式 - TortoiseSCM";
            Size = new Size(760, 500); MinimumSize = new Size(660, 470);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.Controls.Add(new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Text = description }, 0, 0);
            choices.DropDownStyle = ComboBoxStyle.DropDownList; choices.Dock = DockStyle.Fill; choices.AccessibleName = "结构冲突处理方式";
            foreach (string value in options) choices.Items.Add(value == "keep-local" ? "保留本地更改" : value == "take-incoming" ? "采用传入版本" :
                kind == "local-move" ? "指定新的移动目标" : kind == "incoming-delete" ? "以新名称重新添加本地文件" : "本地项另存新名称，保留双方");
            layout.Controls.Add(choices, 0, 1);
            var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            nameRow.Controls.Add(new Label { Text = kind == "local-move" ? "新名称 / 仓库路径：" : "本地的新名称：", AutoSize = true, Padding = new Padding(0, 5, 0, 0) }, 0, 0);
            rename.Dock = DockStyle.Fill; rename.Enabled = false; rename.AccessibleName = "保留本地项的新名称"; nameRow.Controls.Add(rename, 1, 0); layout.Controls.Add(nameRow, 0, 2);
            explanation.Dock = DockStyle.Fill; layout.Controls.Add(explanation, 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var cancel = DialogStyle.Button("取消"); cancel.DialogResult = DialogResult.Cancel; CancelButton = cancel;
            var accept = DialogStyle.Button("确认选择"); accept.Enabled = false;
            choices.SelectedIndexChanged += delegate {
                rename.Enabled = Resolution == "rename";
                accept.Enabled = Resolution != null && (Resolution != "rename" || !String.IsNullOrWhiteSpace(rename.Text));
                explanation.Text = Resolution == "take-incoming" ? "将放弃这个冲突项的本地更改，采用已准备的传入状态。原始本地文件保存在恢复目录中。" :
                    Resolution == "keep-local" ? (kind == "local-move" ? "继续本地移动。仅移动时保留服务器新内容；如果本地也修改过内容，将采用备份中的本地内容。请确认是否需要先手工合并内容。" :
                    kind == "incoming-move" ? "跟随服务器移动到新的路径，在新位置采用备份中的本地内容，原路径不再保留。服务器内容仍保存在备份中；请确认是否需要先手工合并内容，处理后再单独提交。" :
                    "保留本地意图，并将其应用到已准备的服务器状态上。结果仍需单独检查和提交。") :
                    kind == "local-move" ? "将传入文件移到指定位置，原路径不再保留。仅移动时保留传入内容；本地同时编辑时采用本地备份内容。\r\n\r\n文件名相对于本地移动后的目录；也可输入 /目录/文件名。目标父目录必须已加载且受版本控制，不自动创建或更新目录。" :
                    kind == "incoming-delete" ? "服务器已删除原文件。以新的名称将备份中的本地内容重新添加，原路径保持删除。" :
                    "仅输入新的文件名。本地文件以新名称保留，原路径采用传入状态。结果不会自动提交。";
            };
            rename.TextChanged += delegate { accept.Enabled = Resolution != null && (Resolution != "rename" || !String.IsNullOrWhiteSpace(rename.Text)); };
            accept.Click += delegate { if (!accept.Enabled) return; DialogResult = DialogResult.OK; Close(); };
            buttons.Controls.Add(cancel); buttons.Controls.Add(accept); layout.Controls.Add(buttons, 0, 4);
            Controls.Add(layout); AcceptButton = accept;
        }
    }
}
