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
        private readonly ComboBox provider = new ComboBox();
        private readonly ComboBox mergeProvider = new ComboBox();
        private bool synchronizingProvider;
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
            var diffPage = CreatePage("差异查看器", "默认使用包内 TortoiseGitMerge；可切换到 Beyond Compare。BC 路径留空自动检测。");
            var mergePage = CreatePage("合并工具", "默认使用包内 TortoiseGitMerge；备选 Beyond Compare 三方合并需 Pro 许可证。");
            AddPath(clientPage, 1, "cm.exe", cm); AddPath(clientPage, 2, "gluon.exe", gluon);
            clientPage.Controls.Add(FieldLabel("Plastic 命令\r\n超时（分钟）"), 0, 3); clientPage.Controls.Add(timeout, 1, 3);
            AddHint(clientPage, 4, "此超时只限制 Plastic 命令，不限制比较/合并工具编辑时间。");
            AddBeyondComparePath(diffPage, diffTool, diffStatus, provider);
            AddBeyondComparePath(mergePage, mergeTool, mergeStatus, mergeProvider);
            provider.SelectedIndex = mergeProvider.SelectedIndex = config.UseTortoiseMerge ? 0 : 1;
            provider.SelectedIndexChanged += delegate { SynchronizeProvider(provider, mergeProvider); };
            mergeProvider.SelectedIndexChanged += delegate { SynchronizeProvider(mergeProvider, provider); };
            AddHint(diffPage, 3, "比较参数由 TortoiseSCM 固定管理。\r\n左侧：基线或较早版本；右侧：本地文件或较新版本。\r\n请关闭本次比较窗口后返回；无需配置参数模板。");
            AddHint(mergePage, 3, "合并参数由 TortoiseSCM 固定管理。\r\nTortoise：左远程、右本地；BC：左本地、右远程。\r\n祖先：基线；输出：独立合并结果。\r\n保存并关闭工具后，仍需在 TortoiseSCM 中明确应用结果；不会自动解决冲突或签入。");
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
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
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
            config.BeyondComparePath = path.Length == 0 || provider.SelectedIndex == 0 ? path : BeyondCompareTool.NormalizeExecutable(path);
            config.UseBeyondCompare = true;
            config.UseTortoiseMerge = provider.SelectedIndex == 0;
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

        private void SynchronizeProvider(ComboBox source, ComboBox destination)
        {
            if (synchronizingProvider) return;
            synchronizingProvider = true;
            try { destination.SelectedIndex = source.SelectedIndex; }
            finally { synchronizingProvider = false; }
            UpdateBeyondCompareStatus();
        }

        private void UpdateBeyondCompareStatus()
        {
            string status;
            try
            {
                if (provider.SelectedIndex == 0)
                {
                    ComparisonTool.ResolveExecutable(new PlasticClientConfig { UseTortoiseMerge = true });
                    diffStatus.Text = mergeStatus.Text = "已找到包内 TortoiseGitMerge。比较和合并使用原生工具，也可在上方切换至 BC。";
                    return;
                }
                string resolved = BeyondCompareTool.ResolveExecutable(diffTool.Text.Trim());
                status = String.IsNullOrEmpty(resolved) ? "未找到 Beyond Compare。请安装后点击“自动检测”，或浏览选择 BComp.exe。"
                    : "已找到 BComp.exe。比较与合并使用同一路径；三方合并需要 Pro 许可证。";
            }
            catch (Exception) { status = provider.SelectedIndex == 0 ? "未找到包内 TortoiseGitMerge，请安装完整包或选择 Beyond Compare。" : "未找到可用的 Beyond Compare。请安装后点击“自动检测”，或浏览选择 BComp.exe。"; }
            diffStatus.Text = status; mergeStatus.Text = status;
        }

        private void AddBeyondComparePath(TableLayoutPanel grid, TextBox input, Label status, ComboBox choice)
        {
            AddPath(grid, 1, "BComp.exe", input, true);
            var detect = DialogStyle.Button("自动检测 BC"); detect.Width = 110;
            detect.AccessibleName = "自动检测 Beyond Compare";
            // Automatic mode must remain relative to this application version after upgrades.
            detect.Click += delegate { input.Text = String.Empty; UpdateBeyondCompareStatus(); };
            choice.DropDownStyle = ComboBoxStyle.DropDownList; choice.Width = 245;
            choice.AccessibleName = "Comparison tool provider";
            choice.Items.AddRange(new object[] { "TortoiseGitMerge（默认）", "Beyond Compare" });
            var selection = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            selection.Controls.Add(choice); selection.Controls.Add(detect);
            grid.Controls.Add(selection, 0, 2); grid.SetColumnSpan(selection, 3);
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
