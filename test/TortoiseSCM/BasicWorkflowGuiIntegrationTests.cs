// GPL-2.0-or-later. Real server writes restricted to New-TestWorkspace fixtures.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace TortoiseSCM
{
    internal static class BasicWorkflowGuiIntegrationTests
    {
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        private static string artifacts;
        private static PlasticClient client;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length < 2 || args.Length > 4 || (args.Length == 4 && args[3] != "--file-actions"))
                    throw new ArgumentException("Usage: BasicWorkflowGuiIntegrationTests <manifest.json> <artifacts> [expected-cm.exe] [--file-actions]");
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Control.CheckForIllegalCrossThreadCalls = true;
                artifacts = Path.GetFullPath(args[1]); Directory.CreateDirectory(artifacts);
                var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0]));
                Require((bool)manifest["complete"], "Fixture setup completed");
                var config = PlasticClientConfig.Load();
                if (args.Length >= 3 && !Path.GetFullPath(config.CmPath).Equals(Path.GetFullPath(args[2]), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The GUI's configured cm.exe differs from the fixture client. Set the same path before running this acceptance test.");
                client = new PlasticClient(config);
                string producer = (string)manifest["producer"], consumer = (string)manifest["consumer"], partial = (string)manifest["partial"];
                foreach (string root in new[] { producer, consumer, partial })
                {
                    var workspace = client.DiscoverWorkspace(root);
                    Require(workspace != null && workspace.Repository == (string)manifest["repository"] && workspace.Selector.Contains((string)manifest["branch"])
                        && ((string)manifest["branch"]).StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal)
                        && Path.GetFullPath(root).StartsWith(Path.GetFullPath((string)manifest["runDirectory"]) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                        "Write target is isolated autotest workspace: " + root);
                }
                if (args.Length == 4)
                {
                    CheckOperationDirectorySelection();
                    RunFileActions(producer, "standard");
                    Update(partial);
                    RunFileActions(partial, "partial");
                    Console.WriteLine("PASS: file actions GUI integration (" + assertions + " assertions; in-process dialogs, not Explorer clicks)");
                    return 0;
                }
                CheckOperationDirectorySelection();
                RunMode(producer, consumer, false);
                Update(partial);
                RunMode(partial, consumer, true);
                Console.WriteLine("PASS: basic workflow GUI integration (" + assertions + " assertions; actual WinForms dialogs and cm; no Explorer mouse automation)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void CheckOperationDirectorySelection()
        {
            string root = Path.Combine(Path.GetTempPath(), "tscm-add-selection-" + Guid.NewGuid().ToString("N"));
            string nested = Path.Combine(root, "nested");
            using (var form = new OperationForm(null, "add", new string[0]))
            {
                var rows = Field<ListView>(form, "files");
                form.CreateControl(); rows.CreateControl();
                var directory = new ListViewItem("root") { Tag = new PlasticStatusItem { Path = root, IsDirectory = true }, Checked = true };
                var nestedDirectory = new ListViewItem("root\\nested") { Tag = new PlasticStatusItem { Path = nested, IsDirectory = true }, Checked = true };
                var child = new ListViewItem("root\\nested\\child.txt") { Tag = new PlasticStatusItem { Path = Path.Combine(nested, "child.txt") }, Checked = true };
                var sibling = new ListViewItem("sibling.txt") { Tag = new PlasticStatusItem { Path = root + "-sibling.txt" }, Checked = true };
                rows.Items.Add(directory); rows.Items.Add(nestedDirectory); rows.Items.Add(child); rows.Items.Add(sibling);

                directory.Checked = false;
                typeof(OperationForm).GetMethod("OnItemChecked", Flags).Invoke(form, new object[] { rows, new ItemCheckedEventArgs(directory) });
                Require(!directory.Checked && !nestedDirectory.Checked && !child.Checked && sibling.Checked,
                    "Add directory unchecking clears all recursive descendants only");
                directory.Checked = true;
                typeof(OperationForm).GetMethod("OnItemChecked", Flags).Invoke(form, new object[] { rows, new ItemCheckedEventArgs(directory) });
                Require(directory.Checked && nestedDirectory.Checked && child.Checked && sibling.Checked,
                    "Add directory checking selects all recursive descendants");
            }
        }

        private static void RunMode(string root, string consumer, bool partial)
        {
            string prefix = (partial ? "partial" : "standard") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string path = Path.Combine(root, prefix + " 新文件.txt"), sibling = Path.Combine(root, prefix + " excluded.txt");
            const string contents = "真实 GUI 添加签入\r\nUnicode 中文 & spaces\r\n";
            File.WriteAllText(path, contents, new UTF8Encoding(false)); File.WriteAllText(sibling, "private sibling", new UTF8Encoding(false));
            string before = Status(root);
            using (var form = LaunchOperation("add", path, false, "继续添加", path))
                Require(Status(root) == before && Pending(root, path, "PR"), prefix + " cancelled Add preserves private status");
            using (var form = LaunchOperation("add", path, true, "继续添加", path))
            {
                Require(Pending(root, path, "AD") && Pending(root, sibling, "PR"), prefix + " shell Add adds exact file, excludes private sibling");
                Require(!Field<bool>(form, "busy"), "Add returns an interactive operation window");
            }
            using (var form = Launch("checkin", path, true, null, null))
            {
                Require(Field<PlasticWorkspace>(form, "workspace").IsPartial == partial, "GUI detects expected workspace mode");
                var rows = Field<ListView>(form, "files");
                Require(rows.Items.Count == 1 && Same(((PlasticStatusItem)rows.Items[0].Tag).Path, path), "File-scoped checkin displays exact pending file");
                rows.Items[0].Checked = true;
                string error = ""; Set(form, "reportError", new Action<string>(text => error = text));
                Field<Button>(form, "checkin").PerformClick(); Application.DoEvents();
                Require(error.Contains("说明") && Pending(root, path, "AD"), "Empty comment blocked without write");
                Field<TextBox>(form, "comment").Text = prefix + " GUI add 中文\r\nsecond line";
                ReviewAndSubmit(form, new[] { path }, false, prefix + "-cancel-review");
                Require(Pending(root, path, "AD") && rows.CheckedItems.Count == 1 && Field<TextBox>(form, "comment").Text.Contains("second line"), "Cancelled review preserves pending state, checks and multiline comment");
                ReviewAndSubmit(form, new[] { path }, true, prefix + "-add-review");
                Require(!Pending(root, path, "AD") && Field<TextBox>(form, "comment").Text.Length == 0 && rows.Items.Count == 0, "Successful checkin clears file pending state and draft");
                string previous = Status(root); error = ""; Field<Button>(form, "checkin").PerformClick(); Application.DoEvents();
                Require(Status(root) == previous && !Field<bool>(form, "busy"), "Repeated checkin with no pending selection does not write");
                InvokeTask(form, "RefreshAsync"); Require(rows.Items.Count == 0, "Refresh after checkin shows no scoped pending changes");
            }
            Update(consumer);
            Require(File.ReadAllText(Path.Combine(consumer, Path.GetFileName(path))) == contents && !File.Exists(Path.Combine(consumer, Path.GetFileName(sibling))), "Separate consumer receives exact committed UTF-8 bytes and no excluded file");
            before = Status(root);
            using (var form = Launch("remove", path, false, "从磁盘删除", path))
                Require(File.Exists(path) && Status(root) == before, "Cancelled Delete preserves controlled file and pending state");
            using (var form = Launch("remove", path, true, "从磁盘删除", path))
                Require(!File.Exists(path) && Pending(root, path, "DE") && File.Exists(sibling), "Shell Delete removes exact managed file and stages deletion, preserving sibling");
            using (var form = Launch("checkin", path, true, null, null))
            {
                var rows = Field<ListView>(form, "files"); Require(rows.Items.Count == 1, "Missing deleted path still launches checkin");
                rows.Items[0].Checked = true; Field<TextBox>(form, "comment").Text = prefix + " GUI delete";
                ReviewAndSubmit(form, new[] { path }, true, prefix + "-delete-review");
                Require(rows.Items.Count == 0 && !Pending(root, path, "DE"), "Deletion checkin clears pending deletion");
            }
            Update(consumer);
            Require(!File.Exists(Path.Combine(consumer, Path.GetFileName(path))) && File.ReadAllText(sibling) == "private sibling", "Separate consumer receives deletion and local private sibling remains intact");

            string directory = Path.Combine(root, prefix + " 递归目录"); Directory.CreateDirectory(directory);
            string first = Path.Combine(directory, "甲.txt"), second = Path.Combine(directory, "乙.txt");
            File.WriteAllText(first, "alpha"); File.WriteAllText(second, "beta");
            using (var form = LaunchOperation("add", directory, true, "目录操作会包含其全部子项", directory))
                Require(Pending(root, first, "AD") && Pending(root, second, "AD"), "Directory Add recursively includes both children after explicit warning");
            using (var form = Launch("checkin", directory, true, null, null))
            {
                var rows = Field<ListView>(form, "files"); var folder = rows.Items.Cast<ListViewItem>().Single(row => ((PlasticStatusItem)row.Tag).IsDirectory);
                folder.Checked = true;
                Require(rows.CheckedItems.Count == 3, "Checking added directory checks its two children");
                Field<TextBox>(form, "comment").Text = prefix + " recursive directory";
                ReviewAndSubmit(form, new[] { directory, first, second }, true, prefix + "-directory-review");
                Require(rows.Items.Count == 0, "Recursive GUI checkin leaves no pending directory descendants");
            }
            Update(consumer);
            Require(File.ReadAllText(Path.Combine(consumer, Path.GetFileName(directory), "甲.txt")) == "alpha" && File.ReadAllText(Path.Combine(consumer, Path.GetFileName(directory), "乙.txt")) == "beta", "Consumer sees both recursive additions");
            using (var form = Launch("remove", directory, true, "目录包含后代", directory))
                Require(!Directory.Exists(directory) && Pending(root, directory, "DE"), "Directory Delete produces native collapsed pending directory deletion");
            using (var form = Launch("checkin", directory, true, null, null))
            {
                foreach (ListViewItem row in Field<ListView>(form, "files").Items) row.Checked = true;
                Field<TextBox>(form, "comment").Text = prefix + " recursive deletion";
                ReviewAndSubmit(form, new[] { directory }, true, prefix + "-directory-delete-review");
            }
            Update(consumer);
            Require(!Directory.Exists(Path.Combine(consumer, Path.GetFileName(directory))), "Consumer sees committed recursive deletion");
            Console.WriteLine("Completed " + prefix);
        }

        private static void RunFileActions(string root, string mode)
        {
            string path = Path.Combine(root, mode + " action 中文.txt"), sibling = Path.Combine(root, mode + " private.txt");
            const string baseline = "file action baseline 中文\r\n";
            File.WriteAllText(path, baseline, new UTF8Encoding(false));
            File.WriteAllText(sibling, "preserve private sibling", new UTF8Encoding(false));
            foreach (var command in new[] { PlasticCommand.Add, PlasticCommand.Checkin })
                Require(Wait(client.RunAsync(new PlasticCommandRequest { Command = command, WorkingDirectory = root,
                    Paths = new[] { path }, Comment = "GUI audit file action fixture" }, CancellationToken.None)).Succeeded, "Seed isolated " + mode + " file");
            string clean = Status(root);
            using (var form = OpenOperation("checkout", path))
            {
                Require(Status(root) == clean && Field<Label>(form, "status").Text.Contains("签出"), "Shell checkout opens operation window without checking out automatically");
                using (var guard = new DialogGuard(form, true, "继续签出", path))
                {
                    Field<Button>(form, "execute").PerformClick();
                    Pump(() => !Field<bool>(form, "busy") && guard.Count == 1, "Checkout GUI handler completes");
                    Require(guard.Count == 1 && guard.Error == "", "Checkout requires exact-path confirmation");
                }
                Require(Pending(root, path, "CO"), "Confirmed checkout creates native checkout state");
            }
            using (var checkin = Launch("checkin", root, false, null, null))
            {
                var rows = Field<ListView>(checkin, "files");
                var row = rows.Items.Cast<ListViewItem>().SingleOrDefault(item => Same(((PlasticStatusItem)item.Tag).Path, path));
                Require(row != null && row.SubItems.Count > 2 && row.SubItems[2].Text.Contains("签出"),
                    "Check-in interface shows checked-out file status");
            }
            string checkoutDirectory = Path.Combine(root, mode + " recursive checkout");
            string checkoutFirst = Path.Combine(checkoutDirectory, "first.txt"), checkoutSecond = Path.Combine(checkoutDirectory, "second.txt");
            Directory.CreateDirectory(checkoutDirectory); File.WriteAllText(checkoutFirst, "recursive first"); File.WriteAllText(checkoutSecond, "recursive second");
            foreach (var command in new[] { PlasticCommand.Add, PlasticCommand.Checkin })
                Require(Wait(client.RunAsync(new PlasticCommandRequest { Command = command, WorkingDirectory = root,
                    Paths = new[] { checkoutDirectory }, Recursive = true, Comment = "GUI recursive checkout fixture" }, CancellationToken.None)).Succeeded,
                    "Seed recursive checkout directory with " + command);
            using (var form = LaunchOperation("checkout-recursive", checkoutDirectory, true, "继续递归签出", checkoutDirectory))
                Require(Pending(root, checkoutFirst, "CO") && Pending(root, checkoutSecond, "CO"),
                    "Recursive checkout checks out every descendant");
            Require(Wait(client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root,
                Paths = new[] { checkoutDirectory }, Recursive = true }, CancellationToken.None)).Succeeded &&
                !Pending(root, checkoutFirst, "CO") && !Pending(root, checkoutSecond, "CO"),
                "Recursive checkout cleanup restores unlocked state");
            File.WriteAllText(path, baseline + "local edit to discard\r\n", new UTF8Encoding(false));
            string edited = File.ReadAllText(path), dirty = Status(root);
            using (var form = Launch("status", root, false, null, null))
            {
                foreach (bool accept in new[] { false, true })
                {
                    var rows = Field<ListView>(form, "files");
                    foreach (ListViewItem row in rows.Items) { row.Selected = false; row.Checked = Same(((PlasticStatusItem)row.Tag).Path, sibling); }
                    rows.Items.Cast<ListViewItem>().Single(row => Same(((PlasticStatusItem)row.Tag).Path, path)).Selected = true;
                    using (var guard = new DialogGuard(form, accept, "所选项的本地更改将丢失", path))
                    {
                        rows.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "丢弃所选行的修改…").PerformClick();
                        Pump(() => !Field<bool>(form, "busy") && guard.Count == 1, "Row context undo finishes");
                        Require(guard.Error == "", "Undo confirmation identifies highlighted path");
                    }
                    Require(File.ReadAllText(sibling) == "preserve private sibling" && Pending(root, sibling, "PR"), "Undo highlighted row preserves separately checked private sibling");
                    Require(accept ? File.ReadAllText(path) == baseline && !Wait(client.GetStatusAsync(root, CancellationToken.None)).Any(item => Same(item.Path, path)) :
                        File.ReadAllText(path) == edited && Status(root) == dirty, accept ? "Confirmed row undo restores baseline and clears checkout" : "Cancelled row undo preserves bytes and native state");
                }
            }
            string destination = Path.Combine(root, mode + " renamed 中文.txt");
            MoveFromShell(path, destination, false, mode + "-move-cancel");
            Require(File.Exists(path) && !File.Exists(destination) && File.ReadAllText(path) == baseline, "Cancelled rename preserves source");
            MoveFromShell(path, destination, true, mode + "-move");
            Require(!File.Exists(path) && File.ReadAllText(destination) == baseline && Pending(root, destination, "MV"), "Confirmed rename preserves content and creates native move");
            string ignore = Path.Combine(root, "ignore.conf");
            string rulesBefore = File.Exists(ignore) ? File.ReadAllText(ignore) : null;
            using (var form = Launch("ignore", sibling, false, "精确路径写入", sibling))
                Require((File.Exists(ignore) ? File.ReadAllText(ignore) : null) == rulesBefore && Pending(root, sibling, "PR"), "Cancelled ignore preserves rules and private state");
            using (var form = Launch("ignore", sibling, true, "精确路径写入", sibling))
                Require(File.ReadAllText(sibling) == "preserve private sibling" && File.ReadAllText(ignore).Contains(Path.GetFileName(sibling)), "Confirmed ignore writes exact rule and preserves file bytes");
            Require(File.ReadAllText(destination) == baseline && Pending(root, destination, "MV"), "Ignore does not untrack controlled renamed file");
        }

        private static void MoveFromShell(string source, string destination, bool accept, string capture)
        {
            using (var form = (MainForm)Program.CreateLaunchForm(LaunchRequest.Parse(new[] { "--command", "move", "--path", source })))
            using (var timer = new System.Windows.Forms.Timer { Interval = 40 })
            using (var guard = new DialogGuard(form, false, null, null))
            {
                bool handled = false;
                timer.Tick += delegate {
                    var dialog = Application.OpenForms.OfType<PathInputForm>().SingleOrDefault();
                    if (dialog == null || handled) return;
                    handled = true; Field<TextBox>(dialog, "destination").Text = destination;
                    Save(dialog, Path.Combine(artifacts, capture + ".png"));
                    dialog.Size = dialog.MinimumSize; Application.DoEvents(); Save(dialog, Path.Combine(artifacts, capture + "-minimum.png"));
                    (accept ? dialog.AcceptButton : dialog.CancelButton).PerformClick();
                };
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-25000, -25000);
                timer.Start(); form.Show();
                Pump(() => handled && Field<bool>(form, "loaded") && !Field<bool>(form, "busy") && !Application.OpenForms.OfType<PathInputForm>().Any(), "Shell rename dialog finishes");
                Require(guard.Error == "", "Rename has no unexpected error dialog");
            }
        }

        private static MainForm Launch(string command, string path, bool accept, string expectedNote, string expectedPath)
        {
            var form = Program.CreateLaunchForm(LaunchRequest.Parse(new[] { "--command", command, "--path", path })) as MainForm;
            Require(form != null, "Shell route " + command + " creates MainForm");
            Set(form, "messageStore", new CommitMessageStore(Path.Combine(artifacts, "message-history")));
            using (var guard = new DialogGuard(form, accept, expectedNote, expectedPath))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-25000, -25000);
                form.Show(); Pump(() => Field<bool>(form, "loaded") && !Field<bool>(form, "busy") && (expectedNote == null || guard.Count > 0), "Shell " + command + " initializes and completes");
                Require(guard.Error == "", "No unexpected startup dialog: " + guard.Error);
                Require(expectedNote == null ? guard.Count == 0 : guard.Count == 1, "Expected exact confirmation count for " + command);
            }
            return form;
        }

        private static OperationForm OpenOperation(string command, string path)
        {
            var form = Program.CreateLaunchForm(LaunchRequest.Parse(new[] { "--command", command, "--path", path })) as OperationForm;
            Require(form != null, "Shell route " + command + " creates OperationForm");
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-25000, -25000);
            form.Show();
            Pump(() => Field<bool>(form, "ready") && !Field<bool>(form, "busy"), "Shell " + command + " initializes and completes");
            return form;
        }

        private static OperationForm LaunchOperation(string command, string path, bool accept, string expectedNote, string expectedPath)
        {
            var form = OpenOperation(command, path);
            using (var guard = new DialogGuard(form, accept, expectedNote, expectedPath))
            {
                Field<Button>(form, "execute").PerformClick();
                Pump(() => !Field<bool>(form, "busy") && guard.Count == 1, "Shell " + command + " operation finishes");
                Require(guard.Error == "", "No unexpected operation dialog: " + guard.Error);
                Require(guard.Count == 1, "Expected exact confirmation count for " + command);
            }
            return form;
        }

        private static void ReviewAndSubmit(MainForm form, string[] expectedPaths, bool confirm, string capture)
        {
            bool handled = false; string error = "";
            using (var timer = new System.Windows.Forms.Timer { Interval = 40 })
            using (var guard = new DialogGuard(form, false, null, null))
            {
                timer.Tick += delegate {
                    var dialog = Application.OpenForms.Cast<Form>().OfType<CheckinReviewForm>().FirstOrDefault();
                    if (dialog == null || handled) return;
                    handled = true;
                    try
                    {
                        var rows = Field<ListView>(dialog, "files");
                        Require(rows.Items.Count == expectedPaths.Length, "Review includes complete exact effective scope: " + capture);
                        string rendered = String.Join("\n", rows.Items.Cast<ListViewItem>().Select(row => row.Text).ToArray());
                        foreach (string path in expectedPaths) Require(rendered.Contains(Path.GetFileName(path)), "Review includes " + Path.GetFileName(path));
                        Require(!rendered.Contains("excluded.txt") && Field<TextBox>(dialog, "comment").Text == Field<TextBox>(form, "comment").Text, "Review preserves comment and excludes sibling");
                        Save(dialog, Path.Combine(artifacts, capture + ".png")); dialog.Size = dialog.MinimumSize; Application.DoEvents();
                        Require(dialog.RectangleToScreen(dialog.ClientRectangle).Contains(Field<Button>(dialog, "confirm").RectangleToScreen(Field<Button>(dialog, "confirm").ClientRectangle)), "Review confirmation visible at minimum size");
                        Save(dialog, Path.Combine(artifacts, capture + "-minimum.png"));
                        Field<Button>(dialog, confirm ? "confirm" : "cancel").PerformClick();
                    }
                    catch (Exception ex) { error = ex.ToString(); dialog.DialogResult = DialogResult.Cancel; dialog.Close(); }
                };
                timer.Start(); Field<Button>(form, "checkin").PerformClick();
                Pump(() => !Field<bool>(form, "busy"), "Checkin returns from review/server write");
                Require(handled && error == "" && guard.Error == "", "Actual review dialog handled: " + error + guard.Error);
                Require(!Field<bool>(form, "submissionNeedsRefresh"), "Checkin has no uncertain failure gate: " + Field<TextBox>(form, "output").Text);
            }
            Save(form, Path.Combine(artifacts, capture + "-main.png"));
        }

        private sealed class DialogGuard : IDisposable
        {
            private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 40 };
            internal int Count; internal string Error = ""; private readonly HashSet<IntPtr> seen = new HashSet<IntPtr>();
            internal DialogGuard(Form owner, bool accept, string expectedNote, string expectedPath)
            {
                timer.Tick += delegate {
                    if (!owner.IsHandleCreated) return;
                    IntPtr popup = GetLastActivePopup(owner.Handle); var name = new StringBuilder(64); GetClassName(popup, name, name.Capacity);
                    if (popup == owner.Handle || name.ToString() != "#32770" || !seen.Add(popup)) return;
                    uint pid; GetWindowThreadProcessId(popup, out pid);
                    if (pid != System.Diagnostics.Process.GetCurrentProcess().Id) return;
                    Count++; var text = new StringBuilder();
                    EnumChildWindows(popup, delegate(IntPtr child, IntPtr unused) { var value = new StringBuilder(8192); GetWindowText(child, value, value.Capacity); text.AppendLine(value.ToString()); return true; }, IntPtr.Zero);
                    bool expected = expectedNote != null && text.ToString().Contains(expectedNote) &&
                        text.ToString().IndexOf(expectedPath, StringComparison.OrdinalIgnoreCase) >= 0;
                    File.AppendAllText(Path.Combine(artifacts, "native-confirmations.txt"), text + "\r\n", Encoding.UTF8);
                    if (!expected) Error += text;
                    PostMessage(popup, 0x111, new IntPtr(expected && accept ? 1 : 2), IntPtr.Zero);
                }; timer.Start();
            }
            public void Dispose() { timer.Stop(); timer.Dispose(); }
        }

        private static void Update(string root)
        {
            var result = Wait(client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = root, Paths = new[] { root }, Recursive = true }, CancellationToken.None));
            Require(result.Succeeded, "Consumer update succeeds: " + result.Error);
        }
        private static bool Pending(string root, string path, string state) { return Wait(client.GetStatusAsync(root, CancellationToken.None)).Any(item => Same(item.Path, path) && item.StatusCode == state); }
        private static string Status(string root) { return String.Join("\n", Wait(client.GetStatusAsync(root, CancellationToken.None)).Select(item => item.StatusCode + "|" + item.Path).OrderBy(value => value).ToArray()); }
        private static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static T Wait<T>(Task<T> task) { Pump(() => task.IsCompleted, "Native request completes"); return task.GetAwaiter().GetResult(); }
        private static void InvokeTask(object form, string name) { var task = (Task)form.GetType().GetMethod(name, Flags).Invoke(form, null); Pump(() => task.IsCompleted, name); task.GetAwaiter().GetResult(); }
        private static T Field<T>(object form, string name) { return (T)form.GetType().GetField(name, Flags).GetValue(form); }
        private static void Set(object form, string name, object value) { form.GetType().GetField(name, Flags).SetValue(form, value); }
        private static void Save(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void Pump(Func<bool> condition, string message)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(120); Application.DoEvents();
            while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Require(condition(), message);
        }
        private static void Require(bool value, string message) { assertions++; Console.WriteLine((value ? "PASS " : "FAIL ") + message); if (!value) throw new Exception(message); }
        private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int length);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    }
}
