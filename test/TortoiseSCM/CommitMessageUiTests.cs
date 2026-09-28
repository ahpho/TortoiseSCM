// GPL-2.0-or-later. Local message-library controls and successful-checkin persistence.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class CommitMessageUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Repository = "message-ui@local";
        private static int assertions;
        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "bin/TortoiseSCM/qa/commit-message-ui" : args[0]); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-message-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "message-ui\nunused\nStandard\n");
            string selector = "repository \"" + Repository + "\"\n  path \"/\"\n    branch \"/main\"\n";
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector);
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
            try {
                TestLibrary(root, artifacts); TestConcurrentTemplates(root); TestReplacement(client, root, selector, artifacts);
                for (int scenario = 0; scenario < 7; scenario++) TestSubmission(client, root, scenario);
                TestPending(client, root); TestStorageFailure(client, root);
                Console.WriteLine("PASS: commit message UI (" + assertions + " assertions)");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void TestLibrary(string root, string artifacts)
        {
            var store = new CommitMessageStore(Path.Combine(root, "library"));
            string message = "修复资源路径 中文\r\n\r\n验证：已有工作区与空工作区\r\n关联任务：42";
            store.RecordSuccess(Repository, message); store.RecordSuccess("other@local", "别的仓库");
            using (var form = new CommitMessageLibraryForm(store, Repository, "当前草稿\r\n完整内容")) {
                bool accept = false; int confirmations = 0;
                Set(form, "confirm", new Func<string, bool>(text => { confirmations++; return accept; }));
                form.Show(); Application.DoEvents();
                Require(form.AcceptButton == Field<Button>(form, "close") && form.CancelButton == Field<Button>(form, "close"), "Library defaults to close, never applies an accidental Enter");
                var recent = Field<ListBox>(form, "recent");
                Require(recent.Items.Count == 1 && !Field<Button>(form, "use").Enabled, "Repository history is isolated and needs explicit selection");
                recent.SelectedIndex = 0; Application.DoEvents();
                Require(Field<TextBox>(form, "recentText").ReadOnly && Field<TextBox>(form, "recentText").Text == message, "History displays exact full multiline text");
                Save(form, Path.Combine(artifacts, "commit-message-history.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form);
                Save(form, Path.Combine(artifacts, "commit-message-history-minimum.png"));
                Field<Button>(form, "clearRecent").PerformClick();
                Require(confirmations == 1 && store.Load(Repository).Recent.Count == 1, "Declined clear preserves history");
                accept = true; Field<Button>(form, "clearRecent").PerformClick();
                Require(store.Load(Repository).Recent.Count == 0 && store.Load("other@local").Recent.Count == 1, "Confirmed clear affects only this repository");
                Field<TabControl>(form, "tabs").SelectedIndex = 1;
                Field<Button>(form, "currentDraft").PerformClick();
                Require(Field<TextBox>(form, "templateText").Text == "当前草稿\r\n完整内容" && Field<TextBox>(form, "templateName").Text.Length == 0, "Explicit action seeds a new template from current draft");
                Require(!Field<Button>(form, "saveTemplate").Enabled, "Unnamed template cannot save");
                Field<TextBox>(form, "templateName").Text = "发布检查";
                Field<Button>(form, "saveTemplate").PerformClick();
                Require(store.Load(Repository).Templates.Single().Name == "发布检查" && store.Load(Repository).Templates.Single().Text.Contains("完整内容"), "Actual save button creates named multiline template");
                Field<TextBox>(form, "templateText").Text = "更新说明\r\n已完成验证";
                accept = false; int before = confirmations; Field<Button>(form, "saveTemplate").PerformClick();
                Require(confirmations == before + 1 && store.Load(Repository).Templates.Single().Text.StartsWith("当前草稿"), "Saving existing name requires explicit overwrite confirmation");
                accept = true; Field<Button>(form, "saveTemplate").PerformClick();
                Require(store.Load(Repository).Templates.Single().Text == "更新说明\r\n已完成验证", "Confirmed update persists exact edited text");
                Field<TextBox>(form, "templateName").Text = "另存模板";
                Field<Button>(form, "saveTemplate").PerformClick();
                Require(store.Load(Repository).Templates.Count == 2, "Changing the name creates a separate template");
                form.Size = new Size(860, 650); Application.DoEvents();
                Save(form, Path.Combine(artifacts, "commit-message-templates.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form);
                Save(form, Path.Combine(artifacts, "commit-message-templates-minimum.png"));
                accept = false; Field<Button>(form, "deleteTemplate").PerformClick();
                Require(store.Load(Repository).Templates.Count == 2, "Declined delete preserves template");
                accept = true; Field<Button>(form, "deleteTemplate").PerformClick();
                Require(store.Load(Repository).Templates.Count == 1 && !Field<Button>(form, "deleteTemplate").Enabled, "Confirmed delete removes selected template and clears selection");
                Field<Button>(form, "newTemplate").PerformClick();
                Require(Field<TextBox>(form, "templateText").Text.Length == 0 && !Field<Button>(form, "use").Enabled, "New template begins empty and cannot replace draft");
                Field<ListBox>(form, "templates").SelectedIndex = 0; Field<Button>(form, "use").PerformClick();
                Require(form.DialogResult == DialogResult.OK && form.SelectedMessage == "更新说明\r\n已完成验证", "Explicit Use returns complete template text");
            }
            store.RecordSuccess(Repository, message);
            using (var form = new CommitMessageLibraryForm(store, Repository, "draft")) {
                form.Show(); Application.DoEvents(); Field<ListBox>(form, "recent").SelectedIndex = 0;
                Field<Button>(form, "use").PerformClick();
                Require(form.DialogResult == DialogResult.OK && form.SelectedMessage == message, "Explicit Use returns complete recent success");
            }
            using (var form = new CommitMessageLibraryForm(store, Repository, "draft")) {
                form.Show(); Application.DoEvents(); Field<ListBox>(form, "recent").SelectedIndex = 0;
                Field<Button>(form, "close").PerformClick();
                Require(form.DialogResult == DialogResult.Cancel && form.SelectedMessage == null, "Library cancellation does not return selected text");
            }
        }

        private static void TestConcurrentTemplates(string root)
        {
            for (int scenario = 0; scenario < 3; scenario++) {
                var store = new CommitMessageStore(Path.Combine(root, "concurrent-" + scenario));
                if (scenario != 0) store.SaveTemplate(Repository, "同名模板", "最初内容");
                using (var form = new CommitMessageLibraryForm(store, Repository, "")) {
                    form.Show(); Application.DoEvents(); Field<TabControl>(form, "tabs").SelectedIndex = 1;
                    if (scenario != 0) Field<ListBox>(form, "templates").SelectedIndex = 0;
                    Field<TextBox>(form, "templateName").Text = "同名模板";
                    Field<TextBox>(form, "templateText").Text = "本窗口未保存的编辑\r\n第二行";
                    if (scenario == 0) store.SaveTemplate(Repository, "同名模板", "另一窗口已保存");
                    Set(form, "confirm", new Func<string, bool>(text => { store.SaveTemplate(Repository, "同名模板", "另一窗口已保存"); return true; }));
                    Field<Button>(form, scenario == 2 ? "deleteTemplate" : "saveTemplate").PerformClick();
                    Require(store.Load(Repository).Templates.Single().Text == "另一窗口已保存", "Concurrent create/update/delete never silently overwrites latest body: " + scenario);
                    Require(Field<TextBox>(form, "templateText").Text == "本窗口未保存的编辑\r\n第二行" && Field<TextBox>(form, "templateName").Text == "同名模板", "Stale operation preserves unsaved editor: " + scenario);
                    Require(Field<Label>(form, "status").Text.Contains("操作失败"), "Conflict provides visible recovery message");
                    Field<Button>(form, "refresh").PerformClick();
                    Require(Field<TextBox>(form, "templateText").Text == "本窗口未保存的编辑\r\n第二行" && Field<ListBox>(form, "templates").Items.Count == 1, "Explicit refresh updates list while retaining draft");
                    Set(form, "confirm", new Func<string, bool>(text => true));
                    Field<Button>(form, "saveTemplate").PerformClick();
                    Require(store.Load(Repository).Templates.Single().Text == "另一窗口已保存", "Refreshing list cannot silently rebind old editor evidence to the latest template");
                    store.RecordSuccess(Repository, "history to clear");
                    Field<Button>(form, "refresh").PerformClick();
                    Field<TabControl>(form, "tabs").SelectedIndex = 0; Field<Button>(form, "clearRecent").PerformClick();
                    Require(store.Load(Repository).Recent.Count == 0 && Field<TextBox>(form, "templateText").Text == "本窗口未保存的编辑\r\n第二行" && Field<TextBox>(form, "templateName").Text == "同名模板", "Clearing history preserves unsaved template editor on hidden tab");
                    Field<TabControl>(form, "tabs").SelectedIndex = 1; Field<Button>(form, "saveTemplate").PerformClick();
                    Require(store.Load(Repository).Templates.Single().Text == "另一窗口已保存", "History-only reload also preserves original editor evidence");
                    Field<ListBox>(form, "templates").SelectedIndex = 0;
                    Require(Field<TextBox>(form, "templateText").Text == "另一窗口已保存", "Selecting refreshed item reviews latest persisted body");
                    Set(form, "confirm", new Func<string, bool>(text => true));
                    Field<TextBox>(form, "templateText").Text = "重新核对后保存";
                    Field<Button>(form, "saveTemplate").PerformClick();
                    Require(store.Load(Repository).Templates.Single().Text == "重新核对后保存", "Explicit refreshed retry succeeds");
                }
            }
        }

        private static MainForm Open(PlasticClient client, string root, CommitMessageStore store)
        {
            var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root }), false);
            Set(form, "client", client); Set(form, "workspace", client.DiscoverWorkspace(root));
            Set(form, "loaded", true); Set(form, "messageStore", store);
            Field<Label>(form, "scope").Text = "仓库：" + Repository + "\r\n工作区：" + root;
            Set(form, "reportError", new Action<string>(message => { }));
            var row = new PlasticStatusItem { Path = Path.Combine(root, "file.txt"), StatusCode = "CH", StatusDescription = "已修改" };
            Field<ListView>(form, "files").Items.Add(new ListViewItem("file.txt") { Tag = row, Checked = true });
            var workspace = client.DiscoverWorkspace(root);
            var preview = new PlasticCheckinPreview(root, Repository, workspace.Selector, workspace.Name, false,
                new[] { row.Path }, new List<PlasticStatusItem> { row }, new List<PlasticLockItem>(), "", "fingerprint", 0);
            Set(form, "prepareCheckin", new Func<string, IList<string>, string, string, CancellationToken, Task<PlasticCheckinPreview>>((path, paths, repo, selector, token) => Task.FromResult(preview)));
            Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((value, text, uncertain) => DialogResult.OK));
            Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((value, text, token) => Task.FromResult(new PlasticCommandResult { ExitCode = 0 })));
            Set(form, "getPending", new Func<string, CancellationToken, Task<IList<PlasticStatusItem>>>((path, token) => Task.FromResult<IList<PlasticStatusItem>>(new List<PlasticStatusItem>())));
            Invoke(form, "SetBusy", false, ""); form.Show(); Application.DoEvents(); return form;
        }

        private static void TestReplacement(PlasticClient client, string root, string selector, string artifacts)
        {
            var store = new CommitMessageStore(Path.Combine(root, "replacement"));
            using (var form = Open(client, root, store)) {
                var comment = Field<TextBox>(form, "comment"); var button = Field<Button>(form, "messageLibrary");
                int opens = 0, confirmations = 0; bool accept = false; string selection = null, error = "";
                comment.Text = "现有草稿\r\n应保留";
                Set(form, "reportError", new Action<string>(message => error = message));
                Set(form, "showMessageLibrary", new Func<string, string, string>((repo, draft) => {
                    opens++; Require(repo == Repository && draft == comment.Text, "Button passes current repository and complete draft"); return selection;
                }));
                Set(form, "confirmMessageReplacement", new Func<string, bool>(message => { confirmations++; return accept; }));
                button.PerformClick(); Require(opens == 1 && comment.Text.Contains("应保留") && confirmations == 0, "Cancel library leaves current draft unchanged");
                selection = "模板说明\r\n第二行"; button.PerformClick();
                Require(confirmations == 1 && comment.Text.Contains("应保留"), "Declined replacement preserves nonempty draft");
                accept = true; button.PerformClick(); Require(comment.Text == selection && confirmations == 2, "Accepted replacement uses full multiline text");
                button.PerformClick(); Require(confirmations == 2, "Identical message needs no redundant confirmation");
                comment.Clear(); button.PerformClick(); Require(comment.Text == selection && confirmations == 2, "Empty draft accepts explicit Use without overwrite prompt");
                Save(form, Path.Combine(artifacts, "commit-message-main.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form);
                Save(form, Path.Combine(artifacts, "commit-message-main-minimum.png"));
                int previous = opens; Invoke(form, "SetBusy", true, "busy"); button.PerformClick(); Invoke(form, "OpenMessageLibrary");
                Require(!button.Enabled && opens == previous, "Library cannot open during an operation");
                Invoke(form, "SetBusy", false, ""); Set(form, "loaded", false); Invoke(form, "SetBusy", false, "");
                Require(!button.Enabled, "Library disabled until workspace loaded"); Set(form, "loaded", true); Invoke(form, "SetBusy", false, "");
                string original = comment.Text;
                Set(form, "showMessageLibrary", new Func<string, string, string>((repo, draft) => {
                    File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector.Replace("/main", "/main/changed")); return "其他说明";
                }));
                button.PerformClick(); Require(comment.Text == original && error.Contains("已改变"), "Workspace change while library open blocks replacement");
                File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector); error = "";
                Set(form, "showMessageLibrary", new Func<string, string, string>((repo, draft) => "其他说明"));
                Set(form, "confirmMessageReplacement", new Func<string, bool>(message => {
                    File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector.Replace("/main", "/main/changed")); return true;
                }));
                button.PerformClick(); Require(comment.Text == original && error.Contains("已改变"), "Workspace change during overwrite confirmation also blocks replacement");
                File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector);
            }
        }

        private static void TestSubmission(PlasticClient client, string root, int scenario)
        {
            var store = new CommitMessageStore(Path.Combine(root, "submission-" + scenario));
            using (var form = Open(client, root, store)) {
                string message = "成功说明 " + scenario + "\r\n精确保留"; int submits = 0;
                Field<TextBox>(form, "comment").Text = message;
                Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((value, reviewed, uncertain) => {
                    Require(reviewed == message, "Review receives captured message");
                    if (scenario == 0) Field<TextBox>(form, "comment").Text = "review callback changed editor";
                    return scenario == 1 ? DialogResult.Cancel : DialogResult.OK;
                }));
                if (scenario == 2) Set(form, "prepareCheckin", new Func<string, IList<string>, string, string, CancellationToken, Task<PlasticCheckinPreview>>((path, paths, repo, selector, token) => { throw new InvalidOperationException("preflight"); }));
                Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((value, submitted, token) => {
                    submits++; Require(submitted == message, "Submission uses captured reviewed message");
                    if (scenario == 5) throw new IOException("connection failed");
                    if (scenario == 6) throw new OperationCanceledException();
                    return Task.FromResult(new PlasticCommandResult { ExitCode = scenario == 3 ? 7 : scenario == 4 ? -1 : 0, TimedOut = scenario == 4 });
                }));
                Field<Button>(form, "checkin").PerformClick(); Application.DoEvents();
                if (scenario == 0) {
                    Require(store.Load(Repository).Recent.SequenceEqual(new[] { message }), "Only successful captured reviewed message enters local history");
                    Require(Field<TextBox>(form, "comment").Text.Length == 0 && !Field<bool>(form, "submissionUncertain"), "Success keeps existing clear-draft behavior");
                }
                else {
                    Require(store.Load(Repository).Recent.Count == 0, "Cancel, preflight failure, nonzero, timeout, exception and cancellation record no success history: " + scenario);
                    Require(Field<TextBox>(form, "comment").Text == message, "Failed / cancelled checkin preserves draft: " + scenario);
                }
                Require(submits == (scenario == 1 || scenario == 2 ? 0 : 1), "No hidden history-triggered server retry");
                Require(store.Load("other@local").Recent.Count == 0, "Checkin never writes another repository history");
            }
        }

        private static void TestPending(PlasticClient client, string root)
        {
            var store = new CommitMessageStore(Path.Combine(root, "pending"));
            using (var form = Open(client, root, store)) {
                var pending = new TaskCompletionSource<PlasticCommandResult>();
                Field<TextBox>(form, "comment").Text = "等待服务器确认";
                Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((value, text, token) => pending.Task));
                Field<Button>(form, "checkin").PerformClick(); Application.DoEvents();
                Require(!Field<Button>(form, "messageLibrary").Enabled && store.Load(Repository).Recent.Count == 0, "Pending write disables library and records nothing before success");
                pending.SetResult(new PlasticCommandResult { ExitCode = 0 }); PumpUntil(() => !Field<bool>(form, "busy"));
                Require(store.Load(Repository).Recent.Single() == "等待服务器确认" && Field<Button>(form, "messageLibrary").Enabled, "Confirmed completion records message and re-enables library");
            }
        }

        private static void TestStorageFailure(PlasticClient client, string root)
        {
            string blocked = Path.Combine(root, "store-is-a-file"); File.WriteAllText(blocked, "block directory creation");
            using (var form = Open(client, root, new CommitMessageStore(blocked))) {
                int submits = 0, errors = 0; Field<TextBox>(form, "comment").Text = "已成功提交，不可重复";
                Set(form, "reportError", new Action<string>(text => errors++));
                Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((value, text, token) => {
                    submits++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                }));
                Field<Button>(form, "checkin").PerformClick(); Application.DoEvents();
                Require(submits == 1 && errors == 0 && !Field<bool>(form, "submissionUncertain") && !Field<bool>(form, "submissionNeedsRefresh"), "Storage error never changes server success into failure, uncertainty or retry");
                Require(Field<TextBox>(form, "comment").Text.Length == 0 && Field<Button>(form, "checkin").Enabled, "Successful checkin clears draft and restores actions despite history failure");
                Require(Field<TextBox>(form, "output").Text.Contains("无需重新提交") && Field<Label>(form, "status").Text.Contains("历史未保存"), "Nonfatal storage warning remains visible and logged after refresh");
            }
        }

        private static void Bounds(Form form)
        {
            foreach (Control control in Descendants(form).Where(item => item.Visible && (item is Button || item is TextBox || item is ListBox || item is ListView)))
                Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Minimum layout fits " + control.GetType().Name + ": " + control.AccessibleName);
        }
        private static IEnumerable<Control> Descendants(Control parent)
        { foreach (Control child in parent.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
        private static object Invoke(object target, string method, params object[] args) { return target.GetType().GetMethod(method, Flags).Invoke(target, args); }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
        private static void Save(Form form, string path)
        { using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path, ImageFormat.Png); } }
        private static void PumpUntil(Func<bool> complete)
        { var end = DateTime.UtcNow.AddSeconds(10); do { Application.DoEvents(); Thread.Sleep(10); } while (!complete() && DateTime.UtcNow < end); Require(complete(), "Pending UI operation completes"); }
    }
}
