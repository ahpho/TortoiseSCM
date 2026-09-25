// GPL-2.0-or-later. Server-backed keep-local decisions for incoming directory deletion.
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

internal static class PartialDirectoryKeepDeletedTests
{
    private static string run, producer, partial, consumer, cm;
    private static bool fullMode, validated;
    private static int assertions;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly List<object> Evidence = new List<object>();
    private static int Main(string[] args)
    {
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSCM_KEEP_DELETE_CM"))) return Proxy(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " keep-deleted directory assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static async Task Run(string[] args)
    {
        ValidateFixture(args);
        string[] names = { "keep-deleted", "before-add", "after-first-add", "after-last-add", "head-replaced" };
        foreach (string name in names)
        {
            Write(producer, name + "/a.txt", "base " + name);
            Write(producer, name + "/sub/b.txt", "unchanged child " + name);
            Directory.CreateDirectory(Path.Combine(producer, name, "empty"));
        }
        Write(producer, "outside/clean.txt", "outside clean"); Write(producer, "outside/edited.txt", "outside baseline"); Write(producer, "outside/unloaded.txt", "outside unloaded");
        Native(producer, "add", ".", "-R"); Native(producer, "checkin", ".", "--all", "-c=Keep deleted directory baseline");
        Native(partial, "partial", "update", ".");
        if (!fullMode) Native(partial, "partial", "configure", "-/outside/unloaded.txt");
        Write(partial, "outside/edited.txt", "unrelated local edit"); Write(partial, "outside-private.txt", "unrelated private bytes");
        Assert(File.Exists(Path.Combine(partial, ".plastic", "plastic.fullupdate")) == fullMode, "Fixture has the requested native loading mode");
        string outside = Outside(), selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector"));
        var config = new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "keep-deleted-settings.xml") };
        var client = new PlasticClient(config);
        foreach (string name in names)
        {
            Native(producer, "update", ".", "--dontmerge");
            Write(partial, name + "/a.txt", "local 编辑 " + name);
            Native(producer, "remove", name); Native(producer, "checkin", ".", "--all", "-c=Incoming deletion " + name);
            Native(consumer, "update", ".", "--dontmerge");
            Assert(!Directory.Exists(Path.Combine(consumer, name)), name + " is deleted on the server before any decision");
            string before = Snapshot(partial), rulesBefore = Rules();
            var conflict = (await client.PreviewPartialDirectoriesAsync(partial, Token)).Single(item => item.RepositoryPath == "/" + name);
            Assert(conflict.Kind == "incoming-directory-delete" && conflict.ResolutionOptions.Contains("keep-local"), name + " offers explicit keep-local for incoming directory deletion: " + conflict.Reason);
            Assert(conflict.Items.Any(item => item.RepositoryPath == "/" + name + "/empty" && item.IsDirectory), name + " includes the controlled empty directory");
            var oldIds = conflict.Items.ToDictionary(item => item.RepositoryPath, item => item.ItemId);
            var session = await client.PreparePartialDirectoryAsync(partial, "/" + name, Token);
            Assert(Snapshot(partial) == before && Rules() == rulesBefore, name + " preparation does not mutate bytes or load rules");
            client = new PlasticClient(config);
            Assert((await client.GetPartialDirectorySessionAsync(partial, Token)).SessionId == session.SessionId, name + " preparation survives a new client instance");
            if (name == "head-replaced")
            {
                Write(producer, name + "/a.txt", "new occupant identity");
                Native(producer, "add", name, "-R"); Native(producer, "checkin", ".", "--all", "-c=Recreate deleted path with a different identity");
                await Reject(async () => { await client.ResolvePartialDirectoryAsync(partial, "keep-local", Token); }, "New head occupant blocks stale keep-local preparation");
                Assert(Snapshot(partial) == before && Rules() == rulesBefore, "Head replacement refusal preserves old local edits and loading rules");
                await client.CancelPartialDirectoryAsync(partial, Token);
                var replaced = (await client.PreviewPartialDirectoriesAsync(partial, Token)).Single(item => item.RepositoryPath == "/" + name);
                Assert(!replaced.ResolutionOptions.Contains("keep-local"), "Fresh preview rejects replacing another incoming directory identity");
                await Reject(async () => { await client.PreparePartialDirectoryAsync(partial, "/" + name, Token); }, "Fresh preparation also rejects a replacement directory identity");
                Native(consumer, "update", ".", "--dontmerge");
                Assert(File.ReadAllText(Path.Combine(consumer, name, "a.txt")) == "new occupant identity", "Rejected keep-local leaves the server occupant unchanged");
                break;
            }
            if (name == "keep-deleted")
            {
                await client.ResolvePartialDirectoryAsync(partial, "keep-local", Token);
                Assert(!client.HasSavedPartialDirectorySession(partial), "Completed keep-local retires the workspace session");
                Assert(File.ReadAllText(Path.Combine(partial, name, "a.txt")) == "local 编辑 " + name && File.ReadAllText(Path.Combine(partial, name, "sub", "b.txt")) == "unchanged child " + name && Directory.Exists(Path.Combine(partial, name, "empty")), "Keep-local rebuilds edited and unchanged bytes plus the empty directory");
                var pending = await client.GetStatusAsync(partial, Token);
                var selected = pending.Where(item => Within(item.Path, Path.Combine(partial, name))).ToList();
                Assert(selected.Count == conflict.Items.Count && selected.All(item => item.StatusCode == "AD"), "Every recreated directory and file is pending AD with a new identity");
                Native(consumer, "update", ".", "--dontmerge");
                Assert(!Directory.Exists(Path.Combine(consumer, name)), "Keep-local never automatically checks in its reconstructed tree");
                CheckOutside(outside, selector, name);
                CheckOutsideRules(rulesBefore, oldIds.Values, new long[0], name + " before checkin");
                var committed = await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new[] { Path.Combine(partial, name) }, Comment = "Explicit keep-local reconstructed directory checkin" }, Token);
                Assert(committed.Succeeded, "Public selected checkin accepts the recreated directory hierarchy");
                Native(consumer, "update", ".", "--dontmerge");
                Assert(File.ReadAllText(Path.Combine(consumer, name, "a.txt")) == "local 编辑 " + name && File.ReadAllText(Path.Combine(consumer, name, "sub", "b.txt")) == "unchanged child " + name && Directory.Exists(Path.Combine(consumer, name, "empty")), "Independent consumer receives edited bytes and the controlled empty directory");
                var newIds = Identities(consumer);
                Assert(oldIds.All(pair => newIds.ContainsKey(pair.Key) && newIds[pair.Key] > 0 && newIds[pair.Key] != pair.Value), "Every published recreated item has a different positive server identity");
                CheckOutsideRules(rulesBefore, oldIds.Values, oldIds.Keys.Select(path => newIds[path]), name + " after checkin");
            }
            else
            {
                Environment.SetEnvironmentVariable("TSCM_KEEP_DELETE_CM", cm); Environment.SetEnvironmentVariable("TSCM_KEEP_DELETE_FAIL", name);
                config.CmPath = Assembly.GetExecutingAssembly().Location;
                try { await Reject(async () => { await client.ResolvePartialDirectoryAsync(partial, "keep-local", Token); }, name + " injected add failure remains visible"); }
                finally { config.CmPath = cm; Environment.SetEnvironmentVariable("TSCM_KEEP_DELETE_CM", null); Environment.SetEnvironmentVariable("TSCM_KEEP_DELETE_FAIL", null); }
                Assert(Directory.Exists(Path.Combine(partial, name)), name + " reaches the rebuilt-tree add boundary");
                var pending = (await client.GetStatusAsync(partial, Token)).Where(item => Within(item.Path, Path.Combine(partial, name))).ToList();
                int added = pending.Count(item => item.StatusCode == "AD");
                Assert(name == "before-add" ? added == 0 : name == "after-last-add" ? added == conflict.Items.Count : added > 0 && added < conflict.Items.Count, name + " reaches the exact zero/partial/complete AD boundary");
                client = new PlasticClient(config);
                Assert((await client.GetPartialDirectorySessionAsync(partial, Token)).Applying, name + " interrupted state survives a new client instance");
                Write(partial, name + "/a.txt", "recovery 编辑 " + name);
                Write(partial, name + "/empty/new-private.txt", "unknown descendant must survive");
                string recoveryBefore = Snapshot(partial), recoveryRules = Rules();
                await Reject(async () => { await client.RecoverPartialDirectoryAsync(partial, Token); }, name + " recovery refuses an unknown descendant");
                Assert(Snapshot(partial) == recoveryBefore && Rules() == recoveryRules, name + " refusal preserves all bytes and native rules");
                File.Delete(Path.Combine(partial, name, "empty", "new-private.txt"));
                await client.RecoverPartialDirectoryAsync(partial, Token);
                Assert(!client.HasSavedPartialDirectorySession(partial) && !Directory.Exists(Path.Combine(partial, name)), name + " explicit recovery returns to the incoming deletion");
                var backups = Directory.GetFiles(session.RecoveryDirectory, "*", SearchOption.AllDirectories).Where(file => !file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)).Select(File.ReadAllText).ToList();
                Assert(backups.Contains("local 编辑 " + name) && backups.Contains("recovery 编辑 " + name) && backups.Contains("unchanged child " + name), name + " recovery preserves both original and later known edits in backups");
                Assert(!(await client.GetStatusAsync(partial, Token)).Any(item => Within(item.Path, Path.Combine(partial, name))), name + " recovery leaves no pending reconstructed identities");
                Native(consumer, "update", ".", "--dontmerge");
                Assert(!Directory.Exists(Path.Combine(consumer, name)), name + " recovery never publishes reconstructed files");
                CheckOutsideRules(rulesBefore, oldIds.Values, new long[0], name + " after recovery");
            }
            CheckOutside(outside, selector, name);
        }
    }
    private static void CheckOutside(string outside, string selector, string name)
    {
        Assert(Outside() == outside && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector, name + " preserves unrelated edits, private bytes and selector");
        Assert(File.Exists(Path.Combine(partial, ".plastic", "plastic.fullupdate")) == fullMode && File.Exists(Path.Combine(partial, "outside", "unloaded.txt")) == fullMode, name + " preserves full/explicit loading and unrelated exclusions");
    }
    private static Dictionary<string, long> Identities(string root)
    {
        return XDocument.Parse(Native(root, "ls", root, "-R", "--xml", "--encoding=utf-8")).Descendants("LsItem").Where(item => !String.IsNullOrWhiteSpace((string)item.Element("ItemId"))).ToDictionary(item => "/" + ((string)item.Element("CurrentPath")).Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/'), item => (long)item.Element("ItemId"));
    }
    private static void CheckOutsideRules(string before, IEnumerable<long> oldIds, IEnumerable<long> newIds, string label)
    {
        var excluded = oldIds.Concat(newIds).Select(id => ":" + id).ToArray();
        Func<string, string> outside = rules => String.Join("\n", rules.Split('\n').Where(line => !excluded.Any(suffix => line.EndsWith(suffix, StringComparison.Ordinal))).OrderBy(line => line, StringComparer.Ordinal));
        Assert(outside(before) == outside(Rules()), label + " preserves every unrelated native directory loading rule");
    }
    private static void ValidateFixture(string[] args)
    {
        if (args.Length < 1 || args.Length > 3 || (args.Length == 3 && args[2] != "--full")) throw new ArgumentException("Pass isolated manifest.json, optional cm.exe and optional --full.");
        fullMode = args.Length == 3; string manifest = Path.GetFullPath(args[0]);
        var data = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(manifest));
        run = Path.GetDirectoryName(manifest); cm = args.Length >= 2 ? Path.GetFullPath(args[1]) : @"D:\Program Files\PlasticSCM5\client\cm.exe";
        string qa = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)), "qa"), id = Path.GetFileName(run);
        if (!String.Equals(Path.GetDirectoryName(run), qa, StringComparison.OrdinalIgnoreCase) || !id.StartsWith("integration-", StringComparison.Ordinal) || Path.GetFileName(manifest) != "manifest.json" || !(bool)data["complete"]) throw new ArgumentException("Only completed isolated qa fixtures are permitted.");
        producer = Path.Combine(run, "producer"); partial = Path.Combine(run, "partial"); consumer = Path.Combine(run, "consumer");
        foreach (string root in new[] { producer, partial, consumer })
        {
            for (string path = root; !String.IsNullOrEmpty(path); path = Path.GetDirectoryName(path)) if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Fixture paths cannot be redirected.");
            string selector = File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector"));
            if (!selector.Contains("repository \"TestSCM@") || !selector.Contains("smartbranch \"/main/tortoisescm-autotest-" + id + "\"")) throw new ArgumentException("Fixture selector is not its isolated branch.");
            if (Directory.GetFileSystemEntries(root).Any(path => Path.GetFileName(path) != ".plastic")) throw new ArgumentException("Use an empty new fixture.");
        }
        validated = true;
    }
    private static bool Within(string path, string root) { return String.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
    private static string Outside() { return Snapshot(Path.Combine(partial, "outside")) + "|" + File.ReadAllText(Path.Combine(partial, "outside-private.txt")); }
    private static string Rules() { string path = Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories"); return File.Exists(path) ? String.Join("\n", File.ReadAllLines(path).OrderBy(line => line, StringComparer.Ordinal)) : "missing"; }
    private static string Snapshot(string root) { return String.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + ".plastic" + Path.DirectorySeparatorChar)).OrderBy(path => path).Select(path => path.Substring(root.Length) + "|" + Convert.ToBase64String(File.ReadAllBytes(path)))); }
    private static void Write(string root, string relative, string text) { string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text, Utf8); }
    private static void Assert(bool condition, string message) { assertions++; Evidence.Add(new { success = condition, description = message }); if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static async Task Reject(Func<Task> action, string message) { bool rejected = false; try { await action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } catch (IOException) { rejected = true; } catch (PlasticCommandException) { rejected = true; } Assert(rejected, message); }
    private static void Save(bool success, string error) { if (validated) File.WriteAllText(Path.Combine(run, fullMode ? "partial-directory-keep-deleted-full-results.json" : "partial-directory-keep-deleted-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, fullMode, assertions, error, evidence = Evidence }), Utf8); }
    private static string Native(string root, params string[] args) { var result = Execute(cm, root, args); Evidence.Add(new { cwd = root, arguments = args, exitCode = result.Item1, output = result.Item2, error = result.Item3 }); if (result.Item1 != 0) throw new IOException("Native fixture command failed: " + String.Join(" ", args) + " " + result.Item2 + " " + result.Item3); return result.Item2; }
    private static Tuple<int, string, string> Execute(string file, string root, string[] args)
    {
        var start = new ProcessStartInfo(file, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        using (var process = Process.Start(start)) { var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); if (!process.WaitForExit(180000)) { process.Kill(); throw new TimeoutException("Native fixture command timed out: " + String.Join(" ", args)); } return Tuple.Create(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult()); }
    }
    private static int Proxy(string[] args)
    {
        bool add = args.Length > 2 && args[0] == "partial" && args[1] == "add";
        string mode = Environment.GetEnvironmentVariable("TSCM_KEEP_DELETE_FAIL");
        if (add && mode == "before-add") { Console.Error.WriteLine("Injected failure before native add"); return 51; }
        var result = Execute(Environment.GetEnvironmentVariable("TSCM_KEEP_DELETE_CM"), Environment.CurrentDirectory, args); Console.OutputEncoding = Utf8; Console.Write(result.Item2); Console.Error.Write(result.Item3);
        if (add && result.Item1 == 0 && (mode == "after-first-add" || (mode == "after-last-add" && args.Any(arg => arg.EndsWith("b.txt", StringComparison.OrdinalIgnoreCase))))) { Console.Error.WriteLine("Injected failure after native add"); return 52; }
        return result.Item1;
    }
}
