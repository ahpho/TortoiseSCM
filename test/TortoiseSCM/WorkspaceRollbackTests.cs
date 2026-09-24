// GPL-2.0-or-later. Writes only to an explicitly supplied isolated QA workspace.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class WorkspaceRollbackTests
{
    private static int count;
    private static void Assert(bool value, string message) { count++; if (!value) throw new Exception(message); }
    public static int Main(string[] args)
    {
        if (args.Length != 0 && args[0] != "--live") return FakeCm(args);
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
            TrackedRollback();
            if (args.Length == 3 && args[0] == "--live") Live(args[1], Int64.Parse(args[2]));
            else if (args.Length != 0) throw new ArgumentException("Usage: --live <isolated consumer workspace> <base changeset>");
            Console.WriteLine("PASS: " + count + " workspace rollback assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Reject(Action action, string label)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, label);
    }

    private static void TrackedRollback()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-rollback-tracking-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(temporary, "workspace"), metadata = Path.Combine(root, ".plastic");
        Directory.CreateDirectory(metadata);
        try
        {
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "rollback-test\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"test@server:8087\"\r\n  path \"/\"\r\n    smartbranch \"/main\"\r\n");
            string local = Path.Combine(root, "modified.txt"); File.WriteAllText(local, "head");
            var config = new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, SettingsPath = Path.Combine(temporary, "settings.xml"), Timeout = TimeSpan.FromSeconds(10) };
            var client = new PlasticClient(config);
            var result = client.RollbackAsync(root, 3, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded && File.ReadAllText(local) == "target", "Tracked subtractive rollback leaves requested bytes pending");
            var session = new PlasticClient(config).GetMergeSessionAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            Assert(session != null && session.IsRollback && session.Plan.SourceChangeset == 8 && session.Plan.DestinationChangeset == 8 && session.Plan.BaseChangeset == 3,
                "Rollback intent survives restart with loaded and target changesets");
            Reject(() => Run(client, root, PlasticCommand.Checkin, local), "Pending rollback rejects partial-scope checkin");
            Assert(!File.Exists(Path.Combine(metadata, "checkin-calls")), "Partial rollback checkin rejected before native invocation");
            Assert(Run(client, root, PlasticCommand.Checkin, root).Succeeded && File.ReadAllLines(Path.Combine(metadata, "checkin-calls")).Length == 1,
                "Tracked successful rollback accepts explicit whole-workspace checkin");
            Assert(client.GetMergeSessionAsync(root, CancellationToken.None).GetAwaiter().GetResult() == null, "Publishing rollback retires tracking state");

            File.WriteAllText(local, "head"); File.WriteAllText(Path.Combine(metadata, "native-failure"), "");
            result = client.RollbackAsync(root, 3, CancellationToken.None).GetAwaiter().GetResult();
            Assert(!result.Succeeded && File.Exists(Path.Combine(metadata, "plastic.mergeprogress")), "Partially executed native rollback failure is surfaced");
            Reject(() => Run(client, root, PlasticCommand.Checkin, root), "Failed native rollback cannot be published");
            Assert(File.ReadAllLines(Path.Combine(metadata, "checkin-calls")).Length == 1 && File.ReadAllText(local) == "target", "Failed rollback preserves partial pending bytes and starts no native checkin");
            Assert(Run(client, root, PlasticCommand.Undo, root).Succeeded && client.GetMergeSessionAsync(root, CancellationToken.None).GetAwaiter().GetResult() == null,
                "Explicit full undo remains available and retires failed rollback");
            File.Delete(Path.Combine(metadata, "native-failure"));
            Assert(client.RollbackAsync(root, 3, CancellationToken.None).GetAwaiter().GetResult().Succeeded, "Fresh tracked rollback can begin after undo");
            File.AppendAllText(Path.Combine(metadata, "plastic.mergeprogress"), "unrelated native merge state");
            Reject(() => Run(client, root, PlasticCommand.Checkin, root), "Changed native progress cannot reuse rollback authorization");
            Assert(File.ReadAllLines(Path.Combine(metadata, "checkin-calls")).Length == 1, "Progress mismatch rejected before native checkin");
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static PlasticCommandResult Run(PlasticClient client, string root, PlasticCommand command, string path)
    { return client.RunAsync(new PlasticCommandRequest { Command = command, WorkingDirectory = root, Paths = new[] { path }, Recursive = true, Comment = "rollback fixture" }, CancellationToken.None).GetAwaiter().GetResult(); }

    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string root = Environment.CurrentDirectory, metadata = Path.Combine(root, ".plastic"), progress = Path.Combine(metadata, "plastic.mergeprogress");
        if (args[0] == "status")
        {
            var status = new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", 8))));
            if (!args.Contains("--header") && File.Exists(progress))
                status.Add(new XElement("Changes", new XElement("Change", new XElement("Path", Path.Combine(root, "modified.txt")), new XElement("Type", "CO"))));
            Console.WriteLine(status); return 0;
        }
        if (args[0] == "showselector") { Console.WriteLine(File.ReadAllText(Path.Combine(metadata, "plastic.selector"))); return 0; }
        if (args[0] == "find")
        { Console.WriteLine("<PLASTICQUERY><CHANGESET><CHANGESETID>0</CHANGESETID><PARENT>-1</PARENT></CHANGESET><CHANGESET><CHANGESETID>3</CHANGESETID><PARENT>0</PARENT></CHANGESET><CHANGESET><CHANGESETID>8</CHANGESETID><PARENT>3</PARENT></CHANGESET></PLASTICQUERY>"); return 0; }
        if (args[0] == "merge" && args.Contains("--subtractive"))
        {
            if (args[1] != "cs:8" || !args.Contains("--interval-origin=cs:3")) return 90;
            if (args.Contains("--merge"))
            {
                File.WriteAllText(progress, "known subtractive merge 8 to 3"); File.WriteAllText(Path.Combine(root, "modified.txt"), "target");
                if (File.Exists(Path.Combine(metadata, "native-failure"))) return 7;
            }
            Console.WriteLine("FILE_SRC /modified.txt 8 3 42"); return 0;
        }
        if (args[0] == "checkin" || args[0] == "undo")
        {
            if (args[0] == "checkin") File.AppendAllText(Path.Combine(metadata, "checkin-calls"), "checkin\n");
            else File.WriteAllText(Path.Combine(root, "modified.txt"), "head");
            File.Delete(progress); Console.WriteLine("done"); return 0;
        }
        return 99;
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
