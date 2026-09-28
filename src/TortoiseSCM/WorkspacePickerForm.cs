// GPL-2.0-or-later. Editable path selection for existing workspaces.
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class WorkspacePickerForm : Form
    {
        private readonly TextBox directory = new TextBox();
        private readonly Button browse = DialogStyle.Button("浏览…");
        private readonly Button open = DialogStyle.Button("打开");
        private readonly Button cancel = DialogStyle.Button("取消");
        private readonly Label status = new Label();
        internal string SelectedPath { get; private set; }

        internal WorkspacePickerForm()
        {
            DialogStyle.Apply(this);
            Text = "打开已有工作区 - TortoiseSCM";
            ClientSize = new Size(620, 196); MinimumSize = new Size(500, 235); MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            var label = new Label { Text = "工作区目录（可粘贴路径）：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
            layout.Controls.Add(label, 0, 0); layout.SetColumnSpan(label, 2);
            directory.Dock = DockStyle.Fill; directory.Margin = new Padding(0, 3, 0, 0); directory.TabIndex = 0;
            layout.Controls.Add(directory, 0, 1); browse.TabIndex = 1; layout.Controls.Add(browse, 1, 1);
            status.Text = "选择已有 Plastic SCM 工作区的目录，然后点击“打开”。";
            status.Dock = DockStyle.Fill; status.Padding = new Padding(0, 4, 0, 0);
            layout.Controls.Add(status, 0, 2); layout.SetColumnSpan(status, 2);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            buttons.Controls.Add(cancel); buttons.Controls.Add(open);
            layout.Controls.Add(buttons, 0, 3); layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout); AcceptButton = open; CancelButton = cancel; cancel.DialogResult = DialogResult.Cancel;
            open.Click += delegate { OpenDirectory(); };
            cancel.Click += delegate { Close(); };
            browse.Click += delegate {
                using (var picker = new FolderBrowserDialog { Description = "选择已有 Plastic SCM 工作区", ShowNewFolderButton = false }) {
                    string existing = NormalizePath(directory.Text);
                    if (Directory.Exists(existing)) picker.SelectedPath = existing;
                    if (picker.ShowDialog(this) == DialogResult.OK) directory.Text = picker.SelectedPath;
                }
                directory.Focus();
            };
            Shown += delegate { directory.Focus(); };
        }

        private static string NormalizePath(string text)
        {
            string value = (text ?? String.Empty).Trim();
            if (value.Length >= 2 && value[0] == '"' && value[value.Length - 1] == '"') value = value.Substring(1, value.Length - 2);
            return value;
        }

        private void OpenDirectory()
        {
            string path = NormalizePath(directory.Text);
            if (String.IsNullOrWhiteSpace(path)) { status.Text = "请输入或粘贴工作区目录。"; directory.Focus(); return; }
            try {
                path = Path.GetFullPath(path);
                if (!Directory.Exists(path)) { status.Text = "目录不存在或无法访问，请检查路径。"; directory.Focus(); return; }
            }
            catch (Exception ex) {
                if (!(ex is ArgumentException) && !(ex is NotSupportedException) && !(ex is IOException) && !(ex is System.Security.SecurityException)) throw;
                status.Text = "目录路径无效，请检查后重试。"; directory.Focus(); return;
            }
            SelectedPath = path; DialogResult = DialogResult.OK; Close();
        }
    }
}
