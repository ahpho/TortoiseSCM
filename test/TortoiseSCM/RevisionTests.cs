// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using TortoiseSCM;

internal static class RevisionTests
{
    private static int count;
    private static void Assert(bool value, string message) { count++; if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Assert(rejected, message); }
    public static int Main(string[] args)
    {
        try
        {
            var files = PlasticClient.ParseChangesetFiles("C|\"/dir/中文.txt\"|F|\"\"|\"\"\r\nM|\"/old.txt\"|F|\"/old.txt\"|\"/new.txt\"\r\n");
            Assert(files.Count == 2 && files[0].Path == "/dir/中文.txt", "Changeset paths and Unicode parsed");
            Assert(files[1].Path == "/new.txt" && files[1].OldPath == "/old.txt", "Move details preserve both paths");
            bool malformed = false;
            try { PlasticClient.ParseChangesetFiles("unexpected"); } catch (InvalidDataException) { malformed = true; }
            Assert(malformed, "Unexpected diff output rejected");
            var history = PlasticClient.ParseChangesets("<PLASTICQUERY><CHANGESET><CHANGESETID>2</CHANGESETID><COMMENT>old</COMMENT></CHANGESET><CHANGESET><CHANGESETID>3</CHANGESETID><COMMENT>new</COMMENT></CHANGESET></PLASTICQUERY>", "root");
            Assert(history[0].Changeset == 3 && history[1].Changeset == 2, "Repository history sorted newest first");
            string missingScope = Path.Combine(Path.GetTempPath(), "TortoiseSCM-missing-" + Guid.NewGuid().ToString("N"));
            Assert(!Directory.Exists(missingScope) && PlasticClient.IsWithinScope(Path.Combine(missingScope, "child.txt"), missingScope), "Missing directory scope still matches pending descendant");
            Assert(!PlasticClient.IsWithinScope(missingScope + "-sibling\\child.txt", missingScope), "Scope match requires directory boundary");
            if (args.Length > 0)
            {
                if (args.Length != 4 || args[0] != "--live") throw new ArgumentException("Usage: --live <isolated-workspace> <base-changeset> <current-changeset>");
                Live(args[1], Int64.Parse(args[2]), Int64.Parse(args[3]));
            }
            Console.WriteLine("PASS: " + count + " revision assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Live(string root, long baseline, long current)
    {
        root = Path.GetFullPath(root);
        if (!root.Contains("\\bin\\TortoiseSCM\\qa\\integration-")) throw new ArgumentException("Live writes are restricted to the isolated QA workspaces.");
        var client = new PlasticClient(PlasticClientConfig.Load());
        var token = CancellationToken.None;
        string folder = Path.Combine(root, "revision-tests"), file = Path.Combine(folder, "child.txt");
        if (!Directory.Exists(folder)) throw new ArgumentException("Expected controlled revision-tests fixture.");
        var initial = client.GetStatusAsync(root, token).GetAwaiter().GetResult();
        Assert(initial.Count == 0, "Fixture starts clean");
        byte[] original = File.ReadAllBytes(file);
        var history = client.GetHistoryAsync(folder, token).GetAwaiter().GetResult();
        Assert(history.Any(h => h.Changeset == current) && history.Any(h => h.Changeset == baseline), "Directory history includes child-only edit");
        var all = client.GetHistoryAsync(root, token).GetAwaiter().GetResult();
        Assert(all.Any(h => h.Changeset == current), "Root history includes repository changesets");
        var detail = client.GetChangesetAsync(root, current, token).GetAwaiter().GetResult();
        Assert(detail.Files.Any(f => f.Path == "/revision-tests/child.txt" && f.Status == "C"), "Changeset details include changed file");
        Reject(() => client.RollbackAsync(root, baseline, token).GetAwaiter().GetResult(), "Root rollback rejected");
        Reject(() => client.SwitchAsync(file, baseline, token).GetAwaiter().GetResult(), "Scoped snapshot switch rejected");
        foreach (string target in new [] { file, folder })
        {
            try
            {
                var result = client.RollbackAsync(target, baseline, token).GetAwaiter().GetResult();
                Assert(result.Succeeded, "Native rollback succeeds: " + result.Error);
                Assert(File.ReadAllText(file) == "revision one", "Rollback restores historical child bytes");
                Assert(client.GetStatusAsync(root, token).GetAwaiter().GetResult().Any(x => x.Path.Equals(file, StringComparison.OrdinalIgnoreCase)), "Rollback leaves pending change");
                Reject(() => client.SwitchAsync(root, baseline, token).GetAwaiter().GetResult(), "Pending change blocks switch");
                Reject(() => client.RollbackAsync(target, baseline, token).GetAwaiter().GetResult(), "Pending change blocks overwrite");
            }
            finally
            {
                var undo = client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root,
                    Paths = new [] { file }, Recursive = false }, token).GetAwaiter().GetResult();
                Assert(undo.Succeeded && File.ReadAllBytes(file).SequenceEqual(original), "Exact fixture undo restores initial bytes");
            }
        }
        string unrelated = Path.Combine(root, "unrelated-revision-test-private.txt");
        if (File.Exists(unrelated)) throw new InvalidOperationException("Unrelated test file already exists.");
        try
        {
            File.WriteAllText(unrelated, "must remain untouched");
            var result = client.RollbackAsync(folder, baseline, token).GetAwaiter().GetResult();
            Assert(result.Succeeded && File.ReadAllText(unrelated) == "must remain untouched", "Directory rollback preserves unrelated pending file");
        }
        finally
        {
            client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root, Paths = new [] { file } }, token).GetAwaiter().GetResult();
            File.Delete(unrelated);
        }
        try
        {
            var result = client.SwitchAsync(root, baseline, token).GetAwaiter().GetResult();
            Assert(result.Succeeded && File.ReadAllText(file) == "revision one", "Workspace snapshot loads historical content");
            Assert(client.GetStatusAsync(root, token).GetAwaiter().GetResult().Count == 0, "Snapshot switch remains clean");
        }
        finally
        {
            var result = client.SwitchAsync(root, current, token).GetAwaiter().GetResult();
            Assert(result.Succeeded && File.ReadAllBytes(file).SequenceEqual(original), "Workspace snapshot restored");
        }
    }
}
