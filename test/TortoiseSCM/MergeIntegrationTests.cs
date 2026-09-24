// GPL-2.0-or-later. Real server merge workflow through the public CLI.
// Operates only on a fresh fixture created by New-TestWorkspace.ps1.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;

internal static class MergeIntegrationTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 32 * 1024 * 1024 };
    private static readonly List<object> Events = new List<object>();
    private static string executable, runDirectory, cm, repository, targetBranch;
    private static int assertions;

    private static void Assert(bool condition, string description)
    {
        assertions++; Events.Add(new { assertion = description, success = condition });
        if (!condition) throw new InvalidOperationException(description);
        Console.WriteLine("PASS: " + description);
    }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2 || args.Length > 3) throw new ArgumentException("Usage: MergeIntegrationTests.exe <TortoiseSCM.exe> <manifest.json> [cm.exe]");
            executable = Path.GetFullPath(args[0]);
            cm = args.Length == 3 ? Path.GetFullPath(args[2]) : @"D:\Program Files\PlasticSCM5\client\cm.exe";
            var manifest = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[1], Encoding.UTF8));
            runDirectory = (string)manifest["runDirectory"]; repository = (string)manifest["repository"]; targetBranch = (string)manifest["branch"];
            string target = (string)manifest["producer"], source = (string)manifest["consumer"];
            Assert(targetBranch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Merge fixture selects an isolated test branch");
            foreach (string workspace in new[] { target, source })
            {
                Assert(Path.GetFullPath(workspace).StartsWith(Path.GetFullPath(runDirectory).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), "Workspace remains inside its fixture directory");
                Assert(File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")).Contains(targetBranch), "Workspace selector belongs to fixture branch");
                Assert(!Entries(Call("status", workspace)).Any(), "Merge fixture begins clean");
            }
            ContentConflict(target, source);
            Save(true, null); Console.WriteLine("PASS: " + assertions + " live merge CLI assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }

    private static void ContentConflict(string target, string source)
    {
        const string filename = "合并 conflict.txt", baseText = "header\nbase value\nfooter\n", localText = "header\nlocal 中文 value\nfooter\n",
            remoteText = "header\nremote 中文 value\nfooter\n", mergedText = "header\nlocal + remote 中文 resolved\nfooter\n";
        string file = Path.Combine(target, filename), sourceFile = Path.Combine(source, filename);
        WriteNew(file, baseText);
        Call("add", file, "--yes");
        long baseCs = CreatedChangeset(Call("checkin", file, "--yes", "--comment", "merge integration common base"));
        string sourceBranch = targetBranch + "/merge-source";
        Native(runDirectory, "branch", "create", "br:" + sourceBranch + "@" + repository, "--changeset=cs:" + baseCs + "@" + repository, "-c=TortoiseSCM merge integration source");
        Native(runDirectory, "switch", "br:" + sourceBranch + "@" + repository, "--workspace=" + source);
        Assert(File.ReadAllText(sourceFile, Encoding.UTF8) == baseText, "Source branch starts with the common base bytes");
        File.WriteAllText(sourceFile, remoteText, new UTF8Encoding(false));
        long remoteCs = CreatedChangeset(Call("checkin", sourceFile, "--yes", "--comment", "merge integration remote content"));
        File.WriteAllText(file, localText, new UTF8Encoding(false));
        long localCs = CreatedChangeset(Call("checkin", file, "--yes", "--comment", "merge integration local content"));
        Assert(!Entries(Call("status", target)).Any(), "Destination begins clean before merge preview");
        string selector = File.ReadAllText(Path.Combine(target, ".plastic", "plastic.selector"));
        var plan = (Dictionary<string, object>)Data(Call("merge-preview", target, "--changeset", remoteCs.ToString()))["plan"];
        var conflicts = (IList)plan["fileConflicts"];
        Assert(conflicts.Count == 1 && ((Dictionary<string, object>)conflicts[0])["item"].ToString() == "/" + filename, "Preview identifies the real Unicode content conflict");
        Assert(Convert.ToInt64(plan["sourceChangeset"]) == remoteCs && Convert.ToInt64(plan["destinationChangeset"]) == localCs, "Preview identifies source and destination changesets");
        Assert(File.ReadAllText(file, Encoding.UTF8) == localText && !Entries(Call("status", target)).Any(), "Preview leaves working bytes and pending state unchanged");
        var noSession = Data(Call("merge-status", target));
        Assert(noSession["sessionId"] == null, "Preview does not create a merge session");
        Invoke("merge-start", target, 2, "--changeset", remoteCs.ToString());
        var started = Data(Call("merge-start", target, "--changeset", remoteCs.ToString(), "--yes"));
        Assert(!String.IsNullOrEmpty(started["sessionId"].ToString()), "Merge start creates a persistent session");
        var resumed = Data(Call("merge-status", target));
        Assert(resumed["sessionId"].ToString() == started["sessionId"].ToString(), "A separate CLI process resumes the same merge session");
        var files = Data(Call("merge-prepare", target, "--changeset", remoteCs.ToString(), "--item", "/" + filename, "--yes"));
        Assert(File.ReadAllText((string)files["basePath"], Encoding.UTF8) == baseText && File.ReadAllText((string)files["localPath"], Encoding.UTF8) == localText &&
            File.ReadAllText((string)files["remotePath"], Encoding.UTF8) == remoteText, "Prepared three-way files contain exact base, local and remote bytes");
        Invoke("checkin", target, 2, "--yes", "--comment", "must refuse unresolved merge");
        Assert(File.ReadAllText(file, Encoding.UTF8) == localText, "Unresolved checkin is refused without changing local conflict bytes");
        string result = (string)files["resultPath"];
        File.WriteAllText(result, mergedText, new UTF8Encoding(false));
        Call("merge-resolve", target, "--changeset", remoteCs.ToString(), "--item", "/" + filename, "--result", result, "--yes");
        Assert(File.ReadAllText(file, Encoding.UTF8) == mergedText, "Explicit resolution applies the reviewed merged bytes");
        Assert(File.ReadAllText(Path.Combine(target, ".plastic", "plastic.selector")) == selector, "Merge preserves the destination branch selector");
        Assert(Entries(Call("status", target)).Any(), "Resolved merge stays pending before explicit checkin");
        var resolvedPlan = (Dictionary<string, object>)Data(Call("merge-status", target))["plan"];
        Assert(((IList)resolvedPlan["fileConflicts"]).Cast<Dictionary<string, object>>().All(conflict => (bool)conflict["resolved"]), "Merge session records every content conflict as resolved");
        long mergedCs = CreatedChangeset(Call("checkin", target, "--yes", "--comment", "publish reviewed TortoiseSCM merge result"));
        Assert(mergedCs > localCs && !Entries(Call("status", target)).Any(), "Explicit checkin publishes a new clean merged changeset");
        Assert(Data(Call("merge-status", target))["sessionId"] == null, "Successful merge checkin clears the active session");
        string mergeLinks = Native(target, "find", "merge", "where dstchangeset = " + mergedCs, "--xml", "--nototal");
        var links = XDocument.Parse(mergeLinks).Descendants("MERGE");
        Assert(links.Any(link => (string)link.Element("SRCCHANGESET") == remoteCs.ToString() && (string)link.Element("DSTCHANGESET") == mergedCs.ToString() &&
            (string)link.Element("TYPE") == "merge"), "Server records the native source-to-destination merge link");
        Native(runDirectory, "switch", "br:" + targetBranch + "@" + repository, "--workspace=" + source);
        Call("update", source, "--yes");
        Assert(File.ReadAllText(sourceFile, Encoding.UTF8) == mergedText && !Entries(Call("status", source)).Any(), "Independent consumer receives exactly the committed merged bytes");
        Events.Add(new { sourceBranch = sourceBranch, commonBase = baseCs, sourceChangeset = remoteCs, destinationChangeset = localCs, mergedChangeset = mergedCs });
    }

    private static Dictionary<string, object> Data(Dictionary<string, object> response) { return (Dictionary<string, object>)response["data"]; }

    private static void WriteNew(string path, string text)
    {
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write(text);
    }

    private static IEnumerable<Dictionary<string, object>> Entries(Dictionary<string, object> response)
    { return ((IList)((Dictionary<string, object>)response["data"])["entries"]).Cast<Dictionary<string, object>>(); }

    private static Dictionary<string, object> Call(string command, string workspace, params string[] extra)
    { return Invoke(command, workspace, 0, extra); }

    private static Dictionary<string, object> Invoke(string command, string workspace, int expectedExit, params string[] extra)
    {
        var arguments = new List<string> { "--cli", "--json", "--command", command, "--path", workspace, "--cm", cm,
            "--settings-file", Path.Combine(runDirectory, "merge-settings.xml") };
        arguments.AddRange(extra);
        var result = Run(executable, Path.GetDirectoryName(executable), arguments);
        Events.Add(new { command = command, workspace = workspace, arguments = extra, exitCode = result.Item1, output = result.Item2, error = result.Item3 });
        var response = Json.Deserialize<Dictionary<string, object>>(result.Item2);
        if (result.Item1 != expectedExit || Convert.ToInt32(response["exitCode"]) != expectedExit || (bool)response["success"] != (expectedExit == 0))
            throw new InvalidOperationException(command + " returned " + result.Item1 + " instead of " + expectedExit + ": " + result.Item2 + "\n" + result.Item3);
        return response;
    }

    private static string Native(string workspace, params string[] arguments)
    {
        var result = Run(cm, workspace, arguments);
        Events.Add(new { native = arguments, workspace = workspace, exitCode = result.Item1, output = result.Item2, error = result.Item3 });
        if (result.Item1 != 0) throw new InvalidOperationException("Native fixture operation failed: " + String.Join(" ", arguments) + "\n" + result.Item2 + "\n" + result.Item3);
        return result.Item2;
    }

    private static Tuple<int, string, string> Run(string file, string cwd, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(file, String.Join(" ", arguments.Select(Quote))) { WorkingDirectory = cwd, UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(info))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("Merge integration subprocess timed out: " + info.Arguments); }
            if (!Task.WaitAll(new Task[] { output, error }, 5000)) throw new TimeoutException("Merge integration output streams did not close.");
            return Tuple.Create(process.ExitCode, output.Result.TrimStart('\uFEFF'), error.Result);
        }
    }

    private static long CreatedChangeset(Dictionary<string, object> response)
    {
        var match = Regex.Match(response["output"].ToString(), @"cs:(\d+)@");
        if (!match.Success) throw new InvalidOperationException("Checkin did not report a created changeset: " + response["output"]);
        return Int64.Parse(match.Groups[1].Value);
    }

    private static string Quote(string argument)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char value in argument)
        {
            if (value == '\\') { slashes++; continue; }
            result.Append('\\', value == '"' ? slashes * 2 + 1 : slashes); result.Append(value); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Save(bool success, string error)
    {
        if (runDirectory == null) return;
        File.WriteAllText(Path.Combine(runDirectory, "merge-integration-results.json"), Json.Serialize(new { success = success, assertions = assertions, error = error, events = Events }), new UTF8Encoding(false));
    }
}
