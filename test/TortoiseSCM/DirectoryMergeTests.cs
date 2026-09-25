// GPL-2.0-or-later. Native directory-choice state machine regressions.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class DirectoryMergeTests
{
    private static int assertions;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--real-directory") return RealDirectory(args[1], Int64.Parse(args[2]));
        if (args.Length > 0 && args[0] != "--real") return FakeCm(args);
        if (args.Length > 0) return Real(args[1], Int64.Parse(args[2]));
        string temporary = Path.Combine(Path.GetTempPath(), "tscm-dir-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(temporary, "workspace");
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "directory-test\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server:8087\"\r\n");
            File.WriteAllText(Path.Combine(root, "collision.txt"), "local one");
            File.WriteAllText(Path.Combine(root, "second.txt"), "local two");
            var config = new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, SettingsPath = Path.Combine(temporary, "settings.xml") };
            var client = new PlasticClient(config);
            const string contributors = "CONTRIBUTOR|SRC|3|cs:3@test@server:8087|/source\nCONTRIBUTOR|DST|2|cs:2@test@server:8087|/main\nCONTRIBUTOR|BASE|0|cs:0@test@server:8087|/main\n";
            var moved = PlasticClient.ParseMergePlan(contributors + "DIR_CONFLICT|DIV_MV|Move|Divergent move|source|destination|42|False|MV|/old.txt|/source.txt|MV|/old.txt|/destination.txt", root, "test@server:8087", 3).DirectoryConflicts.Single();
            Check(moved.SourcePath == "/source.txt" && moved.DestinationPath == "/destination.txt" && moved.SourceOriginalPath == "/old.txt" && moved.DestinationOriginalPath == "/old.txt", "Variable-width native move contributors parsed accurately");
            Check(moved.ResolutionOptions.SequenceEqual(new[] { "src", "dst" }), "Divergent move offers native source/destination choices only");
            Check(new PlasticDirectoryConflict { Kind = "UNRECOGNIZED" }.ResolutionOptions.Count == 0, "Unknown directory kinds fail closed");
            var directoryType = PlasticClient.ParseMergePlan(contributors + "DIR_CONFLICT|EVIL|Twin|Same dir|source|destination|42|True|ADD|/tree|ADD|/tree", root, "test@server:8087", 3).DirectoryConflicts.Single();
            Check(directoryType.IsDirectory && !directoryType.Resolved, "Native boolean identifies directory type, not solved state");
            var session = client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            Check(session.AwaitingDirectoryResolution && session.Plan.DirectoryConflicts.Count == 2, "Begin creates inspectable directory planning session");
            Check(!File.Exists(Marker(root, "plastic.mergeprogress")) && File.ReadAllText(Path.Combine(root, "collision.txt")) == "local one", "Begin planning never changes workspace files");
            Check(client.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult().SessionId == session.SessionId, "Clean planning session not auto-retired");
            var alternate = new PlasticClient(new PlasticClientConfig { CmPath = config.CmPath, SettingsPath = Path.Combine(temporary, "other-settings", "settings.xml") });
            Reject(() => Checkin(alternate, root), "Alternate settings cannot bypass workspace directory plan marker");
            Reject(() => alternate.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult(), "Alternate settings cannot replace active directory plan");
            Reject(() => alternate.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult(), "Alternate settings status cannot retire another plan marker");
            Reject(() => client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult(), "Second begin cannot overwrite current directory plan");
            Reject(() => Checkin(client, root), "Planning blocks checkin even though workspace is clean");
            Reject(() => client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root, Paths = new List<string> { root }, Recursive = true }, Token).GetAwaiter().GetResult(), "Planning undo directs to non-destructive cancel");
            Reject(() => client.ContinueMergeAsync(root, 3, Token).GetAwaiter().GetResult(), "Continue requires all directory choices");
            Reject(() => client.ResolveDirectoryConflictAsync(root, 3, 1, "unknown", null, Token).GetAwaiter().GetResult(), "Unsupported resolution rejected");
            foreach (string bad in new[] { "../escape", "folder/name", "CON.txt", "collision.txt", "name.", "x:ads" })
                Reject(() => client.ResolveDirectoryConflictAsync(root, 3, 1, "rename", bad, Token).GetAwaiter().GetResult(), "Unsafe rename rejected: " + bad);
            session = client.ResolveDirectoryConflictAsync(root, 3, 1, "rename", "kept-local.txt", Token).GetAwaiter().GetResult();
            Check(session.Plan.DirectoryConflicts[0].Resolved && session.Plan.DirectoryConflicts[0].Resolution == "rename" && session.Plan.DirectoryConflicts[0].Rename == "kept-local.txt", "Chosen row retains explicit resolution/name");
            Check(!session.Plan.DirectoryConflicts[1].Resolved && session.Plan.DirectoryConflicts[1].Index == 2, "Other row keeps stable public index after native renumbering");
            client = new PlasticClient(config);
            Check(client.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult().Plan.DirectoryConflicts[0].Resolved, "Directory selections survive client restart");
            Reject(() => client.ResolveDirectoryConflictAsync(root, 3, 2, "rename", "kept-local.txt", Token).GetAwaiter().GetResult(), "Separate planned rename cannot reuse target");
            session = client.ResolveDirectoryConflictAsync(root, 3, 2, "dst", null, Token).GetAwaiter().GetResult();
            Check(session.Plan.DirectoryConflicts.All(item => item.Resolved), "Second stable index maps to remaining native conflict1");
            Check(File.ReadAllText(Marker(root, "resolve.log")).Contains("1|dst"), "Native invocation uses recomputed conflict index");
            File.WriteAllText(Path.Combine(root, "private-after-plan.txt"), "user bytes");
            Reject(() => client.ContinueMergeAsync(root, 3, Token).GetAwaiter().GetResult(), "Changed working tree prevents plan apply");
            Check(File.ReadAllText(Path.Combine(root, "private-after-plan.txt")) == "user bytes", "Rejected plan preserves unrelated user edits");
            File.Delete(Path.Combine(root, "private-after-plan.txt"));
            session = client.ContinueMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            Check(!session.AwaitingDirectoryResolution && File.Exists(Marker(root, "plastic.mergeprogress")), "Continue creates native pending merge state");
            Check(File.ReadAllText(Path.Combine(root, "kept-local.txt")) == "local one" && File.ReadAllText(Path.Combine(root, "collision.txt")) == "source one", "Rename result preserves both contributor files");
            Check(!Directory.GetFiles(Path.Combine(temporary, "merge-sessions"), "*.dat", SearchOption.AllDirectories).Any(), "Successful apply removes opaque native planning files");
            Check(Checkin(client, root).Succeeded && client.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult() == null, "Native completed directory merge can checkin and retire");
            Reset(root);
            session = client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(root, "user-change.txt"), "preserve");
            client.CancelDirectoryMergeAsync(root, Token).GetAwaiter().GetResult();
            Check(File.ReadAllText(Path.Combine(root, "user-change.txt")) == "preserve" && !client.HasSavedMergeSession(root), "Cancel detaches plan without touching subsequent user edits");
            File.Delete(Path.Combine(root, "user-change.txt"));
            session = client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            File.WriteAllText(Marker(root, "resolve-fail"), "");
            Reject(() => client.ResolveDirectoryConflictAsync(root, 3, 1, "dst", null, Token).GetAwaiter().GetResult(), "Interrupted native choice fails closed");
            Reject(() => Checkin(client, root), "Uncertain directory choice cannot checkin");
            client.CancelDirectoryMergeAsync(root, Token).GetAwaiter().GetResult();
            Check(!client.HasSavedMergeSession(root), "Unapplied failed planning can be cancelled safely");
            File.Delete(Marker(root, "resolve-fail"));
            client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            client.ResolveDirectoryConflictAsync(root, 3, 1, "dst", null, Token).GetAwaiter().GetResult();
            client.ResolveDirectoryConflictAsync(root, 3, 2, "src", null, Token).GetAwaiter().GetResult();
            File.WriteAllText(Marker(root, "continue-fail"), "");
            Reject(() => client.ContinueMergeAsync(root, 3, Token).GetAwaiter().GetResult(), "Native apply partial failure surfaced");
            Reject(() => Checkin(client, root), "Partially applied directory plan cannot checkin");
            Reject(() => client.CancelDirectoryMergeAsync(root, Token).GetAwaiter().GetResult(), "Cancel refuses to detach active native merge");
            Check(client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root, Paths = new List<string> { root }, Recursive = true }, Token).GetAwaiter().GetResult().Succeeded,
                "Explicit full undo can recover a partially applied directory merge");
            Check(!client.HasSavedMergeSession(root), "Successful clean undo detaches failed apply session");
            Check(!File.Exists(Marker(root, "tortoisescm-directory.session")), "Failed apply full undo removes workspace ownership marker");
            Reset(root); File.Delete(Marker(root, "continue-fail"));
            client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            File.WriteAllText(Marker(root, "resolve-mutates"), "");
            Reject(() => client.ResolveDirectoryConflictAsync(root, 3, 1, "dst", null, Token).GetAwaiter().GetResult(), "Unexpected native planning file mutation detected");
            Reject(() => client.CancelDirectoryMergeAsync(root, Token).GetAwaiter().GetResult(), "Uncertain changed planning session cannot discard its checkin guard");
            Check(client.HasSavedMergeSession(root), "Uncertain changed plan remains tracked");
            File.WriteAllText(Path.Combine(root, "collision.txt"), "local one");
            client.CancelDirectoryMergeAsync(root, Token).GetAwaiter().GetResult();
            Check(!client.HasSavedMergeSession(root), "Restored fingerprint allows explicit failed-plan cancellation");
            Console.WriteLine("PASS: " + assertions + " directory merge assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }
    private static int Real(string root, long source)
    {
        try
        {
            var config = new PlasticClientConfig { SettingsPath = Path.Combine(Path.GetDirectoryName(root), "directory-merge-settings.xml") };
            var client = new PlasticClient(config);
            var original = File.ReadAllBytes(Path.Combine(root, "collision.txt"));
            var session = client.BeginMergeAsync(root, source, Token).GetAwaiter().GetResult();
            Check(session.AwaitingDirectoryResolution && session.Plan.DirectoryConflicts.Count == 2, "Real two native directory conflicts found");
            Check(File.ReadAllBytes(Path.Combine(root, "collision.txt")).SequenceEqual(original), "Real begin leaves local collision untouched");
            Reject(() => Checkin(client, root), "Real clean planning checkin rejected");
            session = client.ResolveDirectoryConflictAsync(root, source, 1, "rename", "collision-local.txt", Token).GetAwaiter().GetResult();
            Check(session.Plan.DirectoryConflicts.Count(item => item.Resolved) == 1, "Real native rename choice persisted");
            session = new PlasticClient(config).GetMergeSessionAsync(root, Token).GetAwaiter().GetResult();
            Check(session.Plan.DirectoryConflicts[0].Rename == "collision-local.txt", "Real choice survives persisted session reload");
            session = client.ResolveDirectoryConflictAsync(root, source, 2, "dst", null, Token).GetAwaiter().GetResult();
            Check(session.Plan.DirectoryConflicts.All(item => item.Resolved), "Real stable second index correctly remapped");
            Check(File.ReadAllBytes(Path.Combine(root, "collision.txt")).SequenceEqual(original), "Real choices leave working files unchanged until continue");
            session = client.ContinueMergeAsync(root, source, Token).GetAwaiter().GetResult();
            Check(!session.AwaitingDirectoryResolution && session.Plan.FileConflicts.Count(item => !item.Resolved) == 1, "Real continue enters mixed content conflict phase");
            Check(File.ReadAllBytes(Path.Combine(root, "collision-local.txt")).SequenceEqual(original) && File.ReadAllText(Path.Combine(root, "collision.txt")).Contains("source collision"), "Real native rename keeps both collision contents");
            var files = client.PrepareMergeConflictAsync(root, source, "/content.txt", Token).GetAwaiter().GetResult();
            File.WriteAllText(files.ResultPath, "header\nreviewed directory and file merge\nfooter\n", new UTF8Encoding(false));
            Check(client.ApplyMergeFileResolutionAsync(root, source, "/content.txt", files.ResultPath, Token).GetAwaiter().GetResult().Succeeded, "Real mixed file conflict resolved through same session");
            Check(Checkin(client, root).Succeeded, "Real explicit directory and file merge checkin succeeds");
            Check(client.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult() == null, "Real completed session retires");
            Console.WriteLine("PASS: " + assertions + " real directory merge assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static PlasticCommandResult Checkin(PlasticClient client, string root)
    { return client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = root, Paths = new List<string> { root }, Comment = "publish reviewed directory and file conflict merge" }, Token).GetAwaiter().GetResult(); }
    private static int RealDirectory(string root, long source)
    {
        try
        {
            var config = new PlasticClientConfig { SettingsPath = Path.Combine(Path.GetDirectoryName(root), "real-directory-settings.xml") };
            var client = new PlasticClient(config);
            if (client.HasSavedMergeSession(root)) client.CancelDirectoryMergeAsync(root, Token).GetAwaiter().GetResult();
            var plan = client.PreviewMergeAsync(root, source, Token).GetAwaiter().GetResult();
            Check(plan.DirectoryConflicts.Any(item => item.SourcePath == "/same-dir" && item.Kind == "EVIL"), "Real directory item twin conflict present");
            var session = client.BeginMergeAsync(root, source, Token).GetAwaiter().GetResult();
            var directory = session.Plan.DirectoryConflicts.Single(item => item.SourcePath == "/same-dir");
            session = client.ResolveDirectoryConflictAsync(root, source, directory.Index, "rename", "same-dir-local", Token).GetAwaiter().GetResult();
            Check(session.Plan.DirectoryConflicts.All(item => item.Resolved), "Native directory rename resolves tree collision");
            Check(File.ReadAllText(Path.Combine(root, "same-dir", "nested", "destination.txt")) == "destination descendant", "Planning preserves complete original descendant tree");
            session = client.ContinueMergeAsync(root, source, Token).GetAwaiter().GetResult();
            Check(!session.AwaitingDirectoryResolution, "Directory rename tree applied");
            Check(File.ReadAllText(Path.Combine(root, "same-dir-local", "nested", "destination.txt")) == "destination descendant", "Local descendant preserved under renamed directory");
            Check(File.ReadAllText(Path.Combine(root, "same-dir", "nested", "source.txt")) == "source descendant", "Source descendant received under original directory");
            Check(Checkin(client, root).Succeeded && client.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult() == null, "Directory tree merge explicitly committed and session retired");
            Console.WriteLine("PASS: " + assertions + " real directory-item merge assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static string Marker(string root, string name) { return Path.Combine(root, ".plastic", name); }
    private static void Reset(string root)
    {
        File.Delete(Marker(root, "applied")); File.Delete(Marker(root, "plastic.mergeprogress")); File.Delete(Path.Combine(root, "kept-local.txt"));
        File.WriteAllText(Path.Combine(root, "collision.txt"), "local one");
    }
    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string root = Environment.CurrentDirectory;
        if (args[0] == "status")
        {
            var result = new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", 2))));
            if (!args.Contains("--header") && !args.Contains("--ignored") && File.Exists(Marker(root, "plastic.mergeprogress")))
                result.Add(new XElement("Changes", new XElement("Change", new XElement("Path", Path.Combine(root, "collision.txt")), new XElement("Type", "CH"))));
            Console.WriteLine(result); return 0;
        }
        if (args[0] == "checkin" || args[0] == "undo") { File.Delete(Marker(root, "plastic.mergeprogress")); return 0; }
        if (args[0] != "merge") return 99;
        string storage = args.FirstOrDefault(arg => arg.StartsWith("--mergeresultfile="));
        string resultPath = storage == null ? null : storage.Substring("--mergeresultfile=".Length);
        string solvedPath = storage == null ? null : args.Single(arg => arg.StartsWith("--solvedconflictsfile=")).Substring("--solvedconflictsfile=".Length);
        var solved = resultPath != null && File.Exists(resultPath) ? File.ReadAllLines(resultPath).ToList() : new List<string>();
        if (resultPath != null && !File.Exists(solvedPath)) File.WriteAllText(solvedPath, "fake-state-only");
        if (args.Contains("--resolveconflict"))
        {
            int index = Int32.Parse(args.Single(arg => arg.StartsWith("--conflict=")).Substring(11));
            int id = new[] { 11, 12 }.Where(item => !solved.Any(line => line.StartsWith(item + "|"))).ElementAt(index - 1);
            string choice = args.Single(arg => arg.StartsWith("--resolutionoption=")).Substring(19);
            string rename = args.FirstOrDefault(arg => arg.StartsWith("--resolutioninfo="));
            solved.Add(id + "|" + choice + "|" + (rename == null ? "" : rename.Substring(17)));
            File.AppendAllText(Marker(root, "resolve.log"), index + "|" + choice + "\n");
            File.WriteAllLines(resultPath, solved);
            if (File.Exists(Marker(root, "resolve-mutates"))) File.WriteAllText(Path.Combine(root, "collision.txt"), "unexpected native mutation");
            if (File.Exists(Marker(root, "resolve-fail"))) return 7;
        }
        else if (args.Contains("--merge"))
        {
            foreach (string line in solved)
            {
                string[] fields = line.Split('|');
                if (fields[1] == "rename") { File.Move(Path.Combine(root, "collision.txt"), Path.Combine(root, fields[2])); File.WriteAllText(Path.Combine(root, "collision.txt"), "source one"); }
            }
            File.WriteAllText(Marker(root, "plastic.mergeprogress"), ""); File.WriteAllText(Marker(root, "applied"), ""); return File.Exists(Marker(root, "continue-fail")) ? 8 : 0;
        }
        if (resultPath != null && !File.Exists(resultPath)) File.WriteAllLines(resultPath, solved);
        Console.Write("CONTRIBUTOR|SRC|3|cs:3@test@server:8087|/source\nCONTRIBUTOR|DST|2|cs:2@test@server:8087|/main\nCONTRIBUTOR|BASE|0|cs:0@test@server:8087|/main\n");
        if (File.Exists(Marker(root, "applied"))) { Console.WriteLine("STATUS|ALREADY_CONNECTED"); return 0; }
        foreach (int id in new[] { 11, 12 }.Where(item => !solved.Any(line => line.StartsWith(item + "|"))))
        {
            string path = id == 11 ? "/collision.txt" : "/second.txt";
            Console.WriteLine("DIR_CONFLICT|EVIL|Twin|Different items same name|source|destination|" + id + "|False|ADD|" + path + "|ADD|" + path);
        }
        return 0;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } catch (PlasticCommandException) { rejected = true; } catch (IOException) { rejected = true; }
        Check(rejected, message);
    }
}
