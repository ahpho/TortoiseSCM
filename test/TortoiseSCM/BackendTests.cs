// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TortoiseSCM;

internal static class BackendTests
{
    private static int assertions;
    private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message) { bool failed = false; try { action(); } catch (ArgumentException) { failed = true; } Assert(failed, message); }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "status")
        {
            string changeset = File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-partial")) ? "-1" : "0";
            Console.WriteLine("<StatusOutput><WorkspaceStatus><Status><Changeset>" + changeset + "</Changeset></Status></WorkspaceStatus></StatusOutput>");
            return 0;
        }
        if (args.Length > 1 && args[0] == "partial" && args[1] == "update")
        {
            File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "update-calls.log"), "partial " + args[2] + Environment.NewLine);
            Console.WriteLine("Updated " + args[2]);
            if (Path.GetFileName(args[2]) == "fail.txt") { Console.Error.WriteLine("Deliberate update failure"); return 7; }
            return 0;
        }
        if (args.Length > 0 && args[0] == "update")
        {
            // Fake cm: records the exact requested scope without touching repository data.
            Console.OutputEncoding = Encoding.UTF8;
            File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "update-calls.log"), args[1] + Environment.NewLine);
            Console.WriteLine("Updated " + args[1]);
            if (Path.GetFileName(args[1]) == "fail.txt") { Console.Error.WriteLine("Deliberate update failure"); return 7; }
            return 0;
        }
        if (args.Length > 0 && args[0] == "--helper")
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args[1] == "wait") Thread.Sleep(30000);
            else if (args[1] == "output") { for (int i = 0; i < 20000; i++) { Console.Out.WriteLine("stdout 中文 " + i); Console.Error.WriteLine("stderr 中文 " + i); } }
            else foreach (string value in args.Skip(2)) Console.WriteLine(value);
            return 0;
        }
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, ".plastic"));
        File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "Test\r\n00000000-0000-0000-0000-000000000000\r\nStandard\r\n");
        try
        {
            var config = new PlasticClientConfig();
            var client = new PlasticClient(config);
            Assert(client.DiscoverWorkspace(Path.Combine(temporary, "deleted.txt")).RootPath == temporary, "Deleted path workspace discovery");
            Assert(client.DiscoverWorkspace(temporary + "\\").RootPath == temporary, "Trailing separator normalized");
            Assert(client.DiscoverWorkspace(Path.Combine(temporary, ".plastic", "plastic.workspace")) == null, "Metadata workspace discovery rejected");
            var request = new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = temporary, Comment = "quoted \" 中文 & | comment" };
            Reject(delegate { client.Build(request); }, "Empty checkin selection rejected");
            request.Paths.Add(Path.Combine(temporary, "中文 space.txt"));
            var built = client.Build(request);
            Assert(built.Arguments.Contains("-c=" + request.Comment), "Checkin comment remains one argument");
            Assert(built.Arguments.Contains(request.Paths[0]), "Selected path retained");
            request.Paths[0] = Path.GetTempPath(); Reject(delegate { client.Build(request); }, "Outside workspace rejected");
            request.Paths[0] = Path.Combine(temporary, ".plastic", "plastic.workspace"); Reject(delegate { client.Build(request); }, "Metadata rejected");
            request.Paths[0] = Path.Combine(temporary, "*.txt"); Reject(delegate { client.Build(request); }, "Wildcard rejected");
            request.Paths[0] = Path.Combine(temporary, "file.txt");
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "Test\r\nguid\r\nPartial\r\n");
            Assert(client.DiscoverWorkspace(temporary).IsPartial, "Partial workspace detection");
            built = client.Build(request); Assert(built.Arguments[0] == "partial" && built.Arguments[1] == "checkin", "Partial command routing");
            string xml = "<StatusOutput><Changes><Change><Type>MV</Type><TypeVerbose>Moved</TypeVerbose><Path>new.txt</Path><OldPath>old.txt</OldPath><RevisionType>enTextFile</RevisionType></Change><Change><Type>DE</Type><Path>gone</Path><RevisionType>enDirectory</RevisionType></Change></Changes></StatusOutput>";
            var parsed = PlasticClient.ParseStatus(xml, temporary);
            Assert(parsed[0].Status == "MV" && parsed[0].OldPath == Path.Combine(temporary, "old.txt"), "Moved status preserves source and destination");
            Assert(parsed[1].IsDirectory, "Deleted directory parsed from revision type");
            bool rejectedPath = false;
            try { PlasticClient.ParseStatus("<StatusOutput><Changes><Change><Type>AD</Type><Path>..\\outside.txt</Path></Change></Changes></StatusOutput>", temporary); }
            catch (InvalidDataException) { rejectedPath = true; }
            Assert(rejectedPath, "Status paths cannot escape workspace");
            bool rejectedXml = false; try { PlasticClient.ParseStatus("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///does-not-exist'>]><StatusOutput>&e;</StatusOutput>", temporary); } catch (System.Xml.XmlException) { rejectedXml = true; }
            Assert(rejectedXml, "DTD prohibited");
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var difficult = new [] { "space 中文", "trailing\\", "embed\"quote", "& | % !", "" };
            var helperArgs = new List<string> { "--helper", "args" }; helperArgs.AddRange(difficult);
            var result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = helperArgs }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded && result.Output.TrimStart('\uFEFF') == String.Join(Environment.NewLine, difficult) + Environment.NewLine, "Windows argument quoting round trip");
            result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "output" } }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Output.Contains("stdout 中文 19999") && result.Error.Contains("stderr 中文 19999"), "Both redirected streams drain without deadlock");
            config.Timeout = TimeSpan.FromMilliseconds(200);
            result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "wait" } }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.TimedOut && !result.Succeeded, "Timeout terminates process");
            config.Timeout = TimeSpan.FromSeconds(30);
            using (var cancellation = new CancellationTokenSource(200))
            {
                bool cancelled = false;
                try { client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "wait" } }, cancellation.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled, "Cancellation terminates process");
            }
            UpdateTests(temporary, exe);
            ReadOperationTests();
            if (args.Length > 0 && args[0] == "--read-only")
            {
                string path = args[1];
                var detected = client.GetWorkspaceAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("Live workspace IsPartial=" + detected.IsPartial);
                var history = client.GetHistoryAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                Assert(history.Count > 0 && history[0].Changeset >= 0 && !String.IsNullOrEmpty(history[0].RevisionSpec), "Live structured history");
                var diff = client.GetDiffTextAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                Assert(diff.Path == Path.GetFullPath(path) && !String.IsNullOrEmpty(diff.BaseRevision), "Live headless base comparison");
                Assert(!diff.HasChanges && !diff.IsBinary && diff.DiffText == "", "Unchanged live text has no diff");
            }
            else if (args.Length > 0) LiveTests(client, args[0]);
            Console.WriteLine("PASS: " + assertions + " assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static void ReadOperationTests()
    {
        var diff = PlasticClient.CompareContent("中文 file.txt", "cs:1", Encoding.UTF8.GetBytes("first\nold\nlast\n"), Encoding.UTF8.GetBytes("first\nnew\nlast\n"), false);
        Assert(diff.HasChanges && !diff.IsBinary && diff.DiffText.Contains("-old\n+new\n") && diff.DiffText.Contains("@@ -1,3 +1,3 @@"), "Headless unified text diff");
        diff = PlasticClient.CompareContent("file", "cs:1", new byte[0], Encoding.UTF8.GetBytes("added"), false);
        Assert(diff.DiffText.Contains("@@ -0,0 +1,1 @@") && diff.DiffText.Contains("+added\n\\ No newline"), "Empty base and missing final newline");
        diff = PlasticClient.CompareContent("file", "cs:1", Encoding.UTF8.GetBytes("gone\n"), new byte[0], false);
        Assert(diff.DiffText.Contains("@@ -1,1 +0,0 @@") && diff.DiffText.Contains("-gone"), "Deleted text diff");
        diff = PlasticClient.CompareContent("bin", "cs:1", new byte[] { 0, 1 }, new byte[] { 0, 2 }, false);
        Assert(diff.IsBinary && diff.HasChanges && diff.DiffText.StartsWith("Binary files differ:"), "Binary content marked without replacement decoding");
        diff = PlasticClient.CompareContent("same", "cs:1", Encoding.UTF8.GetBytes("same"), Encoding.UTF8.GetBytes("same"), false);
        Assert(!diff.HasChanges && diff.DiffText == "", "Identical bytes produce no diff");
        byte[] utf16Old = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("旧\n")).ToArray();
        byte[] utf16New = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("新\n")).ToArray();
        diff = PlasticClient.CompareContent("utf16", "cs:1", utf16Old, utf16New, false);
        Assert(!diff.IsBinary && diff.DiffText.Contains("-旧\n+新\n"), "UTF-16 BOM text is decoded explicitly");
        string xml = "<RevisionHistoriesResult><RevisionHistories><RevisionHistory><ItemName>中文.txt</ItemName><Revisions><Revision><ChangesetNumber>42</ChangesetNumber><RevisionSpec>rev:x#cs:42</RevisionSpec><CreationDate>2026-09-24T12:00:00+08:00</CreationDate><Owner>user</Owner><Branch>/main</Branch><Comment>one &amp; two</Comment><Repository>repo</Repository></Revision></Revisions></RevisionHistory></RevisionHistories></RevisionHistoriesResult>";
        var history = PlasticClient.ParseHistory(xml);
        Assert(history.Count == 1 && history[0].Changeset == 42 && history[0].Comment == "one & two" && history[0].Path == "中文.txt", "Structured history retains identity and decoded comment");
        bool rejected = false;
        try { PlasticClient.ParseHistory("<Wrong />"); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Unexpected history XML rejected instead of empty success");
    }

    private static void UpdateTests(string temporary, string exe)
    {
        File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "Test\r\nguid\r\nStandard\r\n");
        File.WriteAllText(Path.Combine(temporary, "fake-partial"), "");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = exe });
        string first = Path.Combine(temporary, "first.txt"), second = Path.Combine(temporary, "second.txt"), failed = Path.Combine(temporary, "fail.txt");
        string log = Path.Combine(temporary, "update-calls.log");
        var request = new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = temporary, Paths = new List<string> { first, second } };
        var result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.Succeeded && File.ReadAllLines(log).SequenceEqual(new [] { "partial " + first, "partial " + second }), "Partial multi-select update preserves each exact scope");
        File.Delete(log);
        request.Paths = new List<string> { first, failed, second };
        result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.ExitCode == 7 && File.ReadAllLines(log).SequenceEqual(new [] { "partial " + first, "partial " + failed }), "Multi-select update stops on first failure");
        Assert(result.Output.Contains(first) && result.Output.Contains(failed) && result.Error.Contains("Deliberate"), "Failed multi-update preserves completed output");
        File.Delete(log);
        request.Paths = new List<string> { first, Path.Combine(Path.GetTempPath(), "outside.txt") };
        Reject(delegate { client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult(); }, "Multi-update validates later paths before first execution");
        Assert(!File.Exists(log), "No update runs before complete selection validation");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool cancelled = false;
            request.Paths = new List<string> { first, second };
            try { client.RunAsync(request, cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !File.Exists(log), "Cancelled build never starts an update");
        }
        File.WriteAllText(Path.Combine(temporary, "fake-partial"), "");
        Assert(!client.DiscoverWorkspace(temporary).IsPartial && client.GetWorkspaceAsync(temporary, CancellationToken.None).GetAwaiter().GetResult().IsPartial,
            "Actual partial status overrides stale Standard metadata");
        request.Paths = new List<string> { first };
        result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.Succeeded && File.ReadAllLines(log).SequenceEqual(new [] { "partial " + first }), "Mutation routes through actual partial mode");
        File.Delete(Path.Combine(temporary, "fake-partial"));
        File.Delete(log);
        request.Paths = new List<string> { first };
        Reject(delegate { client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult(); }, "Standard file update requires explicit root scope");
        Assert(!File.Exists(log), "Rejected standard file update performs no mutation");
        request.Paths = new List<string> { temporary, second };
        Reject(delegate { client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult(); }, "Standard mixed root and file selection rejected before execution");
        Assert(!File.Exists(log), "No root update executes before later non-root rejection");
        request.Paths = new List<string> { temporary };
        result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.Succeeded && File.ReadAllLines(log).SequenceEqual(new [] { temporary }), "Standard update accepts explicitly selected workspace root");
        File.Delete(log);
    }

    private static void LiveTests(PlasticClient client, string workspacePath)
    {
        var workspace = client.DiscoverWorkspace(workspacePath);
        Assert(workspace != null, "Live workspace found");
        var before = client.GetStatusAsync(workspace.RootPath, CancellationToken.None).GetAwaiter().GetResult();
        string file = Path.Combine(workspace.RootPath, "TortoiseSCM-test-" + Guid.NewGuid().ToString("N") + " 中文 & 空格.txt");
        bool added = false;
        try
        {
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write("Disposable TortoiseSCM integration test.");
            var items = client.GetStatusAsync(file, CancellationToken.None).GetAwaiter().GetResult();
            Assert(items.Count == 1 && items[0].StatusCode == "PR" && items[0].Path.Equals(file, StringComparison.OrdinalIgnoreCase), "Live private UTF-8 path status");
            var request = new PlasticCommandRequest { Command = PlasticCommand.Add, WorkingDirectory = workspace.RootPath, Paths = new List<string> { file } };
            added = true;
            var result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded, "Live add: " + result.Error);
            items = client.GetStatusAsync(file, CancellationToken.None).GetAwaiter().GetResult();
            Assert(items.Count == 1 && items[0].StatusCode == "AD", "Live added status");
            request.Command = PlasticCommand.Undo;
            result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded, "Live undo add: " + result.Error); added = false;
            items = client.GetStatusAsync(file, CancellationToken.None).GetAwaiter().GetResult();
            Assert(items.Count == 1 && items[0].StatusCode == "PR", "Undo add returns private file");
        }
        finally
        {
            if (added) client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = workspace.RootPath, Paths = new List<string> { file } }, CancellationToken.None).GetAwaiter().GetResult();
            File.Delete(file);
        }
        var after = client.GetStatusAsync(workspace.RootPath, CancellationToken.None).GetAwaiter().GetResult();
        Assert(String.Join("\n", before.Select(x => x.StatusCode + " " + x.Path).OrderBy(x => x)) == String.Join("\n", after.Select(x => x.StatusCode + " " + x.Path).OrderBy(x => x)), "Live workspace status restored");
    }
}
