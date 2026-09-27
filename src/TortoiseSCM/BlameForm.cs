// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    /// <summary>Native WinForms view for Plastic's read-only annotate operation.</summary>
    internal sealed class BlameForm : Form
    {
        private readonly PlasticClient client;
        private readonly string path;
        private readonly string workspaceRoot;
        private readonly ListView lines = new ListView();
        private readonly Label summary = new Label();
        private readonly Label status = new Label();
        private readonly Button refresh = new Button();
        private readonly Button cancel = new Button();
        private readonly Button history = new Button();
        private readonly Button close = new Button();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private CancellationTokenSource request;
        private bool busy;

        public BlameForm(PlasticClient client, string path, string workspaceRoot)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", "path");
            this.client = client; this.path = Path.GetFullPath(path); this.workspaceRoot = workspaceRoot;
            Text = "Annotate - TortoiseSCM";
            Size = new Size(1120, 720); MinimumSize = new Size(760, 420);
            DialogStyle.Apply(this);
            BuildLayout();
            Shown += async delegate { await LoadAsync(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (busy) { e.Cancel = true; MessageBox.Show(this, "Please wait for annotate to finish.", "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                lifetime.Cancel();
            };
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(DialogStyle.Margin), ColumnCount = 1, RowCount = 4 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); header.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            var fileLabel = new Label { Text = path, Dock = DockStyle.Fill, AutoEllipsis = true, UseMnemonic = false };
            summary.Text = "Loading annotate information..."; summary.Dock = DockStyle.Fill; summary.AutoEllipsis = true;
            header.Controls.Add(fileLabel, 0, 0); header.SetColumnSpan(fileLabel, 2);
            header.Controls.Add(summary, 0, 1);
            refresh.Text = "Refresh"; refresh.Size = new Size(86, 26); refresh.Margin = new Padding(0, 0, 0, 0);
            refresh.Click += async delegate { await LoadAsync(); };
            header.Controls.Add(refresh, 1, 1); layout.Controls.Add(header, 0, 0);

            lines.Dock = DockStyle.Fill; lines.View = View.Details; lines.FullRowSelect = true; lines.MultiSelect = false;
            lines.HideSelection = false; lines.GridLines = false; lines.ShowItemToolTips = true;
            DialogStyle.ApplyList(lines);
            lines.Columns.Add("Line", 58, HorizontalAlignment.Right);
            lines.Columns.Add("Author", 150); lines.Columns.Add("Changeset", 80);
            lines.Columns.Add("Date", 170); lines.Columns.Add("Branch", 170); lines.Columns.Add("Content", 430);
            lines.DoubleClick += async delegate { await OpenHistoryAsync(); };
            var menu = new ContextMenuStrip();
            menu.Items.Add("Show history", null, async delegate { await OpenHistoryAsync(); });
            menu.Items.Add("Copy line", null, delegate { CopySelectedLine(); });
            menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { e.Cancel = busy || lines.SelectedItems.Count == 0; };
            lines.ContextMenuStrip = menu;
            layout.Controls.Add(lines, 0, 1);
            status.Text = ""; status.Dock = DockStyle.Fill; status.AutoEllipsis = true; layout.Controls.Add(status, 0, 2);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };
            cancel.Text = "Cancel"; cancel.Size = new Size(86, 26); cancel.Margin = new Padding(0, 4, 0, 0); cancel.Enabled = false;
            cancel.Click += delegate { if (request != null) request.Cancel(); };
            left.Controls.Add(cancel); footer.Controls.Add(left, 0, 0);
            var right = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = Padding.Empty, WrapContents = false };
            history.Text = "History"; history.Size = new Size(86, 26); history.Margin = new Padding(6, 4, 0, 0); history.Click += async delegate { await OpenHistoryAsync(); };
            close.Text = "Close"; close.Size = new Size(86, 26); close.Margin = new Padding(6, 4, 0, 0); close.Click += delegate { Close(); };
            right.Controls.Add(history); right.Controls.Add(close); footer.Controls.Add(right, 1, 0);
            layout.Controls.Add(footer, 0, 3); Controls.Add(layout); CancelButton = close;
        }

        private async Task LoadAsync()
        {
            if (busy || IsDisposed) return;
            if (request != null) request.Cancel();
            request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            busy = true; refresh.Enabled = false; cancel.Enabled = true; history.Enabled = false; lines.Items.Clear();
            status.Text = "Loading annotate information...";
            try
            {
                var result = await client.GetBlameAsync(path, request.Token).ConfigureAwait(true);
                lines.BeginUpdate();
                try
                {
                    foreach (var item in result)
                    {
                        var row = new ListViewItem(item.Line.ToString()); row.Tag = item;
                        row.SubItems.Add(item.Owner); row.SubItems.Add(item.Changeset.ToString());
                        row.SubItems.Add(item.Date); row.SubItems.Add(item.Branch); row.SubItems.Add(item.Content);
                        row.ToolTipText = String.IsNullOrEmpty(item.Comment) ? "" : item.Comment;
                        lines.Items.Add(row);
                    }
                }
                finally { lines.EndUpdate(); }
                var first = result.FirstOrDefault();
                summary.Text = result.Count + " lines" + (first == null ? "" : "; repository " + first.Repository);
                status.Text = "Annotate loaded.";
            }
            catch (OperationCanceledException) { status.Text = "Annotate cancelled."; }
            catch (Exception ex) { status.Text = "Annotate failed."; MessageBox.Show(this, ex.Message, "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { busy = false; refresh.Enabled = true; cancel.Enabled = false; history.Enabled = lines.Items.Count > 0; if (request != null) { request.Dispose(); request = null; } }
        }

        private async Task OpenHistoryAsync()
        {
            if (busy || lines.SelectedItems.Count == 0) return;
            try { using (var dialog = new HistoryForm(client, path, workspaceRoot)) dialog.ShowDialog(this); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            await Task.CompletedTask;
        }

        private void CopySelectedLine()
        {
            if (lines.SelectedItems.Count == 0) return;
            Clipboard.SetText(String.Join("\t", lines.SelectedItems[0].SubItems.Cast<ListViewItem.ListViewSubItem>().Select(item => item.Text).ToArray()));
        }
    }
}
