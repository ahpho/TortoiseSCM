// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class HistoryDirectDiffUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        private static object Field(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        private static void Wait(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!predicate() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Check(predicate(), "Direct history comparison finishes");
        }
        private static KeyEventArgs Key(ListView list, Keys keys)
        {
            var args = new KeyEventArgs(keys);
            typeof(Control).GetMethod("OnKeyDown", Flags).Invoke(list, new object[] { args });
            Application.DoEvents(); return args;
        }
        private static void DoubleClick(ListView list)
        { typeof(Control).GetMethod("OnDoubleClick", Flags).Invoke(list, new object[] { EventArgs.Empty }); Application.DoEvents(); }
        private static void SelectFile(HistoryForm form, PlasticChangesetFile file)
        {
            var list = (ListView)Field(form, "changedFiles");
            list.Items.Clear(); list.Items.Add(new ListViewItem(new[] { file.Status, file.Path, file.OldPath ?? "", file.ItemType }) { Tag = file }).Selected = true;
            Application.DoEvents();
        }
        internal static void Run(string artifacts)
        {
            string root = Path.GetFullPath(Path.Combine(artifacts, "history-direct-diff-" + Guid.NewGuid().ToString("N")));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "history-direct-ui\nguid\nStandard\n");
            string selectorFile = Path.Combine(metadata, "plastic.selector"), selector = "repository \"history-direct@local\"\n path \"/\"\n branch \"/main\"\n";
            File.WriteAllText(selectorFile, selector);
            try
            {
                using (var form = new HistoryForm(new PlasticClient(new PlasticClientConfig { CmPath = "must-not-be-called.exe" }), root, root))
                {
                    Set(form, "loadingHistory", true); Set(form, "filtering", true); Set(form, "historyRepository", "history-direct@local");
                    form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show();
                    var revisions = (ListView)Field(form, "revisions"); var files = (ListView)Field(form, "changedFiles");
                    revisions.Items.Add(new ListViewItem("10") { Tag = new PlasticHistoryItem { Changeset = 10 } }).Selected = true;
                    Application.DoEvents();
                    int parentCalls = 0, openCalls = 0, markedCalls = 0, unchangedCalls = 0;
                    PlasticChangesetComparison comparison = null, openedComparison = null; PlasticChangesetFile opened = null;
                    var success = new PlasticCommandResult { ExitCode = 0 };
                    Func<long, CancellationToken, Task<PlasticChangesetComparison>> parent = (cs, token) =>
                    { parentCalls++; Check(cs == 10, "Direct action requests selected commit"); return Task.FromResult(comparison); };
                    Set(form, "getParentComparison", parent);
                    Func<PlasticChangesetComparison, PlasticChangesetFile, CancellationToken, Task<PlasticCommandResult>> launch = (pair, file, token) =>
                    { openCalls++; openedComparison = pair; opened = file; return Task.FromResult(success); };
                    Set(form, "openFileComparison", launch);
                    Set(form, "getMarkedComparison", new Func<long, long, CancellationToken, Task<PlasticChangesetComparison>>((from, to, token) =>
                    { markedCalls++; Check(from == 3 && to == 10, "Marked diff preserves exact pair"); comparison.FromChangeset = from; return Task.FromResult(comparison); }));
                    Set(form, "openUnchangedComparison", new Func<PlasticChangesetComparison, string, CancellationToken, Task<PlasticCommandResult>>((pair, path, token) =>
                    { unchangedCalls++; Check(path == "/missing 中文 &.txt" && pair.FromChangeset == 3 && pair.ToChangeset == 10 && pair.Repository == "history-direct@local", "Unchanged marked pair pins repository and exact same server path"); return Task.FromResult(success); }));
                    foreach (string operation in new[] { "A", "C", "D", "M" })
                    {
                        var file = new PlasticChangesetFile { Status = operation, Path = "/missing 中文 &.txt", OldPath = operation == "M" ? "/old.txt" : "", ItemType = "F" };
                        comparison = new PlasticChangesetComparison { RootPath = root, Repository = "history-direct@local", FromChangeset = 1, ToChangeset = 10, Files = new[] { file } };
                        SelectFile(form, file); int before = openCalls;
                        var key = Key(files, Keys.Control | Keys.D);
                        Check(key.Handled && key.SuppressKeyPress && openCalls == before + 1, "Ctrl+D launches directly and suppresses key: " + operation);
                        Check(Object.ReferenceEquals(opened, file) && openedComparison.FromChangeset == 1 && openedComparison.ToChangeset == 10,
                            "Server row and parent pair reach BC unchanged: " + operation);
                        DoubleClick(files); Check(openCalls == before + 2, "Double-click launches direct BC: " + operation);
                        Check(Application.OpenForms.Cast<Form>().All(f => !(f is HistoricalFileForm)), "No intermediate historical file form: " + operation);
                    }
                    Check(!File.Exists(Path.Combine(root, "missing 中文 &.txt")), "Missing working file never required or materialized");
                    int previous = openCalls; Key(files, Keys.Control | Keys.Shift | Keys.D); Check(openCalls == previous, "Modified chord is not Ctrl+D");
                    Set(form, "comparisonChangeset", (long?)3); SelectFile(form, (PlasticChangesetFile)files.Items[0].Tag);
                    ((ToolStripMenuItem)Field(form, "compareMarkedFile")).PerformClick();
                    Check(markedCalls == 1 && openedComparison.FromChangeset == 3, "Marked context command launches directly");
                    comparison.Files = new PlasticChangesetFile[0]; ((ToolStripMenuItem)Field(form, "compareMarkedFile")).PerformClick();
                    Check(unchangedCalls == 1, "Unchanged marked path still opens BC without intermediate form");
                    var original = (PlasticChangesetFile)files.Items[0].Tag;
                    original.Status = "C"; original.OldPath = "";
                    var normalized = new PlasticChangesetFile { Status = "C", Path = original.Path, OldPath = "/renamed-directory/old.txt", ItemType = "F" };
                    comparison.FromChangeset = 1; comparison.Files = new[] { normalized }; DoubleClick(files);
                    Check(Object.ReferenceEquals(opened, normalized) && opened.OldPath == normalized.OldPath, "Renamed parent directory uses comparison-normalized old path");
                    foreach (string kind in new[] { "D", "S", "X" })
                    {
                        SelectFile(form, new PlasticChangesetFile { Status = "C", Path = "/unsupported", ItemType = kind }); previous = openCalls;
                        DoubleClick(files); Key(files, Keys.Control | Keys.D);
                        Check(openCalls == previous && !((Button)Field(form, "historicalFile")).Enabled, "Unsupported item cannot launch BC: " + kind);
                    }
                    SelectFile(form, original);
                    var pending = new TaskCompletionSource<PlasticCommandResult>(); CancellationToken seenToken = CancellationToken.None;
                    Set(form, "openFileComparison", new Func<PlasticChangesetComparison, PlasticChangesetFile, CancellationToken, Task<PlasticCommandResult>>((pair, file, token) =>
                    { openCalls++; seenToken = token; return pending.Task; }));
                    DoubleClick(files); previous = openCalls; DoubleClick(files); Key(files, Keys.Control | Keys.D);
                    Check((bool)Field(form, "comparingFile") && openCalls == previous, "Repeated action blocked until BC exits");
                    pending.SetResult(success); Wait(() => !(bool)Field(form, "comparingFile"));
                    Set(form, "openFileComparison", launch);
                    Set(form, "getParentComparison", new Func<long, CancellationToken, Task<PlasticChangesetComparison>>((cs, token) => { throw new InvalidOperationException("fixture failure"); }));
                    DoubleClick(files); Check(((Label)Field(form, "status")).Text.Contains("fixture failure") && !(bool)Field(form, "comparingFile"), "Preparation errors shown and retry enabled");
                    Set(form, "getParentComparison", parent);
                    var preparing = new TaskCompletionSource<PlasticChangesetComparison>();
                    Set(form, "getParentComparison", new Func<long, CancellationToken, Task<PlasticChangesetComparison>>((cs, token) => preparing.Task));
                    DoubleClick(files); previous = openCalls; File.WriteAllText(selectorFile, selector.Replace("history-direct@local", "other@local"));
                    preparing.SetResult(comparison); Wait(() => !(bool)Field(form, "comparingFile"));
                    Check(openCalls == previous && ((Label)Field(form, "status")).Text.Contains("改变"), "Repository race blocks BC"); File.WriteAllText(selectorFile, selector);
                    Set(form, "getParentComparison", parent);
                    pending = new TaskCompletionSource<PlasticCommandResult>();
                    Set(form, "openFileComparison", new Func<PlasticChangesetComparison, PlasticChangesetFile, CancellationToken, Task<PlasticCommandResult>>((pair, file, token) => { seenToken = token; return pending.Task; }));
                    DoubleClick(files); form.Close(); Check(seenToken.IsCancellationRequested, "Closing history cancels outstanding comparison lifetime");
                    pending.SetCanceled(); Wait(() => !(bool)Field(form, "comparingFile"));
                }
                Check(File.ReadAllText(selectorFile) == selector, "Direct history comparison preserves workspace selector");
            }
            finally { Directory.Delete(root, true); }
            Console.WriteLine("History direct diff UI: " + assertions + " assertions passed.");
        }
    }
}
