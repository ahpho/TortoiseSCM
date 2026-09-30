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
            assertions = 0; Directory.CreateDirectory(artifacts); TestDefaults(artifacts); TestRepositoryNaming(artifacts); TestQueries(artifacts); TestCreation(artifacts); TestCreationRoutes(artifacts); TestStartup(artifacts);
            Console.WriteLine("PASS: workspace creation UI (" + assertions + " assertions)");
        }
        private static PlasticRepositoryInfo Repository(string name)
        { return new PlasticRepositoryInfo { Name = name, Server = "localhost:8087", Specification = name + "@localhost:8087", Id = 1, Guid = Guid.NewGuid().ToString() }; }
        private static IList<PlasticRepositoryInfo> Repositories(string name) { return new List<PlasticRepositoryInfo> { Repository(name) }; }
        private static WorkspaceCreationForm Open()
        {
            var form = new WorkspaceCreationForm(@"D:\Workspaces\新项目");
            EmptyWorkspaces(form);
            Field<TextBox>(form, "workspaceName").Text = "my-workspace";
            Set(form, "confirm", new Func<string, bool>(message => true)); form.Show(); Application.DoEvents(); return form;
        }
        private static void QueryResult(WorkspaceCreationForm form)
        {
            Set(form, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>((server, token) => Task.FromResult(Repositories("示例仓库"))));
            Await(InvokeTask(form, "QueryAsync"));
        }
        private static void EmptyWorkspaces(WorkspaceCreationForm form)
        { Set(form, "getWorkspaces", new Func<CancellationToken, Task<IList<PlasticWorkspace>>>(token => Task.FromResult<IList<PlasticWorkspace>>(new List<PlasticWorkspace>()))); }
        private static void TestDefaults(string artifacts)
        {
            var request = LaunchRequest.Parse(new[] { "--command", "create-workspace", "--parent-path", @"D:\" });
            Require(request.ParentPath == @"D:\" && request.Paths.Count == 0, "Shell parent root is distinct from exact destination");
            request = LaunchRequest.Parse(new[] { "--command", "create-workspace", "--path", @"D:\Exact 中文" });
            Require(request.ParentPath == null && request.Paths[0] == @"D:\Exact 中文", "Explicit destination remains exact");
            foreach (var args in new[] {
                new[] { "--command", "status", "--parent-path", @"D:\" },
                new[] { "--command", "create-workspace", "--parent-path", @"D:\", "--path", @"D:\Exact" },
                new[] { "--command", "create-workspace", "--parent-path", @"D:\", "--parent-path", @"D:\" },
                new[] { "--command", "create-workspace", "--parent-path", "relative" },
                new[] { "--command", "create-workspace", "--parent-path", @"D:relative" },
                new[] { "--command", "create-workspace", "--parent-path", @"\relative" },
                new[] { "--command", "create-workspace", "--parent-path", "" } }) {
                bool rejected = false; try { LaunchRequest.Parse(args); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Invalid checkout parent arguments rejected");
            }
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-defaults-" + Guid.NewGuid().ToString("N"), "目录 with spaces");
            Directory.CreateDirectory(root);
            try {
                var none = new List<PlasticWorkspace>();
                var suggestion = WorkspaceCreationSuggestion.Choose(root, none);
                Require(suggestion.Name == "TestSCM" && suggestion.Path == Path.Combine(root, "TestSCM"), "Unicode and space parent keeps TestSCM default child");
                using (var rootForm = new WorkspaceCreationForm(null, @"D:\")) {
                    Require(Path.GetDirectoryName(Field<TextBox>(rootForm, "directory").Text) == @"D:\", "Drive root produces child destination instead of targeting root");
                    Require(Field<ComboBox>(rootForm, "mode").SelectedIndex == 0, "Gluon is the default workspace mode");
                    Require(Field<TextBox>(rootForm, "server").Text == "plasticscm7.seasungame.com:8087", "Checkout defaults to the requested repository server");
                    Require(Field<Button>(rootForm, "create").Text == "拉取", "Checkout button describes pulling without promising another window");
                }
                Directory.CreateDirectory(Path.Combine(root, "TestSCM"));
                File.WriteAllText(Path.Combine(root, "TestSCM2"), "preserve");
                var registered = new List<PlasticWorkspace> { new PlasticWorkspace { Name = "TESTscm3", RootPath = Path.Combine(root, "other") },
                    new PlasticWorkspace { Name = "unrelated", RootPath = Path.Combine(root, "TestSCM4", "nested") } };
                suggestion = WorkspaceCreationSuggestion.Choose(root, registered);
                Require(suggestion.Name == "TestSCM5" && suggestion.Path == Path.Combine(root, "TestSCM5"), "Avoids directory, file, case-insensitive workspace name, and registered child collision");
                using (var form = new WorkspaceCreationForm(null, root)) {
                    var pending = new TaskCompletionSource<IList<PlasticWorkspace>>();
                    Set(form, "getWorkspaces", new Func<CancellationToken, Task<IList<PlasticWorkspace>>>(token => pending.Task));
                    form.Show(); Application.DoEvents();
                    Require(!Field<Button>(form, "create").Enabled, "Cannot create while local names are unknown");
                    Field<TextBox>(form, "workspaceName").Text = "my-name";
                    Field<TextBox>(form, "directory").Text = Path.Combine(root, "my-path");
                    pending.SetResult(registered); Await(Task.FromResult(0));
                    Require(Field<TextBox>(form, "workspaceName").Text == "my-name" && Field<TextBox>(form, "directory").Text == Path.Combine(root, "my-path"), "Delayed suggestions preserve both user edits");
                    Set(form, "getWorkspaces", new Func<CancellationToken, Task<IList<PlasticWorkspace>>>(token => Task.FromResult<IList<PlasticWorkspace>>(registered)));
                    Set(form, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>((server, token) => Task.FromResult<IList<PlasticRepositoryInfo>>(new List<PlasticRepositoryInfo> { Repository("Alpha"), Repository("TestSCM") })));
                    Await(InvokeTask(form, "QueryAsync"));
                    Require(((PlasticRepositoryInfo)Field<ComboBox>(form, "repositories").SelectedItem).Name == "TestSCM", "Prefers existing TestSCM remote repository without creating or renaming it");
                    form.Close();
                }
                using (var form = new WorkspaceCreationForm(null, root)) {
                    Set(form, "getWorkspaces", new Func<CancellationToken, Task<IList<PlasticWorkspace>>>(token => { throw new IOException("名单读取失败"); }));
                    form.Show(); Application.DoEvents();
                    Require(!Field<bool>(form, "defaultsReady") && !Field<Button>(form, "create").Enabled && Field<TextBox>(form, "status").Text.Contains("名单读取失败"), "Workspace list failure is visible and does not allow unchecked creation");
                    Set(form, "getWorkspaces", new Func<CancellationToken, Task<IList<PlasticWorkspace>>>(token => Task.FromResult<IList<PlasticWorkspace>>(registered)));
                    QueryResult(form);
                    Require(Field<TextBox>(form, "workspaceName").Text == "示例仓库" && Field<TextBox>(form, "directory").Text == Path.Combine(root, "TestSCM5"), "Repository selection names the workspace while retaining a collision-free suggested directory");
                    Save(form, Path.Combine(artifacts, "checkout-defaults.png")); form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form); Save(form, Path.Combine(artifacts, "checkout-defaults-minimum.png"));
                    form.Close();
                }
                Require(File.ReadAllText(Path.Combine(root, "TestSCM2")) == "preserve", "Suggestion checks never modify collisions");
            }
            finally { Directory.Delete(Path.GetDirectoryName(root), true); }
        }
        private static void TestRepositoryNaming(string artifacts)
        {
            using (var form = Open()) {
                string requestedServer = null;
                Set(form, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>((server, token) => {
                    requestedServer = server; return Task.FromResult<IList<PlasticRepositoryInfo>>(new List<PlasticRepositoryInfo> { Repository("Alpha"), Repository("中文 仓库"), Repository("Beta") });
                }));
                Await(InvokeTask(form, "QueryAsync"));
                Require(requestedServer == "plasticscm7.seasungame.com:8087", "Query uses the requested default server unchanged");
                var name = Field<TextBox>(form, "workspaceName"); var list = Field<ComboBox>(form, "repositories");
                Require(name.Text == "Alpha", "Initial repository selection replaces earlier workspace name input");
                name.Text = "manual-workspace";
                Field<ComboBox>(form, "mode").SelectedIndex = 1;
                Require(name.Text == "manual-workspace", "Workspace name remains manually editable after repository selection");
                list.SelectedIndex = 1;
                Require(name.Text == "中文 仓库", "Switching repository immediately synchronizes the workspace name including Unicode and spaces");
                name.Text = "another-manual-name"; list.SelectedIndex = 2;
                Require(name.Text == "Beta", "Each subsequent repository change resets a manually edited name");
                Require(Field<TextBox>(form, "directory").Text == @"D:\Workspaces\新项目", "Repository switches leave the exact destination directory unchanged");
                Save(form, Path.Combine(artifacts, "checkout-repository-name.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form);
                Save(form, Path.Combine(artifacts, "checkout-repository-name-minimum.png"));
            }
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
                Field<TextBox>(form, "workspaceName").Text = "my-workspace";
                Set(form, "confirm", new Func<string, bool>(message => { confirmation = message; return false; }));
                bool expectedPartial = scenario != 4; Field<ComboBox>(form, "mode").SelectedIndex = expectedPartial ? 0 : 1;
                Set(form, "createWorkspace", new Func<PlasticRepositoryInfo, string, string, string, bool, IProgress<string>, CancellationToken, Task<PlasticWorkspaceCreationResult>>((repo, name, path, branch, partial, progress, token) => {
                    writes++; Require(repo.Name == "示例仓库" && name == "my-workspace" && path == @"D:\Workspaces\新项目" && branch == "/main", "Captured inputs reach backend unchanged");
                    Require(partial == expectedPartial, "Selected Gluon or Standard mode reaches backend unchanged");
                    Require(!token.CanBeCanceled, "Confirmed creation cannot be canceled"); return pending.Task;
                }));
                Await(InvokeTask(form, "CreateAsync")); Require(writes == 0 && form.SelectedWorkspacePath == null, "Declined confirmation makes no changes");
                Require(confirmation.Contains("示例仓库@localhost:8087") && confirmation.Contains("/main") && confirmation.Contains(@"D:\Workspaces\新项目") && confirmation.Contains("整个分支"), "Confirmation describes exact repository branch destination and full download");
                Require(confirmation.Contains(expectedPartial ? "Gluon / Partial" : "Standard"), "Confirmation describes selected workspace mode");
                Set(form, "confirm", new Func<string, bool>(message => true)); Task creating = InvokeTask(form, "CreateAsync");
                Require(!Field<Button>(form, "create").Enabled && !Field<Button>(form, "close").Enabled && !Field<TextBox>(form, "server").Enabled, "Creating locks input and close controls");
                Await(InvokeTask(form, "CreateAsync")); form.Close();
                Require(writes == 1 && !form.IsDisposed && form.Visible, "Busy window refuses duplicate create and close");
                if (scenario == 0) pending.SetResult(new PlasticWorkspaceCreationResult { WorkspaceCreated = true, UpdateCompleted = true, WorkspacePath = @"D:\confirmed" });
                else if (scenario == 4) pending.SetException(new ArgumentException("目录不是空目录"));
                else pending.SetResult(new PlasticWorkspaceCreationResult { WorkspaceCreated = scenario == 1, OutcomeUncertain = scenario == 2,
                    WorkspacePath = @"D:\Workspaces\新项目", Stage = scenario == 1 ? "首次更新" : "创建工作区", Error = "模拟失败", RecoveryInstructions = "保留目录，请核查后打开已有工作区继续更新。" });
                Await(creating);
                if (scenario == 0) Require(form.SelectedWorkspacePath == @"D:\confirmed" && form.DialogResult == DialogResult.OK && !form.Visible, "Complete success closes checkout and retains its backend-confirmed destination");
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
        private static void TestCreationRoutes(string artifacts)
        {
            int dialogs = 0, writes = 0;
            var request = LaunchRequest.Parse(new[] { "--command", "create-workspace", "--path", @"D:\Exact checkout 中文" });
            int windowCount = Application.OpenForms.Count;
            int exitCode = Program.RunWorkspaceCreation(request, wizard => {
                dialogs++; EmptyWorkspaces(wizard);
                Set(wizard, "confirm", new Func<string, bool>(text => true));
                Set(wizard, "createWorkspace", new Func<PlasticRepositoryInfo, string, string, string, bool, IProgress<string>, CancellationToken, Task<PlasticWorkspaceCreationResult>>((repo, name, path, branch, partial, progress, token) => {
                    writes++; Require(name == "示例仓库" && path == @"D:\Exact checkout 中文", "Standalone checkout route forwards repository name and exact destination");
                    return Task.FromResult(new PlasticWorkspaceCreationResult { WorkspaceCreated = true, UpdateCompleted = true, WorkspacePath = path });
                }));
                wizard.Show(); Application.DoEvents(); QueryResult(wizard);
                Field<Button>(wizard, "create").PerformClick(); Application.DoEvents();
                Require(wizard.DialogResult == DialogResult.OK && !wizard.Visible, "Standalone checkout route completes its real wizard successfully");
                return wizard.DialogResult;
            });
            Require(exitCode == 0 && dialogs == 1 && writes == 1 && Application.OpenForms.Count == windowCount,
                "Standalone checkout terminates without opening the commit form");
            Require(request.Command == "create-workspace" && request.Paths[0] == @"D:\Exact checkout 中文", "Successful checkout never rewrites its launch request into a commit/status request");
            exitCode = Program.RunWorkspaceCreation(LaunchRequest.Parse(new[] { "--command", "create-workspace", "--parent-path", @"D:\" }), wizard => {
                Require(Path.GetDirectoryName(Field<TextBox>(wizard, "directory").Text) == @"D:\", "Standalone parent-folder route preserves its suggested child directory");
                return DialogResult.Cancel;
            });
            Require(exitCode == 0 && Application.OpenForms.Count == windowCount, "Canceled standalone checkout opens no commit form");
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", @"D:\existing workspace" }), false)) {
                form.Show(); Application.DoEvents(); int opens = 0;
                Set(form, "showWorkspaceCreation", new Func<string>(() => { opens++; return @"D:\confirmed checkout"; }));
                InvokeTaskless(form, "PullRepository");
                Require(opens == 1 && form.Visible && Application.OpenForms.Count == windowCount + 1 && Field<Label>(form, "status").Text.Contains(@"D:\confirmed checkout"),
                    "Main-window checkout reports completion while retaining the current window alone");
                Set(form, "showWorkspaceCreation", new Func<string>(() => { opens++; return null; }));
                string completedStatus = Field<Label>(form, "status").Text;
                InvokeTaskless(form, "PullRepository");
                Require(opens == 2 && Field<Label>(form, "status").Text == completedStatus, "Canceled main-window checkout preserves the previous feedback");
                Set(form, "busy", true); InvokeTaskless(form, "PullRepository"); Set(form, "busy", false);
                Require(opens == 2, "Main-window checkout cannot launch during another operation");
            }
        }
        private static void TestStartup(string artifacts)
        {
            using (var form = new StartupForm()) {
                form.Show(); Application.DoEvents(); Save(form, Path.Combine(artifacts, "startup.png")); form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form); Save(form, Path.Combine(artifacts, "startup-minimum.png"));
                Set(form, "createNew", new Func<string>(() => null)); Field<Button>(form, "create").PerformClick();
                Require(form.SelectedWorkspacePath == null && form.Visible, "Canceled wizard retains startup");
                Set(form, "createNew", new Func<string>(() => @"D:\confirmed checkout")); Field<Button>(form, "create").PerformClick();
                Require(form.SelectedWorkspacePath == null && form.DialogResult != DialogResult.OK && form.Visible && Field<Label>(form, "status").Text.Contains(@"D:\confirmed checkout"),
                    "Successful startup checkout keeps the startup page with completion feedback instead of opening commit");
                Save(form, Path.Combine(artifacts, "startup-checkout-completed.png"));
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
        private static void InvokeTaskless(object owner, string name) { owner.GetType().GetMethod(name, Flags).Invoke(owner, null); }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    }
}
