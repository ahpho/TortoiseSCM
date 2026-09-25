// GPL-2.0-or-later. Real Partial cross-directory moves on isolated branches.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using TortoiseSCM;

internal static class PartialCrossDirectoryTests
{
    private static string run, producer, partial, consumer, cm;
    private static int assertions;
    private static readonly List<object> Evidence = new List<object>();
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSCM_CROSS_PROXY_CM"))) return Proxy(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " real Partial cross-directory assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, "partial-cross-directory-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
    private static void Assert(bool ok, string description)
    { assertions++; Evidence.Add(new { success = ok, description }); if (!ok) throw new Exception(description); Console.WriteLine("PASS: " + description); }
    private static async Task Reject(Func<Task> action, string description)
    { bool rejected = false; try { await action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Assert(rejected, description); }
    private static async Task Run(string[] args)
    {
        var manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = (string)manifest["runDirectory"]; producer = (string)manifest["producer"]; partial = (string)manifest["partial"]; consumer = (string)manifest["consumer"]; cm = args[1];
        string qa = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "qa"));
        Assert(((string)manifest["repository"]).StartsWith("TestSCM@") && ((string)manifest["branch"]).StartsWith("/main/tortoisescm-autotest-") && Path.GetFullPath(run).StartsWith(qa + "\\", StringComparison.OrdinalIgnoreCase), "Cross-directory fixture uses the permitted isolated repository, branch and QA root");
        foreach (string workspace in new[] { producer, partial, consumer }) Assert(Path.GetFullPath(workspace).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")).Contains((string)manifest["branch"]), "Cross-directory workspace remains inside fixture");
        var names = new[] { "take", "keep", "edited", "rename", "short", "failure", "added-parent", "removed-parent", "global-delete" };
        foreach (string directory in new[] { "source", "target", "alternate", "unloaded", "removed" }) Directory.CreateDirectory(Path.Combine(producer, directory));
        foreach (string name in names) WriteNew(Path.Combine(producer, "source", name + ".txt"), "base " + name);
        WriteNew(Path.Combine(producer, "source", "contentguard.txt"), "base content guard");
        foreach (string directory in new[] { "target", "alternate", "unloaded", "removed" }) WriteNew(Path.Combine(producer, directory, "sentinel.txt"), "base sentinel");
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "-c=Cross-directory base");
        Native(partial, "partial", "update", partial, "--dontmerge"); Native(partial, "partial", "configure", "-/unloaded");
        File.WriteAllText(Path.Combine(partial, "source", "contentguard.txt"), "local content guard", Utf8);
        Directory.CreateDirectory(Path.Combine(partial, "pending")); Native(partial, "partial", "add", Path.Combine(partial, "pending"));
        foreach (string name in names)
        {
            string directory = name == "added-parent" ? "pending" : name == "removed-parent" ? "removed" : "target";
            Native(partial, "partial", "move", Path.Combine(partial, "source", name + ".txt"), Path.Combine(partial, directory, name + ".txt"));
            if (name == "edited") File.WriteAllText(Path.Combine(partial, directory, name + ".txt"), "local edited", Utf8);
            File.WriteAllText(Path.Combine(producer, "source", name + ".txt"), "incoming " + name, Utf8);
        }
        Native(producer, "checkin", producer, "--all", "-c=Cross-directory incoming content");
        File.WriteAllText(Path.Combine(partial, "target", "sentinel.txt"), "unrelated pending", Utf8);
        string selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")), load = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories"));
        var config = new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "cross-settings.xml") }; var client = new PlasticClient(config);
        var preview = await client.PreviewPartialStructureAsync(partial, Token);
        Assert(preview.Count == 9 && preview.Count(item => item.ResolutionOptions.Count > 0) == 8, "Preview offers safe cross-directory moves and refuses the pending destination parent");
        await Reject(async () => { await client.PreparePartialStructureAsync(partial, "/pending/added-parent.txt", Token); }, "Uncommitted destination parent is rejected before preparation");
        foreach (string name in new[] { "take", "keep", "edited", "rename", "short" })
        {
            string choice = name == "take" ? "take-incoming" : name == "rename" || name == "short" ? "rename" : "keep-local";
            var session = await client.PreparePartialStructureAsync(partial, "/target/" + name + ".txt", Token);
            if (name == "rename")
            {
                foreach (string invalid in new[] { "/unloaded/chosen.txt", "/missing/chosen.txt", "/pending/chosen.txt", "/alternate/sentinel.txt", "/../escape.txt" })
                    await Reject(async () => { await client.ResolvePartialStructureAsync(partial, "rename", invalid, Token); }, "Rename refuses unavailable, pending, occupied or escaping destination " + invalid);
                Directory.CreateDirectory(Path.Combine(partial, "private"));
                await Reject(async () => { await client.ResolvePartialStructureAsync(partial, "rename", "/private/chosen.txt", Token); }, "Rename refuses a private destination parent");
                Assert((await client.GetPartialStructureSessionAsync(partial, Token)).Ready && File.ReadAllText(Path.Combine(partial, "target", name + ".txt")) == "base " + name, "Rejected destinations leave preparation and local bytes unchanged");
            }
            await client.ResolvePartialStructureAsync(partial, choice, name == "rename" ? "/alternate/chosen.txt" : name == "short" ? "chosen-short.txt" : null, Token);
            string expectedPath = name == "take" ? "source/take.txt" : name == "rename" ? "alternate/chosen.txt" : name == "short" ? "target/chosen-short.txt" : "target/" + name + ".txt";
            Assert(!client.HasSavedPartialStructureSession(partial) && File.ReadAllText(Path.Combine(partial, expectedPath)) == (name == "edited" ? "local edited" : "incoming " + name), name + " preserves reviewed path and content choice");
            Assert(name == "take" ? !File.Exists(Path.Combine(partial, "target", name + ".txt")) : !File.Exists(Path.Combine(partial, "source", name + ".txt")), name + " does not leave duplicate source or target");
            Assert(File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")) == load && !Directory.Exists(Path.Combine(partial, "unloaded")) && File.ReadAllText(Path.Combine(partial, "target", "sentinel.txt")) == "unrelated pending", name + " preserves selector, unloaded tree and unrelated pending content");
            Assert(File.ReadAllText(Path.Combine(session.RecoveryDirectory, "local.bin")) == (name == "edited" ? "local edited" : "base " + name), name + " retains immutable original bytes");
        }
        var failed = await client.PreparePartialStructureAsync(partial, "/target/failure.txt", Token);
        Environment.SetEnvironmentVariable("TSCM_CROSS_PROXY_CM", cm); config.CmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        await Reject(async () => { await client.ResolvePartialStructureAsync(partial, "keep-local", null, Token); }, "A failure after the native cross-directory move retains an uncertain session");
        config.CmPath = cm; Environment.SetEnvironmentVariable("TSCM_CROSS_PROXY_CM", null);
        Assert((await client.GetPartialStructureSessionAsync(partial, Token)).Applying && File.Exists(Path.Combine(partial, "target", "failure.txt")), "Restart sees the moved file and durable uncertain state");
        File.WriteAllText(Path.Combine(partial, "target", "failure.txt"), "post failure edit", Utf8);
        await client.RecoverPartialStructureAsync(partial, Token);
        Assert(!client.HasSavedPartialStructureSession(partial) && File.ReadAllText(Path.Combine(partial, "source", "failure.txt")) == "incoming failure" && !File.Exists(Path.Combine(partial, "target", "failure.txt")), "Explicit recovery restores the incoming source and clears only selected move");
        Assert(File.ReadAllText(Path.Combine(failed.RecoveryDirectory, "local.bin")) == "base failure" && Directory.GetFiles(failed.RecoveryDirectory, "*.bin", SearchOption.AllDirectories).Any(path => File.ReadAllText(path) == "post failure edit"), "Recovery retains original and post-failure moved bytes");
        foreach (string path in new[] { "target/keep.txt", "target/edited.txt", "alternate/chosen.txt", "target/chosen-short.txt" })
            Assert((await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new List<string> { Path.Combine(partial, path) }, Comment = "Explicit cross-directory resolution" }, Token)).Succeeded, "Explicit checkin publishes " + path);
        Native(consumer, "update", consumer, "--dontmerge");
        Assert(File.ReadAllText(Path.Combine(consumer, "target", "keep.txt")) == "incoming keep" && File.ReadAllText(Path.Combine(consumer, "target", "edited.txt")) == "local edited" && File.ReadAllText(Path.Combine(consumer, "alternate", "chosen.txt")) == "incoming rename" && !File.Exists(Path.Combine(consumer, "source", "keep.txt")), "Independent consumer receives the selected move identities and bytes");
        Assert(File.ReadAllText(Path.Combine(consumer, "target", "sentinel.txt")) == "base sentinel" && !Directory.Exists(Path.Combine(consumer, "pending")) && !File.Exists(Path.Combine(consumer, "removed", "removed-parent.txt")), "Selective checkins exclude unrelated edits and unselected parent moves");
        Assert(File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")) == load && !Directory.Exists(Path.Combine(partial, "unloaded")) && File.ReadAllText(Path.Combine(partial, "target", "sentinel.txt")) == "unrelated pending", "Recovery and checkins preserve loading configuration and unrelated pending bytes");
        Native(producer, "update", producer, "--dontmerge"); Native(producer, "remove", Path.Combine(producer, "removed"));
        File.WriteAllText(Path.Combine(producer, "source", "contentguard.txt"), "incoming content guard", Utf8);
        Native(producer, "checkin", producer, "--all", "-c=Incoming directory deletion guard");
        await Reject(async () => { await client.PreparePartialStructureAsync(partial, "/removed/removed-parent.txt", Token); }, "Server-deleted destination parent is rejected before preparation");
        await Reject(async () => { await client.PreparePartialStructureAsync(partial, "/target/global-delete.txt", Token); }, "An unrelated incoming directory deletion blocks exact-file update before mutation");
        Assert(!client.HasSavedPartialStructureSession(partial) && File.ReadAllText(Path.Combine(partial, "target", "global-delete.txt")) == "base global-delete" && File.ReadAllText(Path.Combine(partial, "removed", "removed-parent.txt")) == "base removed-parent", "Global incoming directory guard preserves all pending move bytes");
        Assert((await client.PreviewPartialConflictsAsync(partial, Token)).Any(item => item.RepositoryPath == "/source/contentguard.txt" && item.CanResolve), "Content guard fixture has a real independently edited content conflict");
        await Reject(async () => { await client.PreparePartialConflictAsync(partial, "/source/contentguard.txt", Token); }, "Content preparation rejects a directory deletion at the pinned file revision");
        Assert(!client.HasSavedPartialConflictSession(partial) && File.ReadAllText(Path.Combine(partial, "source", "contentguard.txt")) == "local content guard", "Content directory guard leaves no session or local mutation");
    }
    private static void WriteNew(string path, string content)
    { using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write)) using (var writer = new StreamWriter(file, Utf8)) writer.Write(content); }
    private static string Native(string cwd, params string[] args)
    {
        if (!Path.GetFullPath(cwd).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe fixture cwd");
        var result = Execute(cm, cwd, args); Evidence.Add(new { cwd, args, exitCode = result.Item1, output = result.Item2, error = result.Item3 });
        if (result.Item1 != 0) throw new Exception("Native fixture failed: " + result.Item2 + result.Item3); return result.Item2;
    }
    private static Tuple<int, string, string> Execute(string file, string cwd, string[] args)
    {
        var start = new ProcessStartInfo(file, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(start)) { var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Native fixture timed out"); } return Tuple.Create(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult()); }
    }
    private static int Proxy(string[] args)
    {
        var result = Execute(Environment.GetEnvironmentVariable("TSCM_CROSS_PROXY_CM"), Environment.CurrentDirectory, args); Console.OutputEncoding = Utf8; Console.Write(result.Item2); Console.Error.Write(result.Item3);
        if (args.Length > 1 && args[0] == "partial" && args[1] == "move" && result.Item1 == 0) { Console.Error.WriteLine("Injected failure after native cross-directory move"); return 41; }
        return result.Item1;
    }
}
