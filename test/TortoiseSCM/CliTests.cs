// GPL-2.0-or-later. Black-box tests of the actual WinExe and its UTF-8 pipes.
using System;
using System.Collections;
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

internal static class CliTests
{
    private static int assertions;
    private static string application;
    private static string fakeCm;
    private static string temporary;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

    private static int Main(string[] args)
    {
        // The test executable doubles as a cm substitute; this exercises real process
        // transport and exact argument parsing without requiring a server or UI.
        if (args.Length > 0 && !args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return FakeCm(args);
        temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-CLI-" + Guid.NewGuid().ToString("N") + " 中文 空格");
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: CliTests.exe <TortoiseSCM.exe>");
            application = Path.GetFullPath(args[0]);
            fakeCm = Assembly.GetExecutingAssembly().Location;
            Directory.CreateDirectory(Path.Combine(temporary, ".plastic"));
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "CLI 中文\r\nguid\r\nStandard\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.selector"), "repository \"test@server:8087\"\n path \"/\"\n smartbranch \"/main\"\n");
            Check(Run(0, "--help")["output"].ToString().Contains("--commentsfile"), "Help is machine-readable without paths");
            Run(2, "--unknown");
            Run(2, "--command", "gluon", "--path", temporary);
            Run(2, "--command", "settings", "--path", temporary);
            Run(2, "--command", "changeset", "--path", temporary);
            Run(2, "--command", "changeset", "--path", temporary, "--changeset", "-1");
            Run(2, "--command", "changeset", "--path", temporary, "--changeset", "9223372036854775808");
            Run(2, "--command", "rollback", "--path", temporary, "--changeset", "1");
            Run(2, "--command", "switch", "--path", temporary, "--changeset", "1");
            Run(2, "--command", "status", "--path", temporary, "--changeset", "1");
            Run(2, "--command", "settings", "--diff-tool", fakeCm);
            Run(2, "--command", "status", "--path", temporary, "--external");
            Run(2, "--command", "diff", "--path", temporary, "--path", Path.Combine(temporary, "file.txt"), "--external");
            Run(2, "--command", "merge", "--yes");
            Run(2, "--command", "remove", "--path", temporary);
            Run(2, "--command", "ignore", "--path", temporary);
            Run(2, "--command", "move", "--path", temporary, "--yes");
            Run(2, "--command", "status", "--path", temporary, "--destination", temporary);
            Run(2, "--command", "export", "--path", temporary, "--item", "/file.txt", "--changeset", "1", "--output", Path.Combine(temporary, "export.txt"));
            Run(2, "--command", "export", "--path", temporary, "--changeset", "1", "--output", Path.Combine(temporary, "export.txt"), "--yes");
            Run(2, "--command", "export", "--path", temporary, "--item", "/file.txt", "--changeset", "1", "--yes");
            Run(2, "--command", "diff-history", "--path", temporary, "--item", "/file.txt", "--from", "1");
            Run(2, "--command", "diff-history", "--path", temporary, "--item", "/file.txt", "--from", "-1", "--to", "2");
            Run(2, "--command", "status", "--path", temporary, "--overwrite");
            Run(2, "--command", "status", "--path", temporary, "--from", "1");
            Run(2, "--command", "status", "--path", temporary, "--base", temporary);
            Run(2, "--path", "relative.txt");
            Run(2, "--path", "C:relative.txt");
            Run(2, "--path", "\\relative.txt");
            Run(2, "--path");
            Run(2, "--timeout", "0", "--path", temporary);
            Run(2, "--timeout", "NaN", "--path", temporary);
            Run(2, "--path", temporary, "--command", "add");
            Run(2, "--command", "checkin", "--yes");
            Run(2, "--path", temporary, "--command", "checkin", "--yes");
            Run(2, "--path", temporary, "--recursive");
            Run(2, "--path", temporary, "--command", "status", "--comment", "comment");
            Run(2, "--path", temporary, "--path", Path.GetTempPath());
            Run(1, "--command", "workspace", "--path", Path.GetTempPath());
            var workspace = Data(Run(0, "--command", "workspace", "--path", temporary, "--cm", fakeCm));
            Check(((Dictionary<string, object>)workspace["workspace"])["name"].ToString() == "CLI 中文", "Workspace Unicode round trip");
            var status = Run(0, "--path", temporary, "--path", temporary, "--path", Path.Combine(temporary, "file.txt"), "--cm", fakeCm);
            var entries = (IList)Data(status)["entries"];
            Check(entries.Count == 1, "Overlapping status scopes are deduplicated");
            Check(((Dictionary<string, object>)entries[0])["path"].ToString().EndsWith("中文 space & file.txt"), "Status Unicode and shell metacharacters round trip");
            Check(((IList)Data(Run(0, "--path", Path.Combine(temporary, "file.txt"), "--cm", fakeCm))["entries"]).Count == 0,
                "Scoped status filters backend entries with current paths outside the selection");
            Run(1, "--path", Path.Combine(temporary, "fail.txt"), "--cm", fakeCm);
            Run(1, "--path", temporary, "--cm", Path.Combine(temporary, "missing.exe"));
            Run(124, "--path", Path.Combine(temporary, "timeout.txt"), "--cm", fakeCm, "--timeout", "1");
            string controlled = Path.Combine(temporary, "controlled.txt");
            File.WriteAllText(controlled, "after 中文\n", new UTF8Encoding(false));
            var history = (IList)Data(Run(0, "--command", "history", "--path", controlled, "--cm", fakeCm))["entries"];
            Check(history.Count == 1 && ((Dictionary<string, object>)history[0])["comment"].ToString() == "History 中文", "Structured history Unicode preserved");
            var diffs = (IList)Data(Run(0, "--command", "diff", "--path", controlled, "--cm", fakeCm))["diffs"];
            var diff = (Dictionary<string, object>)diffs[0];
            Check(Convert.ToBoolean(diff["hasChanges"]) && diff["diffText"].ToString().Contains("+after 中文"), "Diff executes headlessly and returns a textual patch");
            string comment = "quoted \" 中文 & | % !\r\nnext line";
            var checkin = Run(0, "--command", "checkin", "--yes", "--path", Path.Combine(temporary, "file.txt"), "--comment", comment, "--cm", fakeCm);
            Check(checkin["output"].ToString().Contains(comment), "Multiline quoted Unicode checkin comment preserved");
            string commentFile = Path.Combine(temporary, "comment.txt");
            File.WriteAllText(commentFile, comment, new UTF8Encoding(false));
            Check(Run(0, "--command", "checkin", "--yes", "--path", Path.Combine(temporary, "file.txt"), "--commentsfile", commentFile, "--cm", fakeCm)["output"].ToString().Contains(comment), "UTF-8 comments file preserved");
            Run(2, "--command", "checkin", "--yes", "--path", temporary, "--comment", comment, "--commentsfile", commentFile);
            foreach (string command in new[] { "add", "checkout", "undo" })
                Run(0, "--command", command, "--yes", "--path", Path.Combine(temporary, "file.txt"), "--cm", fakeCm);
            Run(2, "--command", "update", "--yes", "--path", Path.Combine(temporary, "file.txt"), "--cm", fakeCm);
            Run(2, "--command", "update", "--yes", "--path", Path.Combine(temporary, "file.txt"), "--path", Path.Combine(temporary, "other.txt"), "--cm", fakeCm);
            Check(!File.Exists(Path.Combine(temporary, "fake-update.log")), "Rejected standard updates never reach cm update");
            Run(0, "--command", "update", "--yes", "--path", temporary, "--cm", fakeCm);
            Check(File.ReadAllLines(Path.Combine(temporary, "fake-update.log")).Length == 1, "Explicit standard root update executes once");
            File.WriteAllText(Path.Combine(temporary, "fake-partial.marker"), "partial");
            var partial = Run(0, "--command", "update", "--yes", "--path", Path.Combine(temporary, "file.txt"), "--cm", fakeCm);
            Check(partial["output"].ToString().StartsWith("partial\nupdate\n"), "Partial file update preserves its explicit scope");
            TextModeTests();
            ToolTests(controlled);
            HistoricalFileTests();
            RevisionTests(controlled);
            Console.WriteLine("PASS: " + assertions + " CLI assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static Dictionary<string, object> Data(Dictionary<string, object> response) { return (Dictionary<string, object>)response["data"]; }
    private static void Check(bool condition, string description) { assertions++; if (!condition) throw new Exception(description); }

    private static Dictionary<string, object> Run(int expectedExit, params string[] arguments)
    {
        var result = Invoke(new[] { "--cli", "--json" }.Concat(arguments));
        Check(result.Item3.Length == 0, "JSON mode leaves stderr empty: " + result.Item3);
        Check(result.Item2.StartsWith("{"), "Stdout is BOM-free JSON: " + result.Item2);
        var parsed = Json.Deserialize<Dictionary<string, object>>(result.Item2);
        Check(result.Item1 == expectedExit, "Expected exit " + expectedExit + ", got " + result.Item1 + ": " + result.Item2);
        Check(Convert.ToInt32(parsed["schemaVersion"]) == 1 && Convert.ToInt32(parsed["exitCode"]) == expectedExit && Convert.ToBoolean(parsed["success"]) == (expectedExit == 0), "JSON and process exit codes agree");
        return parsed;
    }

    private static void TextModeTests()
    {
        var help = Invoke(new[] { "--cli", "--help" });
        Check(help.Item1 == 0 && help.Item2.Contains("--commentsfile") && help.Item3.Length == 0, "Text help uses stdout without UI");
        var invalid = Invoke(new[] { "--cli", "--unknown" });
        Check(invalid.Item1 == 2 && invalid.Item2.Length == 0 && invalid.Item3.Contains("Unknown CLI option"), "Text errors use stderr without UI");
        var workspace = Invoke(new[] { "--cli", "--command", "workspace", "--path", temporary, "--cm", fakeCm });
        Check(workspace.Item1 == 0 && workspace.Item2.Trim() == temporary && workspace.Item3.Length == 0, "Text mode preserves UTF-8 path");
        var comment = Invoke(new[] { "--cli", "--command", "checkin", "--yes", "--path", Path.Combine(temporary, "file.txt"), "--cm", fakeCm, "--comment", "--json" });
        Check(comment.Item1 == 0 && comment.Item2.Contains("-c=--json") && !comment.Item2.StartsWith("{"), "Option-looking comment cannot enable JSON mode");
    }

    private static void ToolTests(string controlled)
    {
        string settings = Path.Combine(temporary, "isolated-settings.xml");
        var defaults = Data(Run(0, "--command", "settings", "--settings-file", settings));
        Check(((Dictionary<string, object>)defaults["settings"])["settingsFile"].ToString() == settings, "Settings get uses isolated path");
        Check(!File.Exists(settings), "Settings get does not create a file");
        string diffArgs = "--tool-diff \"{base}\" \"{local}\" \"literal & | % ! 中文\"";
        string mergeArgs = "--tool-merge \"{base}\" \"{local}\" \"{remote}\" \"{merged}\"";
        Run(0, "--command", "settings", "--settings-file", settings, "--yes", "--diff-tool", fakeCm,
            "--diff-args", diffArgs, "--merge-tool", fakeCm, "--merge-args", mergeArgs);
        var saved = (Dictionary<string, object>)Data(Run(0, "--command", "settings", "--settings-file", settings))["settings"];
        Check(saved["diffTool"].ToString() == fakeCm && saved["diffArgs"].ToString() == diffArgs && saved["mergeArgs"].ToString() == mergeArgs, "External tool settings round trip");
        string previous = File.ReadAllText(settings);
        Run(2, "--command", "settings", "--settings-file", settings, "--yes", "--diff-args", "{unknown}");
        Check(File.ReadAllText(settings) == previous, "Invalid settings leave saved configuration intact");
        var diff = Run(0, "--command", "diff", "--external", "--path", controlled, "--cm", fakeCm, "--settings-file", settings);
        Check(Convert.ToBoolean(Data(diff)["external"]), "External diff mode reported");
        Check(diff["output"].ToString().Contains("literal & | % ! 中文"), "External diff argv preserves metacharacters without a shell");
        string basePath = Path.Combine(temporary, "base.txt"), localPath = Path.Combine(temporary, "local.txt"), remotePath = Path.Combine(temporary, "remote.txt"), mergedPath = Path.Combine(temporary, "merged 中文.txt");
        File.WriteAllText(basePath, "base"); File.WriteAllText(localPath, "local"); File.WriteAllText(remotePath, "remote");
        var merge = Run(0, "--command", "merge", "--yes", "--base", basePath, "--local", localPath, "--remote", remotePath, "--output", mergedPath, "--settings-file", settings);
        Check(Data(merge)["outputPath"].ToString() == mergedPath && File.ReadAllText(mergedPath) == "merged 中文", "Explicit external merge produces selected output");
        Check(File.ReadAllText(basePath) == "base" && File.ReadAllText(localPath) == "local" && File.ReadAllText(remotePath) == "remote", "External merge preserves source inputs");
        Run(2, "--command", "merge", "--yes", "--base", basePath, "--local", localPath, "--remote", remotePath, "--output", basePath, "--settings-file", settings);
        Check(File.ReadAllText(basePath) == "base", "Merge output cannot overwrite an input");
        Run(2, "--command", "merge", "--base", basePath, "--local", localPath, "--remote", remotePath, "--output", mergedPath, "--settings-file", settings);
        Run(0, "--command", "settings", "--settings-file", settings, "--yes", "--diff-tool", "", "--merge-tool", "");
        var cleared = (Dictionary<string, object>)Data(Run(0, "--command", "settings", "--settings-file", settings))["settings"];
        Check(cleared["diffTool"].ToString() == "" && cleared["mergeTool"].ToString() == "", "Empty tool paths clear configuration");
        Run(1, "--command", "merge", "--yes", "--base", basePath, "--local", localPath, "--remote", remotePath, "--output", mergedPath, "--settings-file", settings);
        File.WriteAllText(settings, "<invalid-settings />");
        Run(1, "--command", "settings", "--settings-file", settings);
    }

    private static void RevisionTests(string controlled)
    {
        string partial = Path.Combine(temporary, "fake-partial.marker"), clean = Path.Combine(temporary, "fake-clean.marker");
        File.Delete(partial);
        string folder = Path.Combine(temporary, "history-folder"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(temporary, "fake-dirty-path.txt"), controlled);
        var rootHistory = (IList)Data(Run(0, "--command", "history", "--path", temporary, "--cm", fakeCm))["entries"];
        Check(rootHistory.Count == 1 && ((Dictionary<string, object>)rootHistory[0])["comment"].ToString() == "Changeset 中文", "Workspace history lists repository changesets");
        var folderHistory = (IList)Data(Run(0, "--command", "history", "--path", folder, "--cm", fakeCm))["entries"];
        Check(folderHistory.Count == 1, "Directory history includes descendant content edits");
        var details = Data(Run(0, "--command", "changeset", "--changeset", "1", "--path", temporary, "--cm", fakeCm));
        Check(((Dictionary<string, object>)details["changeset"])["comment"].ToString() == "Changeset 中文", "Changeset comment returned in detail");
        Check(((Dictionary<string, object>)((IList)details["files"])[0])["path"].ToString() == "/history-folder/file.txt", "Changeset detail contains changed file paths");
        Run(2, "--command", "changeset", "--changeset", "999", "--path", temporary, "--cm", fakeCm);
        Run(2, "--command", "rollback", "--changeset", "1", "--yes", "--path", controlled, "--cm", fakeCm);
        Run(2, "--command", "switch", "--changeset", "1", "--yes", "--path", temporary, "--cm", fakeCm);
        File.WriteAllText(clean, "clean");
        var noOpRollback = Run(0, "--command", "rollback", "--changeset", "1", "--yes", "--path", temporary, "--cm", fakeCm);
        Check(Data(noOpRollback)["operation"].ToString() == "restore-pending", "Root rollback dispatches separately from snapshot switch");
        Run(2, "--command", "switch", "--changeset", "1", "--yes", "--path", controlled, "--cm", fakeCm);
        var rollback = Run(0, "--command", "rollback", "--changeset", "1", "--yes", "--path", controlled, "--cm", fakeCm);
        Check(rollback["output"].ToString().Contains(controlled + "#cs:1") && Data(rollback)["operation"].ToString() == "restore-pending", "Rollback restores the selected path as pending changes");
        var switched = Run(0, "--command", "switch", "--changeset", "1", "--yes", "--path", temporary, "--cm", fakeCm);
        Check(switched["output"].ToString().StartsWith("switch\ncs:1\n") && Data(switched)["operation"].ToString() == "switch-workspace", "Standard switch selects the whole workspace revision");
        File.WriteAllText(partial, "partial");
        var partialSwitch = Run(0, "--command", "switch", "--changeset", "1", "--yes", "--path", temporary, "--cm", fakeCm);
        Check(partialSwitch["output"].ToString().StartsWith("partial\nupdate\n") && partialSwitch["output"].ToString().Contains("--changeset=1"), "Partial switch uses native loaded-scope changeset update");
    }

    private static void HistoricalFileTests()
    {
        string output = Path.Combine(temporary, "exported 中文.txt"), settings = Path.Combine(temporary, "history-tool.xml");
        Run(0, "--command", "export", "--path", temporary, "--item", "/deleted 中文.txt", "--changeset", "1", "--output", output, "--yes", "--cm", fakeCm);
        Check(File.ReadAllText(output, Encoding.UTF8) == "historical one 中文\n", "Historical export writes exact old content without requiring a working file");
        Run(2, "--command", "export", "--path", temporary, "--item", "/deleted 中文.txt", "--changeset", "2", "--output", output, "--yes", "--cm", fakeCm);
        Check(File.ReadAllText(output, Encoding.UTF8) == "historical one 中文\n", "Export overwrite refusal leaves destination untouched");
        Run(0, "--command", "export", "--path", temporary, "--item", "/deleted 中文.txt", "--changeset", "2", "--output", output, "--yes", "--overwrite", "--cm", fakeCm);
        Check(File.ReadAllText(output, Encoding.UTF8) == "historical two 中文\n", "Explicit export overwrite writes new historical bytes");
        var diff = Data(Run(0, "--command", "diff-history", "--path", temporary, "--item", "/deleted 中文.txt", "--from", "1", "--to", "2", "--cm", fakeCm));
        Check((bool)diff["hasChanges"] && diff["diffText"].ToString().Contains("historical two 中文"), "Historical comparison returns structured text diff");
        var same = Data(Run(0, "--command", "diff-history", "--path", temporary, "--item", "/deleted 中文.txt", "--from", "1", "--to", "1", "--cm", fakeCm));
        Check(!(bool)same["hasChanges"], "Equal historical endpoints return no changes");
        Run(2, "--command", "export", "--path", temporary, "--item", "/../outside", "--changeset", "1", "--output", output, "--yes", "--overwrite", "--cm", fakeCm);
        Run(2, "--command", "export", "--path", temporary, "--item", "/missing.txt", "--changeset", "1", "--output", output, "--yes", "--overwrite", "--cm", fakeCm);
        Check(File.ReadAllText(output, Encoding.UTF8) == "historical two 中文\n", "Missing historical source never truncates an overwrite destination");
        Run(0, "--command", "settings", "--settings-file", settings, "--yes", "--diff-tool", fakeCm, "--diff-args", "--tool-diff \"{base}\" \"{local}\"");
        var external = Data(Run(0, "--command", "diff-history", "--path", temporary, "--item", "/deleted 中文.txt", "--from", "1", "--to", "1", "--external", "--cm", fakeCm, "--settings-file", settings));
        Check((bool)external["external"], "External historical comparison supports identical endpoints");
    }

    private static Tuple<int, string, string> Invoke(IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo { FileName = application,
            Arguments = String.Join(" ", arguments.Select(Quote)),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false, true), StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(start))
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            var timer = Stopwatch.StartNew();
            while (!process.WaitForExit(50))
            {
                process.Refresh();
                IntPtr window = IntPtr.Zero;
                try { window = process.MainWindowHandle; } catch (InvalidOperationException) { if (!process.HasExited) throw; }
                if (window != IntPtr.Zero) { process.Kill(); throw new Exception("CLI opened a UI window: " + start.Arguments); }
                if (timer.Elapsed > TimeSpan.FromSeconds(20)) { process.Kill(); throw new Exception("CLI did not terminate: " + start.Arguments); }
            }
            Check(Task.WaitAll(new Task[] { output, error }, 5000), "CLI output pipes close");
            return Tuple.Create(process.ExitCode, output.Result, error.Result);
        }
    }

    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "--tool-diff")
        {
            if (!File.Exists(args[1]) || !File.Exists(args[2])) return 9;
            Console.WriteLine(String.Join("\n", args)); return 0;
        }
        if (args[0] == "--tool-merge")
        {
            if (!args.Skip(1).Take(3).All(File.Exists)) return 9;
            File.WriteAllText(args[4], "merged 中文", new UTF8Encoding(false)); return 0;
        }
        if (args.Any(arg => arg.EndsWith("timeout.txt"))) Thread.Sleep(30000);
        if (args.Any(arg => arg.EndsWith("fail.txt"))) { Console.Error.WriteLine("Deliberate 中文 failure"); return 7; }
        if (args[0] == "status")
        {
            Console.WriteLine(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-partial.marker")) ? "-1" : "1"))), new XElement("Changes", File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-clean.marker")) ? null : new XElement("Change",
                new XElement("Type", "PR"), new XElement("Path", File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-dirty-path.txt")) ? File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "fake-dirty-path.txt")) : Path.Combine(Environment.CurrentDirectory, "中文 space & file.txt")),
                new XElement("TypeVerbose", "Private"), new XElement("RevisionType", "enTextFile")))).ToString());
        }
        else if (args[0] == "history")
            Console.WriteLine(new XElement("RevisionHistoriesResult", new XElement("RevisionHistory", new XElement("ItemName", args[1]),
                new XElement("Revision", new XElement("ChangesetNumber", "1"), new XElement("Comment", "History 中文")))).ToString());
        else if (args[0] == "find")
            Console.WriteLine(new XElement("PLASTICQUERY", args.Any(arg => arg.Contains("changesetid = 999")) ? null : new XElement("CHANGESET", new XElement("CHANGESETID", "1"),
                new XElement("DATE", "2026-09-25T00:00:00Z"), new XElement("OWNER", "Test"), new XElement("BRANCH", "/main"), new XElement("COMMENT", "Changeset 中文"), new XElement("REPOSITORY", "test"))).ToString());
        else if (args[0] == "showselector") Console.WriteLine("repository \"test@server:8087\"\n path \"/\"\n smartbranch \"/main\"");
        else if (args[0] == "ls") Console.WriteLine(new XElement("LsResults", new XElement("LsItems", args[1] == "/missing.txt" ? null : new XElement("LsItem",
            new XElement("Name", Path.GetFileName(args[1])), new XElement("CurrentPath", args[1]), new XElement("Type", "txt")))).ToString());
        else if (args[0] == "diff") Console.WriteLine("C|\"/history-folder/file.txt\"|F|\"\"|\"\"");
        else if (args[0] == "fileinfo")
            Console.WriteLine("<FileInfos><FileInfo><RevisionChangeset>1</RevisionChangeset><Type>txt</Type></FileInfo></FileInfos>");
        else if (args[0] == "cat")
            File.WriteAllText(args.Single(arg => arg.StartsWith("--file=")).Substring(7), args[1].StartsWith("serverpath:") ?
                (args[1].Contains("#cs:1@") ? "historical one 中文\n" : "historical two 中文\n") : "before 中文\n", new UTF8Encoding(false));
        else
        {
            if (args[0] == "update" || (args[0] == "partial" && args[1] == "update"))
                File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "fake-update.log"), String.Join("|", args) + Environment.NewLine);
            Console.WriteLine(String.Join("\n", args));
        }
        return 0;
    }

    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\""); int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            quoted.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            quoted.Append(character); slashes = 0;
        }
        return quoted.Append('\\', slashes * 2).Append('"').ToString();
    }
}
