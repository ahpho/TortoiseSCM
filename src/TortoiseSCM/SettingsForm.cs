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
        private readonly TextBox mergeTool = new TextBox();
        private readonly Label diffStatus = new Label();
        private readonly Label mergeStatus = new Label();
        private bool synchronizingPath;
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
            diffTool.Text = config.BeyondComparePath; mergeTool.Text = config.BeyondComparePath;
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
            var diffPage = CreatePage("差异查看器", "比较和合并共用 Beyond Compare 路径；留空优先使用包内程序，再检测系统安装。");
            var mergePage = CreatePage("合并工具", "统一使用 Beyond Compare。三方文本合并需要 Beyond Compare Pro 许可证。");
            AddPath(clientPage, 1, "cm.exe", cm); AddPath(clientPage, 2, "gluon.exe", gluon);
            clientPage.Controls.Add(FieldLabel("Plastic 命令\r\n超时（分钟）"), 0, 3); clientPage.Controls.Add(timeout, 1, 3);
            AddHint(clientPage, 4, "此超时只限制 Plastic 命令，不限制 Beyond Compare 编辑时间。");
            AddBeyondComparePath(diffPage, diffTool, diffStatus);
            AddBeyondComparePath(mergePage, mergeTool, mergeStatus);
            AddHint(diffPage, 3, "比较参数由 TortoiseSCM 固定管理。\r\n左侧：基线或较早版本；右侧：本地文件或较新版本。\r\n请关闭本次比较窗口后返回；无需配置参数模板。");
            AddHint(mergePage, 3, "合并参数由 TortoiseSCM 固定管理。\r\n左侧：本地；右侧：远程；祖先：基线；输出：合并结果。\r\n保存并关闭 Beyond Compare 后，仍需在 TortoiseSCM 中明确应用结果；不会自动解决冲突或签入。");
            diffTool.TextChanged += delegate { SynchronizePath(diffTool, mergeTool); };
            mergeTool.TextChanged += delegate { SynchronizePath(mergeTool, diffTool); };
            UpdateBeyondCompareStatus();
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
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
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
            string path = diffTool.Text.Trim();
            config.BeyondComparePath = path.Length == 0 ? String.Empty : BeyondCompareTool.NormalizeExecutable(path);
            config.UseBeyondCompare = true;
            diffTool.Text = config.BeyondComparePath;
            config.Timeout = TimeSpan.FromMinutes((double)timeout.Value);
        }

        private static Label FieldLabel(string text)
        { return new Label { Text = text, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0) }; }

        private void SynchronizePath(TextBox source, TextBox destination)
        {
            if (synchronizingPath) return;
            synchronizingPath = true;
            try { destination.Text = source.Text; }
            finally { synchronizingPath = false; }
            UpdateBeyondCompareStatus();
        }

        private void UpdateBeyondCompareStatus()
        {
            string status;
            try
            {
                string resolved = BeyondCompareTool.ResolveExecutable(diffTool.Text.Trim());
                status = String.IsNullOrEmpty(resolved) ? "未找到 Beyond Compare。请安装后点击“自动检测”，或浏览选择 BComp.exe。"
                    : "已找到 BComp.exe。比较与合并使用同一路径；三方合并需要 Pro 许可证。";
            }
            catch (Exception) { status = "未找到可用的 Beyond Compare。请安装后点击“自动检测”，或浏览选择 BComp.exe。"; }
            diffStatus.Text = status; mergeStatus.Text = status;
        }

        private void AddBeyondComparePath(TableLayoutPanel grid, TextBox input, Label status)
        {
            AddPath(grid, 1, "BComp.exe", input, true);
            var detect = DialogStyle.Button("自动检测");
            detect.AccessibleName = "自动检测 Beyond Compare";
            // Automatic mode must remain relative to this application version after upgrades.
            detect.Click += delegate { input.Text = String.Empty; UpdateBeyondCompareStatus(); };
            grid.Controls.Add(detect, 1, 2);
            status.Dock = DockStyle.Fill; status.AccessibleName = "Beyond Compare 状态";
            grid.Controls.Add(status, 0, 4); grid.SetColumnSpan(status, 3);
        }

        private static void AddHint(TableLayoutPanel grid, int row, string text)
        {
            var label = new Label { Text = text, Dock = DockStyle.Fill };
            grid.Controls.Add(label, 0, row); grid.SetColumnSpan(label, 3);
        }

        private static void AddPath(TableLayoutPanel grid, int row, string label, TextBox input, bool beyondCompare = false)
        {
            grid.Controls.Add(FieldLabel(label), 0, row);
            input.Dock = DockStyle.Fill; input.AccessibleName = label;
            grid.Controls.Add(input, 1, row);
            var browse = new Button { Text = "…", Dock = DockStyle.Top, Height = DialogStyle.ButtonHeight, AccessibleName = "浏览" + label };
            browse.Click += delegate
            {
                using (var picker = new OpenFileDialog { Filter = "Windows 程序 (*.exe)|*.exe", FileName = input.Text })
                    if (picker.ShowDialog() == DialogResult.OK)
                    {
                        try { input.Text = beyondCompare ? BeyondCompareTool.NormalizeExecutable(picker.FileName) : picker.FileName; }
                        catch (Exception ex) { MessageBox.Show(input.FindForm(), ex.Message, "TortoiseSCM"); }
                    }
            };
            grid.Controls.Add(browse, 2, row);
        }
    }
}
