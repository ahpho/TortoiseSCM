// GPL-2.0-or-later. Real HistoryForm workflows on a dedicated, server-backed fixture.
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
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Xml.Linq;
using TortoiseSCM;

internal static class HistoryWorkflowGuiIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static string run, cm, branch, directory;
    private static int assertions;
    private static readonly List<object> evidence = new List<object>();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, EnumWindow callback, IntPtr data);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    private delegate bool EnumWindow(IntPtr window, IntPtr data);
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "/solo") return CompareTool(args);
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            Run(args); Save(true, null); Console.WriteLine("PASS: " + assertions + " real history GUI assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); Save(false, ex.ToString()); return 1; }
    }
    private static void Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: HistoryWorkflowGuiIntegrationTests.exe <fresh manifest> <cm.exe>");
        var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = Path.GetFullPath((string)manifest["runDirectory"]); cm = args[1]; branch = (string)manifest["branch"];
        string producer = (string)manifest["producer"], partial = (string)manifest["partial"];
        Check((bool)manifest["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Completed dedicated fixture");
        foreach (string root in new[] { producer, partial })
        {
            Check(Path.GetFullPath(root).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && Selector(root).Contains(branch), "Dedicated workspace selector");
            Check(Directory.GetFileSystemEntries(root).All(p => Path.GetFileName(p) == ".plastic"), "Fresh empty workspace");
        }
        directory = "history-" + Guid.NewGuid().ToString("N").Substring(0, 10);
        string file = directory + "/版本 中文 &.txt", nested = directory + "/nested/child.txt", sibling = directory + "-sibling/guard.txt";
        Write(producer, file, "version one 中文\r\n"); Write(producer, nested, "nested one\r\n"); Write(producer, sibling, "sibling one\r\n");
        Native(producer, "add", ".", "-R"); Native(producer, "checkin", ".", "--all", "-c=History GUI baseline"); long first = Revision(producer, file);
        Write(producer, file, "version two 中文\r\n"); Native(producer, "checkin", file, "-c=History GUI second"); long second = Revision(producer, file);
        Write(producer, sibling, "sibling two\r\n"); Native(producer, "checkin", sibling, "-c=History GUI sibling-only"); long unrelated = Revision(producer, sibling);
        Write(producer, file, "version three 中文\r\n"); Native(producer, "checkin", file, "-c=History GUI third"); long third = Revision(producer, file);
        Write(producer, nested, "nested two\r\n"); Native(producer, "checkin", nested, "-c=History GUI nested-only"); long child = Revision(producer, nested);
        Native(partial, "partial", "update", ".");
        string tool = Path.Combine(run, "BComp.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, tool);
        Environment.SetEnvironmentVariable("TSCM_HISTORY_GUI_RECEIPT", Path.Combine(run, "bc-receipt.xml"));
        var client = new PlasticClient(new PlasticClientConfig { CmPath = cm, UseBeyondCompare = true, BeyondComparePath = tool, SettingsPath = Path.Combine(run, "isolated-settings.xml") });
        foreach (string root in new[] { producer, partial })
        {
            string kind = root == partial ? "partial" : "standard", local = Path.GetFullPath(Path.Combine(root, file));
            string selector = Selector(root), clean = Native(root, "status", "--short", "--machinereadable"), head = Head(root);
            using (var form = Open(client, local, root, branch))
            {
                EqualHistory(form, new[] { first, second, third }, kind + " file loads all versions after explicit full refresh if needed");
                Select(form, third, "/" + file); Screenshot(form, kind + "-file-history");
                Compare(form, false, "version two 中文\r\n", "version three 中文\r\n");
                Compare(form, true, "version two 中文\r\n", "version three 中文\r\n");
                Select(form, first, null);
                ((ListView)Field(form, "revisions")).ContextMenuStrip.Items.Cast<ToolStripItem>().Single(i => i.Text == "标记为比较起点").PerformClick();
                Select(form, third, "/" + file);
                File.Delete(Path.Combine(run, "bc-receipt.xml")); ((ToolStripMenuItem)Field(form, "compareMarkedFile")).PerformClick();
                Pump(() => !(bool)Field(form, "comparingFile"), "arbitrary comparison"); Receipt("version one 中文\r\n", "version three 中文\r\n");
                Check(Native(root, "status", "--short", "--machinereadable") == clean && Selector(root) == selector, kind + " comparisons preserve working state");
                Select(form, first, null); Restore(form, false, false);
                Check(File.ReadAllText(local) == "version three 中文\r\n" && Native(root, "status", "--short", "--machinereadable") == clean, kind + " cancelled restore leaves bytes and pending state unchanged");
                Write(root, file, "uncommitted sentinel\r\n"); Restore(form, true, true);
                Check(File.ReadAllText(local) == "uncommitted sentinel\r\n" && ((Label)Field(form, "status")).Text.Contains("失败"), kind + " dirty file restore is rejected without overwriting");
                Native(root, root == partial ? new[] { "partial", "undo", local } : new[] { "undo", local });
                Restore(form, true, false);
                Check(File.ReadAllText(local) == "version one 中文\r\n", kind + " restores exact chosen historical bytes");
                var pending = Await(client.GetStatusAsync(root, CancellationToken.None));
                evidence.Add(new { rollbackWorkspace = root, target = first, pending, nativeStatus = Native(root, "status", "--short", "--machinereadable"), restoredBytes = Convert.ToBase64String(File.ReadAllBytes(local)), selectorBefore = selector, selectorAfter = Selector(root), headBefore = head, headAfter = Head(root) });
                Check(pending.Any(p => p.Path.Equals(local, StringComparison.OrdinalIgnoreCase) && (p.StatusCode == "CH" || p.StatusCode == "CO")), kind + " rollback remains CH/CO pending for checkin");
                Check(Selector(root) == selector && Head(root) == head, kind + " rollback changes neither selector nor server head");
                Screenshot(form, kind + "-restored-history");
                Native(root, root == partial ? new[] { "partial", "undo", local } : new[] { "undo", local });
            }
            using (var form = Open(client, Path.Combine(root, directory), root, branch))
            {
                EqualHistory(form, new[] { first, second, third, child }, kind + " directory includes descendants and excludes similarly prefixed sibling-only commit " + unrelated);
                ((Button)Field(form, "refreshHistory")).PerformClick(); Pump(() => !(bool)Field(form, "loadingHistory"), "refresh all");
                EqualHistory(form, new[] { first, second, third, child }, kind + " refresh retains complete directory scope");
                Screenshot(form, kind + "-directory-history");
            }
        }
        // Exactly the constructor used by an Explorer path-history command, without a branch filter.
        using (var form = Open(client, Path.Combine(producer, directory), producer, null))
        { EqualHistory(form, new[] { first, second, third, child }, "Plain right-click directory history excludes every unrelated repository changeset"); Screenshot(form, "plain-directory-history"); }
        Check(!File.Exists(Path.Combine(run, "isolated-settings.xml")), "Read and restore workflows do not persist settings");
    }
    private static object Field(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
    private static HistoryForm Open(PlasticClient client, string path, string root, string selectedBranch)
    {
        Console.WriteLine("OPEN " + path + " branch=" + selectedBranch);
        var form = new HistoryForm(client, path, root, selectedBranch);
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-24000, -24000); form.Show();
        Pump(() => !(bool)Field(form, "loadingHistory"), "recent history");
        if ((bool)Field(form, "hasMoreHistory")) {
            ((Button)Field(form, "refreshHistory")).PerformClick();
            Pump(() => !(bool)Field(form, "loadingHistory"), "explicit full refresh");
        }
        Check(!(bool)Field(form, "hasMoreHistory") && ((ListView)Field(form, "revisions")).Items.Count > 0, "History is complete after explicit refresh when needed"); return form;
    }
    private static void EqualHistory(HistoryForm form, long[] expected, string description)
    {
        var actual = ((ListView)Field(form, "revisions")).Items.Cast<ListViewItem>().Select(i => ((PlasticHistoryItem)i.Tag).Changeset).ToArray();
        Check(actual.SequenceEqual(expected.OrderByDescending(x => x)), description + ": " + String.Join(",", actual));
    }
    private static void Select(HistoryForm form, long cs, string repositoryPath)
    {
        var list = (ListView)Field(form, "revisions");
        foreach (ListViewItem item in list.Items) item.Selected = false;
        list.Items.Cast<ListViewItem>().Single(i => ((PlasticHistoryItem)i.Tag).Changeset == cs).Selected = true;
        Pump(() => Field(form, "detailRequest") == null && ((Button)Field(form, "restore")).Enabled, "selected details");
        if (repositoryPath != null)
        {
            var files = (ListView)Field(form, "changedFiles");
            files.Items.Cast<ListViewItem>().Single(i => ((PlasticChangesetFile)i.Tag).Path == repositoryPath).Selected = true;
            Application.DoEvents(); Check(((Button)Field(form, "historicalFile")).Enabled, "Selected file permits direct comparison");
        }
    }
    private static void Compare(HistoryForm form, bool doubleClick, string left, string right)
    {
        File.Delete(Path.Combine(run, "bc-receipt.xml")); var list = (ListView)Field(form, "changedFiles");
        if (doubleClick) typeof(Control).GetMethod("OnDoubleClick", Flags).Invoke(list, new object[] { EventArgs.Empty });
        else { var key = new KeyEventArgs(Keys.Control | Keys.D); typeof(Control).GetMethod("OnKeyDown", Flags).Invoke(list, new object[] { key }); Check(key.Handled && key.SuppressKeyPress, "Ctrl+D is handled"); }
        Pump(() => !(bool)Field(form, "comparingFile"), "direct comparison"); Receipt(left, right);
        Check(Application.OpenForms.Cast<Form>().All(f => !(f is HistoricalFileForm)), "Direct comparison opens no intermediate historical-file dialog");
    }
    private static int CompareTool(string[] args)
    {
        var receipt = Environment.GetEnvironmentVariable("TSCM_HISTORY_GUI_RECEIPT");
        if (String.IsNullOrEmpty(receipt) || args.Length < 4) return 90;
        new XDocument(new XElement("receipt", new XElement("left", Convert.ToBase64String(File.ReadAllBytes(args[2]))), new XElement("right", Convert.ToBase64String(File.ReadAllBytes(args[3]))),
            new XElement("leftPath", args[2]), new XElement("rightPath", args[3]), new XElement("readonly", args[1] == "/readonly"))).Save(receipt); return 0;
    }
    private static void Receipt(string left, string right)
    {
        string path = Path.Combine(run, "bc-receipt.xml"); Check(File.Exists(path), "Beyond Compare process launched");
        var receipt = XDocument.Load(path).Root; evidence.Add(new { comparison = receipt.ToString() });
        Check((string)receipt.Element("left") == Convert.ToBase64String(Utf8.GetBytes(left)) && (string)receipt.Element("right") == Convert.ToBase64String(Utf8.GetBytes(right)) && (bool)receipt.Element("readonly"), "Comparison receives exact server revision bytes, read-only");
        Check(!File.Exists((string)receipt.Element("leftPath")) && !File.Exists((string)receipt.Element("rightPath")), "Temporary comparison inputs cleaned after tool exits");
    }
    private static void Restore(HistoryForm form, bool confirm, bool expectError)
    {
        int dialogs = 0; uint thread = GetCurrentThreadId(); string lastDialog = null;
        var gate = new object();
        using (var timer = new System.Threading.Timer(delegate
        {
            lock (gate)
            {
                EnumThreadWindows(thread, delegate(IntPtr window, IntPtr ignored)
                {
                    var name = new StringBuilder(64); GetClassName(window, name, name.Capacity);
                    if (name.ToString() != "#32770") return true;
                    var title = new StringBuilder(512); GetWindowText(window, title, title.Capacity);
                    string identity = window + "|" + title;
                    if (lastDialog != identity) { dialogs++; lastDialog = identity; Console.WriteLine("DIALOG " + identity); }
                    int choice = dialogs == 1 && !confirm ? 2 : 1;
                    if (GetDlgItem(window, choice) == IntPtr.Zero) choice = 2;
                    // WM_COMMAND closes even an inactive offscreen native dialog;
                    // BM_CLICK depends on desktop activation and can silently fail.
                    SendMessage(window, 0x0111, (IntPtr)choice, IntPtr.Zero);
                    return false;
                }, IntPtr.Zero);
            }
        }, null, 30, 30))
        { ((Button)Field(form, "restore")).PerformClick(); Pump(() => !(bool)Field(form, "writing"), "restore"); }
        Check(dialogs == (expectError ? 2 : 1), "Actual restore confirmation/error dialogs acknowledged: " + dialogs);
    }
    private static void Screenshot(Form form, string name)
    {
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(run, name + ".png")); }
        var size = form.Size; form.Size = form.MinimumSize; Application.DoEvents();
        using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(run, name + "-minimum.png")); }
        form.Size = size;
    }
    private static void Pump(Func<bool> done, string operation)
    {
        var deadline = DateTime.UtcNow.AddMinutes(15); var progress = DateTime.UtcNow.AddSeconds(30);
        do { Application.DoEvents(); Thread.Sleep(10); if (DateTime.UtcNow > progress) { Console.WriteLine("WAIT " + operation); progress = DateTime.UtcNow.AddSeconds(30); } }
        while (!done() && DateTime.UtcNow < deadline);
        if (!done()) throw new TimeoutException(operation);
    }
    private static T Await<T>(Task<T> task) { Pump(() => task.IsCompleted, "backend verification"); return task.GetAwaiter().GetResult(); }
    private static string Selector(string root) { return File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector")); }
    private static string Head(string root) { return Native(root, "find", "changeset", "where branch = '" + branch + "' order by changesetid desc limit 1", "--format={changesetid}", "--nototal"); }
    private static long Revision(string root, string file) { return (long)XDocument.Parse(Native(root, "fileinfo", file, "--fields=RevisionChangeset", "--xml", "--encoding=utf-8")).Descendants("FileInfo").Single().Element("RevisionChangeset"); }
    private static void Write(string root, string file, string text) { string path = Path.Combine(root, file); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, Utf8); }
    private static string Native(string root, params string[] args)
    {
        if (!Path.GetFullPath(root).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture path");
        using (var process = Process.Start(new ProcessStartInfo(cm, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = root,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 }))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("Fixture command"); }
            string stdout = output.Result, stderr = error.Result; evidence.Add(new { root, args, exitCode = process.ExitCode, stdout, stderr });
            if (process.ExitCode != 0) throw new Exception("Fixture command failed: " + stdout + stderr); return stdout;
        }
    }
    private static void Check(bool value, string text) { assertions++; evidence.Add(new { assertion = text, success = value }); Console.WriteLine((value ? "PASS " : "FAIL ") + text); if (!value) throw new Exception(text); }
    private static void Save(bool success, string error) { if (run != null) File.WriteAllText(Path.Combine(run, "history-gui-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence }), Utf8); }
}
