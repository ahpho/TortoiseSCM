// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class SettingsForm : Form
    {
        private readonly TextBox cm = new TextBox();
        private readonly TextBox gluon = new TextBox();
        private readonly TextBox diffTool = new TextBox();
        private readonly TextBox diffArgs = new TextBox();
        private readonly TextBox mergeTool = new TextBox();
        private readonly TextBox mergeArgs = new TextBox();
        private readonly NumericUpDown timeout = new NumericUpDown();
        private readonly PlasticClientConfig config;

        public SettingsForm()
        {
            config = PlasticClientConfig.Load();
            Text = "TortoiseSCM — 设置";
            Font = new Font("Microsoft YaHei UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            Size = new Size(880, 555);
            MinimumSize = Size;
            MaximizeBox = false;
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 3, RowCount = 9 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 80));
            cm.Text = config.CmPath;
            gluon.Text = config.GluonPath;
            AddPath(grid, 0, "cm.exe", cm);
            AddPath(grid, 1, "gluon.exe", gluon);
            diffTool.Text = config.DiffToolPath; diffArgs.Text = config.DiffToolArguments;
            mergeTool.Text = config.MergeToolPath; mergeArgs.Text = config.MergeToolArguments;
            AddPath(grid, 2, "外部差异工具", diffTool);
            AddArguments(grid, 3, "差异参数", diffArgs);
            AddPath(grid, 4, "外部合并工具", mergeTool);
            AddArguments(grid, 5, "合并参数", mergeArgs);
            grid.Controls.Add(new Label { Text = "超时（分钟）", AutoSize = true }, 0, 6);
            timeout.Minimum = 1;
            timeout.Maximum = 120;
            timeout.Value = Math.Max(1, Math.Min(120, (decimal)config.Timeout.TotalMinutes));
            grid.Controls.Add(timeout, 1, 6);
            var hint = new Label { Text = "差异工具留空时使用官方查看器。参数使用双引号包含路径占位符。\r\n差异：{base}、{local}；合并：{base}、{local}、{remote}、{merged}。\r\n工具需等待窗口关闭后退出（如工具支持，请加 --wait）。只保存 TortoiseSCM 设置。", AutoSize = true, Dock = DockStyle.Fill };
            grid.Controls.Add(hint, 0, 7);
            grid.SetColumnSpan(hint, 3);
            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
            var save = new Button { Text = "保存" };
            var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
            save.Click += delegate
            {
                if (!File.Exists(cm.Text) || !File.Exists(gluon.Text))
                { MessageBox.Show(this, "请选择存在的 cm.exe 和 gluon.exe 文件。", "TortoiseSCM"); return; }
                try
                {
                    config.CmPath = Path.GetFullPath(cm.Text);
                    config.GluonPath = Path.GetFullPath(gluon.Text);
                    config.Timeout = TimeSpan.FromMinutes((double)timeout.Value);
                    ApplyTools();
                    config.Save();
                    DialogResult = DialogResult.OK;
                    Close();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "TortoiseSCM"); }
            };
            buttons.Controls.Add(save);
            buttons.Controls.Add(cancel);
            var merge = new Button { Text = "打开合并工具…", AutoSize = true };
            merge.Click += delegate
            {
                try { ApplyTools(); using (var dialog = new ToolLaunchForm(new PlasticClient(config))) dialog.ShowDialog(this); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "TortoiseSCM"); }
            };
            buttons.Controls.Add(merge);
            grid.Controls.Add(buttons, 0, 8);
            grid.SetColumnSpan(buttons, 3);
            for (int i = 0; i < 7; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
            Controls.Add(grid);
            AcceptButton = save;
            CancelButton = cancel;
        }

        private void ApplyTools()
        {
            PlasticToolArguments.ValidateConfiguration(diffTool.Text.Trim(), diffArgs.Text, false);
            PlasticToolArguments.ValidateConfiguration(mergeTool.Text.Trim(), mergeArgs.Text, true);
            config.DiffToolPath = diffTool.Text.Trim(); config.DiffToolArguments = diffArgs.Text;
            config.MergeToolPath = mergeTool.Text.Trim(); config.MergeToolArguments = mergeArgs.Text;
            config.Timeout = TimeSpan.FromMinutes((double)timeout.Value);
        }

        private static void AddArguments(TableLayoutPanel grid, int row, string label, TextBox input)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, row);
            input.Dock = DockStyle.Fill; grid.Controls.Add(input, 1, row); grid.SetColumnSpan(input, 2);
        }

        private static void AddPath(TableLayoutPanel grid, int row, string label, TextBox input)
        {
            grid.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 4, 0, 0) }, 0, row);
            input.Dock = DockStyle.Fill;
            grid.Controls.Add(input, 1, row);
            var browse = new Button { Text = "浏览…", Dock = DockStyle.Fill };
            browse.Click += delegate
            {
                using (var picker = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", FileName = input.Text })
                    if (picker.ShowDialog() == DialogResult.OK) input.Text = picker.FileName;
            };
            grid.Controls.Add(browse, 2, row);
        }
    }
}
