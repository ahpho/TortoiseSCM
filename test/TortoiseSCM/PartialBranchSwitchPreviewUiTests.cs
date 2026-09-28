// GPL-2.0-or-later. Actual button tests and normal/minimum native preview rendering.
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
    internal static class PartialBranchSwitchPreviewUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "bin/TortoiseSCM/qa/partial-switch-preview-ui" : args[0]); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            TestDialog(artifacts, false); TestDialog(artifacts, true);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-preview-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "preview-ui\nunused\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"preview@local\"\n  path \"/\"\n    branch \"/main\"\n");
            try {
                var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
                TestButton(client, root, false, true); TestButton(client, root, false, false); TestButton(client, root, true, true);
                TestPending(client, root); TestInvalidPreview(client, root); TestStaleExecution(client, root); TestStandard(client, root);
            }
            finally { Directory.Delete(root, true); }
            Console.WriteLine("PASS: Partial branch switch preview UI (" + assertions + " assertions)");
        }

        private static PlasticPartialBranchSwitchPreview Preview(bool blocked)
        {
            var rows = new List<PlasticPartialBranchSwitchDirectory> {
                new PlasticPartialBranchSwitchDirectory { Path = "/", TargetPath = "/", ItemId = 1, Change = "Unchanged", Reason = "目录身份及路径未变化。" },
                new PlasticPartialBranchSwitchDirectory { Path = "/美术资源/角色", TargetPath = blocked ? "/美术资源/主角" : "/美术资源/角色", ItemId = 25,
                    Change = blocked ? "Moved" : "Unchanged", Reason = blocked ? "目标分支已移动已加载目录；请在 Gluon 核对加载配置。" : "目录身份及路径未变化。" }
            };
            return new PlasticPartialBranchSwitchPreview { Repository = "preview@local", Branch = "/main/目标", HeadChangeset = 8,
                LoadedDirectoryCount = 2, LoadingRuleCount = 3, Directories = rows, CanSwitch = !blocked };
        }

        private static void TestDialog(string artifacts, bool blocked)
        {
            var preview = Preview(blocked);
            using (var form = new PartialBranchSwitchPreviewForm(@"D:\Work\游戏项目", preview)) {
                form.Show(); Application.DoEvents();
                var proceed = Field<Button>(form, "proceed"); var close = Field<Button>(form, "close");
                Require(proceed.Visible == !blocked && proceed.Enabled == !blocked, "Only safe review shows execute action");
                Require(form.AcceptButton == close && form.CancelButton == close && close.Focused, "Default action and Escape cancel review");
                Require(Field<ListView>(form, "directories").Items.Count == 2, "Unchanged directories are included alongside blockers");
                Require(!Field<ListView>(form, "directories").LabelEdit, "Rows are read only");
                Require(Field<TextBox>(form, "explanation").Text.Contains("不是完整的文件内容变化清单"), "Scope does not promise a full file diff");
                if (blocked) Require(Field<TextBox>(form, "explanation").Text.Contains("Gluon"), "Blocked review offers manual Gluon next step");
                preview.Branch = "/main/mutated"; preview.Directories[1].Path = "/mutated";
                Require(Field<TextBox>(form, "context").Text.Contains("/main/目标") && !Field<TextBox>(form, "context").Text.Contains("mutated"), "Displayed target is snapshotted");
                Require(Field<ListView>(form, "directories").Items[1].Text == "/美术资源/角色", "Displayed rows are snapshotted");
                Save(form, Path.Combine(artifacts, blocked ? "partial-switch-preview-blocked.png" : "partial-switch-preview-safe.png"));
                form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string name in new[] { "context", "directories", "explanation", "close" }) {
                    var control = Field<Control>(form, name);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Minimum layout fits " + name);
                }
                Save(form, Path.Combine(artifacts, blocked ? "partial-switch-preview-blocked-minimum.png" : "partial-switch-preview-safe-minimum.png"));
                if (blocked) { proceed.PerformClick(); Require(form.Visible, "Hidden blocked execute action cannot accept"); close.PerformClick(); }
                else proceed.PerformClick();
                Require(form.DialogResult == (blocked ? DialogResult.Cancel : DialogResult.OK), "Explicit review result reflects available action");
            }
        }

        private static BranchForm Open(PlasticClient client, string root, bool partial)
        {
            var form = new BranchForm(client, root);
            Set(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => {
                var workspace = client.DiscoverWorkspace(root); workspace.IsPartial = partial; return Task.FromResult(workspace);
            }));
            Set(form, "getBranches", new Func<string, CancellationToken, Task<IList<PlasticBranch>>>((path, token) => Task.FromResult<IList<PlasticBranch>>(
                new List<PlasticBranch> { new PlasticBranch { Repository = "preview@local", Name = "/main", Parent = "", IsCurrent = true, HeadChangeset = 7 },
                    new PlasticBranch { Repository = "preview@local", Name = "/main/目标", Parent = "/main", HeadChangeset = 8 } })));
            Set(form, "previewPartialSwitch", new Func<string, string, CancellationToken, Task<PlasticPartialBranchSwitchPreview>>((path, branch, token) => Task.FromResult(Preview(false))));
            Set(form, "confirmSwitch", new Func<string, bool>(text => { throw new Exception("Partial must use single preview confirmation"); }));
            Set(form, "performSwitch", new Func<string, string, CancellationToken, Task<PlasticCommandResult>>((path, branch, token) => { throw new Exception("Partial must use reviewed overload"); }));
            form.Show(); Application.DoEvents(); form.GetType().GetMethod("SelectBranch", Flags).Invoke(form, new object[] { "/main/目标" }); Application.DoEvents();
            return form;
        }

        private static void TestButton(PlasticClient client, string root, bool blocked, bool accept)
        {
            using (var form = Open(client, root, true)) {
                int writes = 0, dialogs = 0; var preview = Preview(blocked);
                Set(form, "previewPartialSwitch", new Func<string, string, CancellationToken, Task<PlasticPartialBranchSwitchPreview>>((path, branch, token) => {
                    Require(path == root && branch == "/main/目标", "Preview receives captured target and root"); return Task.FromResult(preview);
                }));
                Set(form, "showPartialPreview", new Func<PlasticPartialBranchSwitchPreview, bool>(value => { dialogs++; Require(value == preview, "Dialog reviews provider result"); return accept; }));
                Set(form, "performPartialSwitch", new Func<string, PlasticPartialBranchSwitchPreview, CancellationToken, Task<PlasticCommandResult>>((path, value, token) => {
                    writes++; Require(value == preview && !token.CanBeCanceled, "Execution receives same reviewed evidence without mid-write cancellation"); return Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(dialogs == 1 && writes == (!blocked && accept ? 1 : 0), "Safe acceptance alone executes; blocked synthetic acceptance cannot bypass guard: " + dialogs + "/" + writes + "; " + Field<Label>(form, "status").Text);
                Require(!Field<bool>(form, "busy") && Field<Button>(form, "close").Enabled, "Review path releases busy state");
            }
        }

        private static void TestPending(PlasticClient client, string root)
        {
            using (var form = Open(client, root, true)) {
                var pending = new TaskCompletionSource<PlasticPartialBranchSwitchPreview>(); int dialogs = 0;
                Set(form, "previewPartialSwitch", new Func<string, string, CancellationToken, Task<PlasticPartialBranchSwitchPreview>>((path, branch, token) => pending.Task));
                Set(form, "showPartialPreview", new Func<PlasticPartialBranchSwitchPreview, bool>(value => { dialogs++; return false; }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(Field<bool>(form, "busy") && dialogs == 0, "Awaiting preview holds busy state without opening stale dialog");
                foreach (string name in new[] { "switchBranch", "close", "cancel", "refresh" }) Require(!Field<Button>(form, name).Enabled, "Pending preview disables " + name);
                form.Close(); Require(!form.IsDisposed, "Close is guarded during asynchronous preview preparation");
                Field<IList<PlasticBranch>>(form, "entries")[1].Name = "/main/mutated";
                pending.SetResult(Preview(false)); PumpUntil(() => !Field<bool>(form, "busy"));
                Require(dialogs == 1, "Async preview retains original selected target");
            }
        }

        private static void TestInvalidPreview(PlasticClient client, string root)
        {
            for (int scenario = 0; scenario < 3; scenario++) using (var form = Open(client, root, true)) {
                int writes = 0, dialogs = 0; var preview = Preview(false);
                if (scenario == 0) preview.Branch = "/main/other";
                Set(form, "previewPartialSwitch", new Func<string, string, CancellationToken, Task<PlasticPartialBranchSwitchPreview>>((path, branch, token) => {
                    if (scenario == 1) throw new InvalidOperationException("preview-failure"); return Task.FromResult(preview);
                }));
                Set(form, "showPartialPreview", new Func<PlasticPartialBranchSwitchPreview, bool>(value => { dialogs++; value.HeadChangeset++; return true; }));
                Set(form, "performPartialSwitch", new Func<string, PlasticPartialBranchSwitchPreview, CancellationToken, Task<PlasticCommandResult>>((path, value, token) => { writes++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(writes == 0 && dialogs == (scenario == 2 ? 1 : 0), "Invalid, failed or mutated preview cannot execute");
                Require(!Field<bool>(form, "busy") && Field<Label>(form, "status").Text.Contains("刷新"), "Preview failure refreshes and unlocks");
            }
        }

        private static void TestStaleExecution(PlasticClient client, string root)
        {
            using (var form = Open(client, root, true)) {
                int writes = 0;
                Set(form, "showPartialPreview", new Func<PlasticPartialBranchSwitchPreview, bool>(value => true));
                Set(form, "performPartialSwitch", new Func<string, PlasticPartialBranchSwitchPreview, CancellationToken, Task<PlasticCommandResult>>((path, value, token) => {
                    writes++; throw new InvalidOperationException("目标头提交已改变，请重新预览。");
                }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents();
                Require(writes == 1 && Field<Label>(form, "status").Text.Contains("目标头提交已改变"), "Backend rejects stale review and UI retains reason");
                Require(Field<IList<PlasticBranch>>(form, "entries").Count == 2 && !Field<bool>(form, "busy"), "Rejected review refreshes without automatic retry");
            }
        }

        private static void TestStandard(PlasticClient client, string root)
        {
            using (var form = Open(client, root, false)) {
                int confirmations = 0;
                Set(form, "previewPartialSwitch", new Func<string, string, CancellationToken, Task<PlasticPartialBranchSwitchPreview>>((path, branch, token) => { throw new Exception("Standard must not prepare Partial preview"); }));
                Set(form, "confirmSwitch", new Func<string, bool>(text => { confirmations++; Require(text.Contains("整个 Standard 工作区"), "Standard retains original scope"); return false; }));
                Field<Button>(form, "switchBranch").PerformClick(); Application.DoEvents(); Require(confirmations == 1, "Standard keeps its own confirmation");
            }
        }

        private static void PumpUntil(Func<bool> complete)
        {
            var end = DateTime.UtcNow.AddSeconds(10);
            do { Application.DoEvents(); Thread.Sleep(10); } while (!complete() && DateTime.UtcNow < end);
            Require(complete(), "Async preview completed"); Application.DoEvents();
        }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static void Require(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
        private static void Save(Form form, string path)
        { using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path, ImageFormat.Png); } }
    }
}
