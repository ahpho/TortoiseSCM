// GPL-2.0-or-later. Real server tests; only fresh New-TestWorkspace.ps1 fixtures.
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
using TortoiseSCM.Core;

internal static class BuiltInToolsIntegrationTests
{
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly List<object> Evidence = new List<object>();
    private static string run, cm, branch, repository;
    private static int assertions;

    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " built-in tool live assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }

    private static async Task Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: BuiltInToolsIntegrationTests.exe <manifest.json> <cm.exe>");
        var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = Path.GetFullPath((string)manifest["runDirectory"]); cm = args[1];
        branch = (string)manifest["branch"]; repository = (string)manifest["repository"];
        string producer = (string)manifest["producer"], consumer = (string)manifest["consumer"], partial = (string)manifest["partial"];
        Check(branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Dedicated test branch");
        foreach (string workspace in new[] { producer, consumer, partial })
        {
            Check(Path.GetFullPath(workspace).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")).Contains(branch), "Isolated workspace and selector");
            Check(String.IsNullOrWhiteSpace(Native(workspace, "status", "--short", "--machinereadable")), "Fresh fixture has no pending changes");
        }
        var config = new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "builtin-settings.xml"), UseBuiltInDiff = true, UseBuiltInMerge = true };
        var host = new RecordingHost(); var client = new PlasticClient(config) { ToolHost = host };
        const string name = "内置 compare.txt", baseText = "header\r\nbase 中文\r\nfooter\r\n", localText = "header\r\nlocal 中文\r\nfooter\r\n",
            remoteText = "header\r\nremote 中文\r\nfooter\r\n", mergedText = "header\r\nreviewed 双方\r\nfooter\r\n";
        string file = Path.Combine(producer, name), renamed = Path.Combine(producer, "重命名 compare.txt"), other = Path.Combine(consumer, name);
        File.WriteAllText(file, baseText, Utf8); Native(producer, "add", file); Native(producer, "checkin", producer, "-c=Built-in tools common base");
        long baseCs = Revision(file);
        File.WriteAllText(file, localText, Utf8); host.Before = baseText; host.After = localText;
        Check((await client.OpenDiffToolAsync(file, Token)).Succeeded, "Real working diff routes into built-in host");
        Check(!File.Exists(host.LastBase), "Working baseline cleaned after host returns");
        Native(producer, "move", file, renamed);
        Check((await client.OpenDiffToolAsync(renamed, Token)).Succeeded, "Renamed file compares its actual checked-in identity");
        Native(producer, "undo", producer, "--recursive");
        Check(File.ReadAllText(file, Utf8) == baseText && !File.Exists(renamed), "Isolated rename fixture restored");
        string sourceBranch = branch + "/builtin-source";
        Native(consumer, "branch", "create", "br:" + sourceBranch + "@" + repository, "--changeset=cs:" + baseCs + "@" + repository);
        Native(consumer, "switch", "br:" + sourceBranch + "@" + repository, "--workspace=" + consumer);
        File.WriteAllText(other, remoteText, Utf8); Native(consumer, "checkin", other, "-c=Built-in remote version"); long remoteCs = Revision(other);
        File.WriteAllText(file, localText, Utf8); Native(producer, "checkin", file, "-c=Built-in local version"); long localCs = Revision(file);
        host.Before = baseText; host.After = localText;
        Check((await client.OpenRevisionDiffToolAsync(producer, "/" + name, baseCs, localCs, Token)).Succeeded, "Fixed historical endpoints use built-in diff");
        Check(!File.Exists(host.LastBase) && !File.Exists(host.LastLocal), "Historical inputs live until host returns, then cleaned");
        string selector = File.ReadAllText(Path.Combine(producer, ".plastic", "plastic.selector"));
        var session = await client.BeginMergeAsync(producer, remoteCs, Token);
        Check(session.Plan.FileConflicts.Count == 1, "Native Standard content conflict prepared");
        var files = await client.PrepareMergeConflictAsync(producer, remoteCs, "/" + name, Token);
        host.Before = baseText; host.After = localText; host.Remote = remoteText; host.Merged = mergedText;
        await client.RunMergeToolAsync(files.BasePath, files.LocalPath, files.RemotePath, files.ResultPath, Token);
        Check(File.ReadAllText(files.ResultPath, Utf8) == mergedText && File.ReadAllText(file, Utf8) == localText, "Editor saves only independent result with CRLF preserved");
        Check(!(await client.GetMergeSessionAsync(producer, Token)).Plan.FileConflicts[0].Resolved, "Editor save never marks Standard conflict resolved");
        var resumed = new PlasticClient(config) { ToolHost = host };
        var reopened = await resumed.PrepareMergeConflictAsync(producer, remoteCs, "/" + name, Token);
        Check(reopened.ResultPath == files.ResultPath && File.ReadAllText(reopened.ResultPath, Utf8) == mergedText, "New client resumes saved result without overwriting it");
        Check((await resumed.ApplyMergeFileResolutionAsync(producer, remoteCs, "/" + name, files.ResultPath, Token)).Succeeded, "Explicit apply accepts editor result");
        Check(File.ReadAllText(file, Utf8) == mergedText && File.ReadAllText(Path.Combine(producer, ".plastic", "plastic.selector")) == selector, "Apply writes reviewed bytes and retains selector");
        Check((await resumed.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = producer,
            Paths = new List<string> { producer }, Comment = "Publish built-in reviewed merge", Recursive = true }, Token)).Succeeded, "Explicit Standard checkin succeeds");
        Native(consumer, "switch", "br:" + branch + "@" + repository, "--workspace=" + consumer);
        Check(File.ReadAllText(other, Utf8) == mergedText, "Independent Standard consumer receives exact bytes");

        Native(partial, "partial", "update", partial, "--dontmerge", "--report");
        string partialFile = Path.Combine(partial, name), partialLocal = "header\r\npartial local\r\nfooter\r\n", incoming = "header\r\npartial incoming\r\nfooter\r\n";
        File.WriteAllText(partialFile, partialLocal, Utf8); File.WriteAllText(file, incoming, Utf8);
        Native(producer, "checkin", file, "-c=Built-in Partial incoming");
        selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector"));
        string load = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories"));
        host.Before = mergedText; host.After = partialLocal;
        Check((await client.OpenDiffToolAsync(partialFile, Token)).Succeeded, "Partial diff uses loaded base rather than incoming HEAD");
        files = await client.PreparePartialConflictAsync(partial, "/" + name, Token);
        host.Remote = incoming; host.Merged = "header\r\npartial reviewed 中文\r\nfooter\r\n";
        await client.RunMergeToolAsync(files.BasePath, files.LocalPath, files.RemotePath, files.ResultPath, Token);
        Check(!(await client.GetPartialConflictSessionAsync(partial, Token)).Conflicts[0].Resolved && File.ReadAllText(partialFile, Utf8) == partialLocal, "Saving Partial result preserves unresolved state and local bytes");
        Check((await client.ResolvePartialConflictAsync(partial, "/" + name, files.ResultPath, Token)).Succeeded, "Partial explicit apply accepts editor output");
        Check(File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")) == load, "Partial selector and loading rules preserved");
        Check((await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial,
            Paths = new List<string> { partialFile }, Comment = "Publish built-in Partial reviewed merge" }, Token)).Succeeded, "Explicit Partial checkin succeeds");
        Native(consumer, "update", consumer, "--dontmerge");
        Check(File.ReadAllText(other, Utf8) == host.Merged, "Independent consumer receives exact Partial reviewed bytes");
    }

    private sealed class RecordingHost : IPlasticToolHost
    {
        internal string Before, After, Remote, Merged, LastBase, LastLocal;
        public Task<PlasticCommandResult> ShowDiffAsync(string before, string after, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); LastBase = before; LastLocal = after;
            Check(File.ReadAllText(before, Utf8) == Before && File.ReadAllText(after, Utf8) == After, "Built-in host receives exact diff contributors");
            Check(TextComparison.Compare(TextDocument.Load(before), TextDocument.Load(after)).Hunks.Count > 0, "Text engine identifies real version differences");
            return Task.FromResult(new PlasticCommandResult());
        }
        public Task<PlasticCommandResult> ShowMergeAsync(string before, string local, string remote, string result, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Check(File.ReadAllText(before, Utf8) == Before && File.ReadAllText(local, Utf8) == After && File.ReadAllText(remote, Utf8) == Remote, "Built-in host receives exact three-way contributors");
            var document = File.Exists(result) ? TextDocument.Load(result) : TextDocument.CreateResult(result, TextDocument.Load(local));
            document.Save(Merged, TextLineEnding.Preserve, new[] { before, local, remote });
            return Task.FromResult(new PlasticCommandResult());
        }
    }
    private static long Revision(string path)
    { return (long)XDocument.Parse(Native(Path.GetDirectoryName(path), "fileinfo", path, "--xml")).Descendants("FileInfo").Single().Element("RevisionChangeset"); }
    private static string Native(string cwd, params string[] arguments)
    {
        if (!Path.GetFullPath(cwd).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture directory");
        var start = new ProcessStartInfo(cm, String.Join(" ", arguments.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = cwd,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("Native fixture timeout"); }
            string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult();
            Evidence.Add(new { cwd, arguments, exitCode = process.ExitCode, output = stdout, error = stderr });
            if (process.ExitCode != 0) throw new Exception("Native fixture failed: " + stdout + stderr); return stdout;
        }
    }
    private static void Check(bool value, string description)
    { assertions++; Evidence.Add(new { description, success = value }); if (!value) throw new Exception(description); }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, "builtin-tools-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
}
