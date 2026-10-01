// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TortoiseSCM
{
    /// <summary>
    /// Last review gate before Plastic check-in. It displays the immutable preview
    /// produced by MainForm and has no server or filesystem side effects.
    /// </summary>
    internal sealed class CheckinReviewForm : Form
    {
        private readonly PlasticCheckinPreview preview;
        private readonly bool previousResultUncertain;
        private readonly TextBox comment = new TextBox();
        private readonly ListView files = new ListView();
        private readonly ListView locks = new ListView();
        private readonly TextBox scope = new TextBox();
        private readonly Label status = new Label();
        private readonly CheckBox uncertainty = new CheckBox();
        private readonly Button confirm = DialogStyle.Button("确认签入");
        private readonly Button cancel = DialogStyle.Button("取消");

        internal CheckinReviewForm(PlasticCheckinPreview preview, string message, bool previousResultUncertain)
        {
            if (preview == null) throw new ArgumentNullException("preview");
            this.preview = preview;
            this.previousResultUncertain = previousResultUncertain;
            DialogStyle.Apply(this);
            Text = "确认签入 - TortoiseSCM";
            Size = new Size(980, 720);
            MinimumSize = new Size(760, 560);
            AutoScaleMode = AutoScaleMode.Dpi;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 84));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            scope.Dock = DockStyle.Fill;
            scope.Multiline = true;
            scope.ReadOnly = true;
            scope.ScrollBars = ScrollBars.Vertical;
            scope.WordWrap = false;
            scope.AccessibleName = "签入范围、仓库和分支";
            scope.Text = "仓库：" + (preview.Repository ?? "") + "\r\n工作区：" + (preview.RootPath ?? "") +
                "\r\n分支 / selector：" + (preview.Selector ?? "") + " · " + (preview.Paths.Count + " 个请求路径") +
                (preview.IsPartial ? "（Partial）" : "（Standard）") + "\r\n请求路径：" + String.Join("；", preview.Paths.ToArray());
            layout.Controls.Add(scope, 0, 0);

            var messageGroup = new GroupBox { Text = "签入说明（只读预览）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            comment.Multiline = true;
            comment.ReadOnly = true;
            comment.ScrollBars = ScrollBars.Vertical;
            comment.WordWrap = true;
            comment.Dock = DockStyle.Fill;
            comment.AccessibleName = "签入说明预览";
            comment.Text = message ?? "";
            messageGroup.Controls.Add(comment);
            layout.Controls.Add(messageGroup, 0, 1);

            var filesGroup = new GroupBox { Text = "实际签入文件（包含目录范围内的项目）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            files.Dock = DockStyle.Fill;
            files.View = View.Details;
            files.FullRowSelect = true;
            files.HideSelection = false;
            files.CheckBoxes = false;
            files.AccessibleName = "实际签入文件列表";
            DialogStyle.ApplyList(files);
            files.Columns.Add("路径", 560);
            files.Columns.Add("状态", 110);
            files.Columns.Add("说明", 210);
            foreach (PlasticStatusItem item in preview.Files ?? new List<PlasticStatusItem>())
            {
                string path = item.Path ?? "";
                if (!String.IsNullOrEmpty(item.OldPath)) path = item.OldPath + " → " + path;
                var row = new ListViewItem(path);
                row.SubItems.Add(PlasticStatusPresentation.PendingStatus(item));
                row.SubItems.Add(item.IsDirectory ? "目录及其全部后代" : "文件");
                row.Tag = item;
                files.Items.Add(row);
            }
            filesGroup.Controls.Add(files);
            layout.Controls.Add(filesGroup, 0, 2);

            var locksGroup = new GroupBox { Text = "锁提示（仅供核对）", Dock = DockStyle.Fill, Padding = new Padding(8) };
            locks.Dock = DockStyle.Fill;
            locks.View = View.Details;
            locks.FullRowSelect = true;
            locks.HideSelection = false;
            locks.AccessibleName = "签入锁提示列表";
            DialogStyle.ApplyList(locks);
            locks.Columns.Add("路径", 300);
            locks.Columns.Add("持有者", 180);
            locks.Columns.Add("工作区 / 分支", 320);
            foreach (PlasticLockItem item in preview.Locks ?? new List<PlasticLockItem>())
            {
                var row = new ListViewItem(item.Path ?? "");
                row.SubItems.Add(item.Owner ?? "");
                row.SubItems.Add((item.Workspace ?? "") + (String.IsNullOrEmpty(item.HolderBranch) ? "" : " / " + item.HolderBranch));
                row.Tag = item;
                locks.Items.Add(row);
            }
            locksGroup.Controls.Add(locks);
            layout.Controls.Add(locksGroup, 0, 3);

            var warning = preview.LockWarning ?? "";
            if (warning.Length == 0 && locks.Items.Count > 0) warning = "检测到相关锁。锁是提示信息，最终权限由 Plastic 服务器决定。";
            int excludedPrivate = preview.ExcludedPrivateCount;
            if (excludedPrivate > 0) warning += (warning.Length == 0 ? "" : "\r\n") + "已排除 " + excludedPrivate + " 个私有或忽略项；请先添加后再单独签入。";
            var warningLabel = new Label { Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false, Text = warning };
            warningLabel.AccessibleName = "锁提示说明";
            layout.Controls.Add(warningLabel, 0, 4);

            uncertainty.Visible = previousResultUncertain;
            uncertainty.AutoSize = true;
            uncertainty.Text = "我已核对历史和当前状态，确认剩余更改仍需提交后才重试";
            uncertainty.AccessibleName = "确认上次签入结果";
            uncertainty.CheckedChanged += delegate { UpdateButtons(); };
            layout.Controls.Add(uncertainty, 0, 5);

            status.Dock = DockStyle.Fill;
            status.AutoEllipsis = true;
            status.UseMnemonic = false;
            status.Text = previousResultUncertain ? "上次签入结果未确认；请先刷新状态并检查历史。" : "请核对实际文件范围、说明和锁提示。";
            layout.Controls.Add(status, 0, 6);

            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            confirm.Text = previousResultUncertain ? "确认后重试签入" : "确认签入";
            confirm.Width = 130;
            cancel.Width = 90;
            footer.Controls.Add(cancel); footer.Controls.Add(confirm);
            layout.Controls.Add(footer, 0, 7);
            Controls.Add(layout);
            AcceptButton = confirm;
            CancelButton = cancel;
            confirm.Click += delegate { Confirm(); };
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (DialogResult == DialogResult.None) DialogResult = DialogResult.Cancel; };
            UpdateButtons();
        }

        internal PlasticCheckinPreview Preview { get { return preview; } }
        internal string Comment { get { return comment.Text; } }
        internal bool AcceptedPreviousUncertainty { get { return !previousResultUncertain || uncertainty.Checked; } }

        private void Confirm()
        {
            if (!confirm.Enabled) return;
            if (previousResultUncertain && !uncertainty.Checked)
            {
                status.Text = "请先勾选“已核对历史和当前状态”，确认剩余更改仍需提交后再重试。";
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void UpdateButtons()
        { confirm.Enabled = !previousResultUncertain || uncertainty.Checked; }

    }
}
