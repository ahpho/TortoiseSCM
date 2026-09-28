// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TortoiseSCM
{
    // Native switch/merge convention: visible target, read-only review, explicit action.
    internal sealed class PartialBranchSwitchPreviewForm : Form
    {
        private readonly TextBox context = new TextBox();
        private readonly ListView directories = new ListView();
        private readonly TextBox explanation = new TextBox();
        private readonly Button proceed = DialogStyle.Button("确认切换(&S)");
        private readonly Button close = DialogStyle.Button("取消");

        internal PartialBranchSwitchPreviewForm(string root, PlasticPartialBranchSwitchPreview preview)
        {
            if (preview == null || preview.Directories == null || String.IsNullOrEmpty(preview.Branch) ||
                String.IsNullOrEmpty(preview.Repository) || preview.HeadChangeset < 0)
                throw new ArgumentException("目录结构预览不完整，请刷新后重试。");
            // Copy all presentation values now; providers must not retarget an open review.
            bool canSwitch = preview.CanSwitch && preview.Directories.All(row => row != null && row.Change == "Unchanged");
            DialogStyle.Apply(this); Text = "Partial 分支切换预览 - TortoiseSCM";
            Size = new Size(1040, 710); MinimumSize = new Size(780, 620);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 186));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            context.Dock = DockStyle.Fill; context.Multiline = true; context.ReadOnly = true; context.WordWrap = false;
            context.ScrollBars = ScrollBars.Both; context.AccessibleName = "切换目标及已加载范围";
            context.Text = "仓库：" + preview.Repository + "\r\n目标分支：" + preview.Branch + "  ·  cs:" + preview.HeadChangeset +
                "\r\n工作区：" + root + "\r\n已加载目录：" + preview.LoadedDirectoryCount + "；加载规则：" + preview.LoadingRuleCount +
                (preview.IsFullyLoaded ? "；整个工作区已加载" : "；按现有加载配置");
            layout.Controls.Add(context, 0, 0);
            var group = new GroupBox { Text = "目录结构（包含未变化目录）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            directories.Dock = DockStyle.Fill; directories.View = View.Details; directories.MultiSelect = false;
            directories.LabelEdit = false; directories.ShowItemToolTips = true;
            directories.AccessibleName = "目录结构变化与阻止原因"; DialogStyle.ApplyList(directories);
            directories.Columns.Add("当前目录", 205); directories.Columns.Add("目标目录", 205);
            directories.Columns.Add("状态", 90); directories.Columns.Add("项目 ID", 95); directories.Columns.Add("说明 / 阻止原因", 380);
            foreach (var row in preview.Directories) {
                if (row == null) throw new ArgumentException("目录结构预览不完整，请刷新后重试。");
                string reason = ReasonText(row);
                directories.Items.Add(new ListViewItem(new[] { row.Path ?? "", row.TargetPath ?? "", ChangeText(row.Change), row.ItemId.ToString(), reason }) {
                    ToolTipText = (row.Path ?? "") + " → " + (row.TargetPath ?? "") + "\r\n" + reason });
            }
            group.Controls.Add(directories); layout.Controls.Add(group, 0, 1);
            explanation.Dock = DockStyle.Fill; explanation.Multiline = true; explanation.ReadOnly = true;
            explanation.ScrollBars = ScrollBars.Vertical; explanation.AccessibleName = "切换范围与后续操作";
            explanation.Text = (canSwitch ? "目录结构检查通过。确认后将再次检查目标提交、工作区及目录结构。\r\n" :
                "目录结构存在阻止项，不能在此切换。请查看列表中的原因。\r\n关闭此窗口后，可在 Gluon 中核对并调整加载配置，再刷新预览。\r\n") + ScopeText;
            layout.Controls.Add(explanation, 0, 2);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            proceed.Width = 120; proceed.Visible = proceed.Enabled = canSwitch;
            close.Text = canSwitch ? "取消" : "关闭"; close.DialogResult = DialogResult.Cancel;
            proceed.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            footer.Controls.Add(close); footer.Controls.Add(proceed); layout.Controls.Add(footer, 0, 3); Controls.Add(layout);
            CancelButton = close; AcceptButton = close; Shown += delegate { close.Focus(); };
        }

        internal const string ScopeText = "这里只预览目录结构，不是完整的文件内容变化清单。\r\n" +
            "Partial 切换会更新已加载项；完整选中的目录会接收目标分支的新子项。\r\n未加载项仍遵循现有加载配置；不会主动扩大加载范围。\r\n" +
            "请先处理待定更改、私有/忽略文件及合并会话；不会自动暂存或撤销更改。\r\n" +
            "不会自动卸载目录或修改加载配置。结果不确定时请刷新核对，不会自动反向切换。";

        private static string ChangeText(string change)
        {
            switch (change) {
                case "Unchanged": return "未变化";
                case "Moved": return "移动 / 重命名";
                case "Deleted": return "删除";
                case "Replaced": return "替换";
                case "Added": return "新增目录";
                default: return "不支持";
            }
        }

        private static string ReasonText(PlasticPartialBranchSwitchDirectory row)
        {
            const string unloadedParent = "A loaded file would enter a directory that is not loaded at the same path and identity: ";
            if (row.Change == "Unsupported" && row.Reason != null && row.Reason.StartsWith(unloadedParent, StringComparison.Ordinal))
                return "已加载文件将进入尚未按相同路径及身份加载的目录：" + row.Reason.Substring(unloadedParent.Length);
            switch (row.Change) {
                case "Unchanged": return "已加载目录的身份和路径保持不变。";
                case "Moved": return "目标分支中此目录已移动或重命名，需要核对加载配置。";
                case "Deleted": return "目标分支中已不存在此已加载目录。";
                case "Replaced": return "目标分支中此路径由另一个项目占用。";
                case "Added": return "新目录将进入完整加载范围，可能改变加载配置。";
                default: return "包含链接、跨仓库项目或不支持的类型变化。 " + (row.Reason ?? "");
            }
        }
    }
}
