// GPL-2.0-or-later. Real Partial conflict tests in an isolated autotest fixture only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using TortoiseSCM;

internal static class PartialConflictIntegrationTests
{
    private static int assertions;
    private static string cm, run, producer, partial, consumer, repository;
    private static readonly List<object> Evidence = new List<object>();
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static int Main(string[] args)
    {
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSCM_PARTIAL_PROXY_CM")) && args.Length > 0 && !args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Proxy(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " real Partial conflict assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, "partial-conflict-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
    private static void Assert(bool condition, string description)
    { assertions++; Evidence.Add(new { description, success = condition }); if (!condition) throw new Exception(description); Console.WriteLine("PASS: " + description); }
    private static async Task Reject(Func<Task> action, string description)
    { bool rejected = false; try { await action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Assert(rejected, description); }
    private static async Task Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: PartialConflictIntegrationTests.exe <manifest.json> <cm.exe>");
        var manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = (string)manifest["runDirectory"]; producer = (string)manifest["producer"]; partial = (string)manifest["partial"]; consumer = (string)manifest["consumer"]; repository = (string)manifest["repository"]; cm = args[1];
        Assert(((string)manifest["branch"]).StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Fixture branch is isolated");
        foreach (string workspace in new[] { producer, partial, consumer })
            Assert(Path.GetFullPath(workspace).StartsWith(Path.GetFullPath(run) + "\\", StringComparison.OrdinalIgnoreCase) && File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")).Contains((string)manifest["branch"]), "Fixture workspace and selector remain isolated");
        Native(producer, "update", producer, "--dontmerge");
        string relative = "/partial reviewed 中文.txt", filename = relative.Substring(1), local = Path.Combine(partial, filename);
        const string baseText = "header\nbase value\nfooter\n", localText = "header\nlocal 中文 value\nfooter\n", remoteText = "header\nincoming value\nfooter\n", latestText = "header\nnewer incoming value\nfooter\n", mergedText = "header\nlocal + incoming reviewed\nfooter\n";
        WriteNew(Path.Combine(producer, filename), baseText); WriteNew(Path.Combine(producer, "unrelated.txt"), "unrelated base"); WriteNew(Path.Combine(producer, "unloaded.txt"), "not loaded");
        Native(producer, "add", Path.Combine(producer, filename), Path.Combine(producer, "unrelated.txt"), Path.Combine(producer, "unloaded.txt"));
        Native(producer, "checkin", producer, "-c=Partial integration base");
        Native(partial, "partial", "update", partial, "--dontmerge", "--report"); Native(partial, "partial", "configure", "-/unloaded.txt");
        File.WriteAllText(local, localText, Utf8); File.WriteAllText(Path.Combine(partial, "unrelated.txt"), "keep unrelated local bytes", Utf8);
        File.WriteAllText(Path.Combine(producer, filename), remoteText, Utf8); Native(producer, "checkin", Path.Combine(producer, filename), "-c=Partial incoming revision");
        var config = new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "partial-test-settings.xml") }; var client = new PlasticClient(config);
        string selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")), load = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories"));
        var preview = await client.PreviewPartialConflictsAsync(partial, Token);
        await Reject(async () => { await client.PreviewPartialConflictsAsync(local, Token); }, "Partial APIs require the exact workspace root");
        Assert(preview.Count == 1 && preview[0].RepositoryPath == relative && preview[0].CanResolve && preview[0].BaseChangeset < preview[0].IncomingChangeset, "Preview pins the incoming conflict using per-file changesets");
        Assert(File.ReadAllText(local, Utf8) == localText && !client.HasSavedPartialConflictSession(partial), "Preview does not modify local bytes or create a session");
        var files = await client.PreparePartialConflictAsync(partial, relative, Token);
        Assert(File.ReadAllText(files.BasePath, Utf8) == baseText && File.ReadAllText(files.LocalPath, Utf8) == localText && File.ReadAllText(files.RemotePath, Utf8) == remoteText, "Prepared base/local/incoming bytes are exact");
        var session = await new PlasticClient(config).GetPartialConflictSessionAsync(partial, Token);
        Assert(session.Ready && !session.Applying && session.Conflicts.Count == 1 && session.SessionId == files.SessionId && Directory.Exists(session.RecoveryDirectory), "A separate client resumes the durable preparation and exposes backups");
        await Reject(async () => { await client.RunAsync(Request(PlasticCommand.Checkin, local), Token); }, "Unresolved Partial checkin is refused before native execution");
        File.WriteAllText(files.ResultPath, mergedText, Utf8); File.WriteAllText(local, "concurrent editor", Utf8);
        await Reject(async () => { await client.ResolvePartialConflictAsync(partial, relative, files.ResultPath, Token); }, "Local edits after preparation reject apply");
        Assert(File.ReadAllText(local, Utf8) == "concurrent editor", "Rejected apply preserves concurrent local edits");
        File.WriteAllText(local, localText, Utf8); File.WriteAllText(Path.Combine(producer, filename), latestText, Utf8); Native(producer, "checkin", Path.Combine(producer, filename), "-c=Newer incoming after preparation");
        await Reject(async () => { await client.ResolvePartialConflictAsync(partial, relative, files.ResultPath, Token); }, "A newer incoming revision invalidates prepared resolution");
        await client.CancelPartialConflictPreparationAsync(partial, Token);
        Assert(!client.HasSavedPartialConflictSession(partial) && File.ReadAllText(local, Utf8) == localText && File.Exists(files.LocalPath), "Cancel detaches unapplied preparation and retains original backups");
        files = await client.PreparePartialConflictAsync(partial, relative, Token); File.WriteAllText(files.ResultPath, mergedText, Utf8);
        long incoming = (await client.GetPartialConflictSessionAsync(partial, Token)).Conflicts[0].IncomingChangeset;
        var applied = await client.ResolvePartialConflictAsync(partial, relative, files.ResultPath, Token);
        Assert(applied.Succeeded && File.ReadAllText(local, Utf8) == mergedText, "Reviewed result applies without official GUI");
        Assert((await client.GetPartialConflictSessionAsync(partial, Token)).Conflicts[0].Resolved, "Completed resolution is persisted");
        Assert((await client.GetWorkspaceAsync(partial, Token)).IsPartial && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")) == load, "Resolution preserves Partial mode, selector and load configuration");
        Assert(File.ReadAllText(Path.Combine(partial, "unrelated.txt"), Utf8) == "keep unrelated local bytes" && !File.Exists(Path.Combine(partial, "unloaded.txt")), "Unrelated local edits and unloaded files remain untouched");
        var info = Info(local);
        Assert((long)info.Element("RevisionChangeset") == incoming && (long)info.Element("RevisionHeadChangeset") == incoming, "Apply updates native loaded revision but creates no server changeset");
        string previousBackup = files.LocalPath;
        File.WriteAllText(Path.Combine(producer, filename), "incoming after completed resolution", Utf8); Native(producer, "checkin", Path.Combine(producer, filename), "-c=Partial incoming after completed resolution");
        await Reject(async () => { await client.RunAsync(Request(PlasticCommand.Checkin, local), Token); }, "New incoming changes after resolution block checkin");
        files = await client.PreparePartialConflictAsync(partial, relative, Token);
        Assert(files.LocalPath != previousBackup && File.Exists(previousBackup) && File.ReadAllText(files.LocalPath, Utf8) == mergedText, "Explicit re-preparation preserves reviewed work and older immutable backups");
        File.WriteAllText(files.ResultPath, mergedText, Utf8);
        Assert((await client.ResolvePartialConflictAsync(partial, relative, files.ResultPath, Token)).Succeeded, "Re-prepared conflict applies against its new incoming revision");
        incoming = (await client.GetPartialConflictSessionAsync(partial, Token)).Conflicts[0].IncomingChangeset;
        string continuedText = mergedText + "continued local edit\n"; File.WriteAllText(local, continuedText, Utf8);
        var checkedIn = await client.RunAsync(Request(PlasticCommand.Checkin, local), Token);
        Assert(checkedIn.Succeeded && !client.HasSavedPartialConflictSession(partial), "Explicit Partial checkin accepts continued edits and detaches completed session");
        Native(consumer, "update", consumer, "--dontmerge");
        Assert(File.ReadAllText(Path.Combine(consumer, filename), Utf8) == continuedText, "Independent consumer receives exact reviewed and continued-edit bytes");
        long published = (long)Info(local).Element("RevisionChangeset");
        var oldRevision = SafeXml.Load(Native(partial, "ls", relative, "--tree=cs:" + incoming + "@" + repository, "--xml")).Descendants("LsItem").Single();
        var newRevision = SafeXml.Load(Native(partial, "ls", relative, "--tree=cs:" + published + "@" + repository, "--xml")).Descendants("LsItem").Single();
        Assert((string)newRevision.Element("ParentRevId") == (string)oldRevision.Element("RevId"), "Server native parent revision points to the reviewed incoming revision");
        Native(producer, "update", producer, "--dontmerge"); File.WriteAllText(local, "local second conflict", Utf8); File.WriteAllText(Path.Combine(producer, filename), "incoming second conflict", Utf8); Native(producer, "checkin", Path.Combine(producer, filename), "-c=Partial interrupted resolution fixture");
        files = await client.PreparePartialConflictAsync(partial, relative, Token); File.WriteAllText(files.ResultPath, "reviewed second result", Utf8);
        Environment.SetEnvironmentVariable("TSCM_PARTIAL_PROXY_CM", cm);
        config.CmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        await Reject(async () => { await client.ResolvePartialConflictAsync(partial, relative, files.ResultPath, Token); }, "Native update failure after real undo leaves a durable interrupted session");
        config.CmPath = cm; Environment.SetEnvironmentVariable("TSCM_PARTIAL_PROXY_CM", null);
        Assert(File.ReadAllText(files.LocalPath, Utf8) == "local second conflict" && File.ReadAllText(local, Utf8) == continuedText, "Partial native failure preserves original local backup and verified base on disk");
        session = await client.GetPartialConflictSessionAsync(partial, Token);
        Assert(session.Applying && !session.Ready, "Restart exposes interrupted Partial resolution state");
        await Reject(async () => { await client.RunAsync(Request(PlasticCommand.Checkin, local), Token); }, "Interrupted session blocks checkin");
        await Reject(async () => { await client.CancelPartialConflictPreparationAsync(partial, Token); }, "Interrupted session cannot be silently cancelled");
        var undone = await client.RunAsync(Request(PlasticCommand.Undo, local), Token);
        Assert(undone.Succeeded && !client.HasSavedPartialConflictSession(partial) && File.Exists(files.LocalPath) && File.Exists(files.ResultPath), "Explicit selected-file undo detaches interrupted session and keeps recovery artifacts");
        Assert(File.ReadAllText(Path.Combine(partial, "unrelated.txt"), Utf8) == "keep unrelated local bytes", "Recovery undo preserves unrelated pending work");
        string collision = "partial-add-collision.txt", ordinary = "partial-ordinary-add.txt";
        WriteNew(Path.Combine(partial, collision), "local addition"); WriteNew(Path.Combine(partial, ordinary), "ordinary addition");
        Native(partial, "partial", "add", Path.Combine(partial, collision), Path.Combine(partial, ordinary));
        Native(producer, "update", producer, "--dontmerge"); WriteNew(Path.Combine(producer, collision), "incoming addition");
        Native(producer, "add", Path.Combine(producer, collision)); Native(producer, "checkin", Path.Combine(producer, collision), "-c=Partial add collision incoming");
        var additions = await client.PreviewPartialConflictsAsync(partial, Token);
        Assert(additions.Any(item => item.RepositoryPath == "/" + collision && !item.CanResolve) && !additions.Any(item => item.RepositoryPath == "/" + ordinary), "Incoming same-path addition is reported without blocking ordinary additions");
        await Reject(async () => { await client.RunAsync(Request(PlasticCommand.Checkin, Path.Combine(partial, collision)), Token); }, "Incoming added-path collision is refused before native checkin");
        Assert((await client.RunAsync(Request(PlasticCommand.Checkin, Path.Combine(partial, ordinary)), Token)).Succeeded, "Unrelated ordinary Partial addition still checks in successfully");
    }
    private static PlasticCommandRequest Request(PlasticCommand command, string path)
    { return new PlasticCommandRequest { Command = command, WorkingDirectory = partial, Paths = new List<string> { path }, Comment = "Explicit Partial integration reviewed checkin" }; }
    private static XElement Info(string path)
    { return SafeXml.Load(Native(partial, "fileinfo", path, "--fields=RevisionChangeset,RevisionHeadChangeset", "--xml")).Descendants("FileInfo").Single(); }
    private static void WriteNew(string path, string content)
    { using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(stream, Utf8)) writer.Write(content); }
    private static int Proxy(string[] arguments)
    {
        if (arguments.Length > 1 && arguments[0] == "partial" && arguments[1] == "update") { Console.Error.WriteLine("Injected native update failure after real undo"); return 41; }
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("TSCM_PARTIAL_PROXY_CM"), String.Join(" ", arguments.Select(PlasticClient.QuoteArgument)))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(45000)) { process.Kill(); return 42; }
            Console.OutputEncoding = Utf8; Console.Write(output.GetAwaiter().GetResult()); Console.Error.Write(error.GetAwaiter().GetResult()); return process.ExitCode;
        }
    }
    private static string Native(string cwd, params string[] arguments)
    {
        if (!Path.GetFullPath(cwd).StartsWith(Path.GetFullPath(run) + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe native fixture cwd");
        var start = new ProcessStartInfo(cm, String.Join(" ", arguments.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Native fixture timeout"); }
            string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult(); Evidence.Add(new { cwd, arguments, exitCode = process.ExitCode, output = stdout, error = stderr });
            if (process.ExitCode != 0) throw new Exception("Native fixture failed: " + stdout + stderr); return stdout;
        }
    }
}
