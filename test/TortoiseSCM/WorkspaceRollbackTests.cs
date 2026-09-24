// GPL-2.0-or-later. Writes only to an explicitly supplied isolated QA workspace.
using System;
using System.IO;
using System.Linq;
using System.Threading;
using TortoiseSCM;

internal static class WorkspaceRollbackTests
{
    private static int count;
    private static void Assert(bool value, string message) { count++; if (!value) throw new Exception(message); }
    public static int Main(string[] args)
    {
        try
        {
            string graph = "<PLASTICQUERY><CHANGESET><CHANGESETID>0</CHANGESETID><PARENT>-1</PARENT></CHANGESET><CHANGESET><CHANGESETID>3</CHANGESETID><PARENT>0</PARENT></CHANGESET><CHANGESET><CHANGESETID>8</CHANGESETID><PARENT>3</PARENT></CHANGESET><CHANGESET><CHANGESETID>7</CHANGESETID><PARENT>0</PARENT></CHANGESET></PLASTICQUERY>";
            Assert(PlasticClient.IsAncestorChangeset(graph, 8, 3), "Parent chain recognizes ancestor");
            Assert(PlasticClient.IsAncestorChangeset(graph, 8, 0), "Parent chain reaches root changeset");
            Assert(!PlasticClient.IsAncestorChangeset(graph, 8, 7), "Numerically older unrelated branch rejected");
            Assert(!PlasticClient.IsAncestorChangeset(graph, 8, 2), "Missing target rejected");
            Assert(!PlasticClient.IsAncestorChangeset("<PLASTICQUERY><CHANGESET><CHANGESETID>1</CHANGESETID><PARENT>1</PARENT></CHANGESET></PLASTICQUERY>", 1, 0), "Malformed parent cycle terminates");
            Assert(PlasticClient.IsSafeSubtractivePreview("FILE_SRC /a 8 3 20\nAPPLY ADD /deleted\nAPPLY RM /added\nAPPLY MV /new /old\n"), "Conflict-free native operation preview accepted");
            Assert(!PlasticClient.IsSafeSubtractivePreview("CONFLICT /a"), "Conflicts prevent merge execution");
            Assert(!PlasticClient.IsSafeSubtractivePreview("unknown future operation"), "Unknown native output fails closed");
            if (args.Length == 3 && args[0] == "--live") Live(args[1], Int64.Parse(args[2]));
            else if (args.Length != 0) throw new ArgumentException("Usage: --live <isolated consumer workspace> <base changeset>");
            Console.WriteLine("PASS: " + count + " workspace rollback assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Live(string path, long target)
    {
        string root = Path.GetFullPath(path);
        if (!root.Contains("\\bin\\TortoiseSCM\\qa\\integration-") || Path.GetFileName(root) != "consumer")
            throw new ArgumentException("Test writes require an isolated consumer QA workspace.");
        var client = new PlasticClient(PlasticClientConfig.Load());
        var token = CancellationToken.None;
        Assert(client.GetStatusAsync(root, token).GetAwaiter().GetResult().Count == 0, "Fixture starts clean");
        string selector = File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector"));
        Assert(File.Exists(Path.Combine(root, "added.txt")) && File.Exists(Path.Combine(root, "new-name.txt")), "Fixture contains controlled head files");
        byte[] headBytes = File.ReadAllBytes(Path.Combine(root, "modified.txt"));
        bool mutated = false;
        try
        {
            mutated = true;
            var result = client.RollbackAsync(root, target, token).GetAwaiter().GetResult();
            Assert(result.Succeeded, "Native subtractive merge succeeds: " + result.Error);
            Assert(File.ReadAllText(Path.Combine(root, "modified.txt")) == "original modified", "Modified bytes restored");
            Assert(File.ReadAllText(Path.Combine(root, "deleted.txt")) == "original deleted", "Deleted file restored");
            Assert(!File.Exists(Path.Combine(root, "added.txt")), "Later added file removed");
            Assert(File.ReadAllText(Path.Combine(root, "old-name.txt")) == "original moved" && !File.Exists(Path.Combine(root, "new-name.txt")), "Moved file restored to old name");
            Assert(File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector")) == selector, "Branch selector unchanged");
            var pending = client.GetStatusAsync(root, token).GetAwaiter().GetResult();
            Assert(pending.Any(i => i.StatusCode == "DE") && pending.Any(i => i.StatusCode == "MV") && pending.Any(i => i.StatusCode == "CO"), "Rollback remains pending including move/delete/content");
            bool rejected = false;
            try { client.RollbackAsync(root, target, token).GetAwaiter().GetResult(); } catch (ArgumentException) { rejected = true; }
            Assert(rejected, "Existing pending changes prevent second rollback");
        }
        finally
        {
            if (mutated)
            {
                var result = client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root,
                    Paths = new [] { root }, Recursive = true }, token).GetAwaiter().GetResult();
                Assert(result.Succeeded && File.ReadAllBytes(Path.Combine(root, "modified.txt")).SequenceEqual(headBytes), "Explicit test undo restores head bytes");
                Assert(client.GetStatusAsync(root, token).GetAwaiter().GetResult().Count == 0, "Fixture ends clean");
            }
        }
    }
}
