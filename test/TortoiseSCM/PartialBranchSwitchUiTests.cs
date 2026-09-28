// GPL-2.0-or-later. Exercises the real switch button with delayed provider results.
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
    internal static class PartialBranchSwitchUiTests
    {
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        private static bool serverPartial;
        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "bin/TortoiseSCM/qa/partial-branch-switch-ui" : args[0]); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-partial-switch-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
            try {
                SetMetadata(root, true, "/main");
                TestActions(client, root, artifacts);
                TestPending(client, root, false);
                TestPending(client, root, true);
                TestContext(client, root);
                SetMetadata(root, false, "/main");
                TestStandard(client, root);
                Console.WriteLine("PASS: Partial branch switch UI (" + assertions + " assertions)");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void SetMetadata(string root, bool partial, string branch)
        {
            serverPartial = partial;
            // Native Partial workspaces can retain Standard in this metadata file.
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "ui-switch\nunused\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"ui-switch@local\"\n  path \"/\"\n    branch \"" + branch + "\"\n");
        }

        private static BranchForm Open(PlasticClient client, string root)
        {
            var form = new BranchForm(client, root);
            Set(form, "previewPartialSwitch", new Func<string, string, CancellationToken, Task<PlasticPartialBranchSwitchPreview>>((path, branch, token) =>
                Task.FromResult(new PlasticPartialBranchSwitchPreview { Repository = "ui-switch@local", Branch = branch, HeadChangeset = 2, CanSwitch = true,
                    Directories = new List<PlasticPartialBranchSwitchDirectory>() })));
            Set(form, "showPartialPreview", new Func<PlasticPartialBranchSwitchPreview, bool>(preview =>
                Field<Func<string, bool>>(form, "confirmSwitch")(preview.Branch + "\r\n" + root + "\r\n" + PartialBranchSwitchPreviewForm.ScopeText)));
            Set(form, "performPartialSwitch", new Func<string, PlasticPartialBranchSwitchPreview, CancellationToken, Task<PlasticCommandResult>>((path, preview, token) =>
                Field<Func<string, string, CancellationToken, Task<PlasticCommandResult>>>(form, "performSwitch")(path, preview.Branch, token)));
            Set(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => {
                var workspace = client.DiscoverWorkspace(path); workspace.IsPartial = serverPartial; return Task.FromResult(workspace);
            }));
            Set(form, "getBranches", new Func<string, CancellationToken, Task<IList<PlasticBranch>>>((path, token) => {
                bool switched = client.DiscoverWorkspace(path).Selector.Contains("/main/目标");
                IList<PlasticBranch> rows = new List<PlasticBranch> {
                    new PlasticBranch { Name = "/main", Parent = "", Repository = "ui-switch@local", HeadChangeset = 1, IsCurrent = !switched },
                    new PlasticBranch { Name = "/main/目标", Parent = "/main", Repository = "ui-switch@local", HeadChangeset = 2, IsCurrent = switched }
                };
                return Task.FromResult(rows);
            }));
            form.Show(); Application.DoEvents(); Invoke(form, "SelectBranch", "/main/目标"); Application.DoEvents();
            return form;
        }

        private static void TestActions(PlasticClient client, string root, string artifacts)
        {
            using (var form = Open(client, root)) {
                Require(Field<Button>(form, "switchBranch").Enabled, "Partial noncurrent list branch enables switch: " + Field<Label>(form, "status").Text);
                Require(!Field<Button>(form, "merge").Enabled, "Partial merge stays disabled");
                Require(Field<Label>(form, "context").Text.Contains("现有加载配置"), "Partial caption describes loading behavior");
                Invoke(form, "SelectBranch", "/main"); Require(!Field<Button>(form, "switchBranch").Enabled, "Current branch switch disabled");
                Invoke(form, "SelectBranch", new object[] { null }); Require(!Field<Button>(form, "switchBranch").Enabled, "Missing selection switch disabled");
                Invoke(form, "SelectBranch", "/main/目标");
                Field<ComboBox>(form, "viewMode").SelectedIndex = 1; Application.DoEvents();
                Require(Field<Button>(form, "switchBranch").Enabled && !Field<Button>(form, "merge").Enabled, "Partial tree switch enabled independently of merge");
                int calls = 0; string warning = null;
                Set(form, "performSwitch", new Func<string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, token) => { calls++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                Set(form, "confirmSwitch", new Func<string, bool>(text => { warning = text; return false; }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(calls == 0 && !Field<bool>(form, "busy"), "Cancel confirmation never invokes provider or locks window");
                foreach (string expected in new[] { "/main/目标", root, "已加载项", "新子项", "未加载项", "现有加载配置", "扩大加载范围", "自动暂存或撤销", "私有/忽略", "合并会话", "不会自动反向" })
                    Require(warning.Contains(expected), "Partial confirmation includes " + expected);
                Save(form, Path.Combine(artifacts, "partial-branch-switch.png"));
                form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "refresh", "cancel", "rename", "head", "merge", "switchBranch", "close" }) {
                    var control = Field<Control>(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Minimum footer fits " + field);
                }
                Save(form, Path.Combine(artifacts, "partial-branch-switch-minimum.png"));
            }
        }

        private static void TestPending(PlasticClient client, string root, bool success)
        {
            SetMetadata(root, true, "/main");
            using (var form = Open(client, root)) {
                var pending = new TaskCompletionSource<PlasticCommandResult>(); int calls = 0; string submitted = null;
                Set(form, "confirmSwitch", new Func<string, bool>(text => true));
                Set(form, "performSwitch", new Func<string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, token) => {
                    calls++; submitted = branch; Require(path == root && !token.CanBeCanceled, "Write uses captured workspace and cannot be cancelled mid-switch"); return pending.Task;
                }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(calls == 1 && Field<bool>(form, "writing"), "Actual button starts delayed provider once");
                foreach (string name in new[] { "switchBranch", "close", "cancel", "refresh", "merge" })
                    Require(!Field<Button>(form, name).Enabled, "Pending switch disables " + name);
                form.Close(); Application.DoEvents(); Require(!form.IsDisposed && form.Visible, "Window close blocked during switch");
                Field<Button>(form, "switchBranch").PerformClick(); Require(calls == 1, "Repeated button cannot duplicate write");
                Field<IList<PlasticBranch>>(form, "entries")[1].Name = "/main/mutated";
                Require(submitted == "/main/目标", "Switch captures immutable selected name before await");
                // A failed command may still change the selector: always load actual state.
                SetMetadata(root, true, "/main/目标");
                pending.SetResult(new PlasticCommandResult { ExitCode = success ? 0 : -1, TimedOut = !success, Error = success ? "" : "uncertain-result" });
                PumpUntil(() => !Field<bool>(form, "busy"));
                var rows = Field<IList<PlasticBranch>>(form, "entries");
                Require(rows[1].IsCurrent && rows[1].Name == "/main/目标", "Completion refreshes real selector and discards mutated rows");
                Require(Field<Button>(form, "close").Enabled && Field<Button>(form, "refresh").Enabled, "Completion unlocks close and refresh");
                if (!success) Require(Field<Label>(form, "status").Text.Contains("未确认") && Field<Label>(form, "status").Text.Contains("不会自动反向"), "Uncertain result retained after refreshing");
                Require(calls == 1, "No automatic reverse switch or retry");
            }
            SetMetadata(root, true, "/main");
        }

        private static void TestContext(PlasticClient client, string root)
        {
            using (var form = Open(client, root)) {
                int calls = 0, confirmations = 0;
                var pending = new TaskCompletionSource<PlasticWorkspace>();
                var captured = client.DiscoverWorkspace(root); captured.IsPartial = true;
                var loader = Field<Func<string, CancellationToken, Task<PlasticWorkspace>>>(form, "getWorkspace");
                Set(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => pending.Task));
                Set(form, "confirmSwitch", new Func<string, bool>(text => { confirmations++; return true; }));
                Set(form, "performSwitch", new Func<string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, token) => { calls++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(Field<bool>(form, "busy") && confirmations == 0, "Native mode lookup completes before confirmation");
                SetMetadata(root, true, "/main/目标");
                Set(form, "getWorkspace", loader);
                pending.SetResult(captured); PumpUntil(() => !Field<bool>(form, "busy"));
                Require(calls == 0 && confirmations == 0, "Selector race during authoritative lookup prevents prompt and switch");
                Require(Field<IList<PlasticBranch>>(form, "entries")[1].IsCurrent, "Preconfirmation context failure refreshes branch state");
            }
            SetMetadata(root, true, "/main");
            using (var form = Open(client, root)) {
                int calls = 0;
                Set(form, "performSwitch", new Func<string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, token) => { calls++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                Set(form, "confirmSwitch", new Func<string, bool>(text => { SetMetadata(root, true, "/main/目标"); return true; }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(calls == 0 && Field<Label>(form, "status").Text.Contains("工作区分支已改变"), "Selector drift during confirmation blocks provider");
                Require(Field<IList<PlasticBranch>>(form, "entries")[1].IsCurrent, "Selector drift refreshes real current branch");
            }
            SetMetadata(root, true, "/main");
            using (var form = Open(client, root)) {
                int calls = 0;
                Set(form, "performSwitch", new Func<string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, token) => { calls++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                Set(form, "confirmSwitch", new Func<string, bool>(text => { SetMetadata(root, false, "/main"); return true; }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(calls == 0 && !Field<bool>(form, "partial"), "Mode drift blocks provider and refreshes caption mode");
            }
        }

        private static void TestStandard(PlasticClient client, string root)
        {
            using (var form = Open(client, root)) {
                Require(Field<Button>(form, "switchBranch").Enabled && Field<Button>(form, "merge").Enabled, "Standard switch and merge remain enabled");
                string warning = null; Set(form, "confirmSwitch", new Func<string, bool>(text => { warning = text; return false; }));
                Field<Button>(form, "switchBranch").PerformClick();
                Require(warning.Contains("整个 Standard 工作区") && !warning.Contains("Partial 切换"), "Standard confirmation retains whole-workspace scope");
            }
        }

        private static void PumpUntil(Func<bool> complete)
        {
            DateTime end = DateTime.UtcNow.AddSeconds(10);
            do { Application.DoEvents(); Thread.Sleep(10); } while (!complete() && DateTime.UtcNow < end);
            Require(complete(), "Asynchronous UI completed"); Application.DoEvents();
        }
        private static void Save(Form form, string path)
        { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static object Invoke(object target, string name, params object[] args)
        { return target.GetType().GetMethod(name, Flags).Invoke(target, args); }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
    }
}
