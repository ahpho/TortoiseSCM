// GPL-2.0-or-later. Opt-in real HistoryForm-to-Beyond-Compare read-only window smoke.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using TortoiseSCM;

internal static class HistoryBeyondCompareGuiSmokeTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    private static readonly List<string> Evidence = new List<string>();
    private static string output;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); int result = 1; EventHandler start = null;
        start = delegate {
            Application.Idle -= start;
            try { Run(args); Save(true, null); Console.WriteLine("PASS: actual HistoryForm marked comparison through real vendor BC window"); result = 0; }
            catch (Exception ex) { Save(false, ex.ToString()); Console.Error.WriteLine(ex); }
            finally { Application.ExitThread(); }
        };
        Application.Idle += start; Application.Run(); return result;
    }

    private static void Run(string[] args)
    {
        if (args.Length != 7) throw new ArgumentException("Expected workspace, repository path, from cs, to cs, cm.exe, BComp.exe, new output directory.");
        string root = Path.GetFullPath(args[0]), path = args[1], executable = Path.GetFullPath(args[5]);
        long from = Int64.Parse(args[2]), to = Int64.Parse(args[3]); output = Path.GetFullPath(args[6]);
        Check(path.StartsWith("/", StringComparison.Ordinal) && from >= 0 && to > from && !Directory.Exists(output), "Valid fixed repository endpoints and fresh artifact directory");
        Directory.CreateDirectory(output);
        var client = new PlasticClient(new PlasticClientConfig { CmPath = args[4], BeyondComparePath = executable, UseBeyondCompare = true,
            SettingsPath = Path.Combine(output, "isolated-settings.xml"), Timeout = TimeSpan.FromSeconds(90) });
        string repository = client.DiscoverWorkspace(root).Repository;
        var details = client.GetChangesetAsync(root, to, CancellationToken.None).GetAwaiter().GetResult();
        var row = details.Files.Single(f => f.Path == path && (f.ItemType == "F" || f.ItemType == "B"));
        var pair = client.GetChangesetComparisonAsync(root, from, to, CancellationToken.None).GetAwaiter().GetResult();
        Check(row.Status == "C" && String.IsNullOrEmpty(row.OldPath) && pair.Repository == repository &&
            pair.Files.Count(f => f.Path == path && f.Status == "C" && String.IsNullOrEmpty(f.OldPath)) == 1,
            "Smoke fixture is a same-path modification existing at both endpoints; additions, deletions and moves need separate scenarios");
        byte[] expectedLeft = client.GetHistoricalFileAsync(root, path, from, repository, CancellationToken.None).GetAwaiter().GetResult().Content;
        byte[] expectedRight = client.GetHistoricalFileAsync(root, path, to, repository, CancellationToken.None).GetAwaiter().GetResult().Content;
        string extension = Path.GetExtension(path);
        var previousIds = new HashSet<int>(Process.GetProcessesByName("BCompare").Select(p => { using (p) return p.Id; }));
        var previousDirectories = new HashSet<string>(Directory.GetDirectories(Path.GetTempPath(), "TortoiseSCM-history-*"), StringComparer.OrdinalIgnoreCase);
        Process owned = null;
        using (var form = new HistoryForm(client, Path.Combine(root, path.TrimStart('/').Replace('/', '\\')), root))
        {
            Set(form, "loadingHistory", true); Set(form, "filtering", true); Set(form, "historyRepository", repository); Set(form, "comparisonChangeset", (long?)from);
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false; form.Show(); Application.DoEvents();
            var revisions = Field<ListView>(form, "revisions");
            revisions.Items.Add(new ListViewItem(to.ToString()) { Tag = new PlasticHistoryItem { Changeset = to } }).Selected = true;
            var files = Field<ListView>(form, "changedFiles");
            files.Items.Add(new ListViewItem(new[] { row.Status, row.Path, row.OldPath ?? "", row.ItemType }) { Tag = row }).Selected = true;
            Application.DoEvents();
            var menu = Field<ToolStripMenuItem>(form, "compareMarkedFile");
            Check(menu.Enabled && menu.Text.Contains(from.ToString()), "Real marked-file comparison menu targets selected arbitrary changeset");
            menu.PerformClick(); Application.DoEvents();
            try
            {
                Pump(() => {
                    foreach (var process in Process.GetProcessesByName("BCompare"))
                    {
                        try
                        {
                            if (!previousIds.Contains(process.Id) && process.MainWindowHandle != IntPtr.Zero &&
                                String.Equals(process.MainModule.FileName, Path.Combine(Path.GetDirectoryName(executable), "BCompare.exe"), StringComparison.OrdinalIgnoreCase) &&
                                process.MainWindowTitle.Contains("#cs:" + from) && process.MainWindowTitle.Contains("#cs:" + to) && process.MainWindowTitle.Contains(path))
                            { owned = process; return true; }
                        }
                        finally { if (owned != process) process.Dispose(); }
                    }
                    if (!Field<bool>(form, "comparingFile")) throw new InvalidOperationException(Field<Label>(form, "status").Text);
                    return false;
                }, "Unique new vendor Beyond Compare window shows repository path and both selected versions", 60);
                Check(Application.OpenForms.Cast<Form>().All(f => !(f is HistoricalFileForm)), "No HistoricalFileForm intermediate window");
                Check(Field<bool>(form, "comparingFile") && !Field<Button>(form, "historicalFile").Enabled, "History waits and prevents duplicate comparison until BC closes");
                string[] directories = Directory.GetDirectories(Path.GetTempPath(), "TortoiseSCM-history-*").Where(p => !previousDirectories.Contains(p))
                    .Where(p => File.Exists(Path.Combine(p, "from-" + from + extension)) && File.Exists(Path.Combine(p, "to-" + to + extension))).ToArray();
                Check(directories.Length == 1, "Exactly one owned historical export directory remains alive while BC is open");
                string temporary = directories[0], left = Path.Combine(temporary, "from-" + from + extension), right = Path.Combine(temporary, "to-" + to + extension);
                byte[] leftBytes = File.ReadAllBytes(left), rightBytes = File.ReadAllBytes(right);
                Check(leftBytes.SequenceEqual(expectedLeft) && rightBytes.SequenceEqual(expectedRight), "BC input bytes equal separately downloaded historical endpoints, independent of current working file");
                owned.WaitForInputIdle(5000); var pause = DateTime.UtcNow.AddMilliseconds(700); while (DateTime.UtcNow < pause) { Application.DoEvents(); Thread.Sleep(10); }
                Rect bounds; Check(GetWindowRect(owned.MainWindowHandle, out bounds), "Read BC window bounds");
                using (var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr dc = graphics.GetHdc(); bool captured;
                    try { captured = PrintWindow(owned.MainWindowHandle, dc, 2); } finally { graphics.ReleaseHdc(dc); }
                    Check(captured, "Read-only capture of owned Beyond Compare window"); bitmap.Save(Path.Combine(output, "history-beyond-compare-window.png"));
                }
                Evidence.Add("Owned PID: " + owned.Id + "; title: " + owned.MainWindowTitle);
                Check(owned.CloseMainWindow(), "Normal close accepted only by test-owned read-only /solo window");
                Pump(() => !Field<bool>(form, "comparingFile"), "History waiter finishes after BC closes", 30);
                Check(Field<Label>(form, "status").Text == "Beyond Compare 已关闭。", "History reports successful close, including BC difference exit code");
                Check(!Directory.Exists(temporary), "Historical contributor files are removed after BC closes");
                Check(Field<Button>(form, "historicalFile").Enabled && Application.OpenForms.Cast<Form>().All(f => !(f is HistoricalFileForm)), "History comparison can be repeated and no intermediate dialog appeared");
                using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(output, "history-after-beyond-compare.png")); }
            }
            finally { if (owned != null) { if (!owned.HasExited) owned.CloseMainWindow(); owned.Dispose(); } }
            form.Close();
        }
    }
    private static void Pump(Func<bool> condition, string message, int seconds) { var until = DateTime.UtcNow.AddSeconds(seconds); do { Application.DoEvents(); if (condition()) { Check(true, message); return; } Thread.Sleep(100); } while (DateTime.UtcNow < until); throw new TimeoutException(message); }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
    private static void Check(bool value, string message) { Evidence.Add((value ? "PASS: " : "FAIL: ") + message); Console.WriteLine(Evidence.Last()); if (!value) throw new Exception(message); }
    private static void Save(bool success, string error) { if (output != null && Directory.Exists(output)) File.WriteAllText(Path.Combine(output, "history-bc-result.json"), new JavaScriptSerializer().Serialize(new { success, error, evidence = Evidence }), new UTF8Encoding(false)); }
}
