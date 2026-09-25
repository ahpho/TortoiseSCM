// GPL-2.0-or-later. Scope-safe checkin after a retained directory is replaced on the server.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using TortoiseSCM;

internal static class PartialDirectoryReaddCollisionTests
{
    private static string run, producer, partial, consumer, branch, cm, executable;
    private static int assertions;
    private static bool publicOnly;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly List<object> Evidence = new List<object>();
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " directory re-add collision assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, publicOnly ? "directory-readd-collision-public-results.json" : "directory-readd-collision-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
    private static void Assert(bool condition, string message)
    { assertions++; Evidence.Add(new { condition, message }); if (!condition) throw new Exception(message); Console.WriteLine("PASS: " + message); }
    private static async Task Run(string[] args)
    {
        if (args.Length < 2 || args.Length > 4 || (args.Length == 4 && args[3] != "--public-only")) throw new ArgumentException("Pass manifest, cm.exe, optional public executable and --public-only.");
        var manifest = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        string id = (string)manifest["runId"], candidate = (string)manifest["runDirectory"];
        string assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string qa = String.Equals(Path.GetFileName(assemblyDirectory), "qa", StringComparison.OrdinalIgnoreCase) ? assemblyDirectory : Path.Combine(Path.GetDirectoryName(assemblyDirectory), "qa");
        if (!Regex.IsMatch(id, "^integration-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$") || !Object.Equals(manifest["complete"], true) ||
            !String.Equals(Path.GetFullPath(candidate), Path.Combine(qa, id), StringComparison.OrdinalIgnoreCase) ||
            !String.Equals(Path.GetFullPath(args[0]), Path.Combine(candidate, "manifest.json"), StringComparison.OrdinalIgnoreCase) ||
            (string)manifest["branch"] != "/main/tortoisescm-autotest-" + id || !((string)manifest["repository"]).StartsWith("TestSCM@")) throw new Exception("Only completed isolated TestSCM fixtures are accepted");
        run = candidate; branch = (string)manifest["branch"]; cm = Path.GetFullPath(args[1]); executable = args.Length > 2 ? Path.GetFullPath(args[2]) : null; publicOnly = args.Length == 4;
        producer = Path.Combine(run, "producer"); partial = Path.Combine(run, "partial"); consumer = Path.Combine(run, "consumer");
        foreach (string role in new[] { "producer", "partial", "consumer" })
        {
            string workspace = Path.Combine(run, role);
            Assert(String.Equals(Path.GetFullPath((string)manifest[role]), workspace, StringComparison.OrdinalIgnoreCase), role + " fixture path is isolated");
            for (string cursor = workspace; cursor != null; cursor = Path.GetDirectoryName(cursor)) if (Directory.Exists(cursor) && (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new Exception("Reparse fixture path");
            string selector = File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector"));
            Assert(selector.Contains("\"" + branch + "\"") && selector.Contains("\"" + (string)manifest["repository"] + "\""), role + " selects dedicated branch");
        }
        if (!publicOnly) Setup();
        else if (!File.Exists(Path.Combine(run, "directory-readd-collision-results.json"))) throw new Exception("Public-only mode requires this suite's existing isolated fixture");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "collision-settings.xml") });
        string before = Snapshot(), status = Status(), head = Head();
        foreach (string name in new[] { "old", "old/a.txt", "old/sub", "old/sub/empty" })
        {
            string path = Path.Combine(partial, name);
            if (!publicOnly)
            {
                bool rejected = false;
                try { var result = await client.RunAsync(Request(path), Token); rejected = !result.Succeeded; }
                catch (ArgumentException) { rejected = true; }
                Assert(rejected, "Core rejects reappeared AD ancestor when selecting " + name);
            }
            if (executable != null) PublicCheckin(path, 2);
            Assert(Snapshot() == before && Status() == status && Head() == head, name + " rejection preserves bytes, directories, pending status, loading and server head");
        }
        string sentinel = Path.Combine(partial, "sentinel.txt");
        File.WriteAllText(sentinel, publicOnly ? "public scoped sentinel" : "core scoped sentinel", Utf8);
        string subtree = Subtree();
        if (executable != null) PublicCheckin(sentinel, 0);
        else Assert((await client.RunAsync(Request(sentinel), Token)).Succeeded, "Core permits unrelated exact sentinel checkin");
        Assert(Subtree() == subtree, "Unrelated exact checkin preserves retained local subtree");
        Native(consumer, "update", consumer, "--dontmerge");
        Assert(File.ReadAllText(Path.Combine(consumer, "sentinel.txt")) == (publicOnly ? "public scoped sentinel" : "core scoped sentinel"), "Independent consumer receives only unrelated sentinel content");
        Assert(File.ReadAllText(Path.Combine(consumer, "old/server-new.txt")) == "new server identity" && !File.Exists(Path.Combine(consumer, "old/a.txt")) && !File.Exists(Path.Combine(consumer, "old/sub/b.txt")) && Directory.Exists(Path.Combine(consumer, "old/sub/empty")), "Consumer retains server replacement and receives no stale local children");
        var pending = await client.GetStatusAsync(partial, Token);
        Assert(pending.Count(item => item.StatusCode == "AD" && item.Path.StartsWith(Path.Combine(partial, "old"), StringComparison.OrdinalIgnoreCase)) == 5 && !pending.Any(item => Path.GetFileName(item.Path) == "sentinel.txt"), "All five retained additions remain pending while sentinel is committed");
    }
    private static void Setup()
    {
        foreach (string workspace in new[] { producer, partial, consumer }) if (Directory.GetFileSystemEntries(workspace).Any(path => Path.GetFileName(path) != ".plastic")) throw new Exception("Use an empty fresh fixture");
        Directory.CreateDirectory(Path.Combine(producer, "old/sub/empty"));
        File.WriteAllText(Path.Combine(producer, "old/a.txt"), "base a", Utf8); File.WriteAllText(Path.Combine(producer, "old/sub/b.txt"), "base b", Utf8); File.WriteAllText(Path.Combine(producer, "sentinel.txt"), "base sentinel", Utf8);
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "--all", "-c=Directory collision baseline");
        Native(partial, "partial", "update", partial, "--dontmerge");
        File.WriteAllText(Path.Combine(partial, "old/a.txt"), "retained local a", Utf8);
        Native(producer, "remove", Path.Combine(producer, "old")); Native(producer, "checkin", producer, "--all", "-c=Delete directory before keep-local reconstruction");
        Native(partial, "partial", "undo", Path.Combine(partial, "old/a.txt")); Native(partial, "partial", "configure", "-/old");
        Directory.CreateDirectory(Path.Combine(partial, "old/sub/empty")); File.WriteAllText(Path.Combine(partial, "old/a.txt"), "retained local a", Utf8); File.WriteAllText(Path.Combine(partial, "old/sub/b.txt"), "base b", Utf8);
        Native(partial, "partial", "add", Path.Combine(partial, "old"), "-R");
        Directory.CreateDirectory(Path.Combine(producer, "old/sub/empty")); File.WriteAllText(Path.Combine(producer, "old/server-new.txt"), "new server identity", Utf8);
        Native(producer, "add", Path.Combine(producer, "old"), "-R"); Native(producer, "checkin", Path.Combine(producer, "old"), "--all", "-c=Reappear directory and empty child with unrelated identities");
    }
    private static PlasticCommandRequest Request(string path)
    { return new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new List<string> { path }, Comment = "Directory re-add collision scoped checkin" }; }
    private static void PublicCheckin(string path, int expected)
    {
        var args = new[] { "--cli", "--json", "--command", "checkin", "--path", path, "--cm", cm, "--settings-file", Path.Combine(run, "collision-settings.xml"), "--yes", "--comment", "Directory collision public scope check" };
        var result = Execute(executable, partial, args); Evidence.Add(new { executable, args, exitCode = result.Item1, output = result.Item2, error = result.Item3 });
        var response = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Deserialize<Dictionary<string, object>>(result.Item2);
        Assert(result.Item1 == expected && Object.Equals(response["success"], expected == 0) && Object.Equals(response["exitCode"], expected), "Public exact checkin " + path.Substring(partial.Length) + " returns " + expected);
    }
    private static string Snapshot()
    {
        return String.Join("\n", Directory.GetFileSystemEntries(partial, "*", SearchOption.AllDirectories).Where(path => !path.StartsWith(Path.Combine(partial, ".plastic") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).OrderBy(path => path).Select(path => path.Substring(partial.Length) + "|" + (Directory.Exists(path) ? "dir" : Convert.ToBase64String(File.ReadAllBytes(path))))) + "|" + File.ReadAllText(Path.Combine(partial, ".plastic/plastic.fullycheckeddirectories")) + "|" + File.Exists(Path.Combine(partial, ".plastic/plastic.fullupdate"));
    }
    private static string Subtree() { return String.Join("\n", Directory.GetFileSystemEntries(Path.Combine(partial, "old"), "*", SearchOption.AllDirectories).OrderBy(path => path).Select(path => path.Substring(partial.Length) + "|" + (Directory.Exists(path) ? "dir" : Convert.ToBase64String(File.ReadAllBytes(path))))); }
    private static string Status() { return Native(partial, "status", "--short", "--machinereadable"); }
    private static string Head() { return Native(producer, "find", "changeset", "where branch = '" + branch + "' order by changesetid desc limit 1", "--format={changesetid}", "--nototal"); }
    private static string Native(string cwd, params string[] args)
    {
        if (!Path.GetFullPath(cwd).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe native fixture cwd");
        var result = Execute(cm, cwd, args); Evidence.Add(new { cwd, args, exitCode = result.Item1, output = result.Item2, error = result.Item3 });
        if (result.Item1 != 0) throw new Exception("Native failed: " + result.Item2 + result.Item3); return result.Item2;
    }
    private static Tuple<int, string, string> Execute(string file, string cwd, string[] args)
    {
        var start = new ProcessStartInfo(file, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        using (var process = Process.Start(start)) { var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Fixture subprocess timed out"); } return Tuple.Create(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult()); }
    }
}
