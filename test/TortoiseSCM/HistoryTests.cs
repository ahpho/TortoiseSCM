// GPL-2.0-or-later. Bounded publication-history pagination and path filtering.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class HistoryTests
{
    private static int assertions;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (args.Length != 0 && args[0] != "--real") return FakeCm(args);
        if (args.Length != 0) return Real(args[1]);
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-history-pages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "history-pages\nguid\nStandard\n");
            string selector = Path.Combine(temporary, ".plastic", "plastic.selector");
            File.WriteAllText(selector, "repository \"test@server:8087\"");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(10) });
            BatchAssertions(temporary);
            File.Delete(Log(temporary));
            var first = client.GetHistoryPageAsync(temporary, null, 2, Token).GetAwaiter().GetResult();
            Check(first.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 7, 6 }) && first.HasMore && first.NextBeforeChangeset == 6, "Repository uses descending cursor pagination");
            Check(first.ScannedChangesets == 2 && DiffCount(temporary) == 0, "Repository page requires no per-changeset diff calls");
            Check(File.ReadAllLines(Log(temporary)).Length == 1, "Repository page is one bounded find query");
            File.WriteAllText(Path.Combine(temporary, ".plastic", "new-head"), "");
            var second = client.GetHistoryPageAsync(temporary, first.NextBeforeChangeset, 2, Token).GetAwaiter().GetResult();
            Check(second.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 5, 4 }), "Concurrent new commit does not duplicate or skip old-page cursor");
            var last = client.GetHistoryPageAsync(temporary, 2, 2, Token).GetAwaiter().GetResult();
            Check(last.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 1, 0 }) && !last.HasMore && last.NextBeforeChangeset == null, "Last page identifies exact exhaustion");
            int count = File.ReadAllLines(Log(temporary)).Length;
            Check(!client.GetHistoryPageAsync(temporary, 0, 2, Token).GetAwaiter().GetResult().HasMore && File.ReadAllLines(Log(temporary)).Length == count, "Zero cursor terminates without server query");
            Reject(() => client.GetHistoryPageAsync(temporary, null, 0, Token).GetAwaiter().GetResult(), "Zero scan budget rejected");
            Reject(() => client.GetHistoryPageAsync(temporary, null, 101, Token).GetAwaiter().GetResult(), "Unbounded scan budget rejected");
            Reject(() => client.GetHistoryPageAsync(temporary, -1, 2, Token).GetAwaiter().GetResult(), "Negative cursor rejected");
            var noMatch = client.GetHistoryPageAsync(Path.Combine(temporary, "missing"), 8, 2, Token).GetAwaiter().GetResult();
            Check(noMatch.Items.Count == 0 && noMatch.HasMore && noMatch.ScannedChangesets == 2 && noMatch.NextBeforeChangeset == 6, "Empty path page remains explicitly incomplete with advancing cursor");
            int cached = DiffCount(temporary);
            var directory = client.GetHistoryPageAsync(Path.Combine(temporary, "folder"), 8, 2, Token).GetAwaiter().GetResult();
            Check(directory.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 7 }), "Directory boundary excludes folderish sibling");
            Check(DiffCount(temporary) == cached, "Changing path scope reuses complete immutable changeset diff cache");
            var file = client.GetHistoryPageAsync(Path.Combine(temporary, "folder", "file.txt"), 8, 7, Token).GetAwaiter().GetResult();
            Check(file.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 7, 5, 4, 3, 2, 1 }), "File path events include old-revision publication, move out, move in, ancestor move and deletion");
            Check(file.Items.First().Comment == "publish rollback using old revision", "Rollback publication metadata retained independently of revision creation");
            var upper = client.GetHistoryPageAsync(Path.Combine(temporary, "FOLDER", "FILE.TXT"), 8, 7, Token).GetAwaiter().GetResult();
            Check(upper.Items.Count == file.Items.Count, "Windows path matching remains case insensitive");
            var branchFirst = client.GetHistoryPageAsync(temporary, "/main", 8, 2, Token).GetAwaiter().GetResult();
            Check(branchFirst.Branch == "/main" && branchFirst.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 7 }) && branchFirst.ScannedChangesets == 2 && branchFirst.NextBeforeChangeset == 6,
                "Branch history filters exact branch while retaining global scan budget and cursor");
            var emptyBranchPage = client.GetHistoryPageAsync(temporary, "/main/topic", 8, 1, Token).GetAwaiter().GetResult();
            Check(emptyBranchPage.Items.Count == 0 && emptyBranchPage.HasMore && emptyBranchPage.NextBeforeChangeset == 7 && emptyBranchPage.ScannedChangesets == 1, "Sparse branch empty page advances global cursor");
            var branchNext = client.GetHistoryPageAsync(temporary, "/main/topic", emptyBranchPage.NextBeforeChangeset, 1, Token).GetAwaiter().GetResult();
            Check(branchNext.Items.Single().Changeset == 6, "Sparse branch continuation finds next matching publication");
            var branchClient = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
            int branchDiffBefore = DiffCount(temporary);
            var branchPath = branchClient.GetHistoryPageAsync(Path.Combine(temporary, "folder", "file.txt"), "/main/topic", 8, 7, Token).GetAwaiter().GetResult();
            Check(branchPath.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 4, 2 }), "Branch and path filters intersect, including ancestor deletion");
            Check(DiffCount(temporary) == branchDiffBefore + 3, "Other-branch changesets never trigger file diff calls");
            var quotedBranch = client.GetHistoryPageAsync(temporary, "/main/quoted '中文'", 8, 7, Token).GetAwaiter().GetResult();
            Check(quotedBranch.Items.Single().Changeset == 3, "Quoted Unicode branch names match literally");
            Check(File.ReadAllLines(Log(temporary)).Where(line => line.StartsWith("find changeset ")).All(line => !line.Contains("quoted") && !line.Contains("branch =")), "Branch names never enter native changeset find expressions");
            Reject(() => client.GetHistoryPageAsync(temporary, "/missing", null, 2, Token).GetAwaiter().GetResult(), "Missing history branch fails before returning incomplete result");
            Reject(() => client.GetHistoryPageAsync(temporary, "/main@foreign", null, 2, Token).GetAwaiter().GetResult(), "Foreign repository branch spec rejected");
            Reject(() => client.GetHistoryPageAsync(temporary, "", null, 2, Token).GetAwaiter().GetResult(), "Empty branch cannot silently remove filter");
            Check(first.Branch == null, "Legacy history overload remains all branches");
            Check(!client.GetHistoryPageAsync(temporary, "/main/topic", 0, 2, Token).GetAwaiter().GetResult().HasMore, "Branch zero cursor validates branch then terminates");
            cached = DiffCount(temporary);
            File.WriteAllText(selector, "repository \"another@server:8087\"");
            client.GetHistoryPageAsync(Path.Combine(temporary, "folder"), 8, 2, Token).GetAwaiter().GetResult();
            Check(DiffCount(temporary) == cached + 2, "Repository identity partitions diff cache");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool cancelled = false;
                try { client.GetHistoryPageAsync(temporary, null, 2, cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "History observes cancellation before command launch");
            }
            File.WriteAllText(Path.Combine(temporary, ".plastic", "bad-page"), "");
            Reject(() => client.GetHistoryPageAsync(temporary, 5, 2, Token).GetAwaiter().GetResult(), "Server ignoring cursor fails closed");
            File.Delete(Path.Combine(temporary, ".plastic", "bad-page"));
            File.WriteAllText(Path.Combine(temporary, ".plastic", "diff-fail"), "");
            var fresh = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
            Reject(() => fresh.GetHistoryPageAsync(Path.Combine(temporary, "folder"), 8, 1, Token).GetAwaiter().GetResult(), "Failed diff does not yield silently incomplete page");
            File.Delete(Path.Combine(temporary, ".plastic", "diff-fail"));
            Check(fresh.GetHistoryPageAsync(Path.Combine(temporary, "folder"), 8, 1, Token).GetAwaiter().GetResult().Items.Count == 1, "Failed diff is not cached");
            Check(!PlasticClient.HistoryPathMatches(new PlasticChangesetFile { Path = "/unrelated", OldPath = "", ItemType = "D" }, "/folder/file.txt"), "Empty old directory path never matches arbitrary descendant");
            Check(PlasticClient.HistoryPathMatches(new PlasticChangesetFile { Path = "/folder", OldPath = "", ItemType = "D" }, "/folder/file.txt"), "Ancestor directory record affects descendant");
            File.WriteAllText(Path.Combine(temporary, ".plastic", "large-repository"), "");
            fresh = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
            cached = DiffCount(temporary);
            fresh.GetHistoryPageAsync(Path.Combine(temporary, "folder"), 161, 100, Token).GetAwaiter().GetResult();
            fresh.GetHistoryPageAsync(Path.Combine(temporary, "folder"), 61, 40, Token).GetAwaiter().GetResult();
            Check(DiffCount(temporary) == cached + 140, "Large history obeys exact per-request diff budget");
            fresh.GetHistoryPageAsync(Path.Combine(temporary, "folder"), 31, 1, Token).GetAwaiter().GetResult();
            Check(DiffCount(temporary) == cached + 140, "Recent immutable page remains cached");
            fresh.GetHistoryPageAsync(Path.Combine(temporary, "folder"), 161, 1, Token).GetAwaiter().GetResult();
            Check(DiffCount(temporary) == cached + 141, "Old page evicted after bounded cache fills");
            File.WriteAllText(Path.Combine(temporary, ".plastic", "find-slow"), "");
            using (var cancel = new CancellationTokenSource())
            {
                var pending = client.GetHistoryPageAsync(temporary, null, 2, cancel.Token);
                for (int wait = 0; wait < 100 && !File.Exists(Path.Combine(temporary, ".plastic", "find-entered")); wait++) Thread.Sleep(20);
                Check(File.Exists(Path.Combine(temporary, ".plastic", "find-entered")), "Cancellation fixture entered a running native query");
                cancel.Cancel(); bool cancelled = false;
                try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Running history query cancels instead of publishing incomplete page");
            }
            Console.WriteLine("PASS: " + assertions + " history page assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }
    private static void BatchAssertions(string root)
    {
        string marker = Path.Combine(root, ".plastic", "batch-mode");
        File.WriteAllText(marker, "valid");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
        var directory = client.GetHistoryPageAsync(Path.Combine(root, "folder"), 8, 7, Token).GetAwaiter().GetResult();
        Check(directory.Items.Select(item => item.Changeset).SequenceEqual(new long[] { 7, 5, 4, 3, 2, 1 }), "Batch preserves rollback publication, old/new move paths and directory deletion");
        Check(DiffCount(root) == 0 && BatchCount(root) == 1 && directory.FallbackReason == null, "One log command filters entire directory page without individual diffs");
        var file = client.GetHistoryPageAsync(Path.Combine(root, "folder", "file.txt"), 8, 7, Token).GetAwaiter().GetResult();
        Check(file.Items.Select(item => item.Changeset).SequenceEqual(directory.Items.Select(item => item.Changeset)), "Batch ancestor records are checked without losing moved or deleted descendants");
        Check(DiffCount(root) == 2, "Only two ambiguous ancestor records need exact file/directory diffs");
        var branch = client.GetHistoryPageAsync(Path.Combine(root, "folder"), "/main/topic", 8, 7, Token).GetAwaiter().GetResult();
        Check(branch.Items.Select(item => item.Changeset).SequenceEqual(new long[] { 4, 2 }), "Batch branch and path filters intersect exactly");
        Check(File.ReadAllLines(Log(root)).Where(line => line.StartsWith("log ")).All(line => line.Contains("--allbranches") && line.Contains("--repositorypaths") && line.Contains("--from=cs:0")), "Batch explicitly includes all branches and correct exclusive lower boundary");
        int calls = BatchCount(root);
        var initial = client.GetHistoryPageAsync(Path.Combine(root, "folder"), 1, 1, Token).GetAwaiter().GetResult();
        Check(initial.Items.Count == 0 && !initial.HasMore && BatchCount(root) == calls, "Initial empty changeset zero needs no unsupported log or diff");
        foreach (string mode in new[] { "unsupported", "missing", "duplicate", "branch", "path", "field", "malformed", "unknown-type", "foreign", "no-changes", "repeated-field", "nested-field" })
        {
            File.WriteAllText(marker, mode);
            client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
            int before = DiffCount(root);
            var page = client.GetHistoryPageAsync(Path.Combine(root, "folder"), 8, 2, Token).GetAwaiter().GetResult();
            Check(page.Items.Select(item => item.Changeset).SequenceEqual(new long[] { 7 }) && !String.IsNullOrEmpty(page.FallbackReason), "Batch " + mode + " reports safe fallback and preserves correct result");
            Check(DiffCount(root) == before + 2, "Batch " + mode + " never trusts incomplete interval");
        }
        File.WriteAllText(marker, "slow");
        using (var cancel = new CancellationTokenSource())
        {
            var pending = client.GetHistoryPageAsync(Path.Combine(root, "folder"), 8, 2, cancel.Token);
            for (int wait = 0; wait < 100 && !File.Exists(Path.Combine(root, ".plastic", "log-entered")); wait++) Thread.Sleep(20);
            Check(File.Exists(Path.Combine(root, ".plastic", "log-entered")), "Cancellation enters actual running batch command");
            cancel.Cancel(); bool cancelled = false;
            try { pending.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "Cancelled batch does not return incomplete page or start fallback");
        }
        File.WriteAllText(marker, "switch-repository");
        bool changed = false;
        try { client.GetHistoryPageAsync(Path.Combine(root, "folder"), 8, 2, Token).GetAwaiter().GetResult(); }
        catch (InvalidOperationException) { changed = true; }
        Check(changed, "Repository change during batch fails closed");
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server:8087\"");
        Check(File.ReadAllLines(Log(root)).Where(line => line.StartsWith("find changeset ")).All(line => line.Contains("on repository 'test@server:8087'")), "History find queries explicitly pin the captured repository");
        Check(File.ReadAllLines(Log(root)).Where(line => line.StartsWith("log ") || line.StartsWith("diff ")).All(line => line.Contains("@test@server:8087")), "Batch and exact ancestor/fallback diff queries pin the captured repository");
        Check(File.ReadAllLines(Log(root)).Where(line => line.StartsWith("log ")).All(line => Regex.IsMatch(line, @"--from=cs:\d+@test@server:8087")), "Both batch interval endpoints pin the same captured repository");
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"quote'repo@server:8087\"");
        Reject(() => client.GetHistoryPageAsync(root, null, 2, Token).GetAwaiter().GetResult(), "Unsafe query repository is rejected before executing native query");
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server:8087\"");
        File.Delete(marker);
    }
    private static int BatchCount(string root) { return File.ReadAllLines(Log(root)).Count(line => line.StartsWith("log ")); }
    private static int Real(string root)
    {
        try
        {
            var client = new PlasticClient(new PlasticClientConfig());
            var repository = client.GetHistoryPageAsync(root, 45, 2, Token).GetAwaiter().GetResult();
            Check(repository.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 44, 43 }) && repository.HasMore, "Real server honors descending limit/cursor");
            var file = client.GetHistoryPageAsync(Path.Combine(root, "历史 中文 & file.txt"), 45, 2, Token).GetAwaiter().GetResult();
            Check(file.Items.Any(i => i.Changeset == 44 && i.Comment.Contains("rollback")), "Real reused-revision rollback publication cs44 appears in file path history");
            var missing = client.GetHistoryPageAsync(Path.Combine(root, "never-created-history-test-path"), 45, 2, Token).GetAwaiter().GetResult();
            Check(missing.Items.Count == 0 && missing.HasMore && missing.NextBeforeChangeset == 43 && missing.ScannedChangesets == 2, "Real empty scope page bounded and explicitly incomplete");
            var second = client.GetHistoryPageAsync(root, repository.NextBeforeChangeset, 2, Token).GetAwaiter().GetResult();
            Check(second.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 42, 41 }), "Real continuation retrieves older commits without duplicates");
            Console.WriteLine("PASS: " + assertions + " real history page assertions (read-only; rollback cs44 found)"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static string Log(string root) { return Path.Combine(root, ".plastic", "calls.log"); }
    private static int DiffCount(string root) { return File.Exists(Log(root)) ? File.ReadAllLines(Log(root)).Count(line => line.StartsWith("diff ")) : 0; }
    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string root = Environment.CurrentDirectory;
        File.AppendAllText(Log(root), String.Join(" ", args) + "\n");
        if (args[0] == "status")
        {
            Console.WriteLine(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", 7),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))), new XElement("WkConfigName", "/main@test@server:8087"))); return 0;
        }
        if (args[0] == "find" && args[1] == "branch")
        {
            Console.WriteLine(new XElement("PLASTICQUERY", new[] { "/main", "/main/topic", "/main/quoted '中文'" }.Select(name => new XElement("BRANCH", new XElement("NAME", name),
                new XElement("PARENT", name == "/main" ? "" : "/main"), new XElement("CHANGESET", 7), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"))))); return 0;
        }
        if (args[0] == "find")
        {
            if (File.Exists(Path.Combine(root, ".plastic", "find-slow")))
            { File.WriteAllText(Path.Combine(root, ".plastic", "find-entered"), ""); Thread.Sleep(5000); }
            var before = Regex.Match(args[2], @"changesetid < (\d+)");
            var limit = Regex.Match(args[2], @"limit (\d+)");
            if (!limit.Success || !args[2].Contains("order by changesetid desc")) return 30;
            int top = File.Exists(Path.Combine(root, ".plastic", "new-head")) ? 8 : 7;
            if (File.Exists(Path.Combine(root, ".plastic", "large-repository"))) top = 160;
            var ids = Enumerable.Range(0, top + 1).Reverse().Where(i => !before.Success || i < Int32.Parse(before.Groups[1].Value)).Take(Int32.Parse(limit.Groups[1].Value)).ToList();
            if (File.Exists(Path.Combine(root, ".plastic", "bad-page"))) ids = new List<int> { 999 };
            Console.WriteLine(new XElement("PLASTICQUERY", ids.Select(i => new XElement("CHANGESET", new XElement("CHANGESETID", i),
                new XElement("COMMENT", i == 7 ? "publish rollback using old revision" : "commit " + i), new XElement("BRANCH", i == 3 ? "/main/quoted '中文'" : i % 2 == 0 ? "/main/topic" : "/main"))))); return 0;
        }
        if (args[0] == "log")
        {
            string marker = Path.Combine(root, ".plastic", "batch-mode");
            if (!File.Exists(marker)) return 99;
            string mode = File.ReadAllText(marker);
            if (mode == "unsupported") return 99;
            if (mode == "malformed") { Console.WriteLine("<LogList>"); return 0; }
            if (mode == "slow") { File.WriteAllText(Path.Combine(root, ".plastic", "log-entered"), ""); Thread.Sleep(5000); }
            if (mode == "switch-repository") File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"other@server:8087\"");
            int top = Int32.Parse(args[1].Substring(3).Split('@')[0]), bottom = Int32.Parse(args.Single(arg => arg.StartsWith("--from=cs:")).Substring(10).Split('@')[0]);
            var records = new List<XElement>();
            for (int id = top; id > bottom; id--)
            {
                if (mode == "missing" && id == top) continue;
                string source = id == 7 ? "/folder/file.txt" : id == 6 ? "/folderish/other.txt" : id == 5 ? "/folder/file.txt" : id == 4 ? "/original.txt" : id == 3 || id == 2 ? "/folder" : "/folder/file.txt";
                string destination = id == 5 ? "/elsewhere.txt" : id == 4 ? "/folder/file.txt" : id == 3 ? "/other-directory" : source;
                var item = new XElement("Item", new XElement("Type", id >= 6 ? "Changed" : id >= 3 ? "Moved" : id == 2 ? "Deleted" : "Added"), new XElement("SrcCmPath", source), new XElement("DstCmPath", destination), new XElement("RevNo", 1));
                if (mode == "path") item.Element("SrcCmPath").Value = "relative/path";
                if (mode == "field") item.Element("DstCmPath").Remove();
                if (mode == "repeated-field") item.Add(new XElement("DstCmPath", "/other"));
                if (mode == "nested-field") item.Element("DstCmPath").Add(new XElement("Nested", "bad"));
                if (mode == "unknown-type") item.Element("Type").Value = "Unknown";
                var record = new XElement("Changeset", new XElement("ChangesetId", mode == "foreign" ? 9000 + id : id),
                    new XElement("Branch", mode == "branch" ? "/other" : id == 3 ? "/main/quoted '\u4e2d\u6587'" : id % 2 == 0 ? "/main/topic" : "/main"), new XElement("Changes", item));
                if (mode == "no-changes") record.Element("Changes").Remove();
                records.Add(record);
                if (mode == "duplicate") records.Add(new XElement(record));
            }
            Console.WriteLine(new XElement("LogList", records)); return 0;
        }
        if (args[0] == "diff")
        {
            if (File.Exists(Path.Combine(root, ".plastic", "diff-fail"))) return 9;
            int cs = Int32.Parse(args[1].Substring(3).Split('@')[0]);
            Console.WriteLine(cs == 7 ? "C|/folder/file.txt|F||" : cs == 6 ? "C|/folderish/other.txt|F||" :
                cs == 5 ? "M|/elsewhere.txt|F|/folder/file.txt|/elsewhere.txt" : cs == 4 ? "M|/folder/file.txt|F|/original.txt|/folder/file.txt" :
                cs == 3 ? "M|/other-directory|D|/folder|/other-directory" : cs == 2 ? "D|/folder|D||" : "A|/folder/file.txt|F||"); return 0;
        }
        return 99;
    }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidDataException) { rejected = true; } catch (PlasticCommandException) { rejected = true; }
        Check(rejected, message);
    }
}
