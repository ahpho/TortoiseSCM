// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class CommitMessageLibraryForm : Form
    {
        private readonly CommitMessageStore store;
        private readonly string repository;
        private readonly string draft;
        private readonly TabControl tabs = new TabControl();
        private readonly ListBox recent = new ListBox();
        private readonly TextBox recentText = new TextBox();
        private readonly ListBox templates = new ListBox();
        private readonly TextBox templateName = new TextBox();
        private readonly TextBox templateText = new TextBox();
        private readonly Button clearRecent = DialogStyle.Button("清空历史…");
        private readonly Button newTemplate = DialogStyle.Button("新建模板");
        private readonly Button currentDraft = DialogStyle.Button("载入当前说明");
        private readonly Button saveTemplate = DialogStyle.Button("保存模板…");
        private readonly Button deleteTemplate = DialogStyle.Button("删除模板…");
        private readonly Button use = DialogStyle.Button("使用此说明");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly Button refresh = DialogStyle.Button("刷新列表");
        private readonly Label status = new Label();
        private readonly TextBox context = new TextBox();
        private CommitMessageLibrary library;
        private Dictionary<string, string> reviewedTemplates = new Dictionary<string, string>(StringComparer.Ordinal);
        private Func<string, bool> confirm;

        internal string SelectedMessage { get; private set; }

        internal CommitMessageLibraryForm(CommitMessageStore store, string repository, string draft)
        {
            if (store == null) throw new ArgumentNullException("store");
            this.store = store; this.repository = repository; this.draft = draft ?? "";
            confirm = message => MessageBox.Show(this, message, Text, MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
            DialogStyle.Apply(this); Text = "提交说明历史与模板 - TortoiseSCM";
            Size = new Size(860, 650); MinimumSize = new Size(700, 530);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            context.Multiline = true; context.ReadOnly = true; context.Dock = DockStyle.Fill;
            context.Text = "仓库：" + repository + "\r\n仅保存在当前 Windows 用户的本机；历史仅包含本程序成功提交的说明。";
            context.AccessibleName = "说明库所属仓库"; layout.Controls.Add(context, 0, 0);
            tabs.Dock = DockStyle.Fill; tabs.SelectedIndexChanged += delegate { UpdateButtons(); };
            tabs.TabPages.Add(BuildRecentPage()); tabs.TabPages.Add(BuildTemplatePage());
            layout.Controls.Add(tabs, 0, 1);
            status.Dock = DockStyle.Fill; status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft;
            status.UseMnemonic = false; layout.Controls.Add(status, 0, 2);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
            close.DialogResult = DialogResult.Cancel; close.Click += delegate { Close(); };
            use.Width = 110; use.Click += delegate { UseMessage(); };
            refresh.Click += delegate { Run(() => {
                ReloadPreservingEditor();
                status.Text = "列表已刷新，编辑内容已保留。可选择模板核对最新内容。";
            }); };
            footer.Controls.Add(close); footer.Controls.Add(use); footer.Controls.Add(refresh); layout.Controls.Add(footer, 0, 3);
            Controls.Add(layout); CancelButton = close; AcceptButton = close;
            Shown += delegate { close.Focus(); };
            Reload();
        }

        private TabPage BuildRecentPage()
        {
            var page = new TabPage("最近成功提交");
            var layout = PageLayout(3);
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            var toolbar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            toolbar.Controls.Add(new Label { Text = "最近 20 条，选择后在下方查看完整说明。", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            clearRecent.Dock = DockStyle.Fill; clearRecent.Width = 110;
            clearRecent.Click += delegate { Run(() => {
                if (!confirm("清空此仓库在本机保存的成功提交说明历史？模板和服务器提交不受影响。")) return;
                store.ClearRecent(repository); ReloadPreservingEditor(); status.Text = "已清空本机说明历史；模板编辑内容已保留。";
            }); };
            toolbar.Controls.Add(clearRecent, 1, 0); layout.Controls.Add(toolbar, 0, 0);
            recent.Dock = DockStyle.Fill; recent.IntegralHeight = false; recent.HorizontalScrollbar = true;
            recent.AccessibleName = "最近成功提交说明";
            recent.SelectedIndexChanged += delegate {
                recentText.Text = recent.SelectedIndex < 0 ? "" : library.Recent[recent.SelectedIndex]; UpdateButtons();
            };
            ConfigureBody(recentText, "历史说明完整内容"); recentText.ReadOnly = true;
            layout.Controls.Add(recent, 0, 1); layout.Controls.Add(recentText, 0, 2); page.Controls.Add(layout); return page;
        }

        private TabPage BuildTemplatePage()
        {
            var page = new TabPage("命名模板");
            var layout = PageLayout(5);
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 70));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            templates.Dock = DockStyle.Fill; templates.IntegralHeight = false; templates.HorizontalScrollbar = true;
            templates.AccessibleName = "命名提交说明模板";
            templates.SelectedIndexChanged += delegate {
                if (templates.SelectedIndex >= 0) {
                    var selected = library.Templates[templates.SelectedIndex]; templateName.Text = selected.Name; templateText.Text = selected.Text;
                    reviewedTemplates[selected.Name] = selected.Text;
                }
                UpdateButtons();
            };
            layout.Controls.Add(templates, 0, 0);
            var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
            nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110)); nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            nameRow.Controls.Add(new Label { Text = "模板名称(&N)：", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
            templateName.Dock = DockStyle.Fill; templateName.MaxLength = CommitMessageStore.MaxTemplateNameLength;
            templateName.AccessibleName = "模板名称"; templateName.TextChanged += delegate { UpdateButtons(); };
            nameRow.Controls.Add(templateName, 1, 0); layout.Controls.Add(nameRow, 0, 1);
            ConfigureBody(templateText, "模板说明编辑器"); templateText.MaxLength = CommitMessageStore.MaxMessageLength;
            templateText.TextChanged += delegate { UpdateButtons(); }; layout.Controls.Add(templateText, 0, 2);
            layout.Controls.Add(new Label { Text = "更改名称会另存；保存同名模板前需确认覆盖。未保存的编辑不会修改模板。", Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 0, 3);
            var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Margin = Padding.Empty };
            currentDraft.Width = 110;
            newTemplate.Click += delegate { StartTemplate(""); };
            currentDraft.Click += delegate { StartTemplate(draft); };
            saveTemplate.Click += delegate { Run(SaveTemplate); };
            deleteTemplate.Click += delegate { Run(DeleteTemplate); };
            toolbar.Controls.Add(newTemplate); toolbar.Controls.Add(currentDraft); toolbar.Controls.Add(saveTemplate); toolbar.Controls.Add(deleteTemplate);
            layout.Controls.Add(toolbar, 0, 4); page.Controls.Add(layout); return page;
        }

        private static TableLayoutPanel PageLayout(int rows)
        {
            var result = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = rows, Padding = new Padding(4) };
            result.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); return result;
        }

        private static void ConfigureBody(TextBox box, string accessibleName)
        {
            box.Dock = DockStyle.Fill; box.Multiline = true; box.AcceptsReturn = true;
            box.ScrollBars = ScrollBars.Vertical; box.AccessibleName = accessibleName;
        }

        private void Reload()
        {
            var updated = store.Load(repository);
            recent.ClearSelected(); templates.ClearSelected(); library = updated;
            reviewedTemplates = library.Templates.ToDictionary(item => item.Name, item => item.Text, StringComparer.Ordinal);
            recent.Items.Clear(); templates.Items.Clear(); recentText.Clear(); templateName.Clear(); templateText.Clear();
            foreach (string message in library.Recent) recent.Items.Add(message.Replace("\r\n", "  ").Replace('\r', ' ').Replace('\n', ' '));
            foreach (var template in library.Templates) templates.Items.Add(template.Name);
            status.Text = library.Recent.Count + " 条历史 · " + library.Templates.Count + " 个模板（最多各 20 条）";
            UpdateButtons();
        }

        private void ReloadPreservingEditor()
        {
            string name = templateName.Text, text = templateText.Text;
            var reviewed = reviewedTemplates;
            Reload(); reviewedTemplates = reviewed;
            templateName.Text = name; templateText.Text = text;
        }

        private void StartTemplate(string text)
        {
            templates.ClearSelected(); templateName.Clear(); templateText.Text = text;
            reviewedTemplates = library.Templates.ToDictionary(item => item.Name, item => item.Text, StringComparer.Ordinal);
            templateName.Focus();
        }

        private void SaveTemplate()
        {
            string name = templateName.Text, text = templateText.Text;
            // Compare the version displayed by this window under the store's lock.
            // A concurrent create or edit must not turn confirmation into an unseen overwrite.
            string expectedText;
            bool existing = reviewedTemplates.TryGetValue(name, out expectedText);
            if (existing &&
                !confirm("将覆盖本机同名模板“" + name + "”的完整说明。继续保存？")) return;
            store.SaveTemplate(repository, name, text, existing ? expectedText : null); Reload();
            templates.SelectedIndex = library.Templates.ToList().FindIndex(item => item.Name == name);
            status.Text = "模板已保存到本机。";
        }

        private void DeleteTemplate()
        {
            if (templates.SelectedIndex < 0) return;
            var selected = library.Templates[templates.SelectedIndex]; string name = selected.Name;
            if (!confirm("删除本机模板“" + name + "”？此操作不能撤销。")) return;
            store.DeleteTemplate(repository, name, selected.Text); Reload(); status.Text = "模板已删除。";
        }

        private void UseMessage()
        {
            string selected = tabs.SelectedIndex == 0 ? recentText.Text : templateText.Text;
            if (String.IsNullOrWhiteSpace(selected)) return;
            SelectedMessage = selected; DialogResult = DialogResult.OK; Close();
        }

        private void UpdateButtons()
        {
            use.Enabled = tabs.SelectedIndex == 0 ? recent.SelectedIndex >= 0 : !String.IsNullOrWhiteSpace(templateText.Text);
            clearRecent.Enabled = library != null && library.Recent.Count != 0;
            saveTemplate.Enabled = !String.IsNullOrWhiteSpace(templateName.Text) && !String.IsNullOrWhiteSpace(templateText.Text);
            deleteTemplate.Enabled = templates.SelectedIndex >= 0;
            currentDraft.Enabled = !String.IsNullOrWhiteSpace(draft);
        }

        private void Run(Action action)
        {
            try { action(); }
            catch (Exception ex) { status.Text = "操作失败：" + ex.Message + " 编辑内容已保留，可刷新列表后重试。"; }
        }
    }
}
