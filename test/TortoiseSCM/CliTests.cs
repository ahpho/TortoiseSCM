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
    private static string mergeSettingsDirectory;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

    private static int Main(string[] args)
    {
        // The test executable doubles as a cm substitute; this exercises real process
        // transport and exact argument parsing without requiring a server or UI.
        if (args.Length > 0 && !args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return FakeCm(args);
        // Leave headroom below legacy helper MAX_PATH for nested private merge artifacts.
        temporary = Path.Combine(Path.GetTempPath(), "TSCMCLI-" + Guid.NewGuid().ToString("N").Substring(0, 12) + " 中文 空格");
        mergeSettingsDirectory = temporary + "-settings";
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
            foreach (string action in new[] { "partial-conflict-prepare", "partial-conflict-tool", "partial-conflict-resolve" })
            {
                Run(2, "--command", action, "--path", temporary, "--item", "/file.txt");
                Run(2, "--command", action, "--path", temporary, "--yes");
                Run(2, "--command", action, "--path", temporary, "--item", "/file.txt", "--changeset", "1", "--yes");
            }
            Run(2, "--command", "partial-conflict-cancel", "--path", temporary);
            foreach (string operation in new[] { "partial-directory-prepare", "partial-directory-resolve", "partial-directory-cancel", "partial-directory-recover" })
                Run(2, "--command", operation, "--path", temporary);
            Run(2, "--command", "partial-directory-preview", "--path", temporary, "--path", Path.Combine(temporary, "file.txt"));
            Run(2, "--command", "partial-directory-preview", "--path", temporary, "--item", "/tree");
            Run(2, "--command", "partial-directory-prepare", "--path", temporary, "--yes");
            Run(2, "--command", "partial-directory-resolve", "--path", temporary, "--yes");
            Run(2, "--command", "partial-directory-resolve", "--path", temporary, "--resolution", "rename", "--yes");
            Run(2, "--command", "partial-directory-resolve", "--path", temporary, "--resolution", "take-incoming", "--rename", "new", "--yes");
            Run(2, "--command", "partial-directory-resolve", "--path", temporary, "--resolution", "take-incoming", "--item", "/tree", "--yes");
            Run(2, "--command", "partial-directory-status", "--path", temporary, "--changeset", "1");
            foreach (string operation in new[] { "partial-structure-prepare", "partial-structure-resolve", "partial-structure-cancel", "partial-structure-recover" })
                Run(2, "--command", operation, "--path", temporary);
            Run(2, "--command", "partial-structure-preview", "--path", temporary, "--item", "/file.txt");
            Run(2, "--command", "partial-structure-preview", "--path", temporary, "--path", Path.Combine(temporary, "file.txt"));
            Run(2, "--command", "partial-structure-prepare", "--path", temporary, "--yes");
            Run(2, "--command", "partial-structure-resolve", "--path", temporary, "--yes");
            Run(2, "--command", "partial-structure-resolve", "--path", temporary, "--resolution", "src", "--yes");
            Run(2, "--command", "partial-structure-resolve", "--path", temporary, "--resolution", "rename", "--yes");
            Run(2, "--command", "partial-structure-resolve", "--path", temporary, "--resolution", "keep-local", "--rename", "other.txt", "--yes");
            Run(2, "--command", "partial-structure-resolve", "--path", temporary, "--resolution", "take-incoming", "--conflict", "1", "--yes");
            Run(2, "--command", "partial-structure-resolve", "--path", temporary, "--resolution", "take-incoming", "--item", "/file.txt", "--yes");
            Run(2, "--command", "partial-structure-prepare", "--path", temporary, "--item", "/file.txt", "--changeset", "1", "--yes");
            Run(2, "--command", "partial-conflict-status", "--path", temporary, "--item", "/file.txt");
            Run(2, "--command", "partial-conflicts", "--path", temporary, "--path", Path.Combine(temporary, "file.txt"));
            Run(2, "--command", "partial-conflict-resolve", "--path", temporary, "--item", "/file.txt", "--yes");
            Run(2, "--command", "merge-directory-resolve", "--path", temporary, "--changeset", "1", "--conflict", "1", "--resolution", "src");
            Run(2, "--command", "merge-directory-resolve", "--path", temporary, "--changeset", "1", "--resolution", "src", "--yes");
            Run(2, "--command", "merge-directory-resolve", "--path", temporary, "--changeset", "1", "--conflict", "0", "--resolution", "src", "--yes");
            Run(2, "--command", "merge-directory-resolve", "--path", temporary, "--changeset", "1", "--conflict", "1", "--resolution", "invalid", "--yes");
            Run(2, "--command", "merge-directory-resolve", "--path", temporary, "--changeset", "1", "--conflict", "1", "--resolution", "rename", "--yes");
            Run(2, "--command", "merge-directory-resolve", "--path", temporary, "--changeset", "1", "--conflict", "1", "--resolution", "dst", "--rename", "other.txt", "--yes");
            Run(2, "--command", "merge-continue", "--path", temporary, "--changeset", "1");
            Run(2, "--command", "merge-directory-cancel", "--path", temporary);
            Run(2, "--command", "status", "--path", temporary, "--conflict", "1", "--resolution", "src");
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
            // Text transport is independent of remote Partial structure discovery.
            // Use the Standard fixture; real Partial publishing is covered separately.
            File.Delete(Path.Combine(temporary, "fake-partial.marker"));
            TextModeTests();
            File.WriteAllText(Path.Combine(temporary, "fake-partial.marker"), "partial");
            ToolTests(controlled);
            HistoricalFileTests();
            ChangesetComparisonTests();
            LockTests();
            RevisionTests(controlled);
            HistoryPageTests();
            BranchTests(controlled);
            BranchTreeTests();
            CreateBranchTests();
            ShelvesTests();
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
        finally
        {
            if (Directory.Exists(temporary)) Directory.Delete(temporary, true);
            if (Directory.Exists(mergeSettingsDirectory))
            {
                foreach (string file in Directory.GetFiles(mergeSettingsDirectory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(mergeSettingsDirectory, true);
            }
        }
    }

    private static Dictionary<string, object> Data(Dictionary<string, object> response) { return (Dictionary<string, object>)response["data"]; }

    private static void ShelvesTests()
    {
        string marker = Path.Combine(temporary, ".plastic", "fake-shelves.marker");
        string created = Path.Combine(temporary, ".plastic", "fake-shelve-comment.txt");
        string failure = Path.Combine(temporary, ".plastic", "fake-shelve-failure.txt");
        string clean = Path.Combine(temporary, "fake-clean.marker");
        string partial = Path.Combine(temporary, "fake-partial.marker");
        string ignored = Path.Combine(temporary, ".plastic", "fake-ignored.marker");
        string selected = Path.Combine(temporary, "shelve 中文 & selected.txt");
        bool wasClean = File.Exists(clean), wasPartial = File.Exists(partial), wasIgnored = File.Exists(ignored);
        try
        {
            File.Delete(clean); File.Delete(partial);
            File.WriteAllText(marker, "active"); File.WriteAllText(selected, "pending bytes");
            string[] read = { "--command", "shelves", "--path", temporary, "--cm", fakeCm };
            string[] detail = { "--command", "shelve-details", "--path", temporary, "--shelve", "5", "--cm", fakeCm };
            string[] create = { "--command", "shelve-create", "--path", selected, "--comment", "shelf 中文 & \"quote\"\r\nline", "--yes", "--cm", fakeCm };
            foreach (string[] command in new[] { read, detail, create })
                foreach (string[] extra in new[] { new[] { "--recursive" }, new[] { "--branch", "/main" }, new[] { "--changeset", "1" },
                    new[] { "--item", "/file.txt" }, new[] { "--external" }, new[] { "--before", "2" }, new[] { "--filter", "x" }, new[] { "--from", "1" } })
                    Run(2, command.Concat(extra).ToArray());
            Run(2, read.Concat(new[] { "--yes" }).ToArray());
            Run(2, read.Concat(new[] { "--shelve", "5" }).ToArray());
            Run(2, detail.Concat(new[] { "--path", selected }).ToArray());
            Run(2, "--command", "shelve-details", "--path", temporary);
            foreach (string id in new[] { "-1", "bad", "9223372036854775808" })
                Run(2, "--command", "shelve-details", "--path", temporary, "--shelve", id);
            Run(2, "--command", "status", "--path", temporary, "--shelve", "5");
            Run(2, "--command", "shelve-create", "--path", selected, "--comment", "x");
            Run(2, "--command", "shelve-create", "--path", selected, "--yes");
            Run(2, "--command", "shelve-create", "--path", selected, "--comment", "  ", "--yes");
            Run(2, "--command", "shelve-apply", "--path", temporary, "--shelve", "5");
            Run(2, "--command", "shelve-delete", "--path", temporary, "--shelve", "5");
            var shelves = (IList)Data(Run(0, read))["shelves"];
            var shelf = (Dictionary<string, object>)shelves[0];
            Check(shelves.Count == 1 && Convert.ToInt64(shelf["shelveId"]) == 5 && Convert.ToInt64(shelf["objectId"]) == 55 &&
                shelf["comment"].ToString() == "Saved 中文\nline" && shelf["repository"].ToString() == "test@server:8087", "Shelves metadata retains distinct ids, Unicode comments and repository");
            var files = (IList)Data(Run(0, detail))["files"];
            Check(files.Count == 1 && ((Dictionary<string, object>)files[0])["path"].ToString() == "/shelve 中文 & selected.txt", "Shelves details return structured changed file list");
            var saved = Data(Run(0, create));
            Check(Convert.ToBoolean(saved["localChangesPreserved"]) && ((IList)saved["paths"]).Count == 1 && File.ReadAllText(selected) == "pending bytes", "Shelves create reports explicit selection and preserves bytes");
            Check(File.ReadAllText(created) == "shelf 中文 & \"quote\"\r\nline", "Shelves create transports multiline quoted comment");
            File.Delete(created);
            string commentsFile = Path.Combine(temporary, "shelve-comment.txt"); File.WriteAllText(commentsFile, "file comment 中文", new UTF8Encoding(false));
            Run(0, "--command", "shelve-create", "--path", selected, "--commentsfile", commentsFile, "--yes", "--cm", fakeCm);
            Check(File.ReadAllText(created) == "file comment 中文", "Shelves create supports UTF-8 commentsfile");
            File.Delete(created);
            File.WriteAllText(partial, "partial");
            Run(0, create);
            string[] calls = File.ReadAllLines(Path.Combine(temporary, ".plastic", "cli-cm-calls.log"));
            Check(calls.Any(line => line.Contains("\"partial\",\"shelveset\",\"create\"") && line.Contains("--applychanged")), "Partial shelves create uses native partial operation");
            File.Delete(partial); File.Delete(created);
            File.WriteAllText(failure, "native");
            Run(1, create); Check(!File.Exists(created), "Native shelveset failure does not claim successful saved metadata");
            File.WriteAllText(failure, "malformed"); Run(1, read);
            File.WriteAllText(failure, "timeout"); Run(124, read.Concat(new[] { "--timeout", "1" }).ToArray());

            // Apply is deliberately restricted to a clean Standard workspace and
            // verifies the shelveset remains in this repository before mutation.
            File.Delete(failure); File.Delete(marker); File.WriteAllText(clean, "clean");
            var applied = Data(Run(0, "--command", "shelve-apply", "--path", temporary, "--shelve", "5", "--yes", "--cm", fakeCm));
            Check(applied["operation"].ToString() == "shelve-apply" &&
                File.ReadAllLines(Path.Combine(temporary, ".plastic", "cli-cm-calls.log")).Any(line => line.Contains("shelveset\",\"apply\",\"sh:5@test@server:8087")),
                "Shelveset apply uses the repository-qualified native command after clean preflight");
            File.WriteAllText(ignored, "ignored");
            Run(2, "--command", "shelve-apply", "--path", temporary, "--shelve", "5", "--yes", "--cm", fakeCm);
            File.Delete(ignored);
            File.WriteAllText(partial, "partial");
            Run(2, "--command", "shelve-apply", "--path", temporary, "--shelve", "5", "--yes", "--cm", fakeCm);
            File.Delete(partial);
            File.Delete(clean); File.WriteAllText(marker, "active");
            Run(2, "--command", "shelve-apply", "--path", temporary, "--shelve", "5", "--yes", "--cm", fakeCm);
            File.WriteAllText(clean, "clean");
            var deleted = Data(Run(0, "--command", "shelve-delete", "--path", temporary, "--shelve", "5", "--yes", "--cm", fakeCm));
            Check(deleted["operation"].ToString() == "shelve-delete" &&
                File.ReadAllLines(Path.Combine(temporary, ".plastic", "cli-cm-calls.log")).Any(line => line.Contains("shelveset\",\"delete\",\"sh:5@test@server:8087")),
                "Shelveset delete uses repository-qualified native command and verifies removal");
            File.Delete(Path.Combine(temporary, ".plastic", "fake-shelve-deleted.txt")); File.Delete(clean);
        }
        finally
        {
            File.Delete(marker); File.Delete(created); File.Delete(failure); File.Delete(selected); File.Delete(clean);
            File.Delete(Path.Combine(temporary, ".plastic", "fake-shelve-deleted.txt"));
            if (wasClean) File.WriteAllText(clean, "clean"); else File.Delete(clean);
            if (wasPartial) File.WriteAllText(partial, "partial"); else File.Delete(partial);
        }
    }

    private static void BranchTests(string controlled)
    {
        string partial = Path.Combine(temporary, "fake-partial.marker"), clean = Path.Combine(temporary, "fake-clean.marker");
        string switchedBranch = Path.Combine(temporary, ".plastic", "fake-branch.txt");
        string failure = Path.Combine(temporary, ".plastic", "fake-branch-failure.txt");
        string calls = Path.Combine(temporary, ".plastic", "cli-cm-calls.log");
        bool wasPartial = File.Exists(partial), wasClean = File.Exists(clean);
        try
        {
            File.Delete(partial);
            foreach (string command in new[] { "branches", "branch-head", "switch-branch" })
            {
                string[] action = new[] { "--command", command, "--path", temporary, "--cm", fakeCm }
                    .Concat(command == "branches" ? new string[0] : new[] { "--branch", "/main/feature 中文" })
                    .Concat(command == "switch-branch" ? new[] { "--yes" } : new string[0]).ToArray();
                foreach (string[] unrelated in new[] { new[] { "--changeset", "1" }, new[] { "--from", "1" }, new[] { "--item", "/file.txt" },
                    new[] { "--comment", "message" }, new[] { "--recursive" }, new[] { "--external" }, new[] { "--before", "1" }, new[] { "--limit", "1" },
                    new[] { "--path", controlled }, new[] { "--overwrite" }, new[] { "--destination", temporary } })
                    Run(2, action.Concat(unrelated).ToArray());
            }
            Run(2, "--command", "branches", "--path", temporary, "--branch", "/main");
            Run(2, "--command", "branches", "--path", temporary, "--yes");
            Run(2, "--command", "branch-head", "--path", temporary);
            Run(2, "--command", "branch-head", "--path", temporary, "--branch", "/main", "--yes");
            Run(2, "--command", "switch-branch", "--path", temporary, "--branch", "/main");
            Run(2, "--command", "switch-branch", "--path", temporary, "--yes");
            Run(2, "--command", "status", "--path", temporary, "--branch", "/main");
            Run(2, "--command", "branch-head", "--path", temporary, "--branch", "/main", "--branch", "/main");
            var optionValue = Invoke(new[] { "--cli", "--command", "branch-head", "--path", temporary, "--branch", "--json", "--cm", fakeCm });
            Check(optionValue.Item1 == 2 && optionValue.Item2.Length == 0 && optionValue.Item3.Length > 0,
                "An option-looking branch value does not enable JSON mode or open a GUI");
            foreach (string invalid in new[] { "", "main", "/main/../other", "/main@other@server", "/main\nother" })
                Run(2, "--command", "branch-head", "--path", temporary, "--branch", invalid, "--cm", fakeCm);
            var branches = (IList)Data(Run(0, "--command", "branches", "--path", temporary, "--cm", fakeCm))["branches"];
            Check(branches.Count == 2, "Branches returns native branch records");
            var main = branches.Cast<Dictionary<string, object>>().Single(branch => branch["name"].ToString() == "/main");
            var feature = branches.Cast<Dictionary<string, object>>().Single(branch => branch["name"].ToString() == "/main/feature 中文");
            Check((bool)main["isCurrent"] && !(bool)feature["isCurrent"], "Branch list identifies the current selector");
            Check(Convert.ToInt64(feature["headChangeset"]) == 42 && feature["parent"].ToString() == "/main" &&
                feature["comment"].ToString() == "Feature 中文", "Native branch head, parent and UTF-8 comments survive JSON transport");
            var head = Data(Run(0, "--command", "branch-head", "--path", temporary, "--branch", "/main/feature 中文", "--cm", fakeCm));
            Check(Convert.ToInt64(head["changeset"]) == 42, "Branch head resolves a fixed changeset for merge preview");
            var text = Invoke(new[] { "--cli", "--command", "branch-head", "--path", temporary, "--branch", "/main/feature 中文", "--cm", fakeCm });
            Check(text.Item1 == 0 && text.Item2.Trim() == "cs:42" && text.Item3.Length == 0, "Branch-head text output is a fixed revision");
            var listText = Invoke(new[] { "--cli", "--command", "branches", "--path", temporary, "--cm", fakeCm });
            Check(listText.Item1 == 0 && listText.Item2.Contains("* /main\tcs:1") && listText.Item2.Contains("/main/feature 中文\tcs:42"), "Branch text list includes current marker and head");
            Run(2, "--command", "branch-head", "--path", temporary, "--branch", "/main/missing", "--cm", fakeCm);
            File.WriteAllText(failure, "error");
            Run(1, "--command", "branches", "--path", temporary, "--cm", fakeCm);
            File.WriteAllText(failure, "malformed");
            Run(1, "--command", "branches", "--path", temporary, "--cm", fakeCm);
            File.WriteAllText(failure, "timeout");
            Run(124, "--command", "branches", "--path", temporary, "--cm", fakeCm, "--timeout", "1");
            File.Delete(failure);
            File.Delete(clean);
            int callStart = File.ReadAllLines(calls).Length;
            Run(2, "--command", "switch-branch", "--path", temporary, "--branch", "/main/feature 中文", "--yes", "--cm", fakeCm);
            Check(!File.ReadAllLines(calls).Skip(callStart).Any(line => line.StartsWith("[\"switch\"")), "Dirty branch switch never reaches native mutation");
            File.WriteAllText(clean, "clean");
            callStart = File.ReadAllLines(calls).Length;
            Run(2, "--command", "switch-branch", "--path", controlled, "--branch", "/main/feature 中文", "--yes", "--cm", fakeCm);
            File.WriteAllText(partial, "partial");
            Run(0, "--command", "branches", "--path", temporary, "--cm", fakeCm);
            Run(2, "--command", "switch-branch", "--path", temporary, "--branch", "/main/feature 中文", "--yes", "--cm", fakeCm);
            Check(!File.ReadAllLines(calls).Skip(callStart).Any(line => line.StartsWith("[\"switch\"")), "File scope and Partial branch switch rejection never mutate the workspace");
            File.Delete(partial);
            var switched = Run(0, "--command", "switch-branch", "--path", temporary, "--branch", "/main/feature 中文", "--yes", "--cm", fakeCm);
            Check(switched["output"].ToString().StartsWith("switch\nbr:/main/feature 中文@test@server:8087\n"), "Branch switch uses the repository-qualified branch selector");
            var workspace = (Dictionary<string, object>)Data(switched)["workspace"];
            Check(workspace["selector"].ToString().Contains("/main/feature 中文"), "Switch JSON reports the resulting workspace selector");
        }
        finally
        {
            File.Delete(switchedBranch); File.Delete(failure);
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.selector"), "repository \"test@server:8087\"\r\n path \"/\"\r\n smartbranch \"/main\"\r\n");
            if (wasPartial) File.WriteAllText(partial, "partial"); else File.Delete(partial);
            if (wasClean) File.WriteAllText(clean, "clean"); else File.Delete(clean);
        }
    }

    private static void BranchTreeTests()
    {
        string mode = Path.Combine(temporary, ".plastic", "fake-branch-tree.txt");
        string calls = Path.Combine(temporary, ".plastic", "cli-cm-calls.log");
        string[] action = { "--command", "branch-tree", "--path", temporary, "--cm", fakeCm };
        foreach (string[] unrelated in new[] { new[] { "--yes" }, new[] { "--branch", "/main" }, new[] { "--changeset", "1" }, new[] { "--from", "1" },
            new[] { "--item", "/file.txt" }, new[] { "--comment", "comment" }, new[] { "--recursive" }, new[] { "--external" }, new[] { "--before", "1" },
            new[] { "--limit", "1" }, new[] { "--overwrite" }, new[] { "--destination", temporary }, new[] { "--path", Path.Combine(temporary, "file.txt") } })
            Run(2, action.Concat(unrelated).ToArray());
        Run(2, "--command", "branch-tree");
        Run(2, action.Concat(new[] { "--filter" }).ToArray());
        Run(2, action.Concat(new[] { "--filter", "a", "--filter", "b" }).ToArray());
        foreach (string command in new[] { "branches", "status", "history-page" })
            Run(2, "--command", command, "--path", temporary, "--filter", "unused");
        try
        {
            File.WriteAllText(mode, "tree");
            int callStart = File.ReadAllLines(calls).Length;
            var tree = Data(Run(0, action));
            var nodes = ((IList)tree["nodes"]).Cast<Dictionary<string, object>>().ToArray();
            Check(tree["filter"] == null && nodes.Length == 4, "Unfiltered branch tree lists each native branch exactly once");
            var main = nodes.Single(node => node["name"].ToString() == "/main");
            var child = nodes.Single(node => node["name"].ToString() == "/main/feature 中文");
            var leaf = nodes.Single(node => node["name"].ToString() == "/leaf");
            var orphan = nodes.Single(node => node["name"].ToString() == "/orphan");
            Check(Array.IndexOf(nodes, main) < Array.IndexOf(nodes, child) && Array.IndexOf(nodes, child) < Array.IndexOf(nodes, leaf) &&
                Convert.ToInt32(leaf["depth"]) == 2 && leaf["parent"].ToString() == "/main/feature 中文", "Hierarchy uses native PARENT, not name segments, and emits parents first");
            Check(Convert.ToBoolean(main["isCurrent"]) && Convert.ToInt32(main["childCount"]) == 1 && Convert.ToInt32(child["childCount"]) == 1,
                "Branch tree preserves current state and direct child counts");
            Check(Convert.ToBoolean(orphan["parentMissing"]) && Convert.ToInt32(orphan["depth"]) == 0, "Missing native parent is flagged without inventing a parent node");
            var filtered = Data(Run(0, action.Concat(new[] { "--filter", "leaf 中文" }).ToArray()));
            var matches = ((IList)filtered["nodes"]).Cast<Dictionary<string, object>>().ToArray();
            Check(filtered["filter"].ToString() == "leaf 中文" && matches.Length == 3 &&
                !Convert.ToBoolean(matches[0]["isMatch"]) && !Convert.ToBoolean(matches[1]["isMatch"]) && Convert.ToBoolean(matches[2]["isMatch"]),
                "Unicode comment filtering keeps unmatched ancestors as context");
            Check(((IList)Data(Run(0, action.Concat(new[] { "--filter", "LEAF" }).ToArray()))["nodes"]).Count == 3, "Branch filtering ignores letter case");
            Check(((IList)Data(Run(0, action.Concat(new[] { "--filter", "does-not-exist" }).ToArray()))["nodes"]).Count == 0, "Unmatched branch filter returns an empty successful result");
            var text = Invoke(new[] { "--cli" }.Concat(action).Concat(new[] { "--filter", "leaf 中文" }));
            Check(text.Item1 == 0 && text.Item2.Contains("* /main") && text.Item2.Contains("[ancestor context]") && text.Item2.Contains("      /leaf") &&
                text.Item2.Contains("[matched]") && text.Item2.Contains("not commit or merge ancestry"), "Text hierarchy shows indentation, current marker, match context and relationship semantics");
            var missingText = Invoke(new[] { "--cli" }.Concat(action).Concat(new[] { "--filter", "orphan" }));
            Check(missingText.Item1 == 0 && missingText.Item2.Contains("[missing parent: /missing]"), "Text tree reports missing parent explicitly");
            var optionValue = Invoke(new[] { "--cli" }.Concat(action).Concat(new[] { "--filter", "--json" }));
            Check(optionValue.Item1 == 0 && optionValue.Item2.StartsWith("Branch parent hierarchy") && optionValue.Item2.Contains("No matching branches.") &&
                optionValue.Item3.Length == 0, "An option-looking filter value does not enable JSON output");
            Check(!File.ReadAllLines(calls).Skip(callStart).Select(line => Json.Deserialize<string[]>(line)[0]).Any(command =>
                command == "branch" || command == "switch" || command == "update" || command == "checkin"), "Branch hierarchy and filtering never invoke a native write");
            File.WriteAllText(mode, "cycle");
            Run(1, action);
            File.WriteAllText(mode, "deep");
            var deep = Run(0, action);
            var deepNodes = ((IList)Data(deep)["nodes"]).Cast<Dictionary<string, object>>().ToArray();
            Check(deepNodes.Length == 101 && deepNodes.Max(node => Convert.ToInt32(node["depth"])) == 100,
                "Deep branch hierarchy keeps exact JSON depths");
            Check(deep["output"].ToString().Length < 15000 && deep["output"].ToString().Contains("[depth 100]"),
                "Deep tree text caps indentation and reports the exact depth instead of quadratic whitespace");
        }
        finally { File.Delete(mode); }
    }

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
        var branch = Data(Run(0, "--command", "history-page", "--path", temporary, "--branch", "/main/feature 中文", "--limit", "2", "--cm", fakeCm));
        Check(branch["branch"].ToString() == "/main/feature 中文" && ((IList)branch["entries"]).Count == 0 &&
            Convert.ToBoolean(branch["hasMore"]) && Convert.ToInt32(branch["nextBeforeChangeset"]) == 2,
            "Branch filter preserves global scan continuation for an empty page");
        var branchLast = Data(Run(0, "--command", "history-page", "--path", temporary, "--branch", "/main/feature 中文", "--before", "2", "--limit", "2", "--cm", fakeCm));
        var branchEntries = (IList)branchLast["entries"];
        Check(branchEntries.Count == 1 && Convert.ToInt32(((Dictionary<string, object>)branchEntries[0])["changeset"]) == 1 && !Convert.ToBoolean(branchLast["hasMore"]),
            "Branch history returns exact published branch changesets without inherited ancestors");
        var scoped = Data(Run(0, "--command", "history-page", "--path", Path.Combine(temporary, "history-folder", "file.txt"), "--branch", "/main/feature 中文", "--cm", fakeCm));
        Check(((IList)scoped["entries"]).Count == 1 && scoped["scope"].ToString() == "/history-folder/file.txt", "Path scope and exact branch filters compose");
        var branchText = Invoke(new[] { "--cli", "--command", "history-page", "--path", temporary, "--branch", "/main/feature 中文", "--limit", "2", "--cm", fakeCm });
        Check(branchText.Item1 == 0 && branchText.Item2.Contains("inherited ancestor commits are excluded") && branchText.Item2.Contains("--before 2"), "Text filtered history explains empty pages and inherited history");
        Run(2, "--command", "history-page", "--path", temporary, "--branch", "", "--cm", fakeCm);
        Run(2, "--command", "history-page", "--path", temporary, "--branch", "/main/../other", "--cm", fakeCm);
        Run(2, "--command", "history-page", "--path", temporary, "--branch", "/main", "--comment", "irrelevant", "--cm", fakeCm);
    }

    private static void CreateBranchTests()
    {
        string created = Path.Combine(temporary, ".plastic", "fake-created-branch.json");
        string failure = Path.Combine(temporary, ".plastic", "fake-create-failure.txt");
        string calls = Path.Combine(temporary, ".plastic", "cli-cm-calls.log");
        string selector = Path.Combine(temporary, ".plastic", "plastic.selector");
        string selectorBefore = File.ReadAllText(selector);
        string[] action = { "--command", "create-branch", "--path", temporary, "--branch", "/main/new 中文", "--changeset", "1", "--comment", "Create 中文\nsecond \"line\"", "--yes", "--cm", fakeCm };
        foreach (string[] unrelated in new[] { new[] { "--from", "1" }, new[] { "--to", "2" }, new[] { "--item", "/file.txt" }, new[] { "--recursive" },
            new[] { "--external" }, new[] { "--before", "1" }, new[] { "--limit", "1" }, new[] { "--overwrite" }, new[] { "--destination", temporary }, new[] { "--path", Path.Combine(temporary, "file.txt") } })
            Run(2, action.Concat(unrelated).ToArray());
        Run(2, "--command", "create-branch", "--path", temporary, "--branch", "/main/new", "--changeset", "1", "--comment", "No confirmation");
        Run(2, "--command", "create-branch", "--path", temporary, "--branch", "/main/new", "--changeset", "1", "--yes");
        Run(2, "--command", "create-branch", "--path", temporary, "--branch", "/main/new", "--changeset", "1", "--comment", " \n", "--yes");
        Run(2, "--command", "create-branch", "--path", temporary, "--branch", "/main/new", "--comment", "Missing changeset", "--yes");
        Run(2, "--command", "create-branch", "--path", temporary, "--changeset", "1", "--comment", "Missing branch", "--yes");
        try
        {
            int callStart = File.ReadAllLines(calls).Length;
            var result = Data(Run(0, action));
            Check(result["operation"].ToString() == "create-branch" && result["branch"].ToString() == "/main/new 中文" && Convert.ToInt32(result["changeset"]) == 1,
                "Create branch returns the created name and fixed origin changeset");
            string[] createCalls = File.ReadAllLines(calls).Skip(callStart).ToArray();
            Check(createCalls.Any(line => line.StartsWith("[\"branch\",\"create\",\"br:/main/new 中文@test@server:8087\"")), "Creation uses repository-qualified branch name");
            Check(!createCalls.Any(line => line.StartsWith("[\"switch\"")) && File.ReadAllText(selector) == selectorBefore, "Creation never switches the workspace or rewrites its selector");
            var native = Json.Deserialize<string[]>(createCalls.Single(line => line.StartsWith("[\"branch\",\"create\"")));
            Check(native.Contains("--changeset=cs:1@test@server:8087") && native.Contains("-c=Create 中文\nsecond \"line\""), "Creation pins the source and passes the exact multiline comment without shell parsing");
            Run(2, action);
            File.Delete(created);
            Run(2, "--command", "create-branch", "--path", temporary, "--branch", "/missing/child", "--changeset", "1", "--comment", "Missing parent", "--yes", "--cm", fakeCm);
            File.WriteAllText(failure, "source");
            callStart = File.ReadAllLines(calls).Length;
            Run(1, action);
            Check(!File.ReadAllLines(calls).Skip(callStart).Any(line => line.StartsWith("[\"branch\",\"create\"")), "An invalid source changeset never reaches branch creation");
            File.WriteAllText(failure, "native");
            var rejected = Run(1, action);
            Check(rejected["error"].ToString().Contains("may already exist"), "Native creation error warns against blindly retrying");
            File.WriteAllText(failure, "postflight");
            var uncertain = Run(1, action);
            Check(uncertain["error"].ToString().Contains("verification failed") && File.Exists(created), "Unverified server creation reports failure without deleting the new branch");
            File.Delete(created); File.Delete(failure);
            File.WriteAllText(failure, "selector");
            callStart = File.ReadAllLines(calls).Length;
            var movedContext = Run(1, action);
            Check(movedContext["error"].ToString().Contains("selector changed") &&
                !File.ReadAllLines(calls).Skip(callStart).Any(line => line.StartsWith("[\"branch\",\"create\"")),
                "A selector change during initial CLI workspace lookup cannot retarget branch creation");
            File.Delete(failure); File.WriteAllText(selector, selectorBefore);
            string partial = Path.Combine(temporary, "fake-partial.marker"); bool wasPartial = File.Exists(partial);
            File.WriteAllText(partial, "partial");
            try
            {
                string comments = Path.Combine(temporary, "branch-comment.txt"); File.WriteAllText(comments, "File comment 中文", new UTF8Encoding(true));
                var partialResult = Data(Run(0, "--command", "create-branch", "--path", temporary, "--branch", "/main/from-partial", "--changeset", "1", "--commentsfile", comments, "--yes", "--cm", fakeCm));
                Check(Convert.ToBoolean(((Dictionary<string, object>)partialResult["workspace"])["isPartial"]), "Partial workspaces can create server branch metadata");
                Check(File.ReadAllText(selector) == selectorBefore, "Partial branch creation preserves loaded configuration");
            }
            finally { if (!wasPartial) File.Delete(partial); }
        }
        finally { File.Delete(created); File.Delete(failure); File.WriteAllText(selector, selectorBefore); }
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
        var guiPreferences = XDocument.Load(settings);
        guiPreferences.Root.SetElementValue("UseBuiltInDiff", true);
        guiPreferences.Root.SetElementValue("UseBuiltInMerge", true);
        guiPreferences.Save(settings);
        var selected = (Dictionary<string, object>)Data(Run(0, "--command", "settings", "--settings-file", settings))["settings"];
        Check((bool)selected["useBuiltInDiff"] && (bool)selected["useBuiltInMerge"], "CLI reports built-in GUI preferences");
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
        Check((bool)XDocument.Load(settings).Root.Element("UseBuiltInDiff") && (bool)XDocument.Load(settings).Root.Element("UseBuiltInMerge"),
            "CLI external diff and merge ignore GUI preferences without overwriting them");
        Run(0, "--command", "settings", "--settings-file", settings, "--yes", "--diff-tool", "", "--merge-tool", "");
        var cleared = (Dictionary<string, object>)Data(Run(0, "--command", "settings", "--settings-file", settings))["settings"];
        Check(!(bool)cleared["useBuiltInDiff"] && !(bool)cleared["useBuiltInMerge"], "Explicit CLI external tool settings deselect built-in mode");
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
        var moved = Data(Run(0, "--command", "diff-history", "--path", temporary, "--from-item", "/old name.txt", "--item", "/new name.txt", "--from", "1", "--to", "2", "--cm", fakeCm));
        Check(moved["fromItem"].ToString() == "/old name.txt" && moved["item"].ToString() == "/new name.txt" &&
            moved["diffText"].ToString().Contains("--- /old name.txt (cs:1)") && moved["diffText"].ToString().Contains("+++ /new name.txt (cs:2)"),
            "Cross-path historical comparison uses old and new path headers");
        var movedExternal = Data(Run(0, "--command", "diff-history", "--path", temporary, "--from-item", "/old name.txt", "--item", "/new name.txt", "--from", "1", "--to", "2", "--external", "--cm", fakeCm, "--settings-file", settings));
        Check((bool)movedExternal["external"] && movedExternal["fromItem"].ToString() == "/old name.txt", "External historical comparison preserves separate endpoint paths");
        string[] crossPath = { "--command", "diff-history", "--path", temporary, "--item", "/new name.txt", "--from", "1", "--to", "2", "--cm", fakeCm };
        foreach (string invalidPath in new[] { "", " ", "/../outside", "/missing.txt", "/new name.txt" })
            Run(2, crossPath.Concat(new[] { "--from-item", invalidPath }).ToArray());
        Run(2, crossPath.Concat(new[] { "--from-item", "/old name.txt", "--from-item", "/old name.txt" }).ToArray());
        Run(2, "--command", "status", "--path", temporary, "--from-item", "/old name.txt");
        var optionValue = Invoke(new[] { "--cli", "--command", "diff-history", "--path", temporary, "--item", "/new name.txt", "--from", "1", "--to", "2", "--from-item", "--json", "--cm", fakeCm });
        Check(optionValue.Item1 == 2 && optionValue.Item2.Length == 0 && optionValue.Item3.Length > 0, "Option-looking from-item value cannot enable JSON mode");
    }

    private static void ChangesetComparisonTests()
    {
        string callsPath = Path.Combine(temporary, ".plastic", "cli-cm-calls.log");
        int initialCalls = File.ReadAllLines(callsPath).Length;
        Directory.CreateDirectory(Path.Combine(temporary, "history-folder"));
        string[] command = { "--command", "diff-changesets", "--path", temporary, "--from", "1", "--to", "2", "--cm", fakeCm };
        var comparison = Data(Run(0, command));
        var files = ((IList)comparison["files"]).Cast<Dictionary<string, object>>().ToList();
        Check(comparison["repository"].ToString() == "test@server:8087" && Convert.ToInt64(comparison["from"]) == 1 && Convert.ToInt64(comparison["to"]) == 2,
            "Changeset comparison exposes repository and exact endpoints");
        Check(files.Count == 4 && files.Any(file => file["path"].ToString() == "/new name.txt" && file["oldPath"].ToString() == "/old name.txt" && file["status"].ToString() == "M"),
            "Changeset comparison returns structured net changes including rename endpoints");
        Check(files.Any(file => file["status"].ToString() == "A" && file["itemType"].ToString() == "D") && files.Any(file => file["status"].ToString() == "D") && files.Any(file => file["status"].ToString() == "C"),
            "Changeset comparison retains added directories, deleted files and changed files");
        var calls = File.ReadAllLines(Path.Combine(temporary, ".plastic", "cli-cm-calls.log")).Select(line => Json.Deserialize<string[]>(line)).ToList();
        Check(calls.Any(call => call.Length > 2 && call[0] == "diff" && call[1] == "cs:1@test@server:8087" && call[2] == "cs:2@test@server:8087" && call.Contains("--repositorypaths")),
            "Changeset comparison asks cm for both endpoint trees directly");
        var equal = Data(Run(0, "--command", "diff-changesets", "--path", temporary, "--from", "1", "--to", "1", "--cm", fakeCm));
        Check(((IList)equal["files"]).Count == 0, "Identical snapshots have zero net changes");
        var reverse = Data(Run(0, "--command", "diff-changesets", "--path", temporary, "--from", "2", "--to", "1", "--cm", fakeCm));
        Check(((IList)reverse["files"]).Cast<Dictionary<string, object>>().Any(file => file["path"].ToString() == "/old name.txt" && file["oldPath"].ToString() == "/new name.txt"),
            "Reversed snapshots preserve requested comparison direction");
        var nested = Data(Run(0, "--command", "diff-changesets", "--path", Path.Combine(temporary, "history-folder"), "--from", "1", "--to", "2", "--cm", fakeCm));
        Check(((IList)nested["files"]).Count == 4, "Workspace locator does not silently filter repository comparison");
        var text = Invoke(new[] { "--cli" }.Concat(command));
        Check(text.Item1 == 0 && text.Item2.Contains("cs:1 -> cs:2") && text.Item2.Contains("4 net changes") && text.Item2.Contains("/old name.txt -> /new name.txt"), "Text comparison reports direction, net count and rename");
        Run(2, "--command", "diff-changesets", "--path", temporary, "--from", "1");
        Run(2, "--command", "diff-changesets", "--from", "1", "--to", "2");
        Run(2, "--command", "diff-changesets", "--path", temporary, "--from", "-1", "--to", "2");
        Run(2, "--command", "diff-changesets", "--path", temporary, "--from", "0", "--to", "9223372036854775808");
        foreach (string[] unrelated in new[] {
            new[] { "--path", Path.Combine(temporary, "history-folder") }, new[] { "--item", "/file.txt" }, new[] { "--from-item", "/file.txt" },
            new[] { "--changeset", "1" }, new[] { "--from", "1" }, new[] { "--external" }, new[] { "--yes" }, new[] { "--recursive" },
            new[] { "--overwrite" }, new[] { "--comment", "unused" }, new[] { "--output", Path.Combine(temporary, "unused.txt") },
            new[] { "--before", "1" }, new[] { "--limit", "1" }, new[] { "--diff-tool", fakeCm }, new[] { "--destination", temporary },
            new[] { "--result", temporary }, new[] { "--base", temporary }, new[] { "--resolution", "src" }, new[] { "--conflict", "1" },
            new[] { "--rename", "other" }, new[] { "--lock-id", "77bdbba7-82e8-407b-8132-76d772be21c5" } })
            Run(2, command.Concat(unrelated).ToArray());
        Run(1, "--command", "diff-changesets", "--path", temporary, "--from", "999", "--to", "2", "--cm", fakeCm);
        Run(1, "--command", "diff-changesets", "--path", temporary, "--from", "1", "--to", "999", "--cm", fakeCm);
        Run(1, "--command", "diff-changesets", "--path", temporary, "--from", "1", "--to", "777", "--cm", fakeCm);
        Run(1, "--command", "diff-changesets", "--path", temporary, "--from", "1", "--to", "778", "--cm", fakeCm);
        Run(124, "--command", "diff-changesets", "--path", temporary, "--from", "1", "--to", "779", "--cm", fakeCm, "--timeout", "1");
        Check(File.ReadAllLines(callsPath).Skip(initialCalls).Select(line => Json.Deserialize<string[]>(line)).All(call => call[0] == "status" || call[0] == "diff"),
            "Snapshot comparisons execute only read-only cm commands, including failed requests");
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
        string file = Path.Combine(temporary, "merge-conflict.txt"), settings = Path.Combine(mergeSettingsDirectory, "settings.xml");
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
        foreach (string artifact in Directory.GetFiles(mergeSettingsDirectory, "*", SearchOption.AllDirectories)) File.SetAttributes(artifact, FileAttributes.Normal);
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
            bool ignoredPending = File.Exists(Path.Combine(metadata, "fake-ignored.marker")) && args.Contains("--ignored");
            bool statusClean = File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-clean.marker")) && !ignoredPending;
            if (File.Exists(Path.Combine(metadata, "fake-create-failure.txt")) && File.ReadAllText(Path.Combine(metadata, "fake-create-failure.txt")) == "selector")
                File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"test@server:8087\"\r\n path \"/\"\r\n smartbranch \"/main/feature 中文\"\r\n");
            Console.WriteLine(new XElement("StatusOutput", new XElement("WkConfigName", (File.Exists(Path.Combine(metadata, "fake-branch.txt")) ? File.ReadAllText(Path.Combine(metadata, "fake-branch.txt")) : "/main") + "@test@server:8087"), new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-partial.marker")) ? "-1" : "1"),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))), new XElement("Changes", statusClean ? null : new XElement("Change",
                new XElement("Type", File.Exists(Path.Combine(metadata, "fake-shelves.marker")) ? "CH" : "PR"), new XElement("Path", File.Exists(Path.Combine(metadata, "fake-shelves.marker")) ? Path.Combine(Environment.CurrentDirectory, "shelve 中文 & selected.txt") : File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-dirty-path.txt")) ? File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "fake-dirty-path.txt")) : Path.Combine(Environment.CurrentDirectory, "中文 space & file.txt")),
                new XElement("TypeVerbose", "Private"), new XElement("RevisionType", "enTextFile")))).ToString());
        }
        else if (args[0] == "history")
            Console.WriteLine(new XElement("RevisionHistoriesResult", new XElement("RevisionHistory", new XElement("ItemName", args[1]),
                new XElement("Revision", new XElement("ChangesetNumber", "1"), new XElement("Comment", "History 中文")))).ToString());
        else if (args[0] == "find" && args[1] == "shelve")
        {
            string failure = Path.Combine(metadata, "fake-shelve-failure.txt");
            if (File.Exists(failure) && File.ReadAllText(failure) == "timeout") { Thread.Sleep(30000); return 0; }
            if (File.Exists(failure) && File.ReadAllText(failure) == "malformed") { Console.WriteLine("<PLASTICQUERY><SHELVE /></PLASTICQUERY>"); return 0; }
            var xml = new XElement("PLASTICQUERY");
            if (!File.Exists(Path.Combine(metadata, "fake-shelve-deleted.txt"))) xml.Add(new XElement("SHELVE", new XElement("ID", "55"), new XElement("SHELVEID", "5"),
                new XElement("COMMENT", "Saved 中文\nline"), new XElement("DATE", "2026-09-27T00:00:00Z"), new XElement("OWNER", "fixture-owner"),
                new XElement("PARENT", "1"), new XElement("REPOSITORY", "test"), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087")));
            string created = Path.Combine(metadata, "fake-shelve-comment.txt");
            if (File.Exists(created)) xml.Add(new XElement("SHELVE", new XElement("ID", "56"), new XElement("SHELVEID", "6"),
                new XElement("COMMENT", File.ReadAllText(created)), new XElement("DATE", "2026-09-27T01:00:00Z"), new XElement("OWNER", "fixture-owner"),
                new XElement("PARENT", "1"), new XElement("REPOSITORY", "test"), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087")));
            Console.WriteLine(xml);
        }
        else if ((args[0] == "shelveset" && args[1] == "create") || (args[0] == "partial" && args[1] == "shelveset" && args[2] == "create"))
        {
            if (File.Exists(Path.Combine(metadata, "fake-shelve-failure.txt"))) { Console.Error.WriteLine("Native shelve creation failed 中文"); return 7; }
            File.WriteAllText(Path.Combine(metadata, "fake-shelve-comment.txt"), args.Single(arg => arg.StartsWith("-c=", StringComparison.Ordinal)).Substring(3));
            Console.WriteLine("Created shelve 6");
        }
        else if (args[0] == "shelveset" && args[1] == "apply")
            Console.WriteLine("Applied " + args[2]);
        else if (args[0] == "shelveset" && args[1] == "delete")
        {
            File.WriteAllText(Path.Combine(metadata, "fake-shelve-deleted.txt"), "delete");
            Console.WriteLine("Deleted " + args[2]);
        }
        else if (args[0] == "diff" && args[1].StartsWith("sh:", StringComparison.Ordinal))
            Console.WriteLine("C|\"/shelve 中文 & selected.txt\"|F|\"\"|\"\"");
        else if (args[0] == "find" && args[1] == "branch")
        {
            string failure = Path.Combine(metadata, "fake-branch-failure.txt");
            if (File.Exists(failure))
            {
                if (File.ReadAllText(failure) == "timeout") { Thread.Sleep(30000); return 0; }
                if (File.ReadAllText(failure) == "malformed") { Console.WriteLine("<PLASTICQUERY><CHANGESET /></PLASTICQUERY>"); return 0; }
                Console.Error.WriteLine("Branch query failed 中文"); return 7;
            }
            var branchXml = new XElement("PLASTICQUERY", new[] { "/main", "/main/feature 中文" }.Select(name => new XElement("BRANCH",
                new XElement("NAME", name), new XElement("PARENT", name == "/main" ? "" : "/main"),
                new XElement("OWNER", "fixture-owner"), new XElement("DATE", "2026-09-27T00:00:00Z"),
                new XElement("COMMENT", name == "/main" ? "Main" : "Feature 中文"), new XElement("REPNAME", "test"),
                new XElement("REPSERVER", "server:8087"), new XElement("CHANGESET", name == "/main" ? "1" : "42"))));
            string created = Path.Combine(metadata, "fake-created-branch.json");
            string treeMode = Path.Combine(metadata, "fake-branch-tree.txt");
            if (File.Exists(treeMode))
            {
                if (File.ReadAllText(treeMode) == "cycle") branchXml.Elements("BRANCH").First().Element("PARENT").Value = "/main/feature 中文";
                else if (File.ReadAllText(treeMode) == "deep")
                {
                    branchXml.RemoveNodes();
                    for (int depth = 0; depth <= 100; ++depth)
                        branchXml.Add(new XElement("BRANCH", new XElement("NAME", depth == 0 ? "/main" : "/node-" + depth),
                            new XElement("PARENT", depth == 0 ? "" : depth == 1 ? "/main" : "/node-" + (depth - 1)), new XElement("OWNER", "fixture-owner"),
                            new XElement("DATE", "2026-09-27T00:00:00Z"), new XElement("COMMENT", "Deep branch"), new XElement("REPNAME", "test"),
                            new XElement("REPSERVER", "server:8087"), new XElement("CHANGESET", "42")));
                }
                else
                    foreach (var addition in new[] { new { Name = "/leaf", Parent = "/main/feature 中文", Comment = "leaf 中文" }, new { Name = "/orphan", Parent = "/missing", Comment = "unavailable parent" } })
                        branchXml.Add(new XElement("BRANCH", new XElement("NAME", addition.Name), new XElement("PARENT", addition.Parent), new XElement("OWNER", "fixture-owner"),
                            new XElement("DATE", "2026-09-27T00:00:00Z"), new XElement("COMMENT", addition.Comment), new XElement("REPNAME", "test"),
                            new XElement("REPSERVER", "server:8087"), new XElement("CHANGESET", "42")));
            }
            if (File.Exists(created))
            {
                var record = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(created));
                branchXml.Add(new XElement("BRANCH", new XElement("NAME", record["name"]), new XElement("PARENT", "/main"), new XElement("OWNER", "fixture-owner"),
                    new XElement("DATE", "2026-09-27T00:00:00Z"), new XElement("COMMENT", record["comment"]), new XElement("REPNAME", "test"),
                    new XElement("REPSERVER", "server:8087"), new XElement("CHANGESET", File.Exists(Path.Combine(metadata, "fake-create-failure.txt")) &&
                        File.ReadAllText(Path.Combine(metadata, "fake-create-failure.txt")) == "postflight" ? "999" : record["changeset"])));
            }
            Console.WriteLine(branchXml);
        }
        else if (args[0] == "branch" && args[1] == "create")
        {
            if (File.Exists(Path.Combine(metadata, "fake-create-failure.txt")) && File.ReadAllText(Path.Combine(metadata, "fake-create-failure.txt")) == "native")
            { Console.Error.WriteLine("Native branch creation failed 中文"); return 7; }
            string name = args[2].Substring(3, args[2].IndexOf('@') - 3);
            string revision = args.Single(arg => arg.StartsWith("--changeset=cs:")).Substring("--changeset=cs:".Length).Split('@')[0];
            string comment = args.Single(arg => arg.StartsWith("-c=")).Substring(3);
            File.WriteAllText(Path.Combine(metadata, "fake-created-branch.json"), Json.Serialize(new { name = name, changeset = revision, comment = comment }));
            Console.WriteLine("Created branch " + name);
        }
        else if (args[0] == "log")
        {
            string revision = args[1].Substring(3).Split('@')[0];
            if (File.Exists(Path.Combine(metadata, "fake-create-failure.txt")) && File.ReadAllText(Path.Combine(metadata, "fake-create-failure.txt")) == "source") revision = "999";
            Console.WriteLine(new XElement("LogList", new XElement("Changeset", new XElement("ChangesetId", revision))));
        }
        else if (args[0] == "find" && args.Any(arg => arg.Contains("order by changesetid desc limit")))
        {
            string query = args[2];
            var before = Regex.Match(query, @"changesetid < (\d+)");
            int limit = Int32.Parse(Regex.Match(query, @"limit (\d+)").Groups[1].Value);
            var ids = Enumerable.Range(0, 4).Reverse().Where(id => !before.Success || id < Int64.Parse(before.Groups[1].Value)).Take(limit);
            Console.WriteLine(new XElement("PLASTICQUERY", ids.Select(id => new XElement("CHANGESET", new XElement("CHANGESETID", id), new XElement("COMMENT", "Published 中文 " + id), new XElement("BRANCH", id == 1 ? "/main/feature 中文" : "/main")))));
        }
        else if (args[0] == "find")
            Console.WriteLine(new XElement("PLASTICQUERY", args.Any(arg => arg.Contains("changesetid = 999")) ? null : new XElement("CHANGESET", new XElement("CHANGESETID", "1"),
                new XElement("DATE", "2026-09-25T00:00:00Z"), new XElement("OWNER", "Test"), new XElement("BRANCH", "/main"), new XElement("COMMENT", "Changeset 中文"), new XElement("REPOSITORY", "test"))).ToString());
        else if (args[0] == "showselector") Console.WriteLine("repository \"test@server:8087\"\n path \"/\"\n smartbranch \"" +
            (File.Exists(Path.Combine(metadata, "fake-branch.txt")) ? File.ReadAllText(Path.Combine(metadata, "fake-branch.txt")) : "/main") + "\"");
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
        else if (args[0] == "ls") Console.WriteLine(new XElement("LsResults", new XElement("LsItems", args[1] == "/missing.txt" ||
            (args[1] == "/old name.txt" && !args.Any(arg => arg.StartsWith("--tree=cs:1@"))) ||
            (args[1] == "/new name.txt" && !args.Any(arg => arg.StartsWith("--tree=cs:2@"))) ? null : new XElement("LsItem",
            new XElement("Name", Path.GetFileName(args[1])), new XElement("CurrentPath", args[1]), new XElement("ItemId", "42"), new XElement("Type", "txt")))).ToString());
        else if (args[0] == "diff" && args.Length > 2 && args[1].StartsWith("cs:") && args[2].StartsWith("cs:"))
        {
            if (args.Take(3).Any(arg => arg.StartsWith("cs:999@") || arg.StartsWith("cs:777@"))) { Console.Error.WriteLine("Comparison endpoint unavailable"); return 7; }
            if (args[2].StartsWith("cs:778@")) { Console.WriteLine("unexpected server output"); return 0; }
            if (args[2].StartsWith("cs:779@")) { Thread.Sleep(30000); return 0; }
            if (args[1] == args[2]) return 0;
            Console.WriteLine("C|\"/history-folder/file.txt\"|F|\"\"|\"\"");
            Console.WriteLine("D|\"/deleted file.txt\"|F|\"\"|\"\"");
            Console.WriteLine("A|\"/new folder\"|D|\"\"|\"\"");
            Console.WriteLine(args[1].StartsWith("cs:1@") ? "M|\"/old name.txt\"|F|\"/old name.txt\"|\"/new name.txt\"" : "M|\"/new name.txt\"|F|\"/new name.txt\"|\"/old name.txt\"");
        }
        else if (args[0] == "diff") Console.WriteLine("C|\"/history-folder/file.txt\"|F|\"\"|\"\"");
        else if (args[0] == "fileinfo")
            Console.WriteLine("<FileInfos><FileInfo><RevisionChangeset>1</RevisionChangeset><Type>txt</Type></FileInfo></FileInfos>");
        else if (args[0] == "cat")
            File.WriteAllText(args.Single(arg => arg.StartsWith("--file=")).Substring(7), args[1].StartsWith("serverpath:") ?
                (args[1].Contains("/merge-conflict.txt#") ? (args[1].Contains("#cs:0@") ? "merge base 中文\n" : args[1].Contains("#cs:1@") ? "merge local 中文\n" : "merge remote 中文\n") :
                    (args[1].Contains("#cs:1@") ? "historical one 中文\n" : "historical two 中文\n")) : "before 中文\n", new UTF8Encoding(false));
        else
        {
            if (args[0] == "switch" && args[1].StartsWith("br:"))
            {
                File.WriteAllText(Path.Combine(metadata, "fake-branch.txt"), args[1].Substring(3, args[1].IndexOf('@') - 3));
                File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"test@server:8087\"\r\n path \"/\"\r\n smartbranch \"" + File.ReadAllText(Path.Combine(metadata, "fake-branch.txt")) + "\"\r\n");
            }
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
