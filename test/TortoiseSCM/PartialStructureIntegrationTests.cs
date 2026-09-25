// GPL-2.0-or-later. Live file structure decisions, isolated autotest workspaces only.
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

internal static class PartialStructureIntegrationTests
{
    private static string run, producer, partial, consumer, cm;
    private static int assertions;
    private static readonly List<object> Evidence = new List<object>();
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSCM_STRUCTURE_PROXY_CM"))) return Proxy(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " real Partial structure assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, "partial-structure-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
    private static void Assert(bool ok, string description)
    { assertions++; Evidence.Add(new { success = ok, description }); if (!ok) throw new Exception(description); Console.WriteLine("PASS: " + description); }
    private static async Task Reject(Func<Task> action, string description)
    { bool rejected = false; try { await action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Assert(rejected, description); }
    private static async Task Run(string[] args)
    {
        var manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = (string)manifest["runDirectory"]; producer = (string)manifest["producer"]; partial = (string)manifest["partial"]; consumer = (string)manifest["consumer"]; cm = args[1];
        Assert(((string)manifest["branch"]).StartsWith("/main/tortoisescm-autotest-"), "Structure fixture uses an isolated branch");
        foreach (string workspace in new[] { producer, partial, consumer }) Assert(Path.GetFullPath(workspace).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")).Contains((string)manifest["branch"]), "Structure workspace remains inside fixture");
        var scenarios = new[] { "delete-take", "delete-keep", "delete-rename", "localdelete-take", "localdelete-keep", "localmove-take", "localmove-keep", "localmove-rename", "incoming-move" };
        foreach (string name in scenarios.Concat(new[] { "sentinel", "unloaded" })) WriteNew(Path.Combine(producer, name + ".txt"), "base " + name);
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "-c=Partial structure test base");
        Native(partial, "partial", "update", partial, "--dontmerge"); Native(partial, "partial", "configure", "-/unloaded.txt");
        foreach (string name in scenarios)
        {
            string local = Path.Combine(partial, name + ".txt");
            if (name.StartsWith("delete-") || name == "incoming-move") File.WriteAllText(local, "local " + name, Utf8);
            else if (name.StartsWith("localdelete")) Native(partial, "partial", "remove", local);
            else Native(partial, "partial", "move", local, Path.Combine(partial, name + "-moved.txt"));
            if (name.StartsWith("delete-")) Native(producer, "remove", Path.Combine(producer, name + ".txt"));
            else if (name == "incoming-move") Native(producer, "move", Path.Combine(producer, name + ".txt"), Path.Combine(producer, "incoming-moved.txt"));
            else File.WriteAllText(Path.Combine(producer, name + ".txt"), "incoming " + name, Utf8);
        }
        foreach (string name in new[] { "add-take", "add-keep", "add-rename", "failure" })
        { WriteNew(Path.Combine(partial, name + ".txt"), "local " + name); Native(partial, "partial", "add", Path.Combine(partial, name + ".txt")); WriteNew(Path.Combine(producer, name + ".txt"), "incoming " + name); Native(producer, "add", Path.Combine(producer, name + ".txt")); }
        Native(producer, "checkin", producer, "--all", "-c=Partial structure incoming set");
        File.WriteAllText(Path.Combine(partial, "sentinel.txt"), "unrelated pending", Utf8);
        string selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")), load = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories"));
        var config = new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "structure-settings.xml") }; var client = new PlasticClient(config);
        var preview = await client.PreviewPartialStructureAsync(partial, Token);
        Assert(preview.Count == 13 && preview.Count(item => item.ResolutionOptions.Count > 0) == 12, "Preview identifies all supported file structures and an explicitly unsupported incoming move");
        Assert(preview.Single(item => item.Kind == "incoming-move").ResolutionOptions.Count == 0, "Incoming move is refused without selecting a parent directory");
        await Reject(async () => { await client.RunAsync(Request(PlasticCommand.Checkin, Path.Combine(partial, "delete-take.txt")), Token); }, "Incoming deletion cannot bypass structural decisions through checkin");
        var preparation = await client.PreparePartialStructureAsync(partial, "/add-take.txt", Token);
        Assert(preparation.Ready && File.ReadAllText(Path.Combine(preparation.RecoveryDirectory, "local.bin")) == "local add-take", "Preparation persists original local bytes before any mutation");
        foreach (var command in new[] { PlasticCommand.Add, PlasticCommand.Checkout, PlasticCommand.Update, PlasticCommand.Undo, PlasticCommand.Checkin })
            await Reject(async () => { await client.RunAsync(Request(command, Path.Combine(partial, "sentinel.txt")), Token); }, "Active structure session blocks ordinary " + command);
        var other = new PlasticClient(new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "other-settings.xml") });
        await Reject(async () => { await other.RunAsync(Request(PlasticCommand.Undo, Path.Combine(partial, "sentinel.txt")), Token); }, "Workspace marker blocks writes from different settings");
        await client.CancelPartialStructureAsync(partial, Token);
        Assert(!client.HasSavedPartialStructureSession(partial) && File.ReadAllText(Path.Combine(partial, "add-take.txt")) == "local add-take", "Cancelling an unapplied preparation preserves pending local addition");
        foreach (string name in new[] { "add-take", "add-keep", "add-rename", "delete-take", "delete-keep", "delete-rename", "localdelete-take", "localdelete-keep", "localmove-take", "localmove-keep", "localmove-rename" })
        {
            string path = "/" + name + (name.StartsWith("localmove") ? "-moved" : "") + ".txt";
            string choice = name.EndsWith("take") ? "take-incoming" : name.EndsWith("keep") ? "keep-local" : "rename";
            var prepared = await client.PreparePartialStructureAsync(partial, path, Token);
            if (choice == "rename")
            {
                await Reject(async () => { await client.ResolvePartialStructureAsync(partial, choice, "../escape.txt", Token); }, "Rename rejects traversal before native mutation");
                await Reject(async () => { await client.ResolvePartialStructureAsync(partial, choice, "CON.txt", Token); }, "Rename rejects reserved devices before native mutation");
            }
            var applied = await client.ResolvePartialStructureAsync(partial, choice, choice == "rename" ? name + "-chosen.txt" : null, Token);
            Assert(applied.Succeeded && !client.HasSavedPartialStructureSession(partial), name + " applies and retires its active session");
            string original = Path.Combine(partial, name + ".txt"), moved = Path.Combine(partial, name + "-moved.txt"), renamed = Path.Combine(partial, name + "-chosen.txt");
            if (name.StartsWith("add")) Assert(File.ReadAllText(original) == (choice == "keep-local" ? "local " : "incoming ") + name && (choice != "rename" || File.ReadAllText(renamed) == "local " + name), name + " preserves selected content and identity placement");
            else if (name.StartsWith("delete-")) Assert(choice == "take-incoming" ? !File.Exists(original) : File.ReadAllText(choice == "rename" ? renamed : original) == "local " + name, name + " applies explicit deletion or local re-add");
            else if (name.StartsWith("localdelete")) Assert(choice == "keep-local" ? !File.Exists(original) : File.ReadAllText(original) == "incoming " + name, name + " resolves local deletion against incoming bytes");
            else Assert(File.ReadAllText(choice == "take-incoming" ? original : choice == "rename" ? renamed : moved) == "incoming " + name, name + " preserves incoming bytes for pure local rename");
            Assert(File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")) == load && !File.Exists(Path.Combine(partial, "unloaded.txt")) && File.ReadAllText(Path.Combine(partial, "sentinel.txt")) == "unrelated pending", name + " preserves selector, load rules and unrelated work");
            Assert(File.Exists(Path.Combine(prepared.RecoveryDirectory, "structure.xml")), name + " retains durable recovery evidence");
        }
        var failure = await client.PreparePartialStructureAsync(partial, "/failure.txt", Token);
        Environment.SetEnvironmentVariable("TSCM_STRUCTURE_PROXY_CM", cm); config.CmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        await Reject(async () => { await client.ResolvePartialStructureAsync(partial, "keep-local", null, Token); }, "Native update failure leaves a durable uncertain structural session");
        config.CmPath = cm; Environment.SetEnvironmentVariable("TSCM_STRUCTURE_PROXY_CM", null);
        Assert((await client.GetPartialStructureSessionAsync(partial, Token)).Applying, "Restart exposes an interrupted structural decision");
        File.WriteAllText(Path.Combine(partial, "failure.txt"), "new edit after interruption", Utf8);
        var recovered = await client.RecoverPartialStructureAsync(partial, Token);
        Assert(recovered.Succeeded && !client.HasSavedPartialStructureSession(partial) && File.ReadAllText(Path.Combine(partial, "failure.txt")) == "incoming failure", "Explicit recovery restores pinned incoming state");
        Assert(Directory.GetFiles(failure.RecoveryDirectory, "*.bin", SearchOption.AllDirectories).Any(file => File.ReadAllText(file) == "new edit after interruption") && File.ReadAllText(Path.Combine(failure.RecoveryDirectory, "local.bin")) == "local failure", "Recovery preserves both original and post-failure local bytes");
        // Publish each reviewed pending decision through the shared checkin guard.
        foreach (string name in new[] { "add-keep.txt", "add-rename-chosen.txt", "delete-keep.txt", "delete-rename-chosen.txt", "localdelete-keep.txt", "localmove-keep-moved.txt", "localmove-rename-chosen.txt" })
            Assert((await client.RunAsync(Request(PlasticCommand.Checkin, Path.Combine(partial, name)), Token)).Succeeded, "Explicit checkin publishes " + name);
        Native(consumer, "update", consumer, "--dontmerge");
        Assert(File.ReadAllText(Path.Combine(consumer, "add-rename-chosen.txt")) == "local add-rename" && File.ReadAllText(Path.Combine(consumer, "delete-keep.txt")) == "local delete-keep" && !File.Exists(Path.Combine(consumer, "localdelete-keep.txt")) && File.ReadAllText(Path.Combine(consumer, "localmove-keep-moved.txt")) == "incoming localmove-keep", "Independent consumer receives reviewed structural outcomes");
    }
    private static PlasticCommandRequest Request(PlasticCommand command, string path)
    { return new PlasticCommandRequest { Command = command, WorkingDirectory = partial, Paths = new List<string> { path }, Comment = "Explicit Partial structural decision" }; }
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
        if (args.Length > 1 && args[0] == "partial" && args[1] == "update") { Console.Error.WriteLine("Injected structural update failure"); return 41; }
        var result = Execute(Environment.GetEnvironmentVariable("TSCM_STRUCTURE_PROXY_CM"), Environment.CurrentDirectory, args); Console.OutputEncoding = Utf8; Console.Write(result.Item2); Console.Error.Write(result.Item3); return result.Item1;
    }
}
