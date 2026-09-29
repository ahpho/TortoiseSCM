// GPL-2.0-or-later. Real launch routing and update confirmation regression coverage.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class LaunchRoutingUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "bin/TortoiseSCM/qa/launch-routing" : args[0]); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-launch-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string first = CreateWorkspace(Path.Combine(root, "中文 workspace"));
                string second = CreateWorkspace(Path.Combine(root, "other"));
                string file = Path.Combine(first, "original selected.txt"); File.WriteAllText(file, "original");
                using (var history = Program.CreateLaunchForm(Request("history", file)))
                {
                    Require(history is HistoryForm && history.Text.Contains("历史记录"), "History launch directly selects its own form");
                    Require(Field<string>(history, "path") == file, "History retains exact original selection instead of pending checked items");
                    Require(Field<string>(history, "workspaceRoot") == first && !Field<bool>(history, "wholeWorkspace"), "File history retains workspace context and file scope");
                }
                using (var history = Program.CreateLaunchForm(Request("history", first)))
                    Require(Field<bool>(history, "wholeWorkspace"), "Root history covers the workspace");
                Reject(() => Program.CreateLaunchForm(Request("history", first, file)), "History refuses multiple paths");
                Reject(() => Program.CreateLaunchForm(Request("history", first, second)), "History refuses mixed workspaces");
                Reject(() => Program.CreateLaunchForm(Request("history", root)), "History refuses a non-workspace path");
                using (var checkin = Program.CreateLaunchForm(Request("checkin", first)))
                    Require(checkin is MainForm, "Checkin retains the pending-change form");
                CheckAllContextMenuRoutes(first, file);

                int writes = 0;
                using (var canceled = CreateUpdate(file, first, false))
                {
                    Inject(canceled, "run", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>((request, token) => { writes++; return Task.FromResult(new PlasticCommandResult()); }));
                    canceled.Show(); Pump(() => Field<Button>(canceled, "update").Enabled);
                    Require(writes == 0, "Opening Update performs no update");
                    Field<Button>(canceled, "close").PerformClick();
                    Require(!canceled.Visible && writes == 0, "Cancel before confirmation never writes");
                }
                using (var update = CreateUpdate(file, first, false))
                {
                    var pending = new TaskCompletionSource<PlasticCommandResult>();
                    PlasticCommandRequest captured = null;
                    Inject(update, "run", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>((request, token) => { writes++; captured = request; return pending.Task; }));
                    update.Show(); Pump(() => Field<Button>(update, "update").Enabled);
                    Require(update.Text == "更新 - TortoiseSCM", "Update has its own title");
                    Require(Field<Label>(update, "scope").Text.Contains("整体更新") && !Field<TextBox>(update, "paths").Visible, "Standard update shows the workspace scope once without duplicate path box");
                    Require(update.AcceptButton == Field<Button>(update, "close"), "Enter cannot accidentally start update");
                    Bounds(update); Save(update, Path.Combine(artifacts, "update-dialog.png"));
                    update.Size = update.MinimumSize; Application.DoEvents(); Bounds(update);
                    Save(update, Path.Combine(artifacts, "update-dialog-minimum.png"));
                    Field<Button>(update, "update").PerformClick(); Pump(() => captured != null);
                    Require(captured.Command == PlasticCommand.Update && captured.Paths.Count == 1 && captured.Paths[0] == first && captured.Recursive, "Confirmed Standard update calls backend once with workspace root");
                    update.Close(); Application.DoEvents();
                    Require(update.Visible && !Field<Button>(update, "close").Enabled, "Closing is blocked while update writes");
                    Field<Button>(update, "update").PerformClick(); Require(writes == 1, "Busy dialog prevents duplicate updates");
                    pending.SetResult(new PlasticCommandResult { Output = "updated native output" });
                    Pump(() => !Field<bool>(update, "busy"));
                    Require(Field<Label>(update, "status").Text == "更新完成。" && Field<TextBox>(update, "output").Text.Contains("updated native output"), "Success preserves native result and completion status");
                    Require(!Field<Button>(update, "update").Enabled, "Completed update requires a fresh scope review before repetition");
                    update.Close();
                }
                foreach (bool timeout in new[] { false, true })
                {
                    using (var update = CreateUpdate(file, first, true))
                    {
                        PlasticCommandRequest captured = null; int calls = 0;
                        Inject(update, "run", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>((request, token) => {
                            captured = request; calls++; return Task.FromResult(new PlasticCommandResult { ExitCode = 7, TimedOut = timeout, Error = "native failure detail" }); }));
                        update.Show(); Pump(() => Field<Button>(update, "update").Enabled);
                        Require(Field<Label>(update, "scope").Text.Contains("仅更新") && Field<TextBox>(update, "paths").Text == file, "Partial update previews selected path");
                        Field<Button>(update, "update").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                        Require(captured != null && captured.Paths.Count == 1 && captured.Paths[0] == file, "Partial update preserves exact selected scope");
                        Require(Field<TextBox>(update, "output").Text.Contains("native failure detail") && Field<Label>(update, "status").Text.Contains(timeout ? "更新超时" : "更新未成功"), "Failure and timeout remain explicit with native detail");
                        Field<Button>(update, "update").PerformClick();
                        Require(calls == 1 && !Field<Button>(update, "update").Enabled, "Failed update does not retry without fresh scope review");
                        update.Close();
                    }
                }
                using (var update = CreateUpdate(file, first, false))
                {
                    int calls = 0;
                    Inject(update, "run", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>((request, token) => { calls++; return Task.FromResult(new PlasticCommandResult()); }));
                    update.Show(); Pump(() => Field<Button>(update, "update").Enabled);
                    Inject(update, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => Task.FromResult(Workspace(first, true))));
                    Field<Button>(update, "update").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                    Require(calls == 0 && Field<TextBox>(update, "output").Text.Contains("已改变"), "Workspace mode change blocks write until renewed confirmation");
                    update.Close();
                }
                using (var update = (UpdateForm)Program.CreateLaunchForm(Request("update", first, second)))
                {
                    update.Show(); Pump(() => !Field<bool>(update, "busy"));
                    Require(!Field<Button>(update, "update").Enabled && Field<TextBox>(update, "output").Text.Contains("同一"), "Update refuses mixed workspaces before any command"); update.Close();
                }
                using (var update = (UpdateForm)Program.CreateLaunchForm(Request("update", file, Path.Combine(first, "second.txt"))))
                {
                    Inject(update, "previewConflicts", new Func<string, CancellationToken, Task<System.Collections.Generic.IList<PlasticPartialConflict>>>((path, token) => Task.FromResult<System.Collections.Generic.IList<PlasticPartialConflict>>(new System.Collections.Generic.List<PlasticPartialConflict>())));
                    Inject(update, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => Task.FromResult(Workspace(first, true))));
                    PlasticCommandRequest captured = null;
                    Inject(update, "run", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>((request, token) => { captured = request; throw new IOException("native process launch failed"); }));
                    update.Show(); Pump(() => Field<Button>(update, "update").Enabled);
                    Field<Button>(update, "update").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                    Require(captured != null && captured.Paths.Count == 2 && captured.Paths[0] == file && captured.Paths[1] == Path.Combine(first, "second.txt"), "Partial multi-selection is never widened to a common parent");
                    Require(Field<TextBox>(update, "output").Text.Contains("native process launch failed") && !Field<Button>(update, "update").Enabled, "Launch exceptions retain diagnostics and prevent an implicit retry");
                    Inject(update, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => { throw new IOException("scope failed"); }));
                    Field<Button>(update, "refresh").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                    Require(!Field<Button>(update, "update").Enabled && Field<TextBox>(update, "paths").Text.Length == 0, "Failed scope refresh clears stale paths and blocks update");
                    update.Close();
                }
                Console.WriteLine("PASS: launch routing UI (" + assertions + " assertions)");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void CheckAllContextMenuRoutes(string workspace, string file)
        {
            // Keep this list in lockstep with PlasticShell's expected menu verbs.
            // The native ShellTests exercise COM visibility and dispatch; this
            // matrix verifies that every dispatched verb reaches the intended
            // WinForms surface instead of silently opening the check-in editor.
            string[] commands = {
                "update", "checkin", "diff", "history", "add", "checkout", "undo",
                "move", "remove", "ignore", "branches", "merge", "shelves", "labels",
                "repository-browser", "revision-graph", "blame", "export", "recover", "rollback",
                "locks", "unlock", "gluon", "settings", "version", "create-workspace"
            };
            foreach (string command in commands)
                Require(LaunchRequest.Parse(new[] { "--command", command, "--path", file }).Command == command,
                    "Context menu verb is accepted: " + command);

            foreach (string command in new[] { "update" })
                using (var form = Program.CreateLaunchForm(Request(command, file)))
                    Require(form is UpdateForm, command + " opens UpdateForm");
            using (var history = Program.CreateLaunchForm(Request("history", file)))
                Require(history is HistoryForm, "history opens HistoryForm");
            foreach (string command in new[] { "add", "checkout", "undo" })
                using (var form = Program.CreateLaunchForm(Request(command, file)))
                {
                    Require(form is OperationForm, command + " opens OperationForm");
                    Require(!(form is MainForm), command + " never opens the check-in editor");
                }
            foreach (string command in new[] { "locks", "unlock" })
                using (var form = Program.CreateLaunchForm(Request(command, workspace)))
                    Require(form is LocksForm && !(form is MainForm), command + " opens LocksForm");

            foreach (string command in new[] {
                "checkin", "diff", "move", "remove", "ignore", "branches", "merge", "shelves", "labels",
                "repository-browser", "revision-graph", "blame", "export", "recover", "rollback", "gluon"
            })
                using (var form = Program.CreateLaunchForm(Request(command, file)))
                    Require(form is MainForm, command + " retains MainForm compatibility route");

            // Settings, version information and checkout-repository are handled
            // by Program.Main before a workspace form is created. Their parser
            // coverage above ensures the native verbs cannot drift or disappear.
        }

        private static UpdateForm CreateUpdate(string selected, string root, bool partial)
        {
            var form = Program.CreateLaunchForm(Request("update", selected)) as UpdateForm;
            Require(form != null, "Production routing opens standalone UpdateForm");
            Inject(form, "previewConflicts", new Func<string, CancellationToken, Task<System.Collections.Generic.IList<PlasticPartialConflict>>>((path, token) => Task.FromResult<System.Collections.Generic.IList<PlasticPartialConflict>>(new System.Collections.Generic.List<PlasticPartialConflict>())));
            Inject(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => Task.FromResult(Workspace(root, partial))));
            return form;
        }
        private static PlasticWorkspace Workspace(string root, bool partial)
        { var workspace = new PlasticClient(new PlasticClientConfig()).DiscoverWorkspace(root); workspace.IsPartial = partial; return workspace; }
        private static string CreateWorkspace(string root)
        {
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "ui\nunused\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"ui@local\"\n  path \"/\"\n    branch \"/main\"\n");
            return root;
        }
        private static LaunchRequest Request(string command, params string[] paths)
        { var result = new LaunchRequest { Command = command }; result.Paths.AddRange(paths); return result; }
        private static void Reject(Func<Form> create, string message)
        { bool rejected = false; try { using (var form = create()) { } } catch (ArgumentException) { rejected = true; } Require(rejected, message); }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static void Inject(object owner, string name, object value) { owner.GetType().GetField(name, Flags).SetValue(owner, value); }
        private static void Pump(Func<bool> condition)
        { var stop = DateTime.UtcNow.AddSeconds(5); do { Application.DoEvents(); Thread.Sleep(10); } while (!condition() && DateTime.UtcNow < stop); Require(condition(), "Async UI operation completes"); }
        private static void Bounds(Control parent)
        { foreach (Control child in parent.Controls) { if (child.Visible) Require(parent.ClientRectangle.Contains(child.Bounds), "Control fits: " + child.GetType().Name); Bounds(child); } }
        private static void Save(Form form, string path)
        { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception("Launch routing: " + message); }
    }
}
