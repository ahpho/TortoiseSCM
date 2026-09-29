// GPL-2.0-or-later. Native-command contract and isolated Gluon regression tests.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TortoiseSCM;

internal static class HistoryLocalStateTests
{
    private static int assertions;
    private static readonly CancellationToken Token = CancellationToken.None;
    private const string Repository = "test@server:8087";

    private static int Main(string[] args)
    {
        if (args.Length > 0 && new[] { "status", "ls", "log", "diff", "find" }.Contains(args[0])) return Fake(args);
        try
        {
            if (args.Length == 3 && args[0] == "--read-only") ReadOnly(args[1], Int32.Parse(args[2])).GetAwaiter().GetResult();
            else if (args.Length == 5 && args[0] == "--live") Live(args).GetAwaiter().GetResult();
            else if (args.Length == 0) Run().GetAwaiter().GetResult();
            else throw new ArgumentException("Use --read-only <workspace> <expected-bold-count> or --live <producer> <partial> <repository> <branch>.");
            Console.WriteLine("PASS: history local state (" + assertions + " assertions)"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-history-local-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "history-local\nguid\nStandard\n");
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n branch \"/main\"");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
        try
        {
            var state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(!await Missing(client, state, 10), "Complete workspace contains its loaded changeset");
            Require(!await Missing(client, state, 2), "Complete workspace includes merged/parent ancestors");
            Require(!await Missing(client, state, 4), "An older unmerged branch is not an incoming update");
            Require(await Missing(client, state, 11), "A newer commit is not loaded");
            Require(await Missing(client, state, 7), "An incoming ancestor remains bold regardless of its older changeset number");
            string selector = File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"\n changeset \"10\"");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(!await Missing(client, state, 11), "A pinned workspace does not claim later branch changes need downloading");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector);
            File.WriteAllText(Path.Combine(root, ".plastic", "complete"), "");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            for (long cs = 0; cs <= 12; cs++) Require(!await Missing(client, state, cs), "Fully updated standard workspace has no bold rows");
            File.Delete(Path.Combine(root, ".plastic", "complete"));
            File.WriteAllText(Path.Combine(root, ".plastic", "partial"), "");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            int calls = File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Length;
            Require(!await Missing(client, state, 4), "Unmerged branch is also regular in mixed-version workspaces");
            for (long unrelated = 100; unrelated < 1100; unrelated++)
                Require(!await Missing(client, state, unrelated), "Unrelated history is not pending download");
            Require(File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Length == calls,
                "A thousand unrelated rows need zero additional native processes");
            Require(await Missing(client, state, 5), "Upper directory stays bold when only one of two changed files is pulled");
            var fileState = await client.GetHistoryLocalStateAsync(Path.Combine(root, "new.txt"), Token);
            Require(!await Missing(client, fileState, 5), "File history only considers its selected scope");
            Require(!await Missing(client, state, 6), "A later revision includes an earlier file change");
            Require(!await Missing(client, state, 7), "Republishing an older revision already loaded locally is not missing");
            Require(await Missing(client, state, 8), "A deletion whose old revision remains local is missing");
            Require(!await Missing(client, state, 9), "A deletion already absent locally is applied");
            Require(await Missing(client, state, 12), "An incoming rename is missing until the destination is loaded");
            int diffCalls = File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Count(line => line.StartsWith("diff "));
            var details = await client.GetHistoryDetailsAsync(root, new PlasticHistoryItem { Changeset = 5, Repository = Repository }, Token);
            Require(details.Files.Count == 2 && File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Count(line => line.StartsWith("diff ")) == diffCalls,
                "Selecting history reuses the exact diff already read for bold state");
            File.WriteAllText(Path.Combine(root, ".plastic", "updated"), "");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(!await Missing(client, state, 5), "Refreshing after the remaining file is pulled clears upper-directory bold");
            Require(!await Missing(client, state, 12), "A pulled rename uses the destination and unchanged revision identity");
            Require(!await Missing(client, state, 6), "Old file history remains loaded after a later rename");
            Require(File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Count(line => line.StartsWith("diff ")) == diffCalls,
                "Fresh local snapshot reuses immutable diffs while recomputing mixed-version state");
            File.WriteAllText(Path.Combine(root, ".plastic", "directory"), "");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(await Missing(client, state, 11), "Loading a moved directory does not hide children still at the old path");
            var destinationState = await client.GetHistoryLocalStateAsync(Path.Combine(root, "new-dir", "child.txt"), Token);
            Require(await Missing(client, destinationState, 11), "History at the move destination still detects an unpulled child at the old path");
            File.WriteAllText(Path.Combine(root, ".plastic", "directory-updated"), "");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(!await Missing(client, state, 11), "Moving the remaining children clears the directory marker");
            int revisionCalls = File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Count(line => line.StartsWith("find revision "));
            Require(!await Missing(client, state, 10), "Batched item identity resolution preserves ancestry semantics");
            Require(File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Count(line => line.StartsWith("find revision ")) - revisionCalls == 2,
                "140 unresolved revision identities require two bounded queries, not 140 processes");
            var rows = await client.GetHistoryNotLoadedAsync(state, Enumerable.Range(5, 8).Select(cs => new PlasticHistoryItem { Changeset = cs, Repository = Repository }).ToArray(), Token);
            Require(!rows[11] && !rows[12] && rows[8], "Batch results reflect the same fresh snapshot, including pending deletion");
            File.WriteAllText(Path.Combine(root, ".plastic", "broken"), "");
            bool failed = false;
            try { await client.GetHistoryLocalStateAsync(root, Token); } catch (InvalidDataException) { failed = true; }
            Require(failed, "Malformed local metadata is unknown rather than marking all history loaded");
            var cancelled = new CancellationToken(true);
            failed = false;
            try { await client.GetHistoryLocalStateAsync(root, cancelled); } catch (OperationCanceledException) { failed = true; }
            Require(failed, "Local-state reads honor cancellation");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<bool> Missing(PlasticClient client, PlasticHistoryLocalState state, long cs)
    { return client.IsHistoryNotLoadedAsync(state, new PlasticHistoryItem { Changeset = cs, Repository = state.Workspace.Repository }, Token); }

    private static int Fake(string[] args)
    {
        string root = Environment.CurrentDirectory;
        bool partial = File.Exists(Path.Combine(root, ".plastic", "partial"));
        bool updated = File.Exists(Path.Combine(root, ".plastic", "updated"));
        File.AppendAllText(Path.Combine(root, ".plastic", "calls"), String.Join(" ", args) + "\n");
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "status")
        {
            if (File.Exists(Path.Combine(root, ".plastic", "broken"))) { Console.WriteLine("<invalid/>"); return 0; }
            Console.WriteLine(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status",
                new XElement("Changeset", partial ? -1 : File.Exists(Path.Combine(root, ".plastic", "complete")) ? 12 : 10), new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))),
                new XElement("WkConfigName", "/main@" + Repository))); return 0;
        }
        if (args[0] == "ls")
        {
            Console.WriteLine(new XElement("LsResults", new XElement("LsItems", Item(root, "", 1, 0),
                Item(root, updated ? "renamed.txt" : "new.txt", 100, 10), Item(root, "stale.txt", updated ? 50 : 20, updated ? 5 : 2),
                File.Exists(Path.Combine(root, ".plastic", "directory")) ? new[] {
                    Item(root, "new-dir", 500, 11), Item(root, File.Exists(Path.Combine(root, ".plastic", "directory-updated")) ? "new-dir/child.txt" : "old-dir/child.txt", 600, 2) } : null))); return 0;
        }
        if (args[0] == "log")
        {
            long top = Int64.Parse(args[1].Substring(3).Split('@')[0]);
            long[] ids = top == 12 ? new long[] { 12, 11, 10, 9, 8, 7, 6, 5, 2, 0 } : top == 10 ? new long[] { 10, 6, 5, 2, 0 } : top == 5 ? new long[] { 5, 2, 0 } : top == 2 ? new long[] { 2, 0 } : new long[] { 0 };
            Console.WriteLine(new XElement("LogList", ids.Select(id => new XElement("Changeset", new XElement("ChangesetId", id))))); return 0;
        }
        if (args[0] == "find" && args[1] == "branch")
        {
            Console.WriteLine(new XElement("PLASTICQUERY", new XElement("BRANCH", new XElement("NAME", "/main"),
                new XElement("CHANGESET", 12), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087")))); return 0;
        }
        if (args[0] == "find")
        {
            var revisions = System.Text.RegularExpressions.Regex.Matches(args[2], @"id = (\d+)").Cast<System.Text.RegularExpressions.Match>().Select(match => Int64.Parse(match.Groups[1].Value));
            Console.WriteLine(new XElement("PLASTICQUERY", revisions.Select(revision => new XElement("REVISION", new XElement("ID", revision),
                new XElement("ITEMID", revision == 20 || revision == 50 ? 2000 : revision == 99 ? 3000 : 1000),
                new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"))))); return 0;
        }
        long cs = Int64.Parse(args[1].Substring(3).Split('@')[0]);
        switch (cs)
        {
            case 5: Console.WriteLine("C|/new.txt|F|||60\nC|/stale.txt|F|||50"); break;
            case 6: Console.WriteLine("C|/new.txt|F|||100"); break;
            case 7: Console.WriteLine("C|/stale.txt|F|||20"); break;
            case 8: Console.WriteLine("D|/stale.txt|F|||20"); break;
            case 9: Console.WriteLine("D|/gone.txt|F|||99"); break;
            case 10: for (int i = 0; i < 140; i++) Console.WriteLine("C|/batch-" + i + ".txt|F|||" + (10000 + i)); break;
            case 11: Console.WriteLine("M|/old-dir|D|/old-dir|/new-dir|500"); break;
            case 12: Console.WriteLine("M|/new.txt|F|/new.txt|/renamed.txt|100"); break;
            default: return 8;
        }
        return 0;
    }

    private static XElement Item(string root, string name, long revision, long cs)
    { return new XElement("LsItem", new XElement("CurrentPath", Path.Combine(root, name)), new XElement("RevId", revision),
        new XElement("Changeset", cs), new XElement("ItemId", name == "" ? 1 : name == "stale.txt" ? 2000 : revision == 500 ? 5000 : revision == 600 ? 6000 : 1000), new XElement("Repository", "rep:" + Repository)); }

    private static async Task ReadOnly(string path, int expectedBold)
    {
        var client = new PlasticClient(new PlasticClientConfig());
        var watch = Stopwatch.StartNew();
        var entries = new System.Collections.Generic.List<PlasticHistoryItem>();
        var state = await client.GetHistoryLocalStateAsync(path, Token);
        Console.WriteLine("Local snapshot ms: " + watch.ElapsedMilliseconds + "; update head: " + state.HeadChangeset);
        int rows = 0, bold = 0; long? before = null;
        do
        {
            var page = await client.GetHistoryPageAsync(path, before, 100, Token);
            entries.AddRange(page.Items);
            rows += page.Items.Count;
            bold += (await client.GetHistoryNotLoadedAsync(state, page.Items, Token)).Values.Count(value => value);
            before = page.HasMore ? page.NextBeforeChangeset : null;
        } while (before.HasValue);
        Console.WriteLine("History rows: " + rows + "; bold: " + bold + "; total ms: " + watch.ElapsedMilliseconds);
        Require(bold == expectedBold, "Read-only real workspace has expected incoming history markers");
        watch.Restart();
        state = await client.GetHistoryLocalStateAsync(path, Token);
        var refreshed = await client.GetHistoryNotLoadedAsync(state, entries, Token);
        Console.WriteLine("Reactivated local snapshot + markers ms: " + watch.ElapsedMilliseconds);
        Require(refreshed.Values.Count(value => value) == expectedBold, "Cached history facts preserve fresh local-state results");
        var selected = entries.First(item => item.Changeset > 0);
        for (int pass = 0; pass < 2; pass++)
        {
            watch.Restart();
            var details = await client.GetHistoryDetailsAsync(path, selected, Token);
            Console.WriteLine("Details pass " + pass + ": " + watch.ElapsedMilliseconds + " ms; files " + details.Files.Count);
            watch.Restart();
            await client.GetChangesetParentComparisonAsync(path, selected.Changeset, selected.Repository, Token);
            Console.WriteLine("Parent comparison pass " + pass + ": " + watch.ElapsedMilliseconds + " ms");
        }
    }

    private static async Task Live(string[] args)
    {
        string producer = args[1], partial = args[2], repository = args[3], branch = args[4];
        var client = new PlasticClient(new PlasticClientConfig());
        Require(branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal) &&
            client.DiscoverWorkspace(producer).Selector.Contains(branch) && client.DiscoverWorkspace(partial).Selector.Contains(branch), "Live writes are isolated to the test branch");
        string folder = Path.Combine(producer, "mixed"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "a.txt"), "base a"); File.WriteAllText(Path.Combine(folder, "b.txt"), "base b");
        Native(producer, "add", folder, "-R"); Native(producer, "checkin", producer, "--all", "-c=History local-state baseline");
        Native(partial, "partial", "configure", "+/mixed");
        File.WriteAllText(Path.Combine(folder, "a.txt"), "new a"); File.WriteAllText(Path.Combine(folder, "b.txt"), "new b");
        Native(producer, "checkin", producer, "--all", "-c=History local-state mixed versions");
        long cs = Int64.Parse(XDocument.Parse(Native(producer, "status", "--header", "--xml", "--encoding=utf-8")).Descendants("Changeset").Single().Value);
        Native(partial, "partial", "update", Path.Combine(partial, "mixed", "a.txt"), "--report");
        var state = await client.GetHistoryLocalStateAsync(Path.Combine(partial, "mixed"), Token);
        Require(await Missing(client, state, cs), "Real Gluon parent directory remains bold with one stale file");
        state = await client.GetHistoryLocalStateAsync(Path.Combine(partial, "mixed", "a.txt"), Token);
        Require(!await Missing(client, state, cs), "Real Gluon pulled file is regular");
        state = await client.GetHistoryLocalStateAsync(Path.Combine(partial, "mixed", "b.txt"), Token);
        Require(await Missing(client, state, cs), "Real Gluon stale file is bold");
        Native(partial, "partial", "update", Path.Combine(partial, "mixed", "b.txt"), "--report");
        state = await client.GetHistoryLocalStateAsync(Path.Combine(partial, "mixed"), Token);
        Require(!await Missing(client, state, cs), "Real Gluon parent becomes regular when both files are pulled");
        Native(partial, "partial", "configure", "-/mixed/b.txt");
        state = await client.GetHistoryLocalStateAsync(Path.Combine(partial, "mixed"), Token);
        Require(await Missing(client, state, cs), "An unloaded file keeps its parent history bold");
        Native(partial, "partial", "configure", "+/mixed/b.txt");
        state = await client.GetHistoryLocalStateAsync(Path.Combine(partial, "mixed"), Token);
        Require(!await Missing(client, state, cs), "Loading the omitted file clears the parent marker");
        Native(producer, "move", Path.Combine(folder, "a.txt"), Path.Combine(folder, "renamed.txt"));
        Native(producer, "remove", Path.Combine(folder, "b.txt"));
        Native(producer, "checkin", producer, "--all", "-c=History local-state move and deletion");
        long structural = Int64.Parse(XDocument.Parse(Native(producer, "status", "--header", "--xml", "--encoding=utf-8")).Descendants("Changeset").Single().Value);
        state = await client.GetHistoryLocalStateAsync(Path.Combine(partial, "mixed"), Token);
        Require(await Missing(client, state, structural), "Real Gluon pending move/deletion is bold");
        Native(partial, "partial", "update", Path.Combine(partial, "mixed"), "--report");
        state = await client.GetHistoryLocalStateAsync(Path.Combine(partial, "mixed"), Token);
        Require(!await Missing(client, state, structural), "Real Gluon applied move/deletion is regular");
        Require(!await Missing(client, state, cs), "Earlier changes stay regular after a later move/deletion");
        Console.WriteLine("Live workspace: " + partial + " repository: " + repository);
    }

    private static string Native(string root, params string[] args)
    {
        var command = new PlasticProcessCommand { FileName = new PlasticClientConfig().CmPath, WorkingDirectory = root, Arguments = args };
        var client = new PlasticClient(new PlasticClientConfig());
        var result = client.ExecuteAsync(command, Token).GetAwaiter().GetResult();
        if (!result.Succeeded) throw new Exception("Native " + String.Join(" ", args) + ": " + result.Output + result.Error);
        return result.Output;
    }

    private static void Require(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
}
