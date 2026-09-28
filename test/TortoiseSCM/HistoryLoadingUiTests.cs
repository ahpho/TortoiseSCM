// GPL-2.0-or-later. Exercise automatic history exhaustion through real fake-cm processes.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Xml.Linq;

namespace TortoiseSCM
{
    internal static class HistoryLoadingUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length > 0 && (args[0] == "find" || args[0] == "diff" || args[0] == "status")) return FakeCm(args);
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "bin/TortoiseSCM/qa/history-loading" : args[0]); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void Run(string artifacts)
        {
            var previousContext = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-history-loading-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Meta(root, "plastic.workspace"), "history-loading\nguid\nStandard\n");
            File.WriteAllText(Meta(root, "plastic.selector"), "repository \"test@server:8087\"");
            File.WriteAllText(Meta(root, "history-loading-fixture"), "");
            Write(root, "head", "125");
            try
            {
                using (var form = new HistoryForm(Client(), root, root))
                {
                    form.Show(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    var revisions = Field<ListView>(form, "revisions");
                    Require(revisions.Items.Count == 126 && Ids(form).First() == 125 && Ids(form).Last() == 0, "Opening automatically loads every repository changeset across three batches: " + revisions.Items.Count + " / " + Field<Label>(form, "status").Text);
                    Require(PageQueries(root) == 3 && !Field<bool>(form, "hasMoreHistory"), "Exhaustion is based on server continuation, not first page size");
                    Require(Ids(form).Distinct().Count() == 126, "Automatic pages contain no duplicates");
                    Require(ReadCalls(root).Count(line => line.Contains("changesetid = ")) == 2, "Intermediate batches do not reload details; completion refreshes the final selected changeset once");
                    var filter = Field<TextBox>(form, "filter");
                    filter.Text = "oldest-only"; Pump(() => Field<Button>(form, "restore").Enabled);
                    Require(Ids(form).SequenceEqual(new long[] { 1 }), "Filtering finds a match beyond the first two batches");
                    filter.Clear(); Pump(() => Field<Button>(form, "restore").Enabled);
                    Require(revisions.Items.Count == 126, "Clearing the filter restores all history");
                    Write(root, "head", "130");
                    int pagesBefore = PageQueries(root);
                    Field<Button>(form, "refreshHistory").PerformClick(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(revisions.Items.Count == 131 && Ids(form).First() == 130 && Ids(form).Last() == 0 && PageQueries(root) - pagesBefore == 3,
                        "Refresh retrieves the new head and all older history again");
                    Require(Field<Button>(form, "refreshHistory").Text == "刷新全部", "Refresh label explicitly describes full refresh");
                    Save(form, Path.Combine(artifacts, "history-all.png"));
                    form.Size = form.MinimumSize; Application.DoEvents();
                    foreach (string name in new[] { "refreshHistory", "cancelHistory", "close" }) {
                        var button = Field<Button>(form, name);
                        Require(button.Parent.ClientRectangle.Contains(button.Bounds), "History " + name + " remains in bounds at minimum size");
                    }
                    Require(Field<Label>(form, "historySummary").Text.Contains("已扫描全部历史"), "Completed history is clearly labelled as complete");
                    Save(form, Path.Combine(artifacts, "history-all-minimum.png"));

                    Write(root, "head", "145"); Write(root, "slow-continuation", "");
                    long[] retained = Ids(form);
                    Field<Button>(form, "refreshHistory").PerformClick();
                    Pump(() => File.Exists(Meta(root, "continuation-entered")));
                    Require(Ids(form).SequenceEqual(retained) && !Field<Button>(form, "refreshHistory").Enabled, "Partial refresh retains previous complete history and disables duplicate refresh");
                    int runningQueries = PageQueries(root);
                    var ignored = (Task)typeof(HistoryForm).GetMethod("LoadHistoryPageAsync", Flags).Invoke(form, new object[] { true });
                    Require(ignored.IsCompleted && PageQueries(root) == runningQueries, "A second refresh cannot start a concurrent scan");
                    Field<Button>(form, "cancelHistory").PerformClick(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(Ids(form).SequenceEqual(retained) && Field<bool>(form, "hasMoreHistory"), "Cancelling later refresh batches preserves existing records and reports incompleteness");
                    Require(Field<Label>(form, "historySummary").Text.Contains("加载未完成") && Field<Button>(form, "refreshHistory").Enabled,
                        "Cancelled refresh stays explicitly retryable: " + Field<Label>(form, "historySummary").Text + " / refresh=" + Field<Button>(form, "refreshHistory").Enabled);
                    File.Delete(Meta(root, "slow-continuation")); File.Delete(Meta(root, "continuation-entered"));
                    Write(root, "fail-continuation", "");
                    Field<Button>(form, "refreshHistory").PerformClick(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(Ids(form).SequenceEqual(retained) && Field<Label>(form, "status").Text.Contains("读取失败") && Field<bool>(form, "hasMoreHistory"),
                        "A later-page failure never replaces the old result or claims complete history");
                    File.Delete(Meta(root, "fail-continuation"));
                    Field<Button>(form, "refreshHistory").PerformClick(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(revisions.Items.Count == 146 && !Field<bool>(form, "hasMoreHistory"), "Retry after failure completes all pages");
                    form.Close();
                }

                Write(root, "slow-continuation", "");
                using (var form = new HistoryForm(Client(), root, root)) {
                    form.Show(); Pump(() => File.Exists(Meta(root, "continuation-entered")));
                    Require(Ids(form).Length == 50 && Field<Button>(form, "cancelHistory").Enabled, "First load publishes the first batch while scanning older history");
                    Field<Button>(form, "cancelHistory").PerformClick(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(Ids(form).Length == 50 && Field<bool>(form, "hasMoreHistory"), "Cancelling initial load preserves already visible rows");
                    form.Close();
                }
                File.Delete(Meta(root, "slow-continuation")); File.Delete(Meta(root, "continuation-entered"));

                Write(root, "slow-details", "");
                using (var form = new HistoryForm(Client(), root, root)) {
                    form.Show(); Pump(() => File.Exists(Meta(root, "detail-entered")));
                    long selected = Ids(form).First();
                    Field<Button>(form, "cancelHistory").PerformClick(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(!Field<Button>(form, "restore").Enabled && Field<ListView>(form, "changedFiles").Items.Count == 0,
                        "Cancelling initial selected details leaves no stale file actions");
                    File.Delete(Meta(root, "slow-details")); File.Delete(Meta(root, "detail-entered"));
                    Field<Button>(form, "refreshHistory").PerformClick(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(((PlasticHistoryItem)Field<ListView>(form, "revisions").SelectedItems[0].Tag).Changeset == selected &&
                        Field<Button>(form, "restore").Enabled && Field<ListView>(form, "changedFiles").Items.Count == 1,
                        "Refreshing the same selected changeset reloads details previously cancelled");
                    form.Close();
                }

                Write(root, "head", "110");
                string file = Path.Combine(root, "deleted 中文.txt");
                using (var form = new HistoryForm(Client(), file, root)) {
                    int pagesBefore = PageQueries(root);
                    form.Show(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(!File.Exists(file) && Ids(form).SequenceEqual(new long[] { 4, 1 }), "Missing local file automatically finds all historical path records");
                    Require(PageQueries(root) - pagesBefore == 3 && Field<int>(form, "scannedChangesets") == 111 && !Field<bool>(form, "hasMoreHistory"),
                        "Two empty path batches do not stop loading older matches");
                    form.Close();
                }
                using (var form = new HistoryForm(Client(), root, root, "/main/older")) {
                    form.Show(); Pump(() => !Field<bool>(form, "loadingHistory"));
                    Require(Ids(form).SequenceEqual(new long[] { 4, 1 }) && !Field<bool>(form, "hasMoreHistory"), "Sparse branch history also traverses empty batches automatically");
                    form.Close();
                }
                Console.WriteLine("PASS: history loading UI (" + assertions + " assertions)");
            }
            finally {
                // Disposing a failed test's form cancels the cm process asynchronously.
                // Let that cancellation finish, without masking the original assertion.
                for (int attempt = 0; attempt < 100; attempt++) {
                    try {
                        if (File.Exists(Meta(root, "calls.log"))) File.Copy(Meta(root, "calls.log"), Path.Combine(artifacts, "history-loading-calls.log"), true);
                        if (Directory.Exists(root)) Directory.Delete(root, true);
                        break;
                    }
                    catch (IOException) { Application.DoEvents(); Thread.Sleep(20); if (attempt == 99) Console.Error.WriteLine("Fixture cleanup incomplete: " + root); }
                }
                SynchronizationContext.SetSynchronizationContext(previousContext);
                WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall;
            }
        }

        internal static int FakeCm(string[] args)
        {
            string root = Environment.CurrentDirectory;
            if (!File.Exists(Meta(root, "history-loading-fixture"))) return 96;
            Console.OutputEncoding = new UTF8Encoding(false);
            File.AppendAllText(Meta(root, "calls.log"), String.Join(" ", args) + "\n");
            if (args[0] == "status") {
                Console.WriteLine(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", 110),
                    new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))), new XElement("WkConfigName", "/main@test@server:8087"))); return 0;
            }
            if (args[0] == "diff") {
                int cs = Int32.Parse(args[1].Substring(3));
                Console.WriteLine(cs == 4 || cs == 1 ? "C|/deleted 中文.txt|F||" : "C|/other.txt|F||"); return 0;
            }
            if (args[0] == "find" && args[1] == "branch") {
                Console.WriteLine(new XElement("PLASTICQUERY", new[] { "/main", "/main/older" }.Select(name => new XElement("BRANCH",
                    new XElement("NAME", name), new XElement("PARENT", name == "/main" ? "" : "/main"),
                    new XElement("CHANGESET", 110), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"))))); return 0;
            }
            if (args[0] != "find" || args[1] != "changeset") return 97;
            string query = args[2];
            var before = Regex.Match(query, @"changesetid < (\d+)");
            var exact = Regex.Match(query, @"changesetid = (\d+)");
            var limit = Regex.Match(query, @"limit (\d+)");
            if (exact.Success && File.Exists(Meta(root, "slow-details"))) {
                Write(root, "detail-entered", ""); Thread.Sleep(30000);
            }
            if (before.Success && File.Exists(Meta(root, "slow-continuation"))) {
                Write(root, "continuation-entered", ""); Thread.Sleep(30000);
            }
            if (before.Success && File.Exists(Meta(root, "fail-continuation"))) { Console.Error.WriteLine("simulated older-history failure"); return 8; }
            int top = Int32.Parse(File.ReadAllText(Meta(root, "head")));
            IEnumerable<int> ids = exact.Success ? new[] { Int32.Parse(exact.Groups[1].Value) } : Enumerable.Range(0, top + 1).Reverse();
            if (before.Success) ids = ids.Where(i => i < Int32.Parse(before.Groups[1].Value));
            if (limit.Success) ids = ids.Take(Int32.Parse(limit.Groups[1].Value));
            Console.WriteLine(new XElement("PLASTICQUERY", ids.Select(i => new XElement("CHANGESET", new XElement("CHANGESETID", i),
                new XElement("COMMENT", i == 1 ? "oldest-only" : "commit " + i), new XElement("OWNER", "tester"),
                new XElement("BRANCH", i == 4 || i == 1 ? "/main/older" : "/main")))));
            return 0;
        }

        private static PlasticClient Client() { return new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(40) }); }
        private static string Meta(string root, string name) { return Path.Combine(root, ".plastic", name); }
        private static void Write(string root, string name, string value) { File.WriteAllText(Meta(root, name), value, new UTF8Encoding(false)); }
        private static string[] ReadCalls(string root) { return File.ReadAllLines(Meta(root, "calls.log")); }
        private static int PageQueries(string root) { return ReadCalls(root).Count(line => line.Contains("order by changesetid desc limit 51")); }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static long[] Ids(HistoryForm form) { return Field<ListView>(form, "revisions").Items.Cast<ListViewItem>().Select(row => ((PlasticHistoryItem)row.Tag).Changeset).ToArray(); }
        private static void Pump(Func<bool> ready) {
            var timer = Stopwatch.StartNew();
            Application.DoEvents();
            while (!ready()) { Application.DoEvents(); if (timer.Elapsed.TotalSeconds > 90) throw new TimeoutException("History UI test timed out"); Thread.Sleep(10); }
            Application.DoEvents();
        }
        private static void Save(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception("History loading: " + message); }
    }
}
