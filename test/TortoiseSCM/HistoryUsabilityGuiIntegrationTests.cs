// GPL-2.0-or-later. Visible dialogs and real cm reads/updates on new isolated workspaces.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class HistoryUsabilityGuiIntegrationTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static string artifacts;
        private static int assertions;
        private static readonly List<object> scenarios = new List<object>();

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            WindowsFormsSynchronizationContext.AutoInstall = false;
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            try { Run(args); SaveResult(true, null); Console.WriteLine("PASS: history usability real GUI (" + assertions + " assertions)"); return 0; }
            catch (Exception error) { SaveResult(false, error.ToString()); Console.Error.WriteLine(error); return 1; }
        }

        private static void Run(string[] args)
        {
            if (args.Length != 3) throw new ArgumentException("Usage: <dedicated branch manifest> <cm.exe> <new artifacts directory>");
            var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Encoding.UTF8));
            string requestedArtifacts = Path.GetFullPath(args[2]);
            if (Directory.Exists(requestedArtifacts)) throw new ArgumentException("Use a new artifacts directory.");
            artifacts = requestedArtifacts;
            Directory.CreateDirectory(artifacts);
            string branch = (string)manifest["branch"], specification = (string)manifest["repository"];
            Require(branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Dedicated branch only");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = args[1], Timeout = TimeSpan.FromMinutes(3) });
            int separator = specification.IndexOf('@');
            var repository = Await(client.GetRepositoriesAsync(specification.Substring(separator + 1), CancellationToken.None))
                .Single(item => item.Specification == specification);
            var published = Await(client.GetHistoryPageAsync((string)manifest["producer"], branch, null, 100, CancellationToken.None)).Items;
            long head = published.Max(item => item.Changeset), older = published.Where(item => item.Changeset > 1).Min(item => item.Changeset);
            Require(head > older, "Fixture contains multiple real changesets for snapshot checks");
            foreach (bool partial in new[] { false, true })
            {
                string role = partial ? "partial" : "standard", root = Path.Combine(artifacts, role);
                var clock = Stopwatch.StartNew();
                using (var wizard = new WorkspaceCreationForm(root))
                {
                    Set(wizard, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>(client.GetRepositoriesAsync));
                    Set(wizard, "getWorkspaces", new Func<CancellationToken, Task<IList<PlasticWorkspace>>>(client.GetRegisteredWorkspacesAsync));
                    Set(wizard, "createWorkspace", new Func<PlasticRepositoryInfo, string, string, string, bool, IProgress<string>, CancellationToken, Task<PlasticWorkspaceCreationResult>>(client.CreateWorkspaceAsync));
                    Set(wizard, "confirm", new Func<string, bool>(message => true));
                    Field<TextBox>(wizard, "server").Text = repository.Server;
                    Field<TextBox>(wizard, "branch").Text = branch;
                    Field<ComboBox>(wizard, "mode").SelectedIndex = partial ? 0 : 1;
                    wizard.Show(); Pump(() => Field<bool>(wizard, "defaultsReady"), "local workspace check");
                    Field<Button>(wizard, "query").PerformClick();
                    Pump(() => Field<CancellationTokenSource>(wizard, "queryCancellation") == null, "repository query");
                    var choices = Field<ComboBox>(wizard, "repositories");
                    choices.SelectedItem = choices.Items.Cast<PlasticRepositoryInfo>().Single(item => item.Specification == specification);
                    Require(Field<TextBox>(wizard, "workspaceName").Text == repository.Name, role + " real repository selection synchronizes name");
                    Field<TextBox>(wizard, "workspaceName").Text = "tscm-history-ux-" + role + "-" + Guid.NewGuid().ToString("N").Substring(0, 10);
                    Screenshot(wizard, role + "-pull-ready");
                    Field<Button>(wizard, "create").PerformClick();
                    Pump(() => wizard.IsDisposed || (!Field<bool>(wizard, "writing") &&
                        (Field<TextBox>(wizard, "status").Text.StartsWith("首次拉取未完成", StringComparison.Ordinal) ||
                         Field<TextBox>(wizard, "status").Text.StartsWith("拉取前检查失败", StringComparison.Ordinal))), "initial pull");
                    Require(wizard.SelectedWorkspacePath == root, role + " actual pull completes: " + Field<TextBox>(wizard, "status").Text);
                    Require(Application.OpenForms.Cast<Form>().All(form => !(form is MainForm)), role + " actual pull opens no commit window");
                }
                var state = Await(client.GetHistoryLocalStateAsync(root, CancellationToken.None));
                Require(state.IsWorkspaceRoot && !state.IsApproximate && state.LoadedThroughChangeset == head, role + " initial pull records actual root baseline");
                string file = Directory.GetFiles(root, "*", SearchOption.AllDirectories).First(item => !item.StartsWith(Path.Combine(root, ".plastic") + "\\", StringComparison.OrdinalIgnoreCase));
                CheckHistory(client, root, root, role + "-root", true, head, !partial);
                CheckHistory(client, Path.GetDirectoryName(file), root, role + "-directory", false, head, false);
                CheckHistory(client, file, root, role + "-file", false, head, false);
                if (partial)
                {
                    var switchResult = Await(client.SwitchAsync(root, older, CancellationToken.None));
                    Require(switchResult.Succeeded, "Gluon root historical snapshot succeeds: " + switchResult.Output);
                    state = Await(client.GetHistoryLocalStateAsync(root, CancellationToken.None));
                    Require(state.LoadedThroughChangeset == older && !state.IsApproximate, "Historical snapshot lowers root watermark");
                    CheckHistory(client, root, root, role + "-older-root", true, older, false);
                    file = Directory.GetFiles(root, "*", SearchOption.AllDirectories).First(item => !item.StartsWith(Path.Combine(root, ".plastic") + "\\", StringComparison.OrdinalIgnoreCase));
                    var update = Await(client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Update, Paths = new List<string> { file } }, CancellationToken.None));
                    Require(update.Succeeded, "Gluon child update succeeds: " + update.Output);
                    Require(Await(client.GetHistoryLocalStateAsync(root, CancellationToken.None)).LoadedThroughChangeset == older, "Child update leaves root watermark unchanged");
                    update = Await(client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Update, Paths = new List<string> { root } }, CancellationToken.None));
                    Require(update.Succeeded, "Gluon root update succeeds: " + update.Output);
                    Require(Await(client.GetHistoryLocalStateAsync(root, CancellationToken.None)).LoadedThroughChangeset == head, "Root update advances watermark back to head");
                    CheckHistory(client, root, root, role + "-updated-root", true, head, false);
                }
                scenarios.Add(new { role, root, head, older, elapsedMs = clock.ElapsedMilliseconds });
            }
        }

        private static void CheckHistory(PlasticClient client, string path, string root, string name, bool rootHistory, long loaded, bool refreshAll)
        {
            var clock = Stopwatch.StartNew();
            using (var form = new HistoryForm(client, path, root))
            {
                form.Show(); Pump(() => !Field<bool>(form, "loadingHistory"), name + " recent history");
                var rows = Field<ListView>(form, "revisions");
                Require(!Field<ProgressBar>(form, "historyProgress").Visible, name + " idle progress is hidden");
                Require(Field<int>(form, "scannedChangesets") <= (rootHistory ? 100 : 50), name + " first load is bounded");
                Require(rows.Items.Cast<ListViewItem>().All(row => row.Font.Bold == (rootHistory && ((PlasticHistoryItem)row.Tag).Changeset > Math.Max(1, loaded))), name + " root-only watermark styling");
                Require(rootHistory || !Field<Label>(form, "historySummary").Text.Contains("粗体"), name + " child history has no bold feature");
                Screenshot(form, name);
                form.Size = form.MinimumSize; Application.DoEvents(); Screenshot(form, name + "-minimum");
                if (refreshAll)
                {
                    Field<Button>(form, "refreshHistory").PerformClick(); Pump(() => !Field<bool>(form, "loadingHistory"), name + " full history");
                    Require(!Field<bool>(form, "hasMoreHistory") && rows.Items.Count > 100, name + " explicit full refresh reads older history");
                    Require(!Field<ProgressBar>(form, "historyProgress").Visible, name + " completed refresh hides progress");
                }
                scenarios.Add(new { name, rootHistory, loaded, rows = rows.Items.Count, scanned = Field<int>(form, "scannedChangesets"), elapsedMs = clock.ElapsedMilliseconds });
                form.Close();
            }
        }

        private static T Await<T>(Task<T> task) { Pump(() => task.IsCompleted, "backend operation"); return task.GetAwaiter().GetResult(); }
        private static void Pump(Func<bool> done, string operation)
        {
            var clock = Stopwatch.StartNew();
            long nextReport = 30000;
            do {
                Application.DoEvents();
                if (clock.ElapsedMilliseconds > nextReport) { Console.WriteLine("WAIT " + operation); nextReport += 30000; }
                if (clock.Elapsed.TotalMinutes > 8) throw new TimeoutException(operation);
                Thread.Sleep(10);
            } while (!done());
            Application.DoEvents();
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Flags).SetValue(owner, value); }
        private static void Screenshot(Form form, string name) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(artifacts, name + ".png"), ImageFormat.Png); } }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
        private static void SaveResult(bool success, string error)
        {
            if (artifacts == null || !Directory.Exists(artifacts)) return;
            File.WriteAllText(Path.Combine(artifacts, "results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, scenarios }), new UTF8Encoding(false));
        }
    }
}
