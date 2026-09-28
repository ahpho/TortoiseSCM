// GPL-2.0-or-later. MainForm successful-message persistence with real isolated checkins.
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

internal static class CommitMessageIntegrationTests
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
            try { Run(args); Save(true, null); Console.WriteLine("PASS: " + assertions + " live commit message assertions"); exitCode = 0; }
            catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); }
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
        string producer = (string)manifest["producer"], partial = (string)manifest["partial"], consumer = (string)manifest["consumer"], reference = (string)manifest["referenceWorkspace"];
        Check((bool)manifest["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Dedicated test branch fixture");
        foreach (string root in new[] { producer, partial, consumer })
            Check(Path.GetFullPath(root).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && Selector(root).Contains(branch) &&
                !Directory.GetFileSystemEntries(root).Any(path => Path.GetFileName(path) != ".plastic"), "Isolated empty test workspace");
        string referenceState = Snapshot(reference), referenceSelector = Selector(reference), referenceStatus = Execute(reference, "status", "--short", "--machinereadable");
        Write(producer, "selected 中文.txt", "base\n"); Write(producer, "excluded.txt", "base\n");
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "--all", "-c=Commit message integration baseline");
        string libraryPath = Path.Combine(run, "message-library");
        var store = new CommitMessageStore(libraryPath);
        var client = new PlasticClient(new PlasticClientConfig { CmPath = cm, Timeout = TimeSpan.FromSeconds(45), SettingsPath = Path.Combine(run, "settings.xml") });
        int successful = 0;
        foreach (string root in new[] { producer, partial })
        {
            if (root == partial) Native(root, "partial", "update", ".", "--report");
            var workspace = client.GetWorkspaceAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            string role = Path.GetFileName(root), selected = Path.Combine(root, "selected 中文.txt"), selector = Selector(root);
            string message = role + " 完整提交说明\r\n第二行 & <原样保留>";
            Write(root, "selected 中文.txt", role + " changed\n"); Write(root, "excluded.txt", role + " remains pending\n");
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root }), false))
            {
                Set(form, "client", client); Set(form, "workspace", workspace); Set(form, "loaded", true); Set(form, "messageStore", store);
                string error = "";
                Set(form, "reportError", new Action<string>(text => error = text));
                form.Show(); Application.DoEvents(); Await(form, "RefreshAsync", null);
                var rows = Field<ListView>(form, "files");
                foreach (ListViewItem row in rows.Items) row.Checked = String.Equals(((PlasticStatusItem)row.Tag).Path, selected, StringComparison.OrdinalIgnoreCase);
                Check(rows.CheckedItems.Count == 1, role + " selects only the intended changed file");
                Field<TextBox>(form, "comment").Text = message;
                int reviews = 0;
                Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((preview, text, uncertain) => {
                    reviews++; Check(preview.IsPartial == (root == partial) && text == message, role + " native mode and complete message reach review");
                    return DialogResult.Cancel;
                }));
                string head = Head(root), pendingBefore = Snapshot(root);
                Await(form, "CheckinSelectionAsync", new object[] { null });
                Check(reviews == 1 && error == "" && Head(root) == head && Snapshot(root) == pendingBefore, role + " cancelled review never commits");
                Check(store.Load(workspace.Repository).Recent.Count == successful && Field<TextBox>(form, "comment").Text == message,
                    role + " cancelled draft remains only in the editor");
                Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((preview, text, uncertain) => DialogResult.OK));
                Await(form, "CheckinSelectionAsync", new object[] { null });
                successful++;
                var recent = new CommitMessageStore(libraryPath).Load(workspace.Repository).Recent;
                Check(error == "" && recent.Count == successful && recent[0] == message, role + " successful real checkin records exact message on disk");
                Check(Field<TextBox>(form, "comment").Text == "" && !Field<bool>(form, "submissionNeedsRefresh") && !Field<bool>(form, "submissionUncertain"),
                    role + " confirmed success clears editor without uncertainty");
                Check(rows.Items.Count == 1 && String.Equals(((PlasticStatusItem)rows.Items[0].Tag).Path, Path.Combine(root, "excluded.txt"), StringComparison.OrdinalIgnoreCase),
                    role + " unselected file remains pending after refresh");
                Check(Selector(root) == selector && File.ReadAllText(Path.Combine(root, "excluded.txt"), Utf8) == role + " remains pending\n",
                    role + " selector and excluded bytes preserved");
            }
            Native(consumer, "update", consumer, "--dontmerge");
            Check(File.ReadAllText(Path.Combine(consumer, "selected 中文.txt"), Utf8) == role + " changed\n" &&
                File.ReadAllText(Path.Combine(consumer, "excluded.txt"), Utf8) == "base\n", role + " independent consumer confirms selected-only commit");
        }
        Check(store.Load("another@repository").Recent.Count == 0, "History is isolated from another repository");
        Check(Snapshot(reference) == referenceState && Selector(reference) == referenceSelector &&
            Execute(reference, "status", "--short", "--machinereadable") == referenceStatus, "Original TestSCM remains unchanged");
    }
    private static void Await(object target, string method, object[] args)
    {
        var task = (Task)target.GetType().GetMethod(method, Flags).Invoke(target, args);
        var until = DateTime.UtcNow.AddMinutes(2);
        while (!task.IsCompleted && DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(10); }
        if (!task.IsCompleted) throw new TimeoutException(method + ": " + Field<Label>(target, "status").Text + "\n" + Field<TextBox>(target, "output").Text);
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
    { if (run != null) File.WriteAllText(Path.Combine(run, "commit-message-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
}
