// GPL-2.0-or-later. Read-only native workspace GUI routing tests, not a real Beyond Compare acceptance test.
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

namespace TortoiseSCM
{
    internal static class WorkingBeyondCompareUiTests
    {
        private static int assertions;
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length != 2) throw new ArgumentException("Usage: WorkingBeyondCompareUiTests <artifact-directory> <prepared-workspace>");
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
                Console.WriteLine("PASS: working comparison UI (" + assertions + " assertions; recording tool host, no real Beyond Compare)");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void Run(string artifacts, string root)
        {
            Directory.CreateDirectory(artifacts);
            var client = new PlasticClient(new PlasticClientConfig { UseBeyondCompare = false, UseBuiltInDiff = true });
            var workspace = client.DiscoverWorkspace(root);
            Require(workspace != null, "Prepared native workspace exists");
            var before = client.GetStatusAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            string statusBefore = StatusSnapshot(before);
            string selectorBefore = File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector"));
            var contentsBefore = Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly)
                .ToDictionary(path => path, File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);
            string[] names = { "added.txt", "deleted.txt", "lost.txt", "renamed.txt" };
            string[] codes = { "AD", "DE", "LD", "MV" };
            string[] bases = { "", "base deleted.txt", "base lost.txt", "base moved.txt" };
            for (int index = 0; index < names.Length; index++)
            {
                string path = Path.Combine(root, names[index]);
                Require(before.Any(item => SamePath(item.Path, path) && item.StatusCode == codes[index]), "Fixture has native " + codes[index] + " state");
                string local = File.Exists(path) ? File.ReadAllText(path) : "";
                TestComparison(client, workspace, before, path, bases[index], local, index == 1 || index == 2, artifacts);
            }
            Require(StatusSnapshot(client.GetStatusAsync(root, CancellationToken.None).GetAwaiter().GetResult()) == statusBefore,
                "Comparison leaves native pending states unchanged");
            Require(File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector")) == selectorBefore, "Comparison leaves workspace selector unchanged");
            Require(Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly).OrderBy(path => path).SequenceEqual(contentsBefore.Keys.OrderBy(path => path)),
                "Comparison creates or removes no workspace files");
            foreach (var pair in contentsBefore) Require(File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value), "Comparison preserves " + Path.GetFileName(pair.Key));
        }

        private static void TestComparison(PlasticClient client, PlasticWorkspace workspace, IList<PlasticStatusItem> rows,
            string path, string expectedBase, string expectedLocal, bool deleted, string artifacts)
        {
            var host = new DelayedHost(); client.ToolHost = host;
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", workspace.RootPath }), false))
            using (var dialogGuard = new System.Windows.Forms.Timer { Interval = 100 })
            {
                Set(form, "client", client); Set(form, "workspace", workspace); Set(form, "loaded", true);
                Field<Label>(form, "scope").Text = (workspace.IsPartial ? "Partial" : "Standard") + " — " + workspace.RootPath;
                var list = Field<ListView>(form, "files");
                foreach (var item in rows)
                {
                    var row = new ListViewItem(Path.GetFileName(item.Path)) { Tag = item };
                    row.SubItems.Add(item.StatusCode); row.SubItems.Add(item.StatusDescription); list.Items.Add(row);
                    row.Selected = SamePath(item.Path, path);
                }
                string closeNotice = "", unexpectedDialog = "";
                Set(form, "reportError", new Action<string>(message => closeNotice = message));
                // Production errors are modal; dismiss and report them rather than hanging a regression run.
                dialogGuard.Tick += delegate {
                    IntPtr popup = GetLastActivePopup(form.Handle);
                    var className = new StringBuilder(64);
                    GetClassName(popup, className, className.Capacity);
                    if (popup != form.Handle && className.ToString() == "#32770")
                    { unexpectedDialog = Field<TextBox>(form, "output").Text; PostMessage(popup, 0x111, new IntPtr(1), IntPtr.Zero); }
                };
                form.Show(); Application.DoEvents(); dialogGuard.Start();
                var execute = typeof(MainForm).GetMethod("ExecuteAsync", Flags, null, new[] { typeof(PlasticCommand), typeof(List<string>) }, null);
                var task = (Task)execute.Invoke(form, new object[] { PlasticCommand.Diff, new List<string> { path } });
                try
                {
                    PumpUntil(() => host.Ready || task.IsCompleted, "Comparison preparation finishes");
                    Require(host.Ready && !task.IsCompleted && unexpectedDialog.Length == 0, "MainForm routes selected native file into the prepared host: " + unexpectedDialog);
                    Require(Field<bool>(form, "busy") && !Field<Button>(form, "actions").Enabled && !list.Enabled && !Field<Button>(form, "checkin").Enabled,
                        "Pending comparison keeps the main window busy and actions disabled");
                    Require(File.ReadAllText(host.BasePath) == expectedBase && (!File.Exists(host.LocalPath) || File.ReadAllText(host.LocalPath) == expectedLocal),
                        "MainForm comparison receives correct baseline and working contents for " + Path.GetFileName(path));
                    Require((File.GetAttributes(host.BasePath) & FileAttributes.ReadOnly) != 0, "Prepared base is read-only during host lifetime");
                    bool deletedEndpoint = !SamePath(host.LocalPath, path) && !File.Exists(path) &&
                        (!File.Exists(host.LocalPath) || (File.GetAttributes(host.LocalPath) & FileAttributes.ReadOnly) != 0);
                    Require(deleted ? deletedEndpoint : SamePath(host.LocalPath, path),
                        "Deleted/missing files use read-only empty placeholders; existing files retain their working path");
                    form.Close(); Application.DoEvents();
                    Require(!form.IsDisposed && form.Visible && closeNotice.Contains("操作正在进行"), "Close is blocked while comparison owns temporary inputs");
                    Require(File.Exists(host.BasePath) && File.Exists(host.LocalPath), "Blocked close preserves both comparison endpoints");
                    if (Path.GetFileName(path) == "deleted.txt")
                    {
                        string prefix = workspace.IsPartial ? "partial" : "standard";
                        Save(form, Path.Combine(artifacts, prefix + "-working-diff.png"));
                        form.Size = form.MinimumSize; Application.DoEvents(); CheckBounds(form);
                        Save(form, Path.Combine(artifacts, prefix + "-working-diff-minimum.png"));
                    }
                }
                finally
                {
                    host.Complete();
                    PumpUntil(() => task.IsCompleted, "Comparison releases GUI after tool completion");
                    task.GetAwaiter().GetResult();
                }
                Require(unexpectedDialog.Length == 0, "No production error dialog: " + unexpectedDialog);
                Require(!Field<bool>(form, "busy") && Field<Button>(form, "actions").Enabled && list.Enabled, "Comparison completion restores main window interaction");
                Require(!File.Exists(host.BasePath) && !Directory.Exists(Path.GetDirectoryName(host.BasePath)), "Comparison completion cleans temporary input directory");
                Require(deleted ? !File.Exists(path) && !File.Exists(host.LocalPath) : File.Exists(path), "Cleanup does not recreate deleted files or remove working files");
                Require(Field<TextBox>(form, "output").Text.Contains("退出码：0"), "Main window records successful comparison result");
                dialogGuard.Stop(); form.Close(); Require(form.IsDisposed, "Main window closes after comparison completes");
            }
        }

        private sealed class DelayedHost : IPlasticToolHost
        {
            internal string BasePath, LocalPath;
            internal volatile bool Ready;
            private readonly TaskCompletionSource<PlasticCommandResult> completion = new TaskCompletionSource<PlasticCommandResult>();
            public Task<PlasticCommandResult> ShowDiffAsync(string basePath, string localPath, CancellationToken token)
            { BasePath = basePath; LocalPath = localPath; Ready = true; return completion.Task; }
            public Task<PlasticCommandResult> ShowMergeAsync(string a, string b, string c, string d, CancellationToken token)
            { throw new InvalidOperationException("Unexpected merge request"); }
            internal void Complete() { completion.TrySetResult(new PlasticCommandResult { ExitCode = 0 }); }
        }

        private static string StatusSnapshot(IEnumerable<PlasticStatusItem> rows)
        { return string.Join("\n", rows.Select(item => item.StatusCode + "|" + item.Path + "|" + item.IsDirectory).OrderBy(value => value).ToArray()); }
        private static bool SamePath(string a, string b) { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Save(Form form, string path)
        { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void CheckBounds(Form form)
        {
            foreach (Control item in Descendants(form).Where(control => control.Visible && (control is Button || control is ListView || control is TextBox)))
                Require(form.RectangleToScreen(form.ClientRectangle).Contains(item.RectangleToScreen(item.ClientRectangle)), "Minimum layout contains " + item.GetType().Name);
        }
        private static IEnumerable<Control> Descendants(Control root)
        { foreach (Control child in root.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
        private static void PumpUntil(Func<bool> condition, string message)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(45);
            while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Require(condition(), message);
        }
        private static void Require(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        [DllImport("user32.dll")] private static extern IntPtr GetLastActivePopup(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder value, int length);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    }
}
