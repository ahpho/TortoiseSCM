// GPL-2.0-or-later. Isolated server-backed incoming move and recovery tests.
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

internal static class PartialIncomingMoveTests
{
    private static string run, producer, partial, consumer, cm;
    private static int assertions;
    private static readonly List<object> Evidence = new List<object>();
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSCM_INCOMING_PROXY_CM"))) return Proxy(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " incoming move assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, "partial-incoming-move-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
    private static void Assert(bool ok, string description)
    { assertions++; Evidence.Add(new { success = ok, description }); if (!ok) throw new Exception(description); Console.WriteLine("PASS: " + description); }
    private static async Task Reject(Func<Task> action, string description)
    { bool rejected = false; try { await action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } Assert(rejected, description); }
    private static string PathIn(string root, string name, string file) { return Path.Combine(root, name, file); }
    private static async Task Run(string[] args)
    {
        var manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = (string)manifest["runDirectory"]; producer = (string)manifest["producer"]; partial = (string)manifest["partial"]; consumer = (string)manifest["consumer"]; cm = args[1];
        Assert(((string)manifest["branch"]).StartsWith("/main/tortoisescm-autotest-"), "Dedicated incoming move branch");
        foreach (string workspace in new[] { producer, partial, consumer }) Assert(Path.GetFullPath(workspace).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")).Contains((string)manifest["branch"]), "Fixture workspace is isolated");
        var names = new[] { "same-take", "same-keep", "cross-take", "cross-keep", "fail-before-load", "fail-after-load", "fail-edit-after-load", "occupied", "full-take" };
        foreach (string name in names)
        {
            Directory.CreateDirectory(PathIn(producer, name, "target"));
            File.WriteAllText(PathIn(producer, name, "old.txt"), "base " + name, Utf8);
            File.WriteAllText(PathIn(producer, name, "sibling.txt"), "base sibling " + name, Utf8);
            File.WriteAllText(PathIn(producer, name, "unloaded.txt"), "unloaded " + name, Utf8);
        }
        File.WriteAllText(Path.Combine(producer, "sentinel.txt"), "base sentinel", Utf8);
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "--all", "-c=Incoming move tests base");
        Native(partial, "partial", "update", partial, "--dontmerge");
        foreach (string name in names)
        {
            if (name != "full-take") Native(partial, "partial", "configure", "-/" + name + "/unloaded.txt");
            File.WriteAllText(PathIn(partial, name, "old.txt"), "local " + name, Utf8);
            string destination = name.StartsWith("cross") ? "target/new.txt" : "new.txt";
            Native(producer, "move", PathIn(producer, name, "old.txt"), PathIn(producer, name, destination));
            File.WriteAllText(PathIn(producer, name, destination), "incoming " + name, Utf8);
            File.WriteAllText(PathIn(producer, name, "sibling.txt"), "incoming sibling " + name, Utf8);
            Native(producer, "checkin", Path.Combine(producer, name), "--all", "-c=Incoming move tests " + name);
        }
        File.WriteAllText(Path.Combine(partial, "sentinel.txt"), "unrelated pending", Utf8);
        File.WriteAllText(PathIn(partial, "occupied", "new.txt"), "private destination", Utf8);
        string selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")), rules = Rules();
        var config = new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "incoming-settings.xml") }; var client = new PlasticClient(config);
        var preview = await client.PreviewPartialStructureAsync(partial, Token);
        Assert(preview.Count(item => item.Kind == "incoming-move" && item.ResolutionOptions.Count == 2) == names.Length - 1, "Same and cross directory incoming moves offer two explicit choices");
        await Reject(async () => { await client.PreparePartialStructureAsync(partial, "/occupied/old.txt", Token); }, "Occupied incoming destination is rejected before backup or mutation");
        foreach (string name in names.Where(value => value != "occupied"))
        {
            string old = PathIn(partial, name, "old.txt"), destination = PathIn(partial, name, name.StartsWith("cross") ? "target/new.txt" : "new.txt");
            var preparation = await client.PreparePartialStructureAsync(partial, "/" + name + "/old.txt", Token);
            Assert(File.ReadAllText(Path.Combine(preparation.RecoveryDirectory, "local.bin")) == "local " + name, name + " backs up local bytes");
            if (name.StartsWith("fail"))
            {
                Environment.SetEnvironmentVariable("TSCM_INCOMING_PROXY_CM", cm); Environment.SetEnvironmentVariable("TSCM_INCOMING_FAIL", name);
                config.CmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                await Reject(async () => { await client.ResolvePartialStructureAsync(partial, "keep-local", null, Token); }, name + " injected failure retains recoverable session");
                config.CmPath = cm; Environment.SetEnvironmentVariable("TSCM_INCOMING_PROXY_CM", null); Environment.SetEnvironmentVariable("TSCM_INCOMING_FAIL", null);
                Assert(!File.Exists(old) && (name == "fail-before-load" ? !File.Exists(destination) : File.Exists(destination)), name + " reached intended unload/load boundary");
                if (name == "fail-edit-after-load") Assert(File.ReadAllText(destination) == "edit immediately after native load", "Post-configure edit is preserved and cannot be mistaken for permission to update");
                File.WriteAllText(old, "new private bytes " + name, Utf8);
                if (File.Exists(destination)) File.WriteAllText(destination, "new controlled bytes " + name, Utf8);
                if (name == "fail-before-load")
                {
                    File.WriteAllText(PathIn(producer, name, "new.txt"), "later incoming " + name, Utf8);
                    Native(producer, "checkin", PathIn(producer, name, "new.txt"), "--all", "-c=Recovery after advancing same incoming identity");
                }
                client = new PlasticClient(config);
                Assert((await client.GetPartialStructureSessionAsync(partial, Token)).Applying, name + " survives client restart");
                await client.RecoverPartialStructureAsync(partial, Token);
                Assert(!File.Exists(old) && File.ReadAllText(destination) == "incoming " + name, name + " recovers pinned incoming bytes despite branch advancement");
                var backups = Directory.GetFiles(preparation.RecoveryDirectory, "*.bin", SearchOption.AllDirectories).Select(file => File.ReadAllText(file)).ToList();
                Assert(backups.Contains("local " + name) && backups.Contains("new private bytes " + name) && (name == "fail-before-load" || backups.Contains("new controlled bytes " + name)), name + " preserves original and recovery-time bytes at both paths");
            }
            else
            {
                string choice = name.EndsWith("keep") ? "keep-local" : "take-incoming";
                await client.ResolvePartialStructureAsync(partial, choice, null, Token);
                Assert(!File.Exists(old) && File.ReadAllText(destination) == (choice == "keep-local" ? "local " : "incoming ") + name, name + " resolves at the server-selected path");
            }
            Assert(!client.HasSavedPartialStructureSession(partial), name + " retires active session after verification");
            Assert(File.ReadAllText(PathIn(partial, name, "sibling.txt")) == "base sibling " + name && File.Exists(PathIn(partial, name, "unloaded.txt")) == (name == "full-take") && File.ReadAllText(Path.Combine(partial, "sentinel.txt")) == "unrelated pending" && Rules() == rules && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector, name + " preserves loaded sibling, unloaded sibling, unrelated pending and configuration");
        }
        foreach (string name in new[] { "same-keep", "cross-keep" })
        {
            string destination = PathIn(partial, name, name.StartsWith("cross") ? "target/new.txt" : "new.txt");
            Assert((await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new List<string> { destination }, Comment = "Explicit incoming move decision" }, Token)).Succeeded, name + " permits explicit single-file checkin");
        }
        Native(consumer, "update", consumer, "--dontmerge");
        Assert(File.ReadAllText(PathIn(consumer, "same-keep", "new.txt")) == "local same-keep" && File.ReadAllText(PathIn(consumer, "cross-keep", "target/new.txt")) == "local cross-keep", "Independent consumer receives local decisions at incoming paths");
    }
    private static string Rules() { return String.Join("\n", File.ReadAllLines(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")).OrderBy(value => value, StringComparer.Ordinal)); }
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
        bool load = args.Length > 2 && args[0] == "partial" && args[1] == "configure" && args[2].StartsWith("+");
        string mode = Environment.GetEnvironmentVariable("TSCM_INCOMING_FAIL");
        if (load && mode == "fail-before-load") { Console.Error.WriteLine("Injected failure before exact incoming load"); return 41; }
        var result = Execute(Environment.GetEnvironmentVariable("TSCM_INCOMING_PROXY_CM"), Environment.CurrentDirectory, args); Console.OutputEncoding = Utf8; Console.Write(result.Item2); Console.Error.Write(result.Item3);
        if (load && mode == "fail-after-load" && result.Item1 == 0) { Console.Error.WriteLine("Injected failure after exact incoming load"); return 42; }
        if (load && mode == "fail-edit-after-load" && result.Item1 == 0) File.WriteAllText(Path.Combine(Environment.CurrentDirectory, args[2].Substring(2).Replace('/', Path.DirectorySeparatorChar)), "edit immediately after native load", Utf8);
        return result.Item1;
    }
}
