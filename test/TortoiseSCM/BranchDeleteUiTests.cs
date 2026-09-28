// GPL-2.0-or-later. Empty leaf branch deletion GUI regression tests.
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
    internal static class BranchDeleteUiTests
    {
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "bin/TortoiseSCM/qa/branch-delete-ui" : args[0]); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-delete-ui-" + Guid.NewGuid().ToString("N"));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-delete\nunused\nPartial\n");
            string selector = "repository \"ui-delete@local\"\n  path \"/\"\n    branch \"/main\"\n";
            string selectorFile = Path.Combine(metadata, "plastic.selector"); File.WriteAllText(selectorFile, selector);
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
            try {
                TestSnapshotAndConfirmation(client, root, selector, artifacts);
                TestPendingAndFailure(client, root, selector, artifacts);
                TestContext(client, root, selector, selectorFile);
                TestBranchActions(client, root, artifacts);
                Require(File.ReadAllText(selectorFile) == selector, "Delete GUI preserves workspace selector");
                Console.WriteLine("PASS: branch delete UI (" + assertions + " assertions)");
            }
            finally { Directory.Delete(root, true); }
        }

        private static PlasticBranch Branch(string name)
        {
            return new PlasticBranch { Name = name, Parent = "/main", Repository = "ui-delete@local",
                BranchId = 42, Guid = "9ee42c3b-edbc-49f9-a8df-861afc1fd046", HeadChangeset = 17,
                Owner = "owner", Comment = "中文空分支" };
        }

        private static void TestSnapshotAndConfirmation(PlasticClient client, string root, string selector, string artifacts)
        {
            PlasticBranch branch = Branch("/main/空分支 & space");
            using (var form = new BranchDeleteForm(client, root, selector, branch)) {
                form.Show(); Application.DoEvents();
                Require(Field<Button>(form, "delete").Enabled && form.ActiveControl == Field<Button>(form, "close"), "Review starts with cancel focused and an explicit delete action");
                Require(form.AcceptButton == null && form.CancelButton == Field<Button>(form, "close"), "Enter does not implicitly accept deletion; escape cancels");
                Require(Field<TextBox>(form, "details").Text.Contains("ui-delete@local") && Field<TextBox>(form, "details").Text.Contains("cs:17") && Field<TextBox>(form, "details").Text.Contains(branch.Name), "Review exposes literal repository, branch and captured head");
                Save(form, Path.Combine(artifacts, "branch-delete.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); CheckBounds(form);
                Save(form, Path.Combine(artifacts, "branch-delete-minimum.png"));
                int calls = 0; string confirmation = null;
                Set(form, "confirm", new Func<string, bool>(message => { confirmation = message; return false; }));
                Set(form, "deleteBranch", new Func<string, PlasticBranch, string, CancellationToken, Task<PlasticCommandResult>>((path, expected, captured, token) => {
                    calls++; Require(expected.Name == "/main/空分支 & space" && expected.HeadChangeset == 17 && expected.BranchId == 42 && expected.Guid == "9ee42c3b-edbc-49f9-a8df-861afc1fd046", "Submission uses immutable dialog snapshot");
                    Require(path == root && captured == selector && !token.CanBeCanceled, "Submission preserves context and is not cancellable");
                    expected.Name = "/main/delegate-mutated";
                    return Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                }));
                Await(InvokeTask(form, "ConfirmDeleteAsync"));
                Require(calls == 0 && !form.Attempted && confirmation.Contains("其他用户") && confirmation.Contains("不可撤销") && confirmation.Contains(branch.Name), "Declining explicit irreversible warning performs no write");
                branch.Name = "/main/externally-mutated"; branch.HeadChangeset = 999; branch.BranchId = 900; branch.Guid = Guid.NewGuid().ToString();
                Set(form, "confirm", new Func<string, bool>(message => true));
                Await(InvokeTask(form, "ConfirmDeleteAsync"));
                Require(calls == 1 && form.DeletedBranch == "/main/空分支 & space" && form.DialogResult == DialogResult.OK, "Confirmed success returns original snapshot name once, insulated from provider mutation");
            }
        }

        private static void TestPendingAndFailure(PlasticClient client, string root, string selector, string artifacts)
        {
            using (var form = new BranchDeleteForm(client, root, selector, Branch("/main/pending"))) {
                form.Show(); Application.DoEvents();
                var pending = new TaskCompletionSource<PlasticCommandResult>(); int calls = 0;
                Set(form, "deleteBranch", new Func<string, PlasticBranch, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, captured, token) => { calls++; return pending.Task; }));
                Task task = InvokeTask(form, "SubmitAsync");
                Require(form.Attempted && !task.IsCompleted && !Field<Button>(form, "delete").Enabled && !Field<Button>(form, "close").Enabled, "Pending server write locks actions and close");
                form.Close(); Application.DoEvents(); Require(!form.IsDisposed && form.Visible, "Window close is blocked during server write");
                Await(InvokeTask(form, "SubmitAsync")); Require(calls == 1, "Repeated submit while pending cannot duplicate write");
                pending.SetResult(new PlasticCommandResult { ExitCode = -1, TimedOut = true, Error = "timeout" }); Await(task);
                Require(form.DeletedBranch == null && !Field<Button>(form, "delete").Enabled && Field<Button>(form, "close").Enabled, "Uncertain write permits close but no stale retry");
                Require(Field<Label>(form, "status").Text.Contains("未确认") && Field<Label>(form, "status").Text.Contains("不会自动"), "Failure tells user to refresh and avoids claiming rollback");
                Await(InvokeTask(form, "SubmitAsync")); Require(calls == 1, "Failure cannot be retried using stale identity");
                Save(form, Path.Combine(artifacts, "branch-delete-uncertain.png")); form.Close();
            }
            using (var form = new BranchDeleteForm(client, root, selector, Branch("/main/pending-success"))) {
                form.Show(); Application.DoEvents();
                var pending = new TaskCompletionSource<PlasticCommandResult>();
                Set(form, "deleteBranch", new Func<string, PlasticBranch, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, captured, token) => pending.Task));
                Task task = InvokeTask(form, "SubmitAsync");
                Require(!task.IsCompleted && form.DeletedBranch == null, "Delayed deletion cannot report early success");
                pending.SetResult(new PlasticCommandResult { ExitCode = 0 }); Await(task);
                Require(form.DeletedBranch == "/main/pending-success" && form.DialogResult == DialogResult.OK && !form.Visible, "Delayed success closes only after verified provider completion");
            }
        }

        private static void TestContext(PlasticClient client, string root, string selector, string selectorFile)
        {
            using (var form = new BranchDeleteForm(client, root, selector, Branch("/main/context"))) {
                int calls = 0;
                Set(form, "deleteBranch", new Func<string, PlasticBranch, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, captured, token) => { calls++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                File.WriteAllText(selectorFile, selector.Replace("/main", "/other"));
                Await(InvokeTask(form, "SubmitAsync"));
                Require(calls == 0 && form.Attempted && Field<Label>(form, "status").Text.Contains("工作区仓库或分支已改变"), "Same-repository selector change blocks write and stale retry");
                File.WriteAllText(selectorFile, selector);
            }
            using (var form = new BranchDeleteForm(client, root, selector, Branch("/main/post-context"))) {
                var pending = new TaskCompletionSource<PlasticCommandResult>();
                Set(form, "deleteBranch", new Func<string, PlasticBranch, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, captured, token) => pending.Task));
                Task task = InvokeTask(form, "SubmitAsync"); File.WriteAllText(selectorFile, selector.Replace("ui-delete@local", "other@local"));
                pending.SetResult(new PlasticCommandResult { ExitCode = 0 }); Await(task);
                Require(form.DeletedBranch == null && Field<Label>(form, "status").Text.Contains("未确认"), "Context drift during successful response does not claim verified success");
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
                var missingHead = Branch("/main/missing-head"); missingHead.HeadChangeset = -1;
                Require(!BranchDeleteForm.CanDelete(missingHead), "Missing head disables delete action");
                var quote = Branch("/main/quote'name");
                var quoteRepo = Branch("/main/quote-repository"); quoteRepo.Repository = "ui'delete@local";
                Require(!BranchDeleteForm.CanDelete(quoteRepo), "Quoted repository disables unsupported query interpolation");
                var leaf = Branch("/main/leaf");
                var entries = new List<PlasticBranch> { rootBranch, parent, child, current, missingId, missingGuid, quote, leaf };
                Set(form, "entries", entries); Set(form, "partial", true); Invoke(form, "RenderBranches");
                foreach (var branch in entries) {
                    Invoke(form, "SelectBranch", branch.Name); Application.DoEvents();
                    Require(Field<ToolStripMenuItem>(form, "deleteBranch").Enabled == Object.ReferenceEquals(branch, leaf), "List guards shape/current/root/children/identity/head: " + branch.Name);
                }
                Field<TextBox>(form, "filter").Text = "parent"; Application.DoEvents(); Invoke(form, "SelectBranch", parent.Name);
                Require(!Field<ToolStripMenuItem>(form, "deleteBranch").Enabled, "Filtered-out native Parent child still blocks deletion");
                Field<TextBox>(form, "filter").Clear(); Invoke(form, "SelectBranch", leaf.Name);
                var menu = Field<ListView>(form, "branches").ContextMenuStrip;
                Require(menu.Items.Contains(Field<ToolStripMenuItem>(form, "deleteBranch")) && Object.ReferenceEquals(menu, Field<TreeView>(form, "branchTree").ContextMenuStrip), "List and tree expose same deletion context action");
                Field<ComboBox>(form, "viewMode").SelectedIndex = 1; Application.DoEvents();
                Require(Field<ToolStripMenuItem>(form, "deleteBranch").Enabled, "Partial tree leaf offers delete action");
                using (var dialog = (BranchDeleteForm)Invoke(form, "CreateDeleteDialog"))
                    Require(Field<PlasticBranch>(dialog, "expected").Name == leaf.Name, "Tree dialog captures selected leaf");
                Set(form, "busy", true); Invoke(form, "UpdateButtons");
                Require(!Field<ToolStripMenuItem>(form, "deleteBranch").Enabled, "Busy branch load blocks destructive action");
                Set(form, "busy", false); Invoke(form, "UpdateButtons");
                form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "refresh", "cancel", "rename", "head", "merge", "switchBranch", "close" })
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(Field<Control>(form, field).RectangleToScreen(Field<Control>(form, field).ClientRectangle)), "Branch footer fits minimum width: " + field);
                Save(form, Path.Combine(artifacts, "branch-delete-tree-minimum.png")); form.Close();
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
