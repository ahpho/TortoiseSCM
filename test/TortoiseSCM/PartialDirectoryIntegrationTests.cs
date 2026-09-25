// GPL-2.0-or-later. Isolated server-backed Partial directory decisions and recovery.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using TortoiseSCM;

internal static class PartialDirectoryIntegrationTests
{
    private static string run, producer, partial, consumer, cm;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly List<object> Evidence = new List<object>();
    private static int assertions;
    private static bool validatedFixture;
    private static bool fullMode;
    private static int Main(string[] args)
    {
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSCM_DIRECTORY_PROXY_CM"))) return Proxy(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " Partial directory assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static async Task Run(string[] args)
    {
        ValidateFixture(args);
        var config = new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "directory-settings.xml") };
        var client = new PlasticClient(config);
        string[] names = { "move-take", "move-keep", "delete-take", "delete-keep", "private-tree", "ignored-tree", "partial-tree", "scope-tree", "config-tree", "head-tree", "changed-tree", "fail-before-load", "fail-after-load", "fail-after-unload" };
        if (fullMode) names = new[] { "move-take", "move-keep", "delete-take", "scope-tree", "config-tree", "fail-before-load", "fail-after-load", "fail-after-unload" };
        foreach (string name in names) { Write(producer, name + "/a.txt", "base " + name); Write(producer, name + "/sub/b.txt", "base child " + name); }
        Write(producer, "outside-guard/safe.txt", "scope sentinel");
        Write(producer, "outside/clean.txt", "outside clean"); Write(producer, "outside/loaded.txt", "outside baseline"); Write(producer, "outside/unloaded.txt", "unloaded baseline");
        Write(producer, "ignore.conf", "*.directory-ignored\n");
        Native(producer, "add", ".", "-R"); Native(producer, "checkin", ".", "--all", "-c=Partial directory baseline");
        Native(partial, "partial", "configure", "+/", "--restorefulldirs");
        Native(partial, "partial", "update", ".");
        if (!fullMode) Native(partial, "partial", "configure", "-/outside/unloaded.txt");
        Assert(File.Exists(Path.Combine(partial, ".plastic", "plastic.fullupdate")) == fullMode, "Fixture starts with the requested full or explicit loading mode");
        Write(partial, "outside/loaded.txt", "outside local edit"); Write(partial, "unrelated-private.txt", "outside private");
        string outside = OutsideSnapshot(), selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector"));
        long baseline = Revision(producer, "outside/clean.txt");

