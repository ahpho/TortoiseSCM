// GPL-2.0-or-later. Real Gluon directory updates with local text/binary changes.
// Compile with src/TortoiseSCM/Core/*.cs and System.Xml.Linq/System.Web.Extensions.
// Run only with a fresh New-TestWorkspace.ps1 manifest; contributors stay on its autotest branch.
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

internal static class PartialDirtyUpdateIntegrationTests
{
    private static string run, producer, partial, consumer, cm, branch;
    private static int assertions;
    private static PlasticClient client;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly List<object> Evidence = new List<object>();
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " real Gluon dirty-directory assertions"); return 0; }
        catch (Exception error) { Save(false, error.ToString()); Console.Error.WriteLine(error); return 1; }
    }
    private static async Task Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected fresh manifest.json and cm.exe");
        var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = (string)manifest["runDirectory"]; producer = (string)manifest["producer"]; partial = (string)manifest["partial"]; consumer = (string)manifest["consumer"]; branch = (string)manifest["branch"]; cm = args[1];
        Check((bool)manifest["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Completed isolated autotest fixture");
        foreach (string root in new[] { producer, partial, consumer })
            Check(Path.GetFullPath(root).StartsWith(Path.GetFullPath(run) + "\\", StringComparison.OrdinalIgnoreCase) && Selector(root).Contains(branch) && !Directory.GetFileSystemEntries(root).Any(p => Path.GetFileName(p) != ".plastic"), "Fresh isolated workspace: " + Path.GetFileName(root));
        client = new PlasticClient(new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "dirty-update-settings.xml"), Timeout = TimeSpan.FromSeconds(90) });
        const string text = "selected/nested/merge.txt", keep = "selected/nested/keep-local.bin", incoming = "selected/nested/accept-incoming.bin", clean = "selected/nested/clean.txt", outside = "outside/keep.txt", discardText = "discard/nested/discard.txt", discardBinary = "discard/nested/discard.bin";
        byte[] baseline = { 0, 10, 255, 20, 0, 30 }, localBytes = { 0, 11, 254, 21, 0, 31 }, incomingBytes = { 0, 12, 253, 22, 0, 32 };
        const string baseText = "header\r\nbase value\r\nfooter\r\n", localText = "header\r\nlocal value\r\nfooter\r\n", remoteText = "header\r\nserver value\r\nfooter\r\n", mergedText = "header\r\nlocal and server reviewed\r\nfooter\r\n";
        Write(producer, text, Utf8.GetBytes(baseText)); Write(producer, keep, baseline); Write(producer, incoming, baseline); Write(producer, clean, Utf8.GetBytes("clean base")); Write(producer, outside, Utf8.GetBytes("outside base")); Write(producer, discardText, Utf8.GetBytes(baseText)); Write(producer, discardBinary, baseline);
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "--all", "-c=Dirty directory baseline text and binary");
        Native(partial, "partial", "update", partial, "--dontmerge", "--report");
        string selector = Selector(partial), load = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories"));
        Write(partial, text, Utf8.GetBytes(localText)); Write(partial, keep, localBytes); Write(partial, incoming, localBytes); Write(partial, outside, Utf8.GetBytes("outside local pending")); Write(partial, discardText, Utf8.GetBytes(localText)); Write(partial, discardBinary, localBytes);
        Write(producer, text, Utf8.GetBytes(remoteText)); Write(producer, keep, incomingBytes); Write(producer, incoming, incomingBytes); Write(producer, clean, Utf8.GetBytes("clean incoming")); Write(producer, outside, Utf8.GetBytes("outside incoming")); Write(producer, discardText, Utf8.GetBytes(remoteText)); Write(producer, discardBinary, incomingBytes);
        Native(producer, "checkin", producer, "--all", "-c=Dirty directory incoming text and binary");
        long outsideRevision = Revision(partial, outside), incomingRevision = Revision(producer, text);
        string snapshot = Snapshot(partial), outsideStatus = await StatusFor(outside);
        var blocked = await Command(PlasticCommand.Update, "selected");
        Record("directory update with incoming text/binary conflicts", blocked);
        Check(!blocked.Succeeded, "Selected-directory update refuses implicit merge");
        Check(Read(partial, text).SequenceEqual(Utf8.GetBytes(localText)) && Read(partial, keep).SequenceEqual(localBytes) && Read(partial, incoming).SequenceEqual(localBytes), "Blocked update preserves text and both binary local bytes");
        Check(Read(partial, outside).SequenceEqual(Utf8.GetBytes("outside local pending")) && Revision(partial, outside) == outsideRevision && await StatusFor(outside) == outsideStatus, "Blocked directory update preserves outside bytes, loaded revision and pending status");
        Evidence.Add(new { description = "Whole workspace bytes identical after refused update", value = Snapshot(partial) == snapshot, cleanFileAfter = Utf8.GetString(Read(partial, clean)) });
        var conflicts = await client.PreviewPartialConflictsAsync(partial, Token);
        Check(conflicts.Count(c => c.RepositoryPath.StartsWith("/selected/", StringComparison.Ordinal)) == 3 && conflicts.Where(c => c.RepositoryPath.StartsWith("/selected/", StringComparison.Ordinal)).All(c => c.CanResolve), "Preview identifies nested text and both binary incoming conflicts");
        Check(!conflicts.Single(c => c.RepositoryPath == "/" + text).IsBinary && conflicts.Single(c => c.RepositoryPath == "/" + keep).IsBinary && conflicts.Single(c => c.RepositoryPath == "/" + incoming).IsBinary, "Native revision type distinguishes text from both binary conflicts");
        var prepared = await client.PreparePartialConflictAsync(partial, "/" + text, Token);
        Check(File.ReadAllBytes(prepared.BasePath).SequenceEqual(Utf8.GetBytes(baseText)) && File.ReadAllBytes(prepared.LocalPath).SequenceEqual(Utf8.GetBytes(localText)) && File.ReadAllBytes(prepared.RemotePath).SequenceEqual(Utf8.GetBytes(remoteText)), "Text preparation exports exact base/local/incoming contributors");
        string beforeCancel = Snapshot(partial);
        await client.CancelPartialConflictPreparationAsync(partial, Token);
        Check(Snapshot(partial) == beforeCancel && !client.HasSavedPartialConflictSession(partial) && File.Exists(prepared.LocalPath), "Cancelling preparation preserves pending bytes and immutable recovery copy");
        prepared = await client.PreparePartialConflictAsync(partial, "/" + text, Token);
        File.WriteAllText(prepared.ResultPath, mergedText, Utf8);
        Check((await client.ResolvePartialConflictAsync(partial, "/" + text, prepared.ResultPath, Token)).Succeeded, "Apply explicitly reviewed text result");
        Check(Read(partial, text).SequenceEqual(Utf8.GetBytes(mergedText)) && Revision(partial, text) == incomingRevision && (await StatusFor(text)).Contains("CH"), "Merged text overlays incoming revision and remains pending for explicit checkin");
        await ChooseBinary(keep, localBytes, incomingBytes, false);
        await ChooseBinary(incoming, localBytes, incomingBytes, true);
        Check((await StatusFor(keep)).Contains("CH"), "Keep-local binary remains pending over incoming revision");
        Check(Read(partial, incoming).SequenceEqual(incomingBytes) && Revision(partial, incoming) == incomingRevision, "Accept-incoming binary exactly matches incoming bytes and loaded revision");
        Check(String.IsNullOrEmpty(await StatusFor(incoming)), "Accept-incoming binary stays clean without a timestamp-only pending change");
        var remainingSession = await client.GetPartialConflictSessionAsync(partial, Token);
        Check(remainingSession == null || !remainingSession.Conflicts.Any(c => c.RepositoryPath == "/" + incoming), "Accepted incoming decision leaves the active session while other pending decisions remain");
        var updated = await Command(PlasticCommand.Update, "selected"); Record("directory update after explicit resolutions", updated);
        Check(updated.Succeeded, "Selected-directory update succeeds after explicit conflict decisions");
        Check(Read(partial, text).SequenceEqual(Utf8.GetBytes(mergedText)) && Read(partial, keep).SequenceEqual(localBytes) && Read(partial, incoming).SequenceEqual(incomingBytes) && Utf8.GetString(Read(partial, clean)) == "clean incoming", "Final directory update retains reviewed results and downloads clean incoming file");
        Check(Read(partial, outside).SequenceEqual(Utf8.GetBytes("outside local pending")) && Revision(partial, outside) == outsideRevision && await StatusFor(outside) == outsideStatus, "Resolution and directory update leave out-of-scope pending work unchanged");
        Check((await client.GetWorkspaceAsync(partial, Token)).IsPartial && Selector(partial) == selector && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")) == load, "All resolution choices preserve Gluon mode, branch and load configuration");
        var committed = await Command(PlasticCommand.Checkin, "selected"); Record("explicit selected-directory checkin", committed);
        Check(committed.Succeeded, "User explicit directory checkin publishes reviewed text and keep-local binary");
        Native(consumer, "update", consumer, "--dontmerge");
        Check(Read(consumer, text).SequenceEqual(Utf8.GetBytes(mergedText)) && Read(consumer, keep).SequenceEqual(localBytes) && Read(consumer, incoming).SequenceEqual(incomingBytes), "Independent consumer receives exact selected text/binary decisions");
        Check(Read(consumer, outside).SequenceEqual(Utf8.GetBytes("outside incoming")) && await StatusFor(outside) == outsideStatus, "Directory checkin does not publish unrelated local pending work");
        long published = Revision(partial, text);
        string repo = (string)manifest["repository"];
        XElement old = SafeXml.Load(Native(partial, "ls", "/" + text, "--tree=cs:" + incomingRevision + "@" + repo, "--xml")).Descendants("LsItem").Single();
        XElement current = SafeXml.Load(Native(partial, "ls", "/" + text, "--tree=cs:" + published + "@" + repo, "--xml")).Descendants("LsItem").Single();
        Check((string)current.Element("ParentRevId") == (string)old.Element("RevId"), "Published text has the reviewed incoming revision as native parent");
        blocked = await Command(PlasticCommand.Update, "discard"); Record("discard directory blocked before undo", blocked);
        Check(!blocked.Succeeded && Read(partial, discardText).SequenceEqual(Utf8.GetBytes(localText)) && Read(partial, discardBinary).SequenceEqual(localBytes), "Second directory blocks with both dirty text and binary untouched");
        // This models the user's explicit destructive choice, never an automatic retry path.
        var undone = await Command(PlasticCommand.Undo, "discard"); Record("explicit recursive discard", undone);
        Check(undone.Succeeded && Read(partial, discardText).SequenceEqual(Utf8.GetBytes(baseText)) && Read(partial, discardBinary).SequenceEqual(baseline), "Explicit recursive undo discards only selected directory text/binary edits");
        updated = await Command(PlasticCommand.Update, "discard"); Record("discard directory update after undo", updated);
        Check(updated.Succeeded && Read(partial, discardText).SequenceEqual(Utf8.GetBytes(remoteText)) && Read(partial, discardBinary).SequenceEqual(incomingBytes), "Directory update after discard downloads exact incoming text/binary bytes");
        Check(String.IsNullOrEmpty(await StatusFor(discardText)) && String.IsNullOrEmpty(await StatusFor(discardBinary)) && await StatusFor(outside) == outsideStatus && Revision(partial, outside) == outsideRevision, "Discard and update clean selected files only; outside pending/revision still unchanged");
        Check(Selector(partial) == selector && (await client.GetWorkspaceAsync(partial, Token)).IsPartial, "Final workspace remains on original isolated Gluon selector");
    }
    private static async Task ChooseBinary(string path, byte[] local, byte[] remote, bool acceptIncoming)
    {
        var files = await client.PreparePartialConflictAsync(partial, "/" + path, Token);
        var reopened = new PlasticClient(new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "dirty-update-settings.xml") });
        Check((await reopened.GetPartialConflictSessionAsync(partial, Token)).Conflicts.Single(c => c.RepositoryPath == "/" + path).IsBinary, "Reopened durable session retains binary classification");
        Check(File.ReadAllBytes(files.LocalPath).SequenceEqual(local) && File.ReadAllBytes(files.RemotePath).SequenceEqual(remote), "Binary contributors retain exact non-text bytes: " + path);
        File.Copy(acceptIncoming ? files.RemotePath : files.LocalPath, files.ResultPath, true); File.SetAttributes(files.ResultPath, FileAttributes.Normal);
        Check((await client.ResolvePartialConflictAsync(partial, "/" + path, files.ResultPath, Token)).Succeeded && Read(partial, path).SequenceEqual(acceptIncoming ? remote : local), "Explicit binary side choice applies: " + (acceptIncoming ? "incoming" : "local"));
        Check(File.ReadAllBytes(files.LocalPath).SequenceEqual(local) && File.ReadAllBytes(files.RemotePath).SequenceEqual(remote), "Binary immutable backups remain available after apply");
    }
    private static Task<PlasticCommandResult> Command(PlasticCommand command, string relative)
    { return client.RunAsync(new PlasticCommandRequest { Command = command, WorkingDirectory = partial, Paths = new List<string> { Path.Combine(partial, relative.Replace('/', '\\')) }, Recursive = true, Comment = "Explicit reviewed dirty-directory integration checkin" }, Token); }
    private static async Task<string> StatusFor(string relative)
    { string path = Path.Combine(partial, relative.Replace('/', '\\')); return String.Join("|", (await client.GetStatusAsync(partial, Token)).Where(p => String.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)).Select(p => p.StatusCode).OrderBy(s => s)); }
    private static long Revision(string root, string relative)
    { return (long)SafeXml.Load(Native(root, "fileinfo", Path.Combine(root, relative.Replace('/', '\\')), "--fields=RevisionChangeset", "--xml")).Descendants("FileInfo").Single().Element("RevisionChangeset"); }
    private static string Selector(string root) { return File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector")); }
    private static byte[] Read(string root, string path) { return File.ReadAllBytes(Path.Combine(root, path.Replace('/', '\\'))); }
    private static void Write(string root, string path, byte[] bytes)
    {
        string target = Path.Combine(root, path.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(target));
        bool existing = File.Exists(target); DateTime previous = existing ? File.GetLastWriteTimeUtc(target) : DateTime.MinValue;
        File.WriteAllBytes(target, bytes);
        // Native cm status uses size/second-granularity timestamp fast paths. Deterministically
        // emulate a later editor save instead of hiding equal-size binary changes in one second.
        if (existing && Math.Abs((File.GetLastWriteTimeUtc(target) - previous).TotalSeconds) < 2) File.SetLastWriteTimeUtc(target, previous.AddSeconds(3));
    }
    private static string Snapshot(string root) { return String.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(p => !p.StartsWith(Path.Combine(root, ".plastic") + "\\", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p).Select(p => p + "|" + Convert.ToBase64String(File.ReadAllBytes(p)))); }
    private static void Record(string description, PlasticCommandResult result) { Evidence.Add(new { description, result.ExitCode, result.Output, result.Error }); }
    private static void Check(bool success, string description) { assertions++; Evidence.Add(new { description, success }); Console.WriteLine((success ? "PASS: " : "FAIL: ") + description); if (!success) throw new Exception(description); }
    private static void Save(bool success, string error) { if (run != null) File.WriteAllText(Path.Combine(run, "partial-dirty-update-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
    private static string Native(string root, params string[] args)
    {
        if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(run) + "\\", StringComparison.OrdinalIgnoreCase) || !Selector(root).Contains(branch)) throw new InvalidOperationException("Unsafe fixture command");
        using (var process = Process.Start(new ProcessStartInfo(cm, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 }))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("Native fixture timeout"); }
            string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult(); Evidence.Add(new { root, args, exitCode = process.ExitCode, stdout, stderr });
            if (process.ExitCode != 0) throw new Exception(stdout + stderr); return stdout;
        }
    }
}
