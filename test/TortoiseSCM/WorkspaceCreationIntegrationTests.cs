// GPL-2.0-or-later. First checkout, update, history and checkin on a dedicated server branch.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using TortoiseSCM;

internal static class WorkspaceCreationIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly List<object> Evidence = new List<object>();
    private static string run, cm, branch;
    private static int assertions;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        int exitCode = 1;
        // A persistent WinForms loop is required for real asynchronous continuations.
        // Repeated standalone DoEvents loops can tear down their marshaling context.
        EventHandler start = null;
        start = delegate {
            Application.Idle -= start;
            try { Run(args); Save(true, null); Console.WriteLine("PASS: " + assertions + " live workspace creation workflow assertions"); exitCode = 0; }
            catch (Exception error) { Save(false, error.ToString()); Console.Error.WriteLine(error); }
            finally { Application.ExitThread(); }
        };
        Application.Idle += start;
        Application.Run();
        return exitCode;
    }
    private static void Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected fresh manifest and cm.exe");
        var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = Path.GetFullPath((string)manifest["runDirectory"]); cm = args[1]; branch = (string)manifest["branch"];
        string producer = (string)manifest["producer"], consumer = (string)manifest["consumer"], reference = (string)manifest["referenceWorkspace"];
        string repository = (string)manifest["repository"], server = repository.Substring(repository.IndexOf('@') + 1);
        Check((bool)manifest["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Dedicated test branch fixture");
        foreach (string root in new[] { producer, consumer })
            Check(Path.GetFullPath(root).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && Selector(root).Contains(branch) &&
                !Directory.GetFileSystemEntries(root).Any(path => Path.GetFileName(path) != ".plastic"), "Isolated empty test workspace");
        string referenceState = Snapshot(reference), referenceSelector = Selector(reference), referenceStatus = Execute(reference, "status", "--short", "--machinereadable");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = cm, Timeout = TimeSpan.FromSeconds(90), SettingsPath = Path.Combine(run, "settings.xml") });
        var repositories = client.GetRepositoriesAsync(server, CancellationToken.None).GetAwaiter().GetResult();
        var selectedRepo = repositories.Single(item => item.Specification == repository);
        string emptyDestination = Path.Combine(run, "empty-gluon");
        var emptyResult = client.CreateWorkspaceAsync(selectedRepo, "tscm-empty-gluon-" + Guid.NewGuid().ToString("N"), emptyDestination, branch, true, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(emptyResult.Succeeded && client.GetWorkspaceAsync(emptyDestination, CancellationToken.None).GetAwaiter().GetResult().IsPartial && Selector(emptyDestination).Contains(branch), "Empty branch creates a real Partial tree and preserves selected branch: " + emptyResult.Error);
        Write(producer, "selected 中文.txt", "baseline\r\n"); Write(producer, "excluded.txt", "baseline\r\n");
        Directory.CreateDirectory(Path.Combine(producer, "目录 & space"));
        File.WriteAllBytes(Path.Combine(producer, "目录 & space", "binary.dat"), new byte[] { 0, 1, 255, 42 });
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "--all", "-c=Workspace wizard isolated baseline");
        string standardDestination = Path.Combine(run, "standard-checkout");
        var standardResult = client.CreateWorkspaceAsync(selectedRepo, "tscm-standard-" + Guid.NewGuid().ToString("N"), standardDestination, branch, null, CancellationToken.None).GetAwaiter().GetResult();
        Check(standardResult.Succeeded && !client.GetWorkspaceAsync(standardDestination, CancellationToken.None).GetAwaiter().GetResult().IsPartial && Selector(standardDestination).Contains(branch), "Original overload retains Standard creation: " + standardResult.Error);
        Check(selectedRepo.Name == "TestSCM" && selectedRepo.Id >= 0 && !String.IsNullOrWhiteSpace(selectedRepo.Guid), "Real repository query returns native identity");
        string destination = Path.Combine(run, "拉取 中文 & space"), name = "tscm-wizard-" + Guid.NewGuid().ToString("N");
        Check(!Directory.Exists(destination), "First checkout starts without an existing local directory");
        using (var wizard = new WorkspaceCreationForm(destination))
        {
            Set(wizard, "getRepositories", new Func<string, CancellationToken, Task<IList<PlasticRepositoryInfo>>>(client.GetRepositoriesAsync));
            Set(wizard, "getWorkspaces", new Func<CancellationToken, Task<IList<PlasticWorkspace>>>(client.GetRegisteredWorkspacesAsync));
            Set(wizard, "createWorkspace", new Func<PlasticRepositoryInfo, string, string, string, bool, IProgress<string>, CancellationToken, Task<PlasticWorkspaceCreationResult>>(client.CreateWorkspaceAsync));
            Set(wizard, "confirm", new Func<string, bool>(text => text.Contains(repository) && text.Contains(destination) && text.Contains(branch)));
            Field<TextBox>(wizard, "server").Text = server;
            Field<TextBox>(wizard, "workspaceName").Text = name;
            Field<TextBox>(wizard, "branch").Text = branch;
            wizard.Show(); Application.DoEvents();
            var queryUntil = DateTime.UtcNow.AddMinutes(1);
            while (Field<object>(wizard, "queryCancellation") != null && DateTime.UtcNow < queryUntil) { Application.DoEvents(); Thread.Sleep(10); }
            Await(wizard, "QueryAsync", null);
            var choices = Field<ComboBox>(wizard, "repositories");
            choices.SelectedItem = choices.Items.Cast<PlasticRepositoryInfo>().Single(item => item.Specification == repository);
            Check(Field<Button>(wizard, "create").Enabled, "Real wizard query enables the selected repository");
            using (var picture = new System.Drawing.Bitmap(wizard.Width, wizard.Height)) { wizard.DrawToBitmap(picture, new System.Drawing.Rectangle(0, 0, wizard.Width, wizard.Height)); picture.Save(Path.Combine(run, "first-checkout-wizard.png")); }
            Await(wizard, "CreateAsync", null);
            Check(wizard.DialogResult == DialogResult.OK && wizard.SelectedWorkspacePath == destination, "Real wizard defaults to Gluon and downloads the isolated branch: " + Field<TextBox>(wizard, "status").Text);
        }
        var workspace = client.GetWorkspaceAsync(destination, CancellationToken.None).GetAwaiter().GetResult();
        Check(workspace.RootPath == destination && workspace.Repository == repository && workspace.IsPartial && Selector(destination).Contains(branch), "Created Gluon workspace selects the requested repository and isolated branch");
        Check(File.ReadAllText(Path.Combine(destination, "selected 中文.txt"), Utf8) == "baseline\r\n" &&
            File.ReadAllBytes(Path.Combine(destination, "目录 & space", "binary.dat")).SequenceEqual(new byte[] { 0, 1, 255, 42 }), "First checkout preserves Unicode paths, CRLF and binary bytes");
        var initialStatus = client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Status, WorkingDirectory = destination, Paths = new List<string> { destination } }, CancellationToken.None).GetAwaiter().GetResult();
        Check(initialStatus.Succeeded && PlasticClient.ParseStatus(initialStatus.Output, destination).Count == 0, "New workspace is clean");
        Check(client.GetHistoryAsync(destination, CancellationToken.None).GetAwaiter().GetResult().Count > 0, "Show log reads the downloaded workspace history");
        bool refused = false;
        try { client.CreateWorkspaceAsync(selectedRepo, name, destination, branch, null, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (ArgumentException) { refused = true; }
        catch (InvalidOperationException) { refused = true; }
        Check(refused && File.ReadAllText(Path.Combine(destination, "selected 中文.txt"), Utf8) == "baseline\r\n", "Repeated creation refuses an existing workspace without changing its files");
        Write(producer, "selected 中文.txt", "server update\r\n"); Write(producer, "incoming.txt", "new server file\n");
        Native(producer, "add", Path.Combine(producer, "incoming.txt")); Native(producer, "checkin", producer, "--all", "-c=Workspace wizard update test");
        var selectedUpdate = client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = destination, Paths = new List<string> { Path.Combine(destination, "selected 中文.txt") } }, CancellationToken.None).GetAwaiter().GetResult();
        Check(selectedUpdate.Succeeded && File.ReadAllText(Path.Combine(destination, "selected 中文.txt"), Utf8) == "server update\r\n" && !File.Exists(Path.Combine(destination, "incoming.txt")) && File.ReadAllText(Path.Combine(destination, "excluded.txt"), Utf8) == "baseline\r\n", "Gluon selected-file update preserves unselected files and does not load other incoming paths");
        var updated = client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = destination, Paths = new List<string> { destination } }, CancellationToken.None).GetAwaiter().GetResult();
        Check(updated.Succeeded && File.ReadAllText(Path.Combine(destination, "selected 中文.txt"), Utf8) == "server update\r\n" && File.ReadAllText(Path.Combine(destination, "incoming.txt"), Utf8) == "new server file\n", "Update downloads the next server changeset");
        string selected = Path.Combine(destination, "selected 中文.txt");
        Write(destination, "selected 中文.txt", "GUI checkin after first checkout\n"); Write(destination, "excluded.txt", "must remain local\n");
        using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", destination }), false))
        {
            Set(form, "client", client); Set(form, "workspace", workspace); Set(form, "loaded", true);
            Set(form, "messageStore", new CommitMessageStore(Path.Combine(run, "message-library")));
            string error = ""; int reviews = 0;
            Set(form, "reportError", new Action<string>(text => error = text));
            form.Show(); Application.DoEvents(); Await(form, "RefreshAsync", null);
            var rows = Field<ListView>(form, "files");
            foreach (ListViewItem row in rows.Items) row.Checked = String.Equals(((PlasticStatusItem)row.Tag).Path, selected, StringComparison.OrdinalIgnoreCase);
            Check(rows.CheckedItems.Count == 1, "New workspace pending dialog selects only the intended file");
            Field<TextBox>(form, "comment").Text = "首次拉取后的 GUI 提交";
            Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((preview, text, uncertain) => { reviews++; return DialogResult.OK; }));
            Await(form, "CheckinSelectionAsync", new object[] { null });
            Check(error == "" && reviews == 1 && Field<TextBox>(form, "comment").Text == "", "Actual MainForm preview and checkin succeed in the new workspace");
            Check(rows.Items.Count == 1 && String.Equals(((PlasticStatusItem)rows.Items[0].Tag).Path, Path.Combine(destination, "excluded.txt"), StringComparison.OrdinalIgnoreCase), "Unselected file remains pending after commit");
            using (var picture = new System.Drawing.Bitmap(form.Width, form.Height)) { form.DrawToBitmap(picture, form.ClientRectangle); picture.Save(Path.Combine(run, "first-checkout-main.png")); }
        }
        Native(consumer, "update", consumer, "--dontmerge");
        Check(File.ReadAllText(Path.Combine(consumer, "selected 中文.txt"), Utf8) == "GUI checkin after first checkout\n" && File.ReadAllText(Path.Combine(consumer, "excluded.txt"), Utf8) == "baseline\r\n", "Independent consumer confirms selected-only GUI commit");
        Check(client.GetHistoryAsync(destination, CancellationToken.None).GetAwaiter().GetResult().Count >= 3, "Show log includes initial, updated and new GUI changesets");
        Check(Snapshot(reference) == referenceState && Selector(reference) == referenceSelector && Execute(reference, "status", "--short", "--machinereadable") == referenceStatus, "Original TestSCM remains unchanged");
    }
    private static void Await(object target, string method, object[] args)
    {
        var task = (Task)target.GetType().GetMethod(method, Flags).Invoke(target, args);
        var until = DateTime.UtcNow.AddMinutes(2);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        if (!task.IsCompleted) throw new TimeoutException(method + ": " + Field<Control>(target, "status").Text);
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
    private static string Selector(string root) { return File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector")); }
    private static void Write(string root, string name, string content) { File.WriteAllText(Path.Combine(root, name), content, Utf8); }
    private static string Head(string root)
    { return Native(root, "find", "changeset", "where branch = '" + branch + "' order by changesetid desc limit 1", "--xml", "--nototal", "--encoding=utf-8"); }
    private static string Snapshot(string root)
    {
        string metadata = Path.Combine(root, ".plastic") + "\\";
        using (var hash = SHA256.Create()) return String.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(metadata, StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => path + "|" + Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)))));
    }
    private static string Native(string root, params string[] args)
    {
        if (!Path.GetFullPath(root).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture path");
        return Execute(root, args);
    }
    private static string Execute(string root, params string[] args)
    {
        var start = new ProcessStartInfo(cm, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = root,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("Native fixture timeout"); }
            string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult();
            Evidence.Add(new { root, args, exitCode = process.ExitCode, output = stdout, error = stderr });
            if (process.ExitCode != 0) throw new Exception(stdout + stderr); return stdout;
        }
    }
    private static void Check(bool value, string description) { assertions++; Evidence.Add(new { description, success = value }); if (!value) throw new Exception(description); Console.WriteLine("PASS: " + description); }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, "workspace-creation-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
}
