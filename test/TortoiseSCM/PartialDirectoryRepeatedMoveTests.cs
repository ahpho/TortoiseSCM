// GPL-2.0-or-later. Real regression for directory identities retaining their old revision after moves.
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

internal static class PartialDirectoryRepeatedMoveTests
{
    private static string run, producer, partial, consumer, cm;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
    private static readonly List<object> Evidence = new List<object>();
    private static int assertions;
    private static bool validatedFixture, fullMode;
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " repeated directory move assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static async Task Run(string[] args)
    {
        ValidateFixture(args);
        var client = new PlasticClient(new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "repeated-directory-settings.xml") });
        Write(producer, "original/a.txt", "base a"); Write(producer, "original/sub/b.txt", "base b");
        Write(producer, "outside/safe.txt", "safe"); Write(producer, "outside/unloaded.txt", "unloaded");
        Native(producer, "add", ".", "-R"); Native(producer, "checkin", ".", "--all", "-c=Repeated directory baseline");
        Native(partial, "partial", "configure", "+/", "--restorefulldirs"); Native(partial, "partial", "update", ".");
        if (!fullMode) Native(partial, "partial", "configure", "-/outside/unloaded.txt");
        Write(partial, "outside/safe.txt", "outside local"); Write(partial, "unrelated-private.txt", "private");
        string selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")), outside = OutsideSnapshot();
        long originalRevision = Revision(partial, "original");
        for (int step = 1; step <= 2; step++)
        {
            string oldName = step == 1 ? "original" : "first", newName = step == 1 ? "first" : "second";
            Write(partial, oldName + "/a.txt", "local " + step);
            Native(producer, "move", oldName, newName); Native(producer, "checkin", ".", "--all", "-c=Pure directory move " + step);
            Assert(Revision(partial, oldName) == originalRevision, "Move " + step + " begins with unchanged historical directory revision");
            var preview = (await client.PreviewPartialDirectoriesAsync(partial, Token)).Single(item => item.RepositoryPath == "/" + oldName);
            Assert(preview.Kind == "incoming-directory-move" && preview.ResolutionOptions.Contains("keep-local"), "Move " + step + " previews the historical directory identity");
            var prepared = await client.PreparePartialDirectoryAsync(partial, "/" + oldName, Token);
            await client.ResolvePartialDirectoryAsync(partial, step == 1 ? "take-incoming" : "keep-local", Token);
            Assert(!client.HasSavedPartialDirectorySession(partial) && !Directory.Exists(Path.Combine(partial, oldName)), "Move " + step + " completes at its incoming location");
            Assert(File.ReadAllText(Path.Combine(partial, newName, "a.txt")) == (step == 1 ? "base a" : "local 2") && File.ReadAllText(Path.Combine(partial, newName, "sub", "b.txt")) == "base b", "Move " + step + " preserves selected and nested file contents");
            Assert(OutsideSnapshot() == outside && selector == File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) && File.Exists(Path.Combine(partial, ".plastic", "plastic.fullupdate")) == fullMode, "Move " + step + " preserves unrelated bytes, selector and loading mode");
            Assert(Directory.GetFiles(prepared.RecoveryDirectory, "local-*.bin").Select(File.ReadAllText).Contains("local " + step), "Move " + step + " retains the original local bytes");
        }
        var result = await client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = partial, Paths = new[] { Path.Combine(partial, "second") }, Comment = "Explicit repeated directory local resolution" }, Token);
        Assert(result.Succeeded, "Repeated directory local choice remains publishable by selected checkin");
        Native(consumer, "update", ".", "--dontmerge");
        Assert(File.ReadAllText(Path.Combine(consumer, "second", "a.txt")) == "local 2" && File.ReadAllText(Path.Combine(consumer, "second", "sub", "b.txt")) == "base b", "Independent consumer receives the second reviewed move result");
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
    private static void Save(bool success, string error) { if (validatedFixture) File.WriteAllText(Path.Combine(run, fullMode ? "partial-directory-repeated-full-results.json" : "partial-directory-repeated-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, fullMode, assertions, error, evidence = Evidence }), Utf8); }
    private static string Native(string root, params string[] args) { var result = Execute(cm, root, args); Evidence.Add(new { cwd = root, arguments = args, exitCode = result.Item1, output = result.Item2, error = result.Item3 }); if (result.Item1 != 0) throw new IOException("Native fixture command failed: " + String.Join(" ", args) + " " + result.Item2 + " " + result.Item3); return result.Item2; }
    private static Tuple<int, string, string> Execute(string file, string root, string[] args)
    {
        var start = new ProcessStartInfo(file, String.Join(" ", args.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8 };
        using (var process = Process.Start(start)) { var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); if (!process.WaitForExit(180000)) { process.Kill(); throw new TimeoutException("Native fixture command timed out: " + String.Join(" ", args)); } return Tuple.Create(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult()); }
    }
}