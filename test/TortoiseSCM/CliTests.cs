// GPL-2.0-or-later. Black-box tests of the actual WinExe and its UTF-8 pipes.
using System;
using System.Collections;
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
        // Leave headroom below legacy helper MAX_PATH for nested private merge artifacts.
        temporary = Path.Combine(Path.GetTempPath(), "TSCMCLI-" + Guid.NewGuid().ToString("N").Substring(0, 12) + " 中文 空格");
        try
        {
            if (args.Length != 1) throw new ArgumentException("Usage: CliTests.exe <TortoiseSCM.exe>");
            application = Path.GetFullPath(args[0]);
            fakeCm = Assembly.GetExecutingAssembly().Location;
            Directory.CreateDirectory(Path.Combine(temporary, ".plastic"));
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "CLI 中文\r\nguid\r\nStandard\r\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.selector"), "repository \"test@server:8087\"\r\n path \"/\"\r\n smartbranch \"/main\"\r\n");
            Check(Run(0, "--help")["output"].ToString().Contains("--commentsfile"), "Help is machine-readable without paths");
            Run(2, "--unknown");
            Run(2, "--command", "gluon", "--path", temporary);
            Run(2, "--command", "settings", "--path", temporary);
            Run(2, "--command", "cache-refresh", "--path", temporary);
            Run(2, "--command", "cache-refresh", "--path", temporary, "--path", Path.Combine(temporary, "file.txt"), "--yes");
            Run(2, "--command", "history-page", "--path", temporary, "--path", Path.Combine(temporary, "file.txt"));
            Run(2, "--command", "history-page", "--path", temporary, "--before", "-1");
            Run(2, "--command", "history-page", "--path", temporary, "--before", "9223372036854775808");
            Run(2, "--command", "history-page", "--path", temporary, "--limit", "0");
            Run(2, "--command", "history-page", "--path", temporary, "--limit", "101");
            Run(2, "--command", "history-page", "--path", temporary, "--limit", "1.5");
            Run(2, "--command", "history", "--path", temporary, "--before", "1");
            Run(2, "--command", "status", "--path", temporary, "--limit", "2");
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
            Run(2, "--command", "merge-preview", "--path", temporary);
            Run(2, "--command", "merge-start", "--path", temporary, "--changeset", "1");
            Run(2, "--command", "merge-resolve", "--path", temporary, "--changeset", "1", "--item", "/file.txt", "--yes");
            Run(2, "--command", "merge-resolve", "--path", temporary, "--changeset", "1", "--result", Path.Combine(temporary, "result.txt"), "--yes");
            Run(2, "--command", "merge-prepare", "--path", temporary, "--changeset", "1", "--item", "/file.txt");
            Run(2, "--command", "merge-conflict-tool", "--path", temporary, "--changeset", "1", "--item", "/file.txt");
            Run(2, "--command", "merge-status", "--path", temporary, "--changeset", "1");
            Run(2, "--command", "status", "--path", temporary, "--result", Path.Combine(temporary, "result.txt"));
            Run(2, "--command", "unlock", "--path", temporary, "--lock-id", Guid.NewGuid().ToString());
            Run(2, "--command", "unlock", "--path", temporary, "--yes");
            Run(2, "--command", "unlock", "--path", temporary, "--lock-id", "not-a-guid", "--yes");
            Run(2, "--command", "locks", "--path", temporary, "--lock-id", Guid.NewGuid().ToString());
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
            LockTests();
            RevisionTests(controlled);
            HistoryPageTests();
            UnknownMergeSessionTests();
            MergeWorkflowTests();
            Console.WriteLine("PASS: " + assertions + " CLI assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            string log = Path.Combine(temporary, ".plastic", "cli-cm-calls.log");
            if (File.Exists(log)) Console.Error.WriteLine("Last fake cm calls:\n" + String.Join("\n", File.ReadAllLines(log).Reverse().Take(12).Reverse()));
            return 1;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    private static Dictionary<string, object> Data(Dictionary<string, object> response) { return (Dictionary<string, object>)response["data"]; }

    private static void HistoryPageTests()
    {
        var first = Data(Run(0, "--command", "history-page", "--path", temporary, "--limit", "2", "--cm", fakeCm));
        var entries = ((IList)first["entries"]).Cast<Dictionary<string, object>>().ToList();
        Check(entries.Select(item => Convert.ToInt32(item["changeset"])).SequenceEqual(new[] { 3, 2 }), "Paged CLI history returns latest changesets in order");
        Check(Convert.ToBoolean(first["hasMore"]) && Convert.ToInt32(first["nextBeforeChangeset"]) == 2 && Convert.ToInt32(first["scannedChangesets"]) == 2,
            "Paged CLI history exposes scan count and exclusive continuation cursor");
        var last = Data(Run(0, "--command", "history-page", "--path", temporary, "--before", "2", "--limit", "2", "--cm", fakeCm));
        Check(((IList)last["entries"]).Count == 2 && !Convert.ToBoolean(last["hasMore"]) && last["nextBeforeChangeset"] == null, "Final CLI history page has no continuation");
        var empty = Data(Run(0, "--command", "history-page", "--path", Path.Combine(temporary, "missing-scope"), "--limit", "2", "--cm", fakeCm));
        Check(((IList)empty["entries"]).Count == 0 && Convert.ToBoolean(empty["hasMore"]) && Convert.ToInt32(empty["nextBeforeChangeset"]) == 2,
            "Empty CLI path page clearly remains incomplete");
        var path = Data(Run(0, "--command", "history-page", "--path", Path.Combine(temporary, "history-folder", "file.txt"), "--limit", "2", "--cm", fakeCm));
        Check(path["scope"].ToString() == "/history-folder/file.txt" && ((IList)path["entries"]).Count == 2, "File path CLI page includes changeset publication events");
        var defaults = Data(Run(0, "--command", "history-page", "--path", temporary, "--cm", fakeCm));
        Check(((IList)defaults["entries"]).Count == 4 && !Convert.ToBoolean(defaults["hasMore"]), "Default CLI page uses bounded fifty-commit scan");
        var zero = Data(Run(0, "--command", "history-page", "--path", temporary, "--before", "0", "--cm", fakeCm));
        Check(((IList)zero["entries"]).Count == 0 && Convert.ToInt32(zero["scannedChangesets"]) == 0 && !Convert.ToBoolean(zero["hasMore"]), "Zero cursor terminates CLI history");
        var text = Invoke(new[] { "--cli", "--command", "history-page", "--path", Path.Combine(temporary, "missing-scope"), "--limit", "2", "--cm", fakeCm });
        Check(text.Item1 == 0 && text.Item2.Contains("Older history remains") && text.Item2.Contains("--before 2") && text.Item2.Contains("does not follow renamed"), "Text CLI reports continuation and path-history semantics");
        var invalid = Invoke(new[] { "--cli", "--command", "history-page", "--path", temporary, "--limit", "--json" });
        Check(invalid.Item1 == 2 && invalid.Item2.Length == 0 && invalid.Item3.Contains("--limit"), "Option-looking limit value cannot enable JSON mode");
    }

    private static void UnknownMergeSessionTests()
    {
        string progress = Path.Combine(temporary, ".plastic", "plastic.mergeprogress");
        string calls = Path.Combine(temporary, ".plastic", "cli-cm-calls.log");
        int before = File.ReadAllLines(calls).Count(line => line.StartsWith("[\"checkin\",", StringComparison.Ordinal));
        File.WriteAllText(progress, "native merge from another session");
        try
        {
            Run(2, "--command", "checkin", "--path", temporary, "--comment", "unknown native merge", "--yes", "--cm", fakeCm,
                "--settings-file", Path.Combine(temporary, "alternate-merge-settings.xml"));
            Check(File.ReadAllLines(calls).Count(line => line.StartsWith("[\"checkin\",", StringComparison.Ordinal)) == before,
                "Alternate settings cannot bypass unknown native merge checkin guard");
            Check(File.Exists(progress), "Rejected unknown merge checkin preserves native state");
        }
        finally { File.Delete(progress); }
    }
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

    private static void LockTests()
    {
        const string id = "77bdbba7-82e8-407b-8132-76d772be21c5";
        var locks = (IList)Data(Run(0, "--command", "locks", "--path", temporary, "--cm", fakeCm))["locks"];
        var own = (Dictionary<string, object>)locks[0];
        Check(locks.Count == 1 && own["lockId"].ToString() == id && own["workspace"].ToString() == "CLI 中文" && (bool)own["canUnlock"], "Structured lock list includes current ownership and Unicode workspace");
        Run(0, "--command", "unlock", "--path", temporary, "--lock-id", id, "--yes", "--cm", fakeCm);
        Check(File.ReadAllText(Path.Combine(temporary, "fake-unlock.log")).Contains(id), "Own lock unlock sends its explicit GUID");
        File.Delete(Path.Combine(temporary, "fake-unlock.log"));
        File.WriteAllText(Path.Combine(temporary, "fake-other-lock.marker"), "other owner");
        locks = (IList)Data(Run(0, "--command", "locks", "--path", temporary, "--cm", fakeCm))["locks"];
        Check(!(bool)((Dictionary<string, object>)locks[0])["canUnlock"], "Foreign lock is visible but cannot be unlocked");
        Run(1, "--command", "unlock", "--path", temporary, "--lock-id", id, "--yes", "--cm", fakeCm);
        Check(!File.Exists(Path.Combine(temporary, "fake-unlock.log")), "Foreign ownership refusal never launches unlock");
    }

    private static void MergeWorkflowTests()
    {
        File.Delete(Path.Combine(temporary, "fake-partial.marker"));
        File.WriteAllText(Path.Combine(temporary, "fake-clean.marker"), "clean");
        string file = Path.Combine(temporary, "merge-conflict.txt"), settings = Path.Combine(temporary, "merge-config", "settings.xml");
        File.WriteAllText(file, "merge local 中文\n", new UTF8Encoding(false));
        Run(0, "--command", "settings", "--settings-file", settings, "--yes", "--merge-tool", fakeCm, "--merge-args", "--tool-merge \"{base}\" \"{local}\" \"{remote}\" \"{merged}\"");
        Check(Data(Run(0, "--command", "merge-status", "--path", temporary, "--cm", fakeCm, "--settings-file", settings))["sessionId"] == null, "Merge status reports no active session without opening UI");
        Run(2, "--command", "merge-prepare", "--path", temporary, "--changeset", "2", "--item", "/merge-conflict.txt", "--yes", "--cm", fakeCm, "--settings-file", settings);
        var plan = (Dictionary<string, object>)Data(Run(0, "--command", "merge-preview", "--path", temporary, "--changeset", "2", "--cm", fakeCm, "--settings-file", settings))["plan"];
        Check(((IList)plan["fileConflicts"]).Count == 1 && Convert.ToInt64(plan["sourceChangeset"]) == 2 && Convert.ToInt64(plan["baseChangeset"]) == 0, "Merge preview returns structured contributors and file conflict");
        var started = Data(Run(0, "--command", "merge-start", "--path", temporary, "--changeset", "2", "--yes", "--cm", fakeCm, "--settings-file", settings));
        var resumed = Data(Run(0, "--command", "merge-status", "--path", temporary, "--cm", fakeCm, "--settings-file", settings));
        Check(started["sessionId"].ToString() == resumed["sessionId"].ToString(), "Merge session survives separate CLI processes");
        var files = Data(Run(0, "--command", "merge-prepare", "--path", temporary, "--changeset", "2", "--item", "/merge-conflict.txt", "--yes", "--cm", fakeCm, "--settings-file", settings));
        Check(File.ReadAllText((string)files["basePath"]) == "merge base 中文\n" && File.ReadAllText((string)files["localPath"]) == "merge local 中文\n" &&
            File.ReadAllText((string)files["remotePath"]) == "merge remote 中文\n", "Merge preparation downloads all three distinct versions");
        var tool = Data(Run(0, "--command", "merge-conflict-tool", "--path", temporary, "--changeset", "2", "--item", "/merge-conflict.txt", "--yes", "--cm", fakeCm, "--settings-file", settings));
        string result = (string)tool["resultPath"];
        Check(File.ReadAllText(result) == "merged 中文" && File.ReadAllText(file) == "merge local 中文\n", "Conflict tool creates separate result without applying it");
        Run(2, "--command", "merge-resolve", "--path", temporary, "--changeset", "3", "--item", "/merge-conflict.txt", "--result", result, "--yes", "--cm", fakeCm, "--settings-file", settings);
        File.WriteAllText(file, "later local edit");
        Run(2, "--command", "merge-resolve", "--path", temporary, "--changeset", "2", "--item", "/merge-conflict.txt", "--result", result, "--yes", "--cm", fakeCm, "--settings-file", settings);
        Check(File.ReadAllText(file) == "later local edit", "Stale merge resolution preserves later workspace edits");
        File.WriteAllText(file, "merge local 中文\n", new UTF8Encoding(false));
        Run(0, "--command", "merge-resolve", "--path", temporary, "--changeset", "2", "--item", "/merge-conflict.txt", "--result", result, "--yes", "--cm", fakeCm, "--settings-file", settings);
        Check(File.ReadAllText(file) == "merged 中文", "Explicit conflict resolution applies only the selected result");
        var resolved = (Dictionary<string, object>)Data(Run(0, "--command", "merge-status", "--path", temporary, "--cm", fakeCm, "--settings-file", settings))["plan"];
        Check((bool)((Dictionary<string, object>)((IList)resolved["fileConflicts"])[0])["resolved"], "Merge status persists resolved state");
        // Session input files are deliberately read-only; normalize only our fixture artifacts for cleanup.
        foreach (string artifact in Directory.GetFiles(Path.Combine(temporary, "merge-config"), "*", SearchOption.AllDirectories)) File.SetAttributes(artifact, FileAttributes.Normal);
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
        string metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        if (Directory.Exists(metadata)) File.AppendAllText(Path.Combine(metadata, "cli-cm-calls.log"), Json.Serialize(args) + Environment.NewLine);
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
        else if (args[0] == "find" && args.Any(arg => arg.Contains("order by changesetid desc limit")))
        {
            string query = args[2];
            var before = Regex.Match(query, @"changesetid < (\d+)");
            int limit = Int32.Parse(Regex.Match(query, @"limit (\d+)").Groups[1].Value);
            var ids = Enumerable.Range(0, 4).Reverse().Where(id => !before.Success || id < Int64.Parse(before.Groups[1].Value)).Take(limit);
            Console.WriteLine(new XElement("PLASTICQUERY", ids.Select(id => new XElement("CHANGESET", new XElement("CHANGESETID", id), new XElement("COMMENT", "Published 中文 " + id), new XElement("BRANCH", "/main")))));
        }
        else if (args[0] == "find")
            Console.WriteLine(new XElement("PLASTICQUERY", args.Any(arg => arg.Contains("changesetid = 999")) ? null : new XElement("CHANGESET", new XElement("CHANGESETID", "1"),
                new XElement("DATE", "2026-09-25T00:00:00Z"), new XElement("OWNER", "Test"), new XElement("BRANCH", "/main"), new XElement("COMMENT", "Changeset 中文"), new XElement("REPOSITORY", "test"))).ToString());
        else if (args[0] == "showselector") Console.WriteLine("repository \"test@server:8087\"\n path \"/\"\n smartbranch \"/main\"");
        else if (args[0] == "lock")
        {
            if (args[1] == "list")
            {
                if (args.Contains("--onlycurrentuser") && File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-other-lock.marker"))) return 0;
                Console.WriteLine("TSLOCK|test|42|77bdbba7-82e8-407b-8132-76d772be21c5|2026-09-25|/main|1|/main|1|Locked|fixture-owner|CLI 中文|/folder/中文 file.txt|END");
            }
            else if (args[1] == "unlock") File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "fake-unlock.log"), String.Join("|", args));
            return 0;
        }
        else if (args[0] == "merge")
        {
            if (args.Contains("--merge"))
            {
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".plastic", "plastic.mergeprogress"), "fake native merge in progress");
                if (args.Contains("--keepdestination")) File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".plastic", "fake-merge-resolved.marker"), "resolved");
                Console.WriteLine("Fake native merge applied"); return 0;
            }
            Console.WriteLine("CONTRIBUTOR|SRC|2|cs:2@test@server:8087|source\nCONTRIBUTOR|DST|1|cs:1@test@server:8087|local\nCONTRIBUTOR|BASE|0|cs:0@test@server:8087|base");
            if (!File.Exists(Path.Combine(Environment.CurrentDirectory, ".plastic", "fake-merge-resolved.marker"))) Console.WriteLine("FILE_CONFLICT|/merge-conflict.txt|0|2|1|42");
        }
        else if (args[0] == "ls") Console.WriteLine(new XElement("LsResults", new XElement("LsItems", args[1] == "/missing.txt" ? null : new XElement("LsItem",
            new XElement("Name", Path.GetFileName(args[1])), new XElement("CurrentPath", args[1]), new XElement("ItemId", "42"), new XElement("Type", "txt")))).ToString());
        else if (args[0] == "diff") Console.WriteLine("C|\"/history-folder/file.txt\"|F|\"\"|\"\"");
        else if (args[0] == "fileinfo")
            Console.WriteLine("<FileInfos><FileInfo><RevisionChangeset>1</RevisionChangeset><Type>txt</Type></FileInfo></FileInfos>");
        else if (args[0] == "cat")
            File.WriteAllText(args.Single(arg => arg.StartsWith("--file=")).Substring(7), args[1].StartsWith("serverpath:") ?
                (args[1].Contains("/merge-conflict.txt#") ? (args[1].Contains("#cs:0@") ? "merge base 中文\n" : args[1].Contains("#cs:1@") ? "merge local 中文\n" : "merge remote 中文\n") :
                    (args[1].Contains("#cs:1@") ? "historical one 中文\n" : "historical two 中文\n")) : "before 中文\n", new UTF8Encoding(false));
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