        foreach (string name in names)
        {
            Native(producer, "update", ".", "--dontmerge");
            bool deletion = name.StartsWith("delete", StringComparison.Ordinal);
            Write(partial, name + "/a.txt", "local " + name);
            if (name == "private-tree") Write(partial, name + "/private.txt", "private descendant");
            if (name == "ignored-tree") Write(partial, name + "/local.directory-ignored", "ignored descendant");
            if (name == "partial-tree") Native(partial, "partial", "configure", "-/" + name + "/sub/b.txt");
            string incoming = name + "-incoming";
            if (deletion) Native(producer, "remove", name); else Native(producer, "move", name, incoming);
            if (name == "scope-tree") Native(producer, "move", "outside-guard", "outside-guard-moved");
            Native(producer, "checkin", ".", "--all", "-c=Partial incoming directory " + name);
            string before = Snapshot(partial), rules = Rules();
            var preview = await client.PreviewPartialDirectoriesAsync(partial, Token);
            var conflict = preview.Single(item => item.RepositoryPath == "/" + name);
            if (name == "scope-tree")
            {
                Assert(conflict.ResolutionOptions.Count == 0, "Incoming directory change outside selected scope blocks native configure");
                await Reject(async () => { await client.PreparePartialDirectoryAsync(partial, "/" + name, Token); }, "Scope-wide directory side effects are rejected before mutation");
                Assert(Snapshot(partial) == before && Rules() == rules, "Scope-side-effect rejection preserves files and load rules");
                Native(producer, "move", "outside-guard-moved", "outside-guard"); Native(producer, "checkin", ".", "--all", "-c=Restore unrelated incoming directory");
                conflict = (await client.PreviewPartialDirectoriesAsync(partial, Token)).Single(item => item.RepositoryPath == "/" + name);
            }
            if (name == "private-tree" || name == "ignored-tree" || name == "partial-tree")
            {
                Assert(conflict.ResolutionOptions.Count == 0, name + " preview refuses unsupported descendants or incomplete loading");
                await Reject(async () => { await client.PreparePartialDirectoryAsync(partial, "/" + name, Token); }, name + " preparation refuses before mutation");
                Assert(Snapshot(partial) == before && Rules() == rules, name + " rejection preserves every byte and load rule");
                Native(producer, "move", incoming, name); Native(producer, "checkin", ".", "--all", "-c=Restore unsupported directory fixture");
                // Revert only test-authored state so later cases have no incoming directory removals outside their scope.
                if (name == "private-tree") File.Delete(Path.Combine(partial, name, "private.txt"));
                if (name == "ignored-tree") File.Delete(Path.Combine(partial, name, "local.directory-ignored"));
                if (name == "partial-tree")
                {
                    Native(partial, "partial", "configure", "+/" + name, "--restorefulldirs");
                    Native(partial, "partial", "configure", "+/outside/unloaded.txt");
                    Native(partial, "partial", "configure", "-/outside/unloaded.txt");
                    Assert(!File.Exists(Path.Combine(partial, ".plastic", "plastic.fullupdate")), "Partial fixture cleanup restores explicit loading after native rule normalization");
                }
                Native(partial, "partial", "undo", Path.Combine(partial, name, "a.txt"));
                continue;
            }
            Assert(conflict.Kind == (deletion ? "incoming-directory-delete" : "incoming-directory-move") && conflict.ResolutionOptions.Contains("take-incoming"), name + " exposes the native directory decision");
            var session = await client.PreparePartialDirectoryAsync(partial, "/" + name, Token);
            Assert(session.Ready && !session.Applying && Directory.Exists(session.RecoveryDirectory), name + " prepares persistent recovery backups");
            Assert(Snapshot(partial) == before && Rules() == rules, name + " preparation leaves workspace and rules unchanged");
            client = new PlasticClient(config);
            Assert((await client.GetPartialDirectorySessionAsync(partial, Token)).SessionId == session.SessionId, name + " preparation survives a new client instance");
            await Reject(async () => { await client.ResolvePartialDirectoryAsync(consumer, "take-incoming", Token); }, name + " cannot resolve in another workspace");
            if (name == "move-take") await VerifyGuards(client, config, baseline, before);
            if (name == "config-tree")
            {
                string metadata = Path.Combine(partial, ".plastic", "plastic.selector"); byte[] original = File.ReadAllBytes(metadata);
                try
                {
                    File.AppendAllText(metadata, "\r\n", Utf8);
                    await Reject(async () => { await client.ResolvePartialDirectoryAsync(partial, "take-incoming", Token); }, "Changed selector/configuration blocks the prepared directory operation");
                    Assert(Snapshot(partial) == before, "Configuration mismatch preserves local tree bytes");
                }
                finally { File.WriteAllBytes(metadata, original); }
            }
            if (name == "head-tree")
            {
                Write(producer, "outside/clean.txt", "server head advanced"); Native(producer, "checkin", "outside/clean.txt", "-c=Advance head after directory preparation");
                await Reject(async () => { await client.ResolvePartialDirectoryAsync(partial, "take-incoming", Token); }, "Changed head blocks a stale prepared directory decision");
                Assert(Snapshot(partial) == before, "Stale head refusal preserves selected and unrelated bytes");
                await client.CancelPartialDirectoryAsync(partial, Token); session = await client.PreparePartialDirectoryAsync(partial, "/" + name, Token);
            }
            if (name == "changed-tree")
            {
                Write(partial, name + "/new-private.txt", "private added after preparation");
                await Reject(async () => { await client.ResolvePartialDirectoryAsync(partial, "take-incoming", Token); }, "New private descendant blocks a prepared directory operation");
                Assert(File.ReadAllText(Path.Combine(partial, name, "new-private.txt")) == "private added after preparation", "New private bytes survive refusal");
                File.Delete(Path.Combine(partial, name, "new-private.txt"));
                Write(partial, name + "/a.txt", "edit after preparation");
                await Reject(async () => { await client.ResolvePartialDirectoryAsync(partial, "take-incoming", Token); }, "New descendant edits block stale directory preparation");
                Assert(File.ReadAllText(Path.Combine(partial, name, "a.txt")) == "edit after preparation", "New descendant bytes survive refusal");
                await client.CancelPartialDirectoryAsync(partial, Token); session = await client.PreparePartialDirectoryAsync(partial, "/" + name, Token);
            }
            if (name == "delete-keep")
            {
                Assert(!session.Conflict.ResolutionOptions.Contains("keep-local"), "Incoming directory deletion does not offer an unverified keep-local re-add");
                await Reject(async () => { await client.ResolvePartialDirectoryAsync(partial, "keep-local", Token); }, "Unsupported directory deletion choice is rejected");
            }
            string choice = !deletion && name.EndsWith("keep", StringComparison.Ordinal) ? "keep-local" : "take-incoming";
            Assert(session.Conflict.ResolutionOptions.Contains(choice), name + " offers the requested explicit choice");
            if (name.StartsWith("fail-", StringComparison.Ordinal))
            {
                Environment.SetEnvironmentVariable("TSCM_DIRECTORY_PROXY_CM", cm); Environment.SetEnvironmentVariable("TSCM_DIRECTORY_FAIL", name);
                config.CmPath = Assembly.GetExecutingAssembly().Location;
                try { await Reject(async () => { await client.ResolvePartialDirectoryAsync(partial, choice, Token); }, name + " injected native failure is surfaced"); }
                finally { config.CmPath = cm; Environment.SetEnvironmentVariable("TSCM_DIRECTORY_PROXY_CM", null); Environment.SetEnvironmentVariable("TSCM_DIRECTORY_FAIL", null); }
                Assert(!Directory.Exists(Path.Combine(partial, name)), name + " reached the exact unload boundary");
                Assert(Directory.Exists(Path.Combine(partial, incoming)) == (name == "fail-after-load"), name + " reaches the intended before/after native load boundary");
                Write(partial, name + "/a.txt", "recovery-time original " + name);
                if (Directory.Exists(Path.Combine(partial, incoming))) Write(partial, incoming + "/a.txt", "recovery-time incoming " + name);
                client = new PlasticClient(config);
                Assert((await client.GetPartialDirectorySessionAsync(partial, Token)).Applying, name + " interrupted session survives restart");
                if (fullMode && name != "fail-after-load")
                {
                    string loadNamespace = (string)XDocument.Load(Path.Combine(session.RecoveryDirectory, "directory.xml")).Root.Attribute("loadNamespace");
                    Assert(String.IsNullOrEmpty(loadNamespace) == (name == "fail-after-unload"), name + " persists the intended namespace discovery boundary");
                    if (name == "fail-before-load")
                    {
                        string metadata = Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories"); byte[] original = File.ReadAllBytes(metadata);
                        string namespaceBefore = Snapshot(partial);
                        try
                        {
                            string altered = Guid.NewGuid().ToString("D");
                            File.WriteAllLines(metadata, File.ReadAllLines(metadata).Select(line => altered + line.Substring(line.IndexOf(':'))), Utf8);
                            await Reject(async () => { await client.RecoverPartialDirectoryAsync(partial, Token); }, "Changed native load namespace blocks recovery");
                            Assert(Snapshot(partial) == namespaceBefore, "Rejected load namespace preserves all descendant bytes");
                        }
                        finally { File.WriteAllBytes(metadata, original); }
                    }
                }
                Write(partial, name + "/unreviewed-private.txt", "do not remove new private bytes");
                await Reject(async () => { await client.RecoverPartialDirectoryAsync(partial, Token); }, name + " recovery refuses an unreviewed private descendant");
                Assert(File.ReadAllText(Path.Combine(partial, name, "unreviewed-private.txt")) == "do not remove new private bytes", name + " rejected recovery preserves unreviewed bytes");
                File.Delete(Path.Combine(partial, name, "unreviewed-private.txt"));
                await client.RecoverPartialDirectoryAsync(partial, Token);
                var contents = Directory.GetFiles(session.RecoveryDirectory, "*", SearchOption.AllDirectories).Where(file => !Path.GetFileName(file).EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText).ToList();
                Assert(contents.Contains("local " + name) && contents.Contains("recovery-time original " + name) && (name != "fail-after-load" || contents.Contains("recovery-time incoming " + name)), name + " keeps original and later edits in recovery backups");
            }
            else await client.ResolvePartialDirectoryAsync(partial, choice, Token);
            Assert(!client.HasSavedPartialDirectorySession(partial), name + " completed operation retires its workspace session marker");
            string destination = deletion ? name : incoming;
            if (deletion && choice == "take-incoming") Assert(!Directory.Exists(Path.Combine(partial, name)), name + " accepts complete incoming deletion");
            else
            {
                Assert(File.ReadAllText(Path.Combine(partial, destination, "a.txt")) == (choice == "keep-local" ? "local " : "base ") + name, name + " verifies reviewed descendant bytes");
                Assert(File.ReadAllText(Path.Combine(partial, destination, "sub", "b.txt")) == "base child " + name, name + " preserves nested controlled descendant");
                if (!deletion) Assert(!Directory.Exists(Path.Combine(partial, name)), name + " removes the old directory location");
            }
            Assert(OutsideSnapshot() == outside && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector && File.Exists(Path.Combine(partial, "outside", "unloaded.txt")) == fullMode, name + " preserves unrelated files, selector and sibling loading");
            Assert(File.Exists(Path.Combine(partial, ".plastic", "plastic.fullupdate")) == fullMode, name + " preserves the original loading mode");
            if (choice == "keep-local")
            {
                var committed = await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new[] { Path.Combine(partial, destination) }, Comment = "Reviewed Partial directory local choice" }, Token);
                Assert(committed.Succeeded, name + " remains pending until explicit selected checkin");
            }
            Native(consumer, "update", ".", "--dontmerge");
            Assert(deletion && choice == "take-incoming" ? !Directory.Exists(Path.Combine(consumer, name)) : File.ReadAllText(Path.Combine(consumer, destination, "a.txt")) == (choice == "keep-local" ? "local " : "base ") + name, name + " independent consumer verifies published state");
        }
    }
    private static async Task VerifyGuards(PlasticClient client, PlasticClientConfig config, long baseline, string before)
    {
        foreach (PlasticCommand command in new[] { PlasticCommand.Add, PlasticCommand.Checkout, PlasticCommand.Update, PlasticCommand.Undo, PlasticCommand.Checkin })
            await RejectDirectoryGuard(async () => { await client.RunAsync(new PlasticCommandRequest { Command = command, WorkingDirectory = partial, Paths = new[] { Path.Combine(partial, "outside", "clean.txt") }, Comment = "must be blocked" }, Token); }, "Active directory session blocks " + command);
        await RejectDirectoryGuard(async () => { await client.RemoveAsync(Path.Combine(partial, "outside", "clean.txt"), Token); }, "Active directory session blocks direct remove");
        await RejectDirectoryGuard(async () => { await client.MoveAsync(Path.Combine(partial, "outside", "clean.txt"), Path.Combine(partial, "outside", "new-name.txt"), Token); }, "Active directory session blocks direct move");
        await RejectDirectoryGuard(async () => { await client.IgnoreAsync(Path.Combine(partial, "unrelated-private.txt"), Token); }, "Active directory session blocks ignore.conf writes");
        await RejectDirectoryGuard(async () => { await client.RollbackAsync(Path.Combine(partial, "outside", "clean.txt"), baseline, Token); }, "Active directory session blocks rollback");
        await RejectDirectoryGuard(async () => { await client.SwitchAsync(partial, baseline, Token); }, "Active directory session blocks switch");
        await RejectDirectoryGuard(async () => { await client.ExportRevisionAsync(partial, "/outside/clean.txt", baseline, Path.Combine(partial, "outside", "clean.txt"), true, Token); }, "Active directory session blocks exporting into the workspace");
        await RejectDirectoryGuard(async () => { await client.RunMergeToolAsync(Path.Combine(partial, "outside", "clean.txt"), Path.Combine(partial, "outside", "loaded.txt"), Path.Combine(partial, "unrelated-private.txt"), Path.Combine(partial, "tool-output.txt"), Token); }, "Active directory session blocks merge tool workspace output");
        var alternate = new PlasticClient(new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "alternate-settings.xml") });
        await RejectDirectoryGuard(async () => { await alternate.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new[] { partial }, Comment = "must be blocked" }, Token); }, "Alternate settings cannot bypass the workspace marker");
        Assert(Snapshot(partial) == before, "Every blocked write preserves all workspace bytes");
    }
    private static void ValidateFixture(string[] args)
    {
        if (args.Length < 1 || args.Length > 3 || (args.Length == 3 && args[2] != "--full")) throw new ArgumentException("Pass an isolated manifest.json, optional cm.exe and optional --full.");
        fullMode = args.Length == 3;
        string manifest = Path.GetFullPath(args[0]); var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest));
        run = Path.GetDirectoryName(manifest); cm = args.Length >= 2 ? Path.GetFullPath(args[1]) : @"D:\Program Files\PlasticSCM5\client\cm.exe";
        string qa = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)), "qa"), id = Path.GetFileName(run);
        if (!String.Equals(Path.GetDirectoryName(run), qa, StringComparison.OrdinalIgnoreCase) || !id.StartsWith("integration-", StringComparison.Ordinal) || Path.GetFileName(manifest) != "manifest.json" || !(bool)data["complete"]) throw new ArgumentException("Only completed isolated qa integration fixtures are permitted.");
        producer = Path.Combine(run, "producer"); partial = Path.Combine(run, "partial"); consumer = Path.Combine(run, "consumer");
        foreach (string root in new[] { producer, partial, consumer })
        {
            for (string path = root; !String.IsNullOrEmpty(path); path = Path.GetDirectoryName(path)) if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Fixture paths cannot be redirected.");
            string selector = File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector"));
            if (!selector.Contains("repository \"TestSCM@") || !selector.Contains("smartbranch \"/main/tortoisescm-autotest-" + id + "\"")) throw new ArgumentException("Fixture selector is not its isolated branch.");
            if (Directory.GetFileSystemEntries(root).Any(path => Path.GetFileName(path) != ".plastic")) throw new ArgumentException("Use an empty new fixture.");
        }
        validatedFixture = true;
    }
    private static string OutsideSnapshot() { return Snapshot(Path.Combine(partial, "outside")) + "|" + File.ReadAllText(Path.Combine(partial, "unrelated-private.txt")); }
    private static string Rules() { string path = Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories"); return File.Exists(path) ? String.Join("\n", File.ReadAllLines(path).OrderBy(line => line, StringComparer.Ordinal)) : "missing"; }
    private static string Snapshot(string root) { return String.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + ".plastic" + Path.DirectorySeparatorChar)).OrderBy(path => path).Select(path => path.Substring(root.Length) + "|" + Convert.ToBase64String(File.ReadAllBytes(path)))); }
    private static long Revision(string root, string name) { return (long)XDocument.Parse(Native(root, "fileinfo", name, "--fields=RevisionChangeset", "--xml", "--encoding=utf-8")).Descendants("FileInfo").Single().Element("RevisionChangeset"); }
    private static void Write(string root, string relative, string text) { string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, Utf8); }
    private static void Assert(bool condition, string message) { assertions++; Evidence.Add(new { success = condition, description = message }); if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static async Task Reject(Func<Task> action, string message) { bool rejected = false; try { await action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } catch (IOException) { rejected = true; } catch (PlasticCommandException) { rejected = true; } Assert(rejected, message); }
    private static async Task RejectDirectoryGuard(Func<Task> action, string message)
    { bool rejected = false; try { await action(); } catch (ArgumentException error) { rejected = error.Message.Contains("directory decision is active"); } Assert(rejected, message); }
    private static void Save(bool success, string error) { if (validatedFixture) File.WriteAllText(Path.Combine(run, fullMode ? "partial-directory-full-results.json" : "partial-directory-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, fullMode, assertions, error, evidence = Evidence }), Utf8); }
    private static string Native(string root, params string[] args) { var result = Execute(cm, root, args); Evidence.Add(new { cwd = root, arguments = args, exitCode = result.Item1, output = result.Item2, error = result.Item3 }); if (result.Item1 != 0) throw new IOException("Native fixture command failed: " + String.Join(" ", args) + " " + result.Item2 + " " + result.Item3); return result.Item2; }
    private static Tuple<int, string, string> Execute(string file, string root, string[] args)
    {
        var start = new ProcessStartInfo(file, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        using (var process = Process.Start(start)) { var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); if (!process.WaitForExit(180000)) { process.Kill(); throw new TimeoutException("Native fixture command timed out: " + String.Join(" ", args)); } return Tuple.Create(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult()); }
    }
    private static int Proxy(string[] args)
    {
        bool load = args.Length > 2 && args[0] == "partial" && args[1] == "configure" && args[2].StartsWith("+", StringComparison.Ordinal);
        bool unload = args.Length > 2 && args[0] == "partial" && args[1] == "configure" && args[2].StartsWith("-", StringComparison.Ordinal);
        string mode = Environment.GetEnvironmentVariable("TSCM_DIRECTORY_FAIL");
        if (load && mode == "fail-before-load") { Console.Error.WriteLine("Injected directory load failure"); return 41; }
        var result = Execute(Environment.GetEnvironmentVariable("TSCM_DIRECTORY_PROXY_CM"), Environment.CurrentDirectory, args); Console.OutputEncoding = Utf8; Console.Write(result.Item2); Console.Error.Write(result.Item3);
        if (load && mode == "fail-after-load" && result.Item1 == 0) { Console.Error.WriteLine("Injected directory failure after native load"); return 42; }
        if (unload && mode == "fail-after-unload" && result.Item1 == 0) { Console.Error.WriteLine("Injected directory failure after native unload"); return 43; }
        return result.Item1;
    }
}
