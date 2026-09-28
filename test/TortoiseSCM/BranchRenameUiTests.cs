// GPL-2.0-or-later. Branch metadata rename GUI regression tests.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class BranchRenameUiTests
    {
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "qa/branch-rename-ui" : args[0]); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-rename-ui-" + Guid.NewGuid().ToString("N"));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-rename\nunused\nPartial\n");
            string selector = "repository \"ui-rename@local\"\n  path \"/\"\n    branch \"/main\"\n";
            string selectorFile = Path.Combine(metadata, "plastic.selector"); File.WriteAllText(selectorFile, selector);
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
            try {
                TestInputAndSnapshot(client, root, selector, artifacts);
                TestPendingAndFailure(client, root, selector, artifacts);
                TestContext(client, root, selector, selectorFile);
                TestBranchActions(client, root, artifacts);
                Require(File.ReadAllText(selectorFile) == selector, "Rename GUI preserves workspace selector");
                Console.WriteLine("PASS: branch rename UI (" + assertions + " assertions)");
            }
            finally { Directory.Delete(root, true); }
        }

        private static PlasticBranch Branch(string name)
        {
            return new PlasticBranch { Name = name, Parent = "/main", Repository = "ui-rename@local",
                BranchId = 42, Guid = "9ee42c3b-edbc-49f9-a8df-861afc1fd046", HeadChangeset = 17,
                Owner = "owner", Comment = "中文分支" };
        }

        private static void TestInputAndSnapshot(PlasticClient client, string root, string selector, string artifacts)
        {
            PlasticBranch branch = Branch("/main/原名称");
            using (var form = new BranchRenameForm(client, root, selector, branch)) {
                form.Show(); Application.DoEvents();
                var name = Field<TextBox>(form, "name"); var action = Field<Button>(form, "rename");
                Require(!action.Enabled, "Unchanged name cannot submit");
                foreach (string invalid in new[] { "", "  ", ".", "..", "-flag", "a/b", "a\\b", "a@b", "a#b", "a:b", "a?b", "a\"b", "a'b", "a\tb" }) {
                    name.Text = invalid; Require(!action.Enabled, "Invalid leaf blocks submit: " + invalid);
                }
                name.Text = "新名称 & space";
                Require(action.Enabled && Field<TextBox>(form, "fullName").Text == "/main/新名称 & space", "Unicode, spaces and ampersands preserve literal preview");
                Require(Field<TextBox>(form, "details").Text.Contains("ui-rename@local") && Field<TextBox>(form, "details").Text.Contains("cs:17"), "Review exposes repository and captured head");
                Save(form, Path.Combine(artifacts, "branch-rename.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); CheckBounds(form);
                Save(form, Path.Combine(artifacts, "branch-rename-minimum.png"));
                int calls = 0; string confirmation = null;
                Set(form, "confirm", new Func<string, bool>(message => { confirmation = message; return false; }));
                Set(form, "renameBranch", new Func<string, PlasticBranch, string, string, CancellationToken, Task<PlasticCommandResult>>((path, expected, leaf, captured, token) => {
                    calls++; Require(expected.Name == "/main/原名称" && expected.HeadChangeset == 17 && expected.BranchId == 42 && expected.Guid == "9ee42c3b-edbc-49f9-a8df-861afc1fd046", "Submission uses immutable dialog snapshot");
                    Require(leaf == "新名称 & space" && path == root && captured == selector && !token.CanBeCanceled, "Submission preserves path/name/context and is not cancellable");
                    return Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                }));
                Await(InvokeTask(form, "ConfirmRenameAsync"));
                Require(calls == 0 && !form.Attempted && confirmation.Contains("其他用户") && confirmation.Contains("原名称"), "Declining explicit metadata warning performs no write");
                branch.Name = "/main/externally-mutated"; branch.HeadChangeset = 999; branch.BranchId = 900; branch.Guid = Guid.NewGuid().ToString();
                Set(form, "confirm", new Func<string, bool>(message => true));
                Await(InvokeTask(form, "ConfirmRenameAsync"));
                Require(calls == 1 && form.RenamedBranch == "/main/新名称 & space" && form.DialogResult == DialogResult.OK, "Confirmed success returns exact new name once");
            }
        }

        private static void TestPendingAndFailure(PlasticClient client, string root, string selector, string artifacts)
        {
            using (var form = new BranchRenameForm(client, root, selector, Branch("/main/pending"))) {
                form.Show(); Application.DoEvents(); Field<TextBox>(form, "name").Text = "renamed";
                var pending = new TaskCompletionSource<PlasticCommandResult>(); int calls = 0;
                Set(form, "renameBranch", new Func<string, PlasticBranch, string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, leaf, captured, token) => { calls++; return pending.Task; }));
                Task task = InvokeTask(form, "SubmitAsync");
                Require(form.Attempted && !task.IsCompleted && !Field<Button>(form, "rename").Enabled && !Field<Button>(form, "close").Enabled && !Field<TextBox>(form, "name").Enabled, "Pending server write locks editor/actions/close");
                form.Close(); Application.DoEvents(); Require(!form.IsDisposed && form.Visible, "Window close is blocked during metadata write");
                Await(InvokeTask(form, "SubmitAsync")); Require(calls == 1, "Repeated submit while pending cannot duplicate write");
                pending.SetResult(new PlasticCommandResult { ExitCode = -1, TimedOut = true, Error = "timeout" }); Await(task);
                Require(form.RenamedBranch == null && !Field<Button>(form, "rename").Enabled && Field<Button>(form, "close").Enabled, "Uncertain write permits close but no stale retry");
                Require(Field<Label>(form, "status").Text.Contains("未确认") && Field<Label>(form, "status").Text.Contains("不会自动"), "Failure tells user to refresh and forbids implied rollback");
                Await(InvokeTask(form, "SubmitAsync")); Require(calls == 1, "Failure cannot be retried using stale identity");
                Save(form, Path.Combine(artifacts, "branch-rename-uncertain.png")); form.Close();
            }
            using (var form = new BranchRenameForm(client, root, selector, Branch("/main/pending-success"))) {
                form.Show(); Application.DoEvents(); Field<TextBox>(form, "name").Text = "completed";
                var pending = new TaskCompletionSource<PlasticCommandResult>();
                Set(form, "renameBranch", new Func<string, PlasticBranch, string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, leaf, captured, token) => pending.Task));
                Task task = InvokeTask(form, "SubmitAsync");
                Require(!task.IsCompleted && form.RenamedBranch == null, "Delayed rename cannot report early success");
                pending.SetResult(new PlasticCommandResult { ExitCode = 0 }); Await(task);
                Require(form.RenamedBranch == "/main/completed" && form.DialogResult == DialogResult.OK && !form.Visible, "Delayed success closes only after verified provider completion");
            }
        }

        private static void TestContext(PlasticClient client, string root, string selector, string selectorFile)
        {
            using (var form = new BranchRenameForm(client, root, selector, Branch("/main/context"))) {
                Field<TextBox>(form, "name").Text = "changed"; int calls = 0;
                Set(form, "renameBranch", new Func<string, PlasticBranch, string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, leaf, captured, token) => { calls++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                File.WriteAllText(selectorFile, selector.Replace("/main", "/other"));
                Await(InvokeTask(form, "SubmitAsync"));
                Require(calls == 0 && form.Attempted && Field<Label>(form, "status").Text.Contains("工作区仓库或分支已改变"), "Same-repository selector change blocks write and stale retry");
                File.WriteAllText(selectorFile, selector);
            }
            using (var form = new BranchRenameForm(client, root, selector, Branch("/main/post-context"))) {
                Field<TextBox>(form, "name").Text = "changed";
                var pending = new TaskCompletionSource<PlasticCommandResult>();
                Set(form, "renameBranch", new Func<string, PlasticBranch, string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, leaf, captured, token) => pending.Task));
                Task task = InvokeTask(form, "SubmitAsync"); File.WriteAllText(selectorFile, selector.Replace("ui-rename@local", "other@local"));
                pending.SetResult(new PlasticCommandResult { ExitCode = 0 }); Await(task);
                Require(form.RenamedBranch == null && Field<Label>(form, "status").Text.Contains("未确认"), "Context drift during successful response does not claim verified success");
                File.WriteAllText(selectorFile, selector);
            }
        }

        private static void TestBranchActions(PlasticClient client, string root, string artifacts)
        {
            using (var form = new BranchForm(client, root)) {
                Field<CancellationTokenSource>(form, "lifetime").Cancel(); form.Show(); Application.DoEvents();
                var rootBranch = Branch("/main"); rootBranch.Parent = "";
                var parent = Branch("/main/parent");
                var child = Branch("/independent-child"); child.Parent = parent.Name;
                var current = Branch("/main/current"); current.IsCurrent = true;
                var missingId = Branch("/main/missing-id"); missingId.BranchId = 0;
                var missingGuid = Branch("/main/missing-guid"); missingGuid.Guid = "invalid";
                var leaf = Branch("/main/leaf");
                var entries = new List<PlasticBranch> { rootBranch, parent, child, current, missingId, missingGuid, leaf };
                Set(form, "entries", entries); Set(form, "partial", true); Invoke(form, "RenderBranches");
                foreach (var branch in entries) {
                    Invoke(form, "SelectBranch", branch.Name); Application.DoEvents();
                    Require(Field<Button>(form, "rename").Enabled == Object.ReferenceEquals(branch, leaf), "List guards branch shape/current/root/children/identity: " + branch.Name);
                }
                Field<TextBox>(form, "filter").Text = "parent"; Application.DoEvents(); Invoke(form, "SelectBranch", parent.Name);
                Require(!Field<Button>(form, "rename").Enabled, "Filtered-out child still blocks parent rename through native Parent relation");
                Field<TextBox>(form, "filter").Clear(); Invoke(form, "SelectBranch", leaf.Name);
                Field<ComboBox>(form, "viewMode").SelectedIndex = 1; Application.DoEvents();
                Require(Field<Button>(form, "rename").Enabled && Field<ToolStripMenuItem>(form, "renameBranch").Enabled, "Partial tree leaf offers matching button/context action");
                using (var dialog = (BranchRenameForm)Invoke(form, "CreateRenameDialog"))
                    Require(Field<PlasticBranch>(dialog, "expected").Name == leaf.Name, "Tree dialog captures selected leaf");
                form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "refresh", "cancel", "rename", "head", "merge", "switchBranch", "close" })
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(Field<Control>(form, field).RectangleToScreen(Field<Control>(form, field).ClientRectangle)), "Branch footer fits minimum width: " + field);
                Save(form, Path.Combine(artifacts, "branch-rename-tree-minimum.png")); form.Close();
            }
        }

        private static void CheckBounds(Control control)
        {
            foreach (Control child in control.Controls) {
                if (!child.Visible) continue;
                Require(control.ClientRectangle.Contains(child.Bounds), "Child fits parent: " + child.GetType().Name + " " + child.Bounds + " in " + control.GetType().Name + " " + control.ClientRectangle);
                CheckBounds(child);
            }
        }
        private static void Save(Form form, string path)
        { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static object Invoke(object target, string name, params object[] args)
        { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
        private static Task InvokeTask(object target, string name) { return (Task)Invoke(target, name); }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static void Await(Task task)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (!task.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
            if (!task.IsCompleted) throw new Exception("UI task timed out."); task.GetAwaiter().GetResult(); Application.DoEvents();
        }
        private static void Require(bool condition, string message)
        { if (!condition) throw new Exception(message); assertions++; }
    }
}
