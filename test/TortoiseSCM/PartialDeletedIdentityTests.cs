// GPL-2.0-or-later. Real DE/LD identity regression after an unchanged-child directory move.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using TortoiseSCM;

internal static class PartialDeletedIdentityTests
{
    private static string run, producer, partial, consumer, cm, executable;
    private static string resultName = "partial-deleted-identity-results.json";
    private static int assertions;
    private static readonly List<object> Evidence = new List<object>();
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (!String.IsNullOrEmpty(Environment.GetEnvironmentVariable("TSCM_DELETED_PROXY_CM"))) return Proxy(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " deleted identity assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, resultName), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
    private static void Assert(bool ok, string description)
    { assertions++; Evidence.Add(new { success = ok, description }); if (!ok) throw new Exception(description); Console.WriteLine("PASS: " + description); }
    private static string Local(string root, string name) { return Path.Combine(root, name == "root-de" ? name + ".txt" : "new/sub/" + name + ".txt"); }
    private static XElement Info(string root, string path)
    { return XDocument.Parse(Native(root, "fileinfo", path, "--fields=RevisionChangeset,Type,Status", "--xml", "--encoding=utf-8")).Descendants("FileInfo").Single(); }
    private static XElement Item(string root, string path)
    { return XDocument.Parse(Native(root, "ls", path, "--xml", "--encoding=utf-8")).Descendants("LsItem").Single(); }
    private static async Task Run(string[] args)
    {
        var manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        string id = (string)manifest["runId"], candidate = (string)manifest["runDirectory"];
        string assemblyDirectory = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
        string qa = String.Equals(Path.GetFileName(assemblyDirectory), "qa", StringComparison.OrdinalIgnoreCase) ? assemblyDirectory : Path.Combine(Path.GetDirectoryName(assemblyDirectory), "qa");
        if (!Regex.IsMatch(id, "^integration-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$") || !Object.Equals(manifest["complete"], true) ||
            !String.Equals(Path.GetFullPath(candidate), Path.Combine(qa, id), StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(Path.GetFullPath(args[0]), Path.Combine(candidate, "manifest.json"), StringComparison.OrdinalIgnoreCase) ||
            (string)manifest["branch"] != "/main/tortoisescm-autotest-" + id || !((string)manifest["repository"]).StartsWith("TestSCM@")) throw new Exception("Only this checkout's completed isolated TestSCM fixtures are accepted");
        run = candidate; producer = (string)manifest["producer"]; partial = (string)manifest["partial"]; consumer = (string)manifest["consumer"]; cm = args[1]; executable = args.Length > 2 ? Path.GetFullPath(args[2]) : null;
        foreach (string role in new[] { "producer", "partial", "consumer" })
        {
            string workspace = (string)manifest[role];
            Assert(String.Equals(Path.GetFullPath(workspace), Path.Combine(run, role), StringComparison.OrdinalIgnoreCase), role + " is isolated");
            for (string cursor = workspace; cursor != null; cursor = Path.GetDirectoryName(cursor)) if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new Exception("Reparse fixture path");
            string selector = File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector"));
            Assert(selector.Contains("\"" + (string)manifest["branch"] + "\"") && selector.Contains("\"" + (string)manifest["repository"] + "\""), role + " selects dedicated branch and repository");
        }
        if (args.Length > 3 && args[3] == "--ambiguity-only")
        {
            resultName = "partial-deleted-identity-ambiguity-results.json";
            await VerifyAmbiguity(new PlasticClient(new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "deleted-settings.xml") })); return;
        }
        var names = new[] { "de-take", "de-keep", "de-recover", "ld-take", "ld-keep", "ld-recover", "de-updated", "root-de" };
        Directory.CreateDirectory(Path.Combine(producer, "old/sub"));
        foreach (string name in names) File.WriteAllText(name == "root-de" ? Local(producer, name) : Path.Combine(producer, "old/sub/" + name + ".txt"), "base " + name, Utf8);
        File.WriteAllText(Path.Combine(producer, "sentinel.txt"), "base sentinel", Utf8);
        File.WriteAllText(Path.Combine(producer, "duplicate-a.txt"), "identical baseline", Utf8);
        File.WriteAllText(Path.Combine(producer, "duplicate-b.txt"), "identical baseline", Utf8);
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "--all", "-c=Deleted identity baseline");
        Native(partial, "partial", "update", partial, "--dontmerge");
        long baseRevision = (long)Info(partial, Path.Combine(partial, "old/sub/de-take.txt")).Element("RevisionChangeset");
        Native(producer, "move", Path.Combine(producer, "old"), Path.Combine(producer, "new"));
        Native(producer, "checkin", producer, "--all", "-c=Pure directory move preserving child revisions");
        Native(partial, "partial", "configure", "-/old"); Native(partial, "partial", "configure", "+/new");
        Assert((long)Info(partial, Local(partial, "de-take")).Element("RevisionChangeset") == baseRevision &&
            (long)Info(partial, Path.Combine(partial, "new/sub")).Element("RevisionChangeset") == baseRevision, "Pure directory move keeps child and parent revisions at their old historical paths");
        File.WriteAllText(Local(producer, "de-updated"), "updated base de-updated", Utf8);
        Native(producer, "checkin", Local(producer, "de-updated"), "--all", "-c=Independent child revision");
        Native(partial, "partial", "update", Local(partial, "de-updated"), "--dontmerge");
        Assert((long)Info(partial, Local(partial, "de-updated")).Element("RevisionChangeset") > (long)Info(partial, Path.Combine(partial, "new/sub")).Element("RevisionChangeset"), "Individually updated child is newer than loaded parent");
        var identities = names.ToDictionary(name => name, name => (long)Item(partial, Local(partial, name)).Element("ItemId"));
        File.WriteAllText(Path.Combine(partial, "sentinel.txt"), "unrelated pending", Utf8);
        string rules = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")), savedSelector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector"));
        var config = new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "deleted-settings.xml") }; var client = new PlasticClient(config);
        foreach (string name in names.Where(value => args.Length <= 3 || args[3] != "--public-only" || new[] { "de-take", "ld-take", "root-de" }.Contains(value)))
        {
            Native(producer, "update", producer, "--dontmerge");
            if (name.StartsWith("ld-")) File.Delete(Local(partial, name)); else Native(partial, "partial", "remove", Local(partial, name));
            File.WriteAllText(Local(producer, name), "incoming " + name, Utf8);
            Native(producer, "checkin", Local(producer, name), "--all", "-c=Incoming edit conflicts with " + name);
            var preview = await client.PreviewPartialStructureAsync(partial, Token);
            if (executable != null) Cli("partial-structure-preview");
            string repositoryPath = "/" + (name == "root-de" ? name + ".txt" : "new/sub/" + name + ".txt");
            var conflict = preview.Single(item => item.RepositoryPath == repositoryPath);
            Assert(conflict.Kind == "local-delete" && conflict.ItemId == identities[name] && conflict.ResolutionOptions.Count == 2, name + " preview verifies original identity and offers both choices");
            PlasticPartialStructureSession preparation;
            if (executable != null && !name.EndsWith("recover"))
            { Cli("partial-structure-prepare", "--item", repositoryPath, "--yes"); preparation = await client.GetPartialStructureSessionAsync(partial, Token); }
            else preparation = await client.PreparePartialStructureAsync(partial, repositoryPath, Token);
            Assert(File.ReadAllText(Path.Combine(preparation.RecoveryDirectory, "base.bin")) == (name == "de-updated" ? "updated base " : "base ") + name, name + " obtains original bytes by identity despite historical path");
            if (name.EndsWith("recover"))
            {
                Environment.SetEnvironmentVariable("TSCM_DELETED_PROXY_CM", cm); config.CmPath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                bool failed = false;
                try { await client.ResolvePartialStructureAsync(partial, "take-incoming", null, Token); } catch (InvalidOperationException) { failed = true; }
                finally { config.CmPath = cm; Environment.SetEnvironmentVariable("TSCM_DELETED_PROXY_CM", null); }
                Assert(failed && !File.Exists(Local(partial, name)), name + " interruption before native undo preserves deletion");
                client = new PlasticClient(config); Assert((await client.GetPartialStructureSessionAsync(partial, Token)).Applying, name + " session survives restart");
                await client.RecoverPartialStructureAsync(partial, Token);
            }
            else if (executable != null) Cli("partial-structure-resolve", "--resolution", name.EndsWith("keep") ? "keep-local" : "take-incoming", "--yes");
            else await client.ResolvePartialStructureAsync(partial, name.EndsWith("keep") ? "keep-local" : "take-incoming", null, Token);
            Assert(name.EndsWith("keep") ? !File.Exists(Local(partial, name)) : File.ReadAllText(Local(partial, name)) == "incoming " + name, name + " resolves intended result");
            Assert(!client.HasSavedPartialStructureSession(partial) && File.ReadAllText(Path.Combine(partial, "sentinel.txt")) == "unrelated pending" &&
                File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.fullycheckeddirectories")) == rules && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == savedSelector, name + " retires session and preserves unrelated pending and configuration");
            if (name.EndsWith("keep")) Assert((await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new List<string> { Local(partial, name) }, Comment = "Explicit keep deletion after directory move" }, Token)).Succeeded, name + " explicit deletion checkin succeeds");
        }
        Native(consumer, "update", consumer, "--dontmerge");
        foreach (string name in names.Where(value => args.Length <= 3 || args[3] != "--public-only" || new[] { "de-take", "ld-take", "root-de" }.Contains(value))) Assert(name.EndsWith("keep") ? !File.Exists(Local(consumer, name)) : File.ReadAllText(Local(consumer, name)) == "incoming " + name, name + " independent consumer verifies chosen result");
        Native(producer, "update", producer, "--dontmerge");
        Native(partial, "partial", "remove", Path.Combine(partial, "duplicate-a.txt"));
        File.WriteAllText(Path.Combine(producer, "duplicate-a.txt"), "incoming duplicate-a", Utf8);
        Native(producer, "checkin", Path.Combine(producer, "duplicate-a.txt"), "--all", "-c=Ambiguous deleted identity must be rejected");
        await VerifyAmbiguity(client);
    }
    private static async Task VerifyAmbiguity(PlasticClient client)
    {
        string pending = Native(partial, "status", partial, "--short", "--machinereadable");
        var preview = await client.PreviewPartialStructureAsync(partial, Token);
        var unsupported = preview.Single(item => item.RepositoryPath == "/duplicate-a.txt");
        Assert(unsupported.Kind == "local-delete" && unsupported.ResolutionOptions.Count == 0 && unsupported.Reason.Contains("identity is ambiguous"), "Same-changeset identical-content DE appears as one unsupported scoped conflict");
        bool prepareRejected = false, checkinRejected = false;
        try { await client.PreparePartialStructureAsync(partial, "/duplicate-a.txt", Token); } catch (ArgumentException error) { prepareRejected = error.Message.Contains("identity is ambiguous"); }
        try { var result = await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new List<string> { Path.Combine(partial, "duplicate-a.txt") }, Comment = "Must not publish ambiguous deletion" }, Token); checkinRejected = !result.Succeeded; }
        catch (ArgumentException) { checkinRejected = true; }
        Assert(prepareRejected && checkinRejected, "Ambiguous deleted identity cannot prepare or bypass resolution through checkin");
        if (executable != null)
        {
            Cli("partial-structure-preview");
            CliExpect("partial-structure-prepare", 2, "--item", "/duplicate-a.txt", "--yes");
            CliPath("checkin", Path.Combine(partial, "duplicate-a.txt"), 2, "--yes", "--comment", "Must not publish ambiguous deletion");
        }
        Assert(!client.HasSavedPartialStructureSession(partial) && !File.Exists(Path.Combine(partial, "duplicate-a.txt")) &&
            File.ReadAllText(Path.Combine(partial, "duplicate-b.txt")) == "identical baseline" && Native(partial, "status", partial, "--short", "--machinereadable") == pending, "Ambiguous DE rejection preserves native pending deletion, sibling bytes and absence of session");
        string sentinel = Path.Combine(partial, "sentinel.txt"); File.WriteAllText(sentinel, "scoped publish despite ambiguous deletion", Utf8);
        if (executable != null) CliPath("checkin", sentinel, 0, "--yes", "--comment", "Publish only unrelated sentinel");
        else Assert((await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new List<string> { sentinel }, Comment = "Publish only unrelated sentinel" }, Token)).Succeeded, "Unrelated exact checkin succeeds despite ambiguous DE elsewhere");
        Native(consumer, "update", consumer, "--dontmerge");
        Assert(File.ReadAllText(Path.Combine(consumer, "sentinel.txt")) == "scoped publish despite ambiguous deletion" &&
            File.ReadAllText(Path.Combine(consumer, "duplicate-a.txt")) == "incoming duplicate-a" &&
            File.ReadAllText(Path.Combine(consumer, "duplicate-b.txt")) == "identical baseline" &&
            (await client.GetStatusAsync(partial, Token)).Any(item => item.StatusCode == "DE" && Path.GetFileName(item.Path) == "duplicate-a.txt"), "Consumer receives only scoped checkin while ambiguous native deletion remains pending");
    }
    private static void Cli(string command, params string[] extra)
    { CliExpect(command, 0, extra); }
    private static void CliExpect(string command, int expected, params string[] extra)
    { CliPath(command, partial, expected, extra); }
    private static void CliPath(string command, string path, int expected, params string[] extra)
    {
        string[] args = new[] { "--cli", "--json", "--command", command, "--path", path, "--cm", cm, "--settings-file", Path.Combine(run, "deleted-settings.xml") }.Concat(extra).ToArray();
        var result = Execute(executable, run, args); Evidence.Add(new { executable, args, exitCode = result.Item1, output = result.Item2, error = result.Item3 });
        var response = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(result.Item2);
        Assert(result.Item1 == expected && Object.Equals(response["success"], expected == 0) && Object.Equals(response["exitCode"], expected), "Public executable " + command + " returns expected " + expected + " with consistent JSON status");
    }
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
        if (args.Length > 1 && args[0] == "partial" && args[1] == "undo") { Console.Error.WriteLine("Injected failure before exact undo"); return 41; }
        var result = Execute(Environment.GetEnvironmentVariable("TSCM_DELETED_PROXY_CM"), Environment.CurrentDirectory, args); Console.OutputEncoding = Utf8; Console.Write(result.Item2); Console.Error.Write(result.Item3); return result.Item1;
    }
}
