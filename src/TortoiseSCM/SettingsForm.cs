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
        private readonly CheckBox builtInDiff = new CheckBox();
        private readonly CheckBox builtInMerge = new CheckBox();
        private readonly NumericUpDown timeout = new NumericUpDown();
        private readonly PlasticClientConfig config;

        public SettingsForm()
        {
            config = PlasticClientConfig.Load();
            DialogStyle.Apply(this);
            Text = "设置 - TortoiseSCM";
            ClientSize = new Size(800, 440);
            MinimumSize = new Size(740, 470);
            MaximizeBox = false;
            cm.Text = config.CmPath; gluon.Text = config.GluonPath;
            diffTool.Text = config.DiffToolPath; diffArgs.Text = config.DiffToolArguments;
            mergeTool.Text = config.MergeToolPath; mergeArgs.Text = config.MergeToolArguments;
            timeout.Minimum = 1; timeout.Maximum = 120;
            timeout.Value = Math.Max(1, Math.Min(120, (decimal)config.Timeout.TotalMinutes));

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(DialogStyle.Margin), ColumnCount = 2, RowCount = 2 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 158));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var navigation = new TreeView { Dock = DockStyle.Fill, HideSelection = false, BorderStyle = BorderStyle.Fixed3D,
                ShowLines = true, ShowRootLines = true, ShowPlusMinus = true, Margin = new Padding(0, 0, DialogStyle.Gap, 0), AccessibleName = "设置类别" };
            var pages = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0) };
            var clientPage = CreatePage("客户端", "使用 Plastic 客户端已有的登录配置。");
            var diffPage = CreatePage("差异查看器", "选择内置文本比较，或配置外部程序。外部程序留空时使用官方查看器。");
            var mergePage = CreatePage("合并工具", "选择内置三方文本编辑器，或配置外部程序。保存结果后仍需明确确认应用。");
            AddPath(clientPage, 1, "cm.exe", cm); AddPath(clientPage, 2, "gluon.exe", gluon);
            clientPage.Controls.Add(FieldLabel("超时（分钟）"), 0, 3); clientPage.Controls.Add(timeout, 1, 3);
            AddPath(diffPage, 1, "程序", diffTool); AddArguments(diffPage, 2, diffArgs);
            AddHint(diffPage, 3, "占位符：{base} 为基线，{local} 为本地文件。\r\n示例：\"{base}\" \"{local}\"\r\n工具应等待窗口关闭后退出；如工具支持，请加 --wait。");
            AddPath(mergePage, 1, "程序", mergeTool); AddArguments(mergePage, 2, mergeArgs);
            AddHint(mergePage, 3, "占位符：{base}、{local}、{remote}、{merged}。\r\n示例：\"{base}\" \"{local}\" \"{remote}\" \"{merged}\"\r\n等待工具退出后可检查结果；不会自动标记冲突已解决。");
            AddBuiltInChoice(diffPage, builtInDiff, "使用内置文本比较", config.UseBuiltInDiff);
            AddBuiltInChoice(mergePage, builtInMerge, "使用内置三方文本编辑器", config.UseBuiltInMerge);
            var merge = DialogStyle.Button("打开合并工具…"); merge.Width = 130;
            merge.Click += delegate
            {
                try { ApplyTools(); using (var dialog = new ToolLaunchForm(WinFormsPlasticToolHost.CreateClient(config, this))) dialog.ShowDialog(this); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "TortoiseSCM"); }
            };
            mergePage.Controls.Add(merge, 1, 5);
            foreach (var page in new[] { clientPage, diffPage, mergePage }) { page.Parent.Visible = false; pages.Controls.Add(page.Parent); }
            var clientNode = navigation.Nodes.Add("客户端"); clientNode.Tag = clientPage.Parent;
            var diffNode = navigation.Nodes.Add("差异查看器"); diffNode.Tag = diffPage.Parent;
            var mergeNode = diffNode.Nodes.Add("合并工具"); mergeNode.Tag = mergePage.Parent;
            navigation.AfterSelect += delegate(object sender, TreeViewEventArgs e)
            {
                foreach (Control page in pages.Controls) page.Visible = page == e.Node.Tag;
                ((Control)e.Node.Tag).BringToFront();
            };
            navigation.ExpandAll();
            layout.Controls.Add(navigation, 0, 0); layout.Controls.Add(pages, 1, 0);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false,
                Padding = new Padding(0, 12, 0, 0), Margin = new Padding(0) };
            var cancel = DialogStyle.Button("取消"); cancel.DialogResult = DialogResult.Cancel;
            var save = DialogStyle.Button("确定"); save.Click += SaveSettings;
            buttons.Controls.Add(cancel); buttons.Controls.Add(save);
            layout.Controls.Add(buttons, 0, 1); layout.SetColumnSpan(buttons, 2);
            Controls.Add(layout); AcceptButton = save; CancelButton = cancel;
            navigation.SelectedNode = clientNode;
        }

        private static TableLayoutPanel CreatePage(string title, string introduction)
        {
            var box = new GroupBox { Text = title, Dock = DockStyle.Fill, Padding = new Padding(12, 18, 12, 12) };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 7, Margin = new Padding(0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 32));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            AddHint(grid, 0, introduction); box.Controls.Add(grid); return grid;
        }

        private void SaveSettings(object sender, EventArgs e)
        {
            if (!File.Exists(cm.Text) || !File.Exists(gluon.Text))
            { MessageBox.Show(this, "请选择存在的 cm.exe 和 gluon.exe 文件。", "TortoiseSCM"); return; }
            try
            {
                config.CmPath = Path.GetFullPath(cm.Text); config.GluonPath = Path.GetFullPath(gluon.Text);
                ApplyTools(); config.Save(); DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "TortoiseSCM"); }
        }

        private void ApplyTools()
        {
            if (!builtInDiff.Checked) PlasticToolArguments.ValidateConfiguration(diffTool.Text.Trim(), diffArgs.Text, false);
            if (!builtInMerge.Checked) PlasticToolArguments.ValidateConfiguration(mergeTool.Text.Trim(), mergeArgs.Text, true);
            config.DiffToolPath = diffTool.Text.Trim(); config.DiffToolArguments = diffArgs.Text;
            config.MergeToolPath = mergeTool.Text.Trim(); config.MergeToolArguments = mergeArgs.Text;
            config.UseBuiltInDiff = builtInDiff.Checked; config.UseBuiltInMerge = builtInMerge.Checked;
            config.Timeout = TimeSpan.FromMinutes((double)timeout.Value);
        }

        private static Label FieldLabel(string text)
        { return new Label { Text = text, AutoSize = true, Padding = new Padding(0, 4, 0, 0) }; }

        private static void AddBuiltInChoice(TableLayoutPanel grid, CheckBox choice, string text, bool selected)
        {
            choice.Text = text; choice.AutoSize = true; choice.AccessibleName = text;
            grid.Controls.Add(choice, 0, 4); grid.SetColumnSpan(choice, 3);
            Action update = delegate
            {
                foreach (Control control in grid.Controls)
                {
                    int row = grid.GetRow(control);
                    if (row == 1 || row == 2) control.Enabled = !choice.Checked;
                }
            };
            choice.CheckedChanged += delegate { update(); };
            choice.Checked = selected; update();
        }

        private static void AddHint(TableLayoutPanel grid, int row, string text)
        {
            var label = new Label { Text = text, Dock = DockStyle.Fill };
            grid.Controls.Add(label, 0, row); grid.SetColumnSpan(label, 3);
        }

        private static void AddArguments(TableLayoutPanel grid, int row, TextBox input)
        {
            grid.Controls.Add(FieldLabel("参数"), 0, row);
            input.Dock = DockStyle.Fill; input.AccessibleName = "参数模板";
            grid.Controls.Add(input, 1, row); grid.SetColumnSpan(input, 2);
        }

        private static void AddPath(TableLayoutPanel grid, int row, string label, TextBox input)
        {
            grid.Controls.Add(FieldLabel(label), 0, row);
            input.Dock = DockStyle.Fill; input.AccessibleName = label;
            grid.Controls.Add(input, 1, row);
            var browse = new Button { Text = "…", Dock = DockStyle.Top, Height = DialogStyle.ButtonHeight, AccessibleName = "浏览" + label };
            browse.Click += delegate
            {
                using (var picker = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", FileName = input.Text })
                    if (picker.ShowDialog() == DialogResult.OK) input.Text = picker.FileName;
            };
            grid.Controls.Add(browse, 2, row);
        }
    }
}
