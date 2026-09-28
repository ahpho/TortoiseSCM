// GPL-2.0-or-later. Real update dialogs against isolated native Standard/Gluon workspaces.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using TortoiseSCM;

internal static class UpdateWorkflowGuiIntegrationTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly List<object> Evidence = new List<object>();
    private static string run, cm, branch;
    private static int assertions, calls;
    private static PlasticClient client;

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); int result = 1;
        EventHandler start = null;
        start = delegate {
            Application.Idle -= start;
            try { Run(args); Save(true, null); Console.WriteLine("PASS: " + assertions + " live update GUI assertions"); result = 0; }
            catch (Exception ex) { Save(false, ex.ToString()); Console.Error.WriteLine(ex); }
            finally { Application.ExitThread(); }
        };
        Application.Idle += start; Application.Run(); return result;
    }

    private static void Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected a fresh New-TestWorkspace manifest and cm.exe");
        var m = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = Path.GetFullPath((string)m["runDirectory"]); cm = args[1]; branch = (string)m["branch"];
        string producer = (string)m["producer"], consumer = (string)m["consumer"], partial = (string)m["partial"];
        Check((bool)m["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Dedicated completed isolated fixture");
        foreach (string root in new[] { producer, consumer, partial })
            Check(Path.GetFullPath(root).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && Selector(root).Contains(branch) &&
                !Directory.GetFileSystemEntries(root).Any(p => Path.GetFileName(p) != ".plastic"), "Fresh empty fixture " + Path.GetFileName(root));
        client = new PlasticClient(new PlasticClientConfig { CmPath = cm, Timeout = TimeSpan.FromSeconds(90), SettingsPath = Path.Combine(run, "update-settings.xml") });
        const string changed = "selected 中文 & folder/nested/changed.txt", deleted = "selected 中文 & folder/nested/deleted.txt", added = "selected 中文 & folder/nested/added.txt", sibling = "sibling/keep.txt";
        Write(producer, changed, "baseline\r\n"); Write(producer, deleted, "delete baseline\n"); Write(producer, sibling, "sibling baseline\n");
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "--all", "-c=Update GUI baseline");
        Native(consumer, "update", consumer, "--dontmerge"); Native(partial, "partial", "update", partial, "--dontmerge", "--report");
        Write(producer, changed, "incoming second\r\n"); Write(producer, added, "incoming addition\n"); Write(producer, sibling, "sibling incoming\n");
        Native(producer, "add", Path.Combine(producer, added)); Native(producer, "remove", Path.Combine(producer, deleted));
        Native(producer, "checkin", producer, "--all", "-c=Update GUI incoming change add delete");

        string consumerBefore = Snapshot(consumer);
        using (var form = Open(Path.Combine(consumer, "selected 中文 & folder", "nested")))
        {
            WaitReady(form); Check(Snapshot(consumer) == consumerBefore && calls == 0, "Opening update performs no mutation");
            Field<Button>(form, "close").PerformClick(); Check(!form.Visible && Snapshot(consumer) == consumerBefore && calls == 0, "Cancel before update preserves workspace");
        }
        using (var form = Open(Path.Combine(partial, "selected 中文 & folder", "nested")))
        {
            WaitReady(form);
            Check(Field<Label>(form, "scope").Text.Contains("仅更新") && Field<TextBox>(form, "paths").Text == Path.Combine(partial, "selected 中文 & folder", "nested"), "Gluon preview preserves exact selected nested directory");
            Capture(form, "partial-update-normal.png"); form.Size = form.MinimumSize; Application.DoEvents(); Capture(form, "partial-update-minimum.png");
            ClickUpdate(form);
            CheckSuccess(form);
            Check(Read(partial, changed) == "incoming second\r\n" && Read(partial, added) == "incoming addition\n" && !File.Exists(Path.Combine(partial, deleted)), "Gluon GUI downloads changed and added files and applies deletion recursively");
            Check(Read(partial, sibling) == "sibling baseline\n", "Gluon selected-directory update preserves sibling revision");
            Check(client.GetStatusAsync(partial, CancellationToken.None).GetAwaiter().GetResult().Count == 0, "Gluon selected update leaves no pending changes");
            int completedCalls = calls; Field<Button>(form, "update").PerformClick(); Application.DoEvents();
            Check(calls == completedCalls && !Field<Button>(form, "update").Enabled, "Completed update cannot accidentally repeat");
            Field<Button>(form, "refresh").PerformClick(); WaitReady(form); string before = Snapshot(partial); ClickUpdate(form); CheckSuccess(form);
            Check(Snapshot(partial) == before, "Refreshing scope and updating an already-current directory is a no-op");
            form.Close();
        }
        using (var form = Open(Path.Combine(consumer, "selected 中文 & folder", "nested")))
        {
            WaitReady(form);
            Check(Field<Label>(form, "scope").Text.Contains("整体更新") && Field<TextBox>(form, "paths").Text == consumer, "Standard GUI explicitly previews whole-workspace update");
            Capture(form, "standard-update-normal.png"); ClickUpdate(form); CheckSuccess(form);
            Check(Read(consumer, changed) == "incoming second\r\n" && Read(consumer, added) == "incoming addition\n" && !File.Exists(Path.Combine(consumer, deleted)) && Read(consumer, sibling) == "sibling incoming\n", "Standard GUI updates full workspace including sibling");
            form.Close();
        }

        // A real network-backed write is held at its boundary so close/repeat behavior is deterministic.
        using (var form = Open(partial))
        {
            WaitReady(form); var release = new TaskCompletionSource<bool>(); int heldCalls = 0;
            Set(form, "run", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>(async (request, token) => {
                heldCalls++; await release.Task; return await client.RunAsync(request, token); }));
            Field<Button>(form, "update").PerformClick(); Pump(() => heldCalls == 1, "Update reaches real-operation boundary");
            form.Close(); Application.DoEvents(); Field<Button>(form, "update").PerformClick();
            Check(form.Visible && Field<bool>(form, "busy") && !Field<Button>(form, "close").Enabled && heldCalls == 1, "Busy dialog blocks close and duplicate update");
            release.SetResult(true); Pump(() => !Field<bool>(form, "busy"), "Held update completes"); CheckSuccess(form);
            Check(Read(partial, sibling) == "sibling incoming\n", "Workspace-root Gluon GUI updates remaining sibling"); form.Close();
        }

        // Both workspace modes must preserve conflicting local edits rather than merge implicitly.
        foreach (string root in new[] { consumer, partial }) Write(root, changed, "local uncommitted bytes\r\n");
        Write(producer, changed, "server conflicting third\r\n"); Native(producer, "checkin", producer, "--all", "-c=Update GUI conflict preservation");
        foreach (string root in new[] { consumer, partial })
        {
            string before = Snapshot(root), selector = Selector(root);
            using (var form = Open(Path.Combine(root, "selected 中文 & folder")))
            {
                WaitReady(form); ClickUpdate(form);
                Check(Field<Label>(form, "status").Text != "更新完成。" && Field<TextBox>(form, "output").Text.Length > 0, "Conflicting update reports failure for " + Path.GetFileName(root));
                Check(Snapshot(root) == before && Selector(root) == selector && Read(root, changed) == "local uncommitted bytes\r\n", "Conflicting GUI update preserves all local bytes and selector for " + Path.GetFileName(root));
                Check(client.GetStatusAsync(root, CancellationToken.None).GetAwaiter().GetResult().Any(i => String.Equals(i.Path, Path.Combine(root, changed.Replace('/', '\\')), StringComparison.OrdinalIgnoreCase) && i.StatusCode == "CH"), "Conflicting local file remains pending");
                Capture(form, Path.GetFileName(root) + "-update-conflict.png"); form.Close();
            }
        }
        int beforeRefusal = calls;
        using (var form = Open(consumer, partial)) { Pump(() => !Field<bool>(form, "busy"), "Mixed selection validation finishes"); Check(!Field<Button>(form, "update").Enabled && Field<TextBox>(form, "output").Text.Contains("同一"), "Mixed workspace selection refused"); form.Close(); }
        using (var form = Open(Path.Combine(run, "not-a-workspace"))) { Pump(() => !Field<bool>(form, "busy"), "Invalid selection validation finishes"); Check(!Field<Button>(form, "update").Enabled && Field<TextBox>(form, "output").Text.Contains("不属于"), "Missing non-workspace path refused"); form.Close(); }
        Check(calls == beforeRefusal, "Rejected paths invoke no update command");
    }

    private static UpdateForm Open(params string[] paths)
    {
        var request = new LaunchRequest { Command = "update" }; request.Paths.AddRange(paths);
        var form = Program.CreateLaunchForm(request) as UpdateForm; Check(form != null, "Production shell routing opens UpdateForm");
        Set(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>(client.GetWorkspaceAsync));
        Set(form, "run", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>((command, token) => { calls++; return client.RunAsync(command, token); }));
        form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
        form.Show(); Application.DoEvents(); return form;
    }
    private static void WaitReady(UpdateForm form) { Pump(() => !Field<bool>(form, "busy"), "Scope read completes"); Check(Field<Button>(form, "update").Enabled, "Update enabled after real scope validation: " + Field<TextBox>(form, "output").Text); }
    private static void ClickUpdate(UpdateForm form) { Field<Button>(form, "update").PerformClick(); Pump(() => !Field<bool>(form, "busy"), "Native GUI update finishes"); }
    private static void CheckSuccess(UpdateForm form) { Check(Field<Label>(form, "status").Text == "更新完成。", "GUI confirms native success: " + Field<TextBox>(form, "output").Text); }
    private static void Pump(Func<bool> predicate, string description) { var until = DateTime.UtcNow.AddSeconds(120); do { Application.DoEvents(); Thread.Sleep(10); } while (!predicate() && DateTime.UtcNow < until); Check(predicate(), description); }
    private static void Capture(Form form, string name) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(run, name)); } }
    private static T Field<T>(object target, string name) { return (T)target.GetType().GetField(name, Flags).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
    private static string Selector(string root) { return File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector"), Utf8); }
    private static void Write(string root, string name, string text) { string path = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, Utf8); }
    private static string Read(string root, string name) { return File.ReadAllText(Path.Combine(root, name), Utf8); }
    private static string Snapshot(string root) { return String.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(Path.Combine(root, ".plastic") + "\\", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).Select(p => p + "|" + Convert.ToBase64String(File.ReadAllBytes(p)))); }
    private static string Native(string root, params string[] args)
    {
        if (!Path.GetFullPath(root).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) || !Selector(root).Contains(branch)) throw new InvalidOperationException("Unsafe native fixture command");
        using (var process = Process.Start(new ProcessStartInfo(cm, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 }))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("Native fixture timeout"); }
            string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult(); Evidence.Add(new { root, args, exitCode = process.ExitCode, stdout, stderr });
            if (process.ExitCode != 0) throw new Exception(stdout + stderr); return stdout;
        }
    }
    private static void Check(bool value, string description) { assertions++; Evidence.Add(new { description, success = value }); Console.WriteLine((value ? "PASS: " : "FAIL: ") + description); if (!value) throw new Exception(description); }
    private static void Save(bool success, string error) { if (run != null) File.WriteAllText(Path.Combine(run, "update-gui-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
}
