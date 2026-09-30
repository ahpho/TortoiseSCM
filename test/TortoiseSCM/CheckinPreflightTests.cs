// GPL-2.0-or-later. Checkin review invariants, using an isolated native-process double.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class CheckinPreflightTests
{
    private static string root;
    private static int assertions;
    private const string Selector = "repository \"repo@server\"";
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Child(args);
        root = Path.Combine(Path.GetTempPath(), "TSCM-checkin-review-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TSCM_CHECKIN_ROOT", root);
        try { Run(); Console.WriteLine("PASS: " + assertions + " checkin preflight assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Environment.SetEnvironmentVariable("TSCM_CHECKIN_ROOT", null); Directory.Delete(root, true); }
    }
    private static void Run()
    {
        Directory.CreateDirectory(Path.Combine(root, ".plastic")); Directory.CreateDirectory(Path.Combine(root, "folder"));
        var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(10), SettingsPath = Path.Combine(root, "settings.xml") });
        Reset();
        var preview = Prepare(client, "file.txt");
        Check(preview.RootPath == root && preview.Repository == "repo@server" && preview.Selector == Selector && !preview.IsPartial, "Snapshot identifies workspace");
        Check(preview.Paths.Single() == Path.Combine(root, "file.txt") && preview.Files.Single().Path == Path.Combine(root, "file.txt"), "Exact-file scope excludes unrelated pending file");
        Check(preview.Locks.Count == 1 && preview.Locks[0].Path == "/file.txt", "Repository lock path is mapped into selected local scope");
        preview.Files[0].Path = "tampered"; preview.Locks[0].Path = "tampered";
        Check(preview.Files[0].Path == Path.Combine(root, "file.txt") && preview.Locks[0].Path == "/file.txt", "Mutable view rows cannot alter retained snapshot");
        bool readOnly = false; try { preview.Paths.Add("bad"); } catch (NotSupportedException) { readOnly = true; }
        Check(readOnly, "Paths collection is immutable");
        Check(client.CheckinPreparedAsync(preview, "reviewed", CancellationToken.None).GetAwaiter().GetResult().Succeeded, "Unchanged preview signs in");
        string[] executed = XDocument.Load(Path.Combine(root, "executed")).Root.Elements("arg").Select(x => x.Value).ToArray();
        Check(executed.Contains("file.txt") && !executed.Contains("other.txt") && !executed.Contains("--private"), "Native checkin retains exact requested scope");
        foreach (string change in new[] { "content", "status", "selector", "name", "late-content", "missing" })
        {
            Reset(); preview = Prepare(client, "file.txt");
            if (change == "content") File.WriteAllText(Path.Combine(root, "file.txt"), "changed after review");
            if (change == "missing") File.Delete(Path.Combine(root, "file.txt"));
            if (change == "selector") File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"other@server\"");
            if (change == "name") File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "different\nguid\nStandard\n");
            if (change == "status" || change == "late-content") File.WriteAllText(Path.Combine(root, "mode"), change);
            Reject(() => client.CheckinPreparedAsync(preview, "reviewed", CancellationToken.None).GetAwaiter().GetResult(), "Changed " + change + " blocks native checkin");
        }
        Reset(); preview = Prepare(client, "folder");
        Check(preview.Files.Count == 1 && preview.Files[0].Path.EndsWith("child.txt"), "Directory preview includes recursive controlled child");
        File.WriteAllText(Path.Combine(root, "mode"), "new-child");
        Reject(() => client.CheckinPreparedAsync(preview, "reviewed", CancellationToken.None).GetAwaiter().GetResult(), "New pending descendant invalidates directory review");
        Reset(); preview = Prepare(client, root);
        Check(preview.Files.Count == 3 && preview.ExcludedPrivateCount == 1, "Root preview excludes private descendants and counts them");
        Check(client.CheckinPreparedAsync(preview, "root review", CancellationToken.None).GetAwaiter().GetResult().Succeeded, "Root checkin allows unrelated excluded private file");
        Reset(); File.WriteAllText(Path.Combine(root, "mode"), "partial");
        preview = client.PrepareCheckinFastAsync(root, new[] { "file.txt" }, "repo@server", Selector, CancellationToken.None).GetAwaiter().GetResult();
        Check(preview.IsPartial, "Fast preview uses authoritative partial workspace status");
        Check(client.CheckinPreparedFastAsync(preview, "partial review", CancellationToken.None).GetAwaiter().GetResult().Succeeded,
            "Fast checkin accepts Standard metadata after Gluon conversion");
        string[] fastExecuted = XDocument.Load(Path.Combine(root, "executed")).Root.Elements("arg").Select(x => x.Value).ToArray();
        Check(fastExecuted.Length > 1 && fastExecuted[0] == "partial" && fastExecuted[1] == "checkin",
            "Fast partial checkin dispatches cm partial command");
        Check(fastExecuted.Contains("--all"), "Partial checkin includes added files when selection contains explicit files");
        Check(new PlasticCommandRequest().CheckinInputMode == PlasticCheckinInputMode.Automatic,
            "Existing callers retain automatic input selection by default");
        Check(client.CheckinPreparedFastAsync(preview, "path method", CancellationToken.None, PlasticCheckinInputMode.Paths).GetAwaiter().GetResult().Succeeded,
            "Fast checkin accepts explicit path input selection");
        var pathExecution = XDocument.Load(Path.Combine(root, "executed")).Root;
        Check(pathExecution.Elements("arg").Any(arg => arg.Value == "file.txt") && !pathExecution.Elements("arg").Any(arg => arg.Value == "-"),
            "Fast path selection reaches the native command unchanged");
        Check(client.CheckinPreparedFastAsync(preview, "stdin method", CancellationToken.None, PlasticCheckinInputMode.StandardInput).GetAwaiter().GetResult().Succeeded,
            "Fast checkin accepts stdin input selection");
        var stdinExecution = XDocument.Load(Path.Combine(root, "executed")).Root;
        Check(stdinExecution.Elements("arg").Any(arg => arg.Value == "-") && !stdinExecution.Elements("arg").Any(arg => arg.Value == Path.Combine(root, "file.txt")) &&
            ((string)stdinExecution.Element("input")).Contains("file.txt") && !((string)stdinExecution.Element("input")).Contains("other.txt"),
            "Fast stdin selection forwards only the explicitly selected file through stdin");
        Reset(); Reject(() => Prepare(client, "private.txt"), "Explicit private selection rejected");
        Reset(); Reject(() => Prepare(client, "missing.txt"), "Missing selected pending path rejected");
        Reset(); File.WriteAllText(Path.Combine(root, "mode"), "added-directory");
        Check(Prepare(client, "folder/child.txt").Files.Any(item => item.Path.EndsWith("child.txt", StringComparison.OrdinalIgnoreCase)),
            "An added child file can be reviewed without selecting its AD directory container");
        Reset(); File.WriteAllText(Path.Combine(root, "mode"), "locks-failed"); preview = Prepare(client, "file.txt");
        Check(preview.Locks.Count == 0 && preview.LockWarning.Length > 0, "Unsupported lock query stays explicit advisory");
        Reset(); preview = Prepare(client, "file.txt"); File.WriteAllText(Path.Combine(root, "mode"), "checkin-failed");
        Check(!client.CheckinPreparedAsync(preview, "reviewed", CancellationToken.None).GetAwaiter().GetResult().Succeeded, "Native failure preserved");
        Reset(); preview = Prepare(client, "file.txt");
        Reject(() => client.CheckinPreparedAsync(preview, "", CancellationToken.None).GetAwaiter().GetResult(), "Empty comment rejected");
        Reject(() => client.CheckinPreparedAsync(preview, "reviewed", new CancellationToken(true)).GetAwaiter().GetResult(), "Canceled review never signs in");
        Reset(); preview = Prepare(client, "file.txt"); File.WriteAllText(Path.Combine(root, ".plastic", "plastic.mergeprogress"), "unknown");
        Reject(() => client.CheckinPreparedAsync(preview, "reviewed", CancellationToken.None).GetAwaiter().GetResult(), "Existing native conflict guard still blocks unowned merge");
    }
    private static PlasticCheckinPreview Prepare(PlasticClient client, string path)
    { return client.PrepareCheckinAsync(root, new[] { path }, "repo@server", Selector, CancellationToken.None).GetAwaiter().GetResult(); }
    private static void Reset()
    {
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "review\nguid\nStandard\n");
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), Selector);
        File.WriteAllText(Path.Combine(root, "mode"), ""); File.WriteAllText(Path.Combine(root, "file.txt"), "review me");
        File.WriteAllText(Path.Combine(root, "other.txt"), "unselected"); File.WriteAllText(Path.Combine(root, "private.txt"), "private");
        File.WriteAllText(Path.Combine(root, "folder", "child.txt"), "child");
        foreach (string file in new[] { "executed", "header-count", ".plastic/plastic.mergeprogress" }) File.Delete(Path.Combine(root, file));
    }
    private static int Child(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        root = Environment.GetEnvironmentVariable("TSCM_CHECKIN_ROOT"); string mode = File.ReadAllText(Path.Combine(root, "mode"));
        if (args[0] == "status")
        {
            if (args.Contains("--header"))
            {
                if (mode == "late-content") File.WriteAllText(Path.Combine(root, "file.txt"), "changed during guarded awaits");
                Console.WriteLine("<StatusOutput><WorkspaceStatus><Status><Changeset>" + (mode == "partial" ? "-1" : "4") + "</Changeset></Status></WorkspaceStatus></StatusOutput>"); return 0;
            }
            var changes = new XElement("Changes", Row("file.txt", mode == "status" ? "CO" : "CH", false), Row("other.txt", "CH", false), Row("folder/child.txt", "CH", false), Row("private.txt", "PR", false));
            if (mode == "new-child") changes.Add(Row("folder/new.txt", "AD", false));
            if (mode == "added-directory") changes.Add(Row("folder", "AD", true));
            Console.WriteLine(new XElement("StatusOutput", changes)); return 0;
        }
        if (args[0] == "lock")
        {
            if (mode == "locks-failed") return 15;
            Console.WriteLine("TSLOCK|repo|12|bfc84710-23bf-4343-bc03-54548f8e4fdd|today|/main|4|/main|4|Locked|tester|review|/file.txt|END"); return 0;
        }
        if ((args[0] == "checkin") || (args.Length > 1 && args[0] == "partial" && args[1] == "checkin"))
        {
            new XDocument(new XElement("args", args.Select(a => new XElement("arg", a)),
                new XElement("input", args.Contains("-") ? Console.In.ReadToEnd() : ""))).Save(Path.Combine(root, "executed"));
            return mode == "checkin-failed" ? 19 : 0;
        }
        return 20;
    }
    private static XElement Row(string path, string status, bool directory)
    { return new XElement("Change", new XElement("Path", Path.Combine(root, path)), new XElement("Type", status), new XElement("RevisionType", directory ? "Directory" : "File")); }
    private static void Reject(Action action, string label)
    {
        bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } catch (OperationCanceledException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(root, "executed")), label);
    }
    private static void Check(bool value, string label) { assertions++; if (!value) throw new Exception(label); }
}
