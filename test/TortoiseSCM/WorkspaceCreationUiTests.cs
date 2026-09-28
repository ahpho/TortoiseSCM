// GPL-2.0-or-later. First-workspace dialog behavior and layout checks.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class WorkspaceCreationUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        [STAThread]
        private static int Main(string[] args)
        {
            int exit = 0; Application.EnableVisualStyles();
            EventHandler start = null;
            start = delegate { Application.Idle -= start; try { Run(args.Length == 0 ? "bin/TortoiseSCM/qa/workspace-creation-ui" : args[0]); }
                catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; } finally { Application.ExitThread(); } };
            Application.Idle += start; Application.Run(); return exit;
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts); TestQueries(artifacts); TestCreation(artifacts); TestStartup(artifacts);
            Console.WriteLine("PASS: workspace creation UI (" + assertions + " assertions)");
        }
        private static PlasticRepositoryInfo Repository(string name)
        { return new PlasticRepositoryInfo { Name = name, Server = "localhost:8087", Specification = name + "@localhost:8087", Id = 1, Guid = Guid.NewGuid().ToString() }; }
        private static IList<PlasticRepositoryInfo> Repositories(string name) { return new List<PlasticRepositoryInfo> { Repository(name) }; }
        private static WorkspaceCreationForm Open()
        {
            var form = new WorkspaceCreationForm(@"D:\Workspaces\新项目");
            Field<TextBox>(form, "workspaceName").Text = "my-workspace";
            Set(form, "confirm", new Func<string, bool>(message => true)); form.Show(); Application.DoEvents(); return form;
        }
        private static void QueryResult(WorkspaceCreationForm form)
        {
            Set(form, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>((server, token) => Task.FromResult(Repositories("示例仓库"))));
            Await(InvokeTask(form, "QueryAsync"));
        }
        private static void TestQueries(string artifacts)
        {
            using (var form = Open()) {
                Require(!Field<Button>(form, "create").Enabled && form.SelectedWorkspacePath == null, "Cannot create before repository selection");
                Require(Field<TextBox>(form, "directory").Text == @"D:\Workspaces\新项目", "Initial path retained");
                var old = new TaskCompletionSource<IList<PlasticRepositoryInfo>>(); CancellationToken token = CancellationToken.None;
                Set(form, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>((server, value) => { token = value; return old.Task; }));
                Task pending = InvokeTask(form, "QueryAsync");
                Require(!Field<Button>(form, "query").Enabled && Field<Button>(form, "cancelQuery").Enabled, "Query supports cancellation and blocks duplicates");
                Field<Button>(form, "cancelQuery").PerformClick();
                Require(token.IsCancellationRequested && Field<Button>(form, "query").Enabled, "Cancel permits a new query immediately");
                QueryResult(form); old.SetResult(Repositories("过期仓库")); Await(pending);
                Require(((PlasticRepositoryInfo)Field<ComboBox>(form, "repositories").SelectedItem).Name == "示例仓库", "Late canceled query cannot replace current list");
                Field<TextBox>(form, "server").Text = "other@cloud";
                Require(Field<ComboBox>(form, "repositories").Items.Count == 0 && !Field<Button>(form, "create").Enabled, "Editing server invalidates repository selection");
                Set(form, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>((server, value) => { throw new IOException("连接失败"); }));
                Await(InvokeTask(form, "QueryAsync"));
                Require(Field<TextBox>(form, "status").Text.Contains("连接失败") && !Field<Button>(form, "create").Enabled, "Query failure is visible and cannot create");
                QueryResult(form); Save(form, Path.Combine(artifacts, "workspace-creation.png")); form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form);
                Save(form, Path.Combine(artifacts, "workspace-creation-minimum.png"));
                var late = new TaskCompletionSource<IList<PlasticRepositoryInfo>>();
                Set(form, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>((server, value) => { token = value; return late.Task; }));
                Task closing = InvokeTask(form, "QueryAsync"); form.Close(); Require(token.IsCancellationRequested, "Closing query cancels read"); late.SetResult(Repositories("late")); Await(closing);
            }
        }
        private static void TestCreation(string artifacts)
        {
            for (int scenario = 0; scenario < 5; scenario++) using (var form = Open()) {
                QueryResult(form); int writes = 0; string confirmation = ""; var pending = new TaskCompletionSource<PlasticWorkspaceCreationResult>();
                Set(form, "confirm", new Func<string, bool>(message => { confirmation = message; return false; }));
                Set(form, "createWorkspace", new Func<PlasticRepositoryInfo, string, string, string, IProgress<string>, CancellationToken, Task<PlasticWorkspaceCreationResult>>((repo, name, path, branch, progress, token) => {
                    writes++; Require(repo.Name == "示例仓库" && name == "my-workspace" && path == @"D:\Workspaces\新项目" && branch == "/main", "Captured inputs reach backend unchanged");
                    Require(!token.CanBeCanceled, "Confirmed creation cannot be canceled"); return pending.Task;
                }));
                Await(InvokeTask(form, "CreateAsync")); Require(writes == 0 && form.SelectedWorkspacePath == null, "Declined confirmation makes no changes");
                Require(confirmation.Contains("示例仓库@localhost:8087") && confirmation.Contains("/main") && confirmation.Contains(@"D:\Workspaces\新项目") && confirmation.Contains("整个分支"), "Confirmation describes exact repository branch destination and full download");
                Set(form, "confirm", new Func<string, bool>(message => true)); Task creating = InvokeTask(form, "CreateAsync");
                Require(!Field<Button>(form, "create").Enabled && !Field<Button>(form, "close").Enabled && !Field<TextBox>(form, "server").Enabled, "Creating locks input and close controls");
                Await(InvokeTask(form, "CreateAsync")); form.Close();
                Require(writes == 1 && !form.IsDisposed && form.Visible, "Busy window refuses duplicate create and close");
                if (scenario == 0) pending.SetResult(new PlasticWorkspaceCreationResult { WorkspaceCreated = true, UpdateCompleted = true, WorkspacePath = @"D:\confirmed" });
                else if (scenario == 4) pending.SetException(new ArgumentException("目录不是空目录"));
                else pending.SetResult(new PlasticWorkspaceCreationResult { WorkspaceCreated = scenario == 1, OutcomeUncertain = scenario == 2,
                    WorkspacePath = @"D:\Workspaces\新项目", Stage = scenario == 1 ? "首次更新" : "创建工作区", Error = "模拟失败", RecoveryInstructions = "保留目录，请核查后打开已有工作区继续更新。" });
                Await(creating);
                if (scenario == 0) Require(form.SelectedWorkspacePath == @"D:\confirmed" && form.DialogResult == DialogResult.OK, "Only complete success opens backend-confirmed workspace");
                else {
                    Require(form.SelectedWorkspacePath == null && form.DialogResult != DialogResult.OK, "Failure never reports successful checkout");
                    Require(Field<Button>(form, "close").Enabled, "Failure permits close");
                    if (scenario == 4) Require(Field<TextBox>(form, "status").Text.Contains("目录不是空目录") && Field<Button>(form, "create").Enabled, "Read-only preflight failure permits corrected retry");
                    else {
                        Require(Field<TextBox>(form, "status").Text.Contains("保留目录") && Field<TextBox>(form, "status").Text.Contains(@"D:\Workspaces\新项目"), "Failure retains recovery instructions and path");
                        Require(Field<Button>(form, "create").Enabled == (scenario == 3), "Created or uncertain workspace cannot be blindly created again");
                        if (scenario == 1) { Save(form, Path.Combine(artifacts, "workspace-creation-recovery.png")); }
                    }
                }
            }
        }
        private static void TestStartup(string artifacts)
        {
            using (var form = new StartupForm()) {
                form.Show(); Application.DoEvents(); Save(form, Path.Combine(artifacts, "startup.png")); form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form); Save(form, Path.Combine(artifacts, "startup-minimum.png"));
                Set(form, "createNew", new Func<string>(() => null)); Field<Button>(form, "create").PerformClick();
                Require(form.SelectedWorkspacePath == null && form.Visible, "Canceled wizard retains startup");
                Set(form, "selectExisting", new Func<string>(() => @"D:\invalid"));
                Set(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => { throw new IOException("无效工作区"); }));
                Await(InvokeTask(form, "OpenAsync")); Require(form.SelectedWorkspacePath == null && Field<Label>(form, "status").Text.Contains("无效工作区"), "Invalid existing folder cannot open main window");
                Set(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => Task.FromResult(new PlasticWorkspace { RootPath = @"D:\confirmed-root" })));
                Await(InvokeTask(form, "OpenAsync")); Require(form.SelectedWorkspacePath == @"D:\confirmed-root" && form.DialogResult == DialogResult.OK, "Existing workspace uses verified root");
            }
        }
        private static void Bounds(Control control)
        { foreach (Control child in control.Controls) { if (child.Visible) Require(child.Right <= control.ClientSize.Width + 2 && child.Bottom <= control.ClientSize.Height + 2, "Control fits " + child.GetType().Name); Bounds(child); } }
        private static void Save(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void Await(Task task) { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Application.DoEvents(); }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Flags).SetValue(owner, value); }
        private static Task InvokeTask(object owner, string name) { return (Task)owner.GetType().GetMethod(name, Flags).Invoke(owner, null); }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    }
}