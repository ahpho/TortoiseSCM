// GPL-2.0-or-later. Root-watermark history tests in an isolated native-process double.
using System;
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
    private const string Repository = "test@server:8087";
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (args.Length != 0) return Fake(args);
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: root history watermark (" + assertions + " assertions)"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "TSCM-history-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        Directory.CreateDirectory(Path.Combine(root, "child"));
        Write(root, "plastic.workspace", "History test\r\nunique-workspace-id\r\nStandard\r\n");
        Selector(root, "/main");
        Write(root, "loaded", "10"); Write(root, "root-revision", "10"); Write(root, "head", "12");
        File.WriteAllText(Path.Combine(root, "child", "file.txt"), "content");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location });
        try
        {
            var state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.IsWorkspaceRoot && !state.IsApproximate && state.LoadedThroughChangeset == 10, "Standard workspace uses one native loaded changeset");
            Require(!await Missing(client, state, 0) && !await Missing(client, state, 1) && !await Missing(client, state, 10) && await Missing(client, state, 11), "Only root versions beyond its watermark are bold; bootstrap revisions stay ordinary");
            int calls = Calls(root).Length;
            var child = await client.GetHistoryLocalStateAsync(Path.Combine(root, "child"), Token);
            var file = await client.GetHistoryLocalStateAsync(Path.Combine(root, "child", "file.txt"), Token);
            Require(!child.IsWorkspaceRoot && !await Missing(client, child, 12) && !await Missing(client, file, 12), "Child directories and file histories are always ordinary");
            Require(Calls(root).Length == calls, "Child local-state checks start no native processes");
            var rows = await client.GetHistoryNotLoadedAsync(state, Enumerable.Range(0, 1000).Select(cs => Entry(cs)).ToArray(), Token);
            Require(rows.Count == 1000 && rows[999] && !rows[10] && Calls(root).Length == calls, "A thousand row comparisons use only the root number, with no native calls");
            Write(root, "loaded", "5");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.LoadedThroughChangeset == 5 && await Missing(client, state, 10), "Standard rollback immediately makes later changesets bold again");
            Write(root, "partial", ""); Write(root, "root-revision", "10");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.IsApproximate && state.LoadedThroughChangeset == 10 && !await Missing(client, state, 10), "An existing Gluon workspace gets one approximate root-only initial value");
            Require(File.Exists(Meta(root, "tortoisescm-root-update.xml")) && Calls(root).Count(line => line.StartsWith("fileinfo ")) == 1, "The approximate initial value is persisted after one root-only fileinfo");
            Write(root, "root-revision", "99");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.LoadedThroughChangeset == 10 && Calls(root).Count(line => line.StartsWith("fileinfo ")) == 1, "Refreshing does not replace the saved watermark with changing root metadata");
            var result = await Update(client, root, Path.Combine(root, "child"));
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(result.Succeeded && state.LoadedThroughChangeset == 10 && await Missing(client, state, 12), "Successful child update cannot clear root history bold");
            result = await Update(client, root, Path.Combine(root, "child", "file.txt"));
            Require(result.Succeeded && (await client.GetHistoryLocalStateAsync(root, Token)).LoadedThroughChangeset == 10, "Successful file update cannot advance root watermark");
            result = await Update(client, root, root);
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(result.Succeeded && !state.IsApproximate && state.LoadedThroughChangeset == 12 && !await Missing(client, state, 12), "Successful Gluon root update replaces the approximate record with its captured target");
            Write(root, "head", "15"); Write(root, "fail-update", "");
            result = await Update(client, root, root);
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(!result.Succeeded && state.LoadedThroughChangeset == 12 && await Missing(client, state, 15), "Failed root update leaves the previous watermark unchanged");
            File.Delete(Meta(root, "fail-update"));
            Write(root, "advance-during-update", "");
            result = await Update(client, root, root);
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(result.Succeeded && state.LoadedThroughChangeset == 15 && await Missing(client, state, 16), "A head published during update is not incorrectly marked downloaded");
            File.Delete(Meta(root, "advance-during-update"));
            var rollback = new PlasticProcessCommand { FileName = Assembly.GetExecutingAssembly().Location, WorkingDirectory = root,
                Arguments = new[] { "partial", "update", root, "--changeset=5", "--report" } };
            var capture = await client.PrepareHistoryRootUpdateAsync(rollback, Token);
            result = await client.ExecuteAsync(rollback, Token);
            await client.CompleteHistoryRootUpdateAsync(capture, result);
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.LoadedThroughChangeset == 5 && await Missing(client, state, 12), "A successful pinned root rollback replaces rather than maximizes the watermark");
            Require(Calls(root).All(line => !line.StartsWith("ls ") && !line.StartsWith("log ") && !line.StartsWith("diff ") && !line.StartsWith("find revision ")), "Root history never scans all files, ancestors, revisions, or per-row diffs");
            result = await client.SwitchAsync(root, 8, Token);
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(result.Succeeded && state.LoadedThroughChangeset == 8 && await Missing(client, state, 12), "The actual Gluon history snapshot switch records its requested root changeset");
            Write(root, "fail-update", "");
            result = await client.SwitchAsync(root, 12, Token);
            Require(!result.Succeeded && (await client.GetHistoryLocalStateAsync(root, Token)).LoadedThroughChangeset == 8, "A failed history snapshot switch cannot change the root watermark");
            File.Delete(Meta(root, "fail-update")); Write(root, "slow-update", "");
            using (var cancellation = new CancellationTokenSource())
            {
                var switching = client.SwitchAsync(root, 12, cancellation.Token);
                await WaitFor(root, "update-entered"); cancellation.Cancel();
                bool cancelled = false;
                try { await switching; } catch (OperationCanceledException) { cancelled = true; }
                Require(cancelled && (await client.GetHistoryLocalStateAsync(root, Token)).LoadedThroughChangeset == 8, "Cancelling the native snapshot update leaves the previous record untouched");
            }
            File.Delete(Meta(root, "slow-update")); File.Delete(Meta(root, "update-entered"));
            Selector(root, "/main/other"); Write(root, "root-revision", "3");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.IsApproximate && state.LoadedThroughChangeset == 3 && await Missing(client, state, 5), "A changed selector never reuses another branch's saved watermark");
            Selector(root, "/main");
            bool invalidated = false;
            try { await Missing(client, state, 5); } catch (InvalidOperationException) { invalidated = true; }
            Require(invalidated, "An in-flight state is invalidated when the workspace selector changes");
            File.Delete(Meta(root, "tortoisescm-root-update.xml")); Write(root, "root-revision", "-1");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.LoadedThroughChangeset == 1 && !await Missing(client, state, 0) && !await Missing(client, state, 1) && await Missing(client, state, 2), "Unavailable root revisions safely fall back while preserving both initial revisions");
            client.RecordHistoryRootLoaded(client.DiscoverWorkspace(root), 20);
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(!state.IsApproximate && state.LoadedThroughChangeset == 20 && !await Missing(client, state, 20), "A confirmed initial workspace download establishes its root watermark");
            Write(root, "plastic.workspace", "History test\nrecreated-workspace-id\nStandard\n"); Write(root, "root-revision", "4");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.IsApproximate && state.LoadedThroughChangeset == 4, "A recreated workspace cannot inherit another workspace identity's record");
            Write(root, "tortoisescm-root-update.xml", "<broken"); Write(root, "root-revision", "6");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Require(state.IsApproximate && state.LoadedThroughChangeset == 6, "A corrupt local marker is replaced by the inexpensive approximate initializer");
            File.Delete(Meta(root, "tortoisescm-root-update.xml")); Write(root, "slow-fileinfo", "");
            var initializing = client.GetHistoryLocalStateAsync(root, Token);
            await WaitFor(root, "fileinfo-entered");
            client.RecordHistoryRootLoaded(client.DiscoverWorkspace(root), 40);
            state = await initializing;
            Require(!state.IsApproximate && state.LoadedThroughChangeset == 40 && (await client.GetHistoryLocalStateAsync(root, Token)).LoadedThroughChangeset == 40,
                "A delayed approximate initializer cannot overwrite an exact record written by another window");
            File.Delete(Meta(root, "tortoisescm-root-update.xml")); File.Delete(Meta(root, "fileinfo-entered"));
            initializing = client.GetHistoryLocalStateAsync(root, Token);
            await WaitFor(root, "fileinfo-entered");
            Write(root, "plastic.workspace", "History test\nidentity-replaced-during-await\nStandard\n");
            bool identityChanged = false;
            try { await initializing; } catch (InvalidOperationException) { identityChanged = true; }
            Require(identityChanged && !File.Exists(Meta(root, "tortoisescm-root-update.xml")), "An old asynchronous root read cannot publish a marker for a recreated workspace");
            File.Delete(Meta(root, "slow-fileinfo")); File.Delete(Meta(root, "fileinfo-entered"));
            var staleCapture = await client.PrepareHistoryRootUpdateAsync(rollback, Token);
            Write(root, "plastic.workspace", "History test\nidentity-replaced-during-update\nStandard\n");
            await client.CompleteHistoryRootUpdateAsync(staleCapture, new PlasticCommandResult { ExitCode = 0 });
            Require(!File.Exists(Meta(root, "tortoisescm-root-update.xml")), "A root update capture is bound to the original workspace identity");
            state = await client.GetHistoryLocalStateAsync(root, Token);
            Write(root, "broken-status", "");
            bool failed = false;
            try { await client.GetHistoryLocalStateAsync(root, Token); } catch (InvalidDataException) { failed = true; }
            Require(failed, "Malformed authoritative status is not treated as all history downloaded");
            File.Delete(Meta(root, "broken-status"));
            failed = false;
            try { await client.GetHistoryLocalStateAsync(root, new CancellationToken(true)); } catch (OperationCanceledException) { failed = true; }
            Require(failed, "Root snapshot honors cancellation before native work");
            failed = false;
            try { await client.GetHistoryNotLoadedAsync(state, new[] { Entry(1) }, new CancellationToken(true)); } catch (OperationCanceledException) { failed = true; }
            Require(failed, "Pure row evaluation still honors cancellation");
            Require(Calls(root).All(line => !line.StartsWith("ls ") && !line.StartsWith("log ") && !line.StartsWith("find revision ")), "Additional snapshot operations do not introduce file scans or ancestry queries");
        }
        finally { Directory.Delete(root, true); }
    }

    private static Task<PlasticCommandResult> Update(PlasticClient client, string root, string selected)
    { return client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = root, Paths = new[] { selected }.ToList() }, Token); }
    private static Task<bool> Missing(PlasticClient client, PlasticHistoryLocalState state, long cs)
    { return client.IsHistoryNotLoadedAsync(state, Entry(cs), Token); }
    private static PlasticHistoryItem Entry(long cs) { return new PlasticHistoryItem { Changeset = cs, Repository = Repository }; }
    private static string Meta(string root, string name) { return Path.Combine(root, ".plastic", name); }
    private static void Write(string root, string name, string value) { File.WriteAllText(Meta(root, name), value); }
    private static string Read(string root, string name) { return File.ReadAllText(Meta(root, name)); }
    private static string[] Calls(string root) { return File.ReadAllLines(Meta(root, "calls")); }
    private static void Selector(string root, string branch) { Write(root, "plastic.selector", "repository \"" + Repository + "\"\r\n  path \"/\"\r\n    smartbranch \"" + branch + "\"\r\n"); }
    private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static async Task WaitFor(string root, string name)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(Meta(root, name)) && DateTime.UtcNow < deadline) await Task.Delay(10);
        Require(File.Exists(Meta(root, name)), "The delayed native operation reached " + name);
    }

    private static int Fake(string[] args)
    {
        string root = Environment.CurrentDirectory;
        File.AppendAllText(Meta(root, "calls"), String.Join(" ", args) + "\n");
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "status")
        {
            if (File.Exists(Meta(root, "broken-status"))) { Console.WriteLine("<invalid/>"); return 0; }
            Console.WriteLine(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status",
                new XElement("Changeset", File.Exists(Meta(root, "partial")) ? "-1" : Read(root, "loaded")),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))))); return 0;
        }
        if (args[0] == "fileinfo")
        {
            if (File.Exists(Meta(root, "slow-fileinfo"))) { Write(root, "fileinfo-entered", ""); Thread.Sleep(500); }
            Console.WriteLine(new XElement("FileInfos", new XElement("FileInfo", new XElement("ClientPath", root),
                new XElement("RevisionChangeset", Read(root, "root-revision")), new XElement("RepSpec", Repository)))); return 0;
        }
        if (args[0] == "find" && args[1] == "branch")
        {
            Console.WriteLine(new XElement("PLASTICQUERY", new XElement("BRANCH", new XElement("NAME", "/main"),
                new XElement("CHANGESET", Read(root, "head")), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087")))); return 0;
        }
        if (args[0] == "find" && args[1] == "changeset")
        {
            long cs = Int64.Parse(args[2].Substring("where changesetid = ".Length));
            Console.WriteLine(new XElement("PLASTICQUERY", new XElement("CHANGESET", new XElement("CHANGESETID", cs), new XElement("REPOSITORY", Repository)))); return 0;
        }
        if (args[0] == "diff") { Console.WriteLine("C|/child/file.txt|F||"); return 0; }
        int verb = args[0] == "partial" ? 1 : 0;
        if (args[verb] == "update")
        {
            if (File.Exists(Meta(root, "slow-update"))) { Write(root, "update-entered", ""); Thread.Sleep(10000); }
            if (File.Exists(Meta(root, "fail-update"))) { Console.Error.WriteLine("Expected update failure"); return 17; }
            string pinned = args.FirstOrDefault(argument => argument.StartsWith("--changeset="));
            string target = pinned == null ? Read(root, "head") : pinned.Substring("--changeset=".Length);
            Write(root, "root-revision", target);
            if (verb == 0) Write(root, "loaded", target);
            if (File.Exists(Meta(root, "advance-during-update"))) Write(root, "head", (Int64.Parse(target) + 1).ToString());
            return 0;
        }
        Console.Error.WriteLine("Unexpected native call: " + String.Join(" ", args)); return 96;
    }
}
