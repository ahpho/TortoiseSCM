// GPL-2.0-or-later. Deterministic merge safety regression tests; no server writes.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class MergeOperationTests
{
    private static int assertions;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (args.Length != 0) return FakeCm(args);
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-merge-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string root = Path.Combine(temporary, "workspace");
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "merge-test\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server:8087\"\r\n  path \"/\"\r\n  smartbranch \"/main\"\r\n");
            string local = Path.Combine(root, "conflict.txt");
            File.WriteAllText(local, "destination");
            var config = new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, SettingsPath = Path.Combine(temporary, "settings.xml"), Timeout = TimeSpan.FromSeconds(10) };
            var client = new PlasticClient(config);
            var plan = client.PreviewMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            Check(plan.BaseChangeset == 0 && plan.FileConflicts.Single().BaseChangeset == 1, "Per-file ancestor is independent of overall merge base");
            Check(plan.Operations.Single().Path == "/automatic.txt", "Automatic operation retained");
            string standard = Preview(false);
            Reject(() => PlasticClient.ParseMergePlan(standard.Replace("test@server", "other@server"), root, "test@server:8087", 3), "Cross-repository contributor rejected");
            Reject(() => PlasticClient.ParseMergePlan(standard + "UNKNOWN|bad\n", root, "test@server:8087", 3), "Unknown record fails closed");
            Reject(() => PlasticClient.ParseMergePlan(standard + "FILE_CONFLICT|/CONFLICT.txt|1|3|2|42\n", root, "test@server:8087", 3), "Case-folded duplicate conflicts rejected");
            Reject(() => client.BeginMergeAsync(local, 3, Token).GetAwaiter().GetResult(), "Explicit workspace root required");
            File.WriteAllText(Path.Combine(root, "automatic.txt"), "ignored user bytes");
            Reject(() => client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult(), "Ignored incoming add collision rejected");
            Check(File.ReadAllText(Path.Combine(root, "automatic.txt")) == "ignored user bytes" && !File.Exists(Marker(root, "plastic.mergeprogress")), "Ignored bytes preserved before mutation");
            File.Delete(Path.Combine(root, "automatic.txt"));
            File.WriteAllText(Marker(root, "dirty"), "");
            Reject(() => client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult(), "Dirty workspace rejected");
            File.Delete(Marker(root, "dirty"));
            var session = client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            Check(session.Plan.FileConflicts.Count == 1 && File.ReadAllText(local) == "destination", "Begin leaves conflicting destination bytes untouched");
            Check(new PlasticClient(config).GetMergeSessionAsync(root, Token).GetAwaiter().GetResult().SessionId == session.SessionId, "Session survives new client and CRLF XML normalization");
            Reject(() => Checkin(client, root), "Unresolved native conflict blocks shared Core checkin");
            Reject(() => client.PrepareMergeConflictAsync(root, 4, "/conflict.txt", Token).GetAwaiter().GetResult(), "Mismatched source rejected");
            File.WriteAllText(Marker(root, "wrong-item"), "");
            Reject(() => client.PrepareMergeConflictAsync(root, 3, "/conflict.txt", Token).GetAwaiter().GetResult(), "Historical path reuse rejected by item identity");
            File.Delete(Marker(root, "wrong-item"));
            File.WriteAllText(Marker(root, "cat-fail"), "");
            Reject(() => client.PrepareMergeConflictAsync(root, 3, "/conflict.txt", Token).GetAwaiter().GetResult(), "Partial failed download rejected");
            File.Delete(Marker(root, "cat-fail"));
            var files = client.PrepareMergeConflictAsync(root, 3, "/conflict.txt", Token).GetAwaiter().GetResult();
            Check(File.ReadAllText(files.BasePath) == "base" && File.ReadAllText(files.LocalPath) == "destination" && File.ReadAllText(files.RemotePath) == "source", "Retry publishes exact contributors after interrupted download");
            File.WriteAllText(files.ResultPath, "reviewed result");
            Check(client.PrepareMergeConflictAsync(root, 3, "/conflict.txt", Token).GetAwaiter().GetResult().ResultPath == files.ResultPath && File.ReadAllText(files.ResultPath) == "reviewed result", "Reopen preserves reviewed output");
            File.SetAttributes(files.BasePath, FileAttributes.Normal); File.WriteAllText(files.BasePath, "tampered");
            Reject(() => client.PrepareMergeConflictAsync(root, 3, "/conflict.txt", Token).GetAwaiter().GetResult(), "Modified cached input rejected");
            File.WriteAllText(files.BasePath, "base"); File.SetAttributes(files.BasePath, FileAttributes.ReadOnly);
            Reject(() => client.ApplyMergeFileResolutionAsync(root, 3, "/conflict.txt", local, Token).GetAwaiter().GetResult(), "Working-file result alias rejected");
            File.WriteAllText(local, "user edited while dialog open");
            Reject(() => client.ApplyMergeFileResolutionAsync(root, 3, "/conflict.txt", files.ResultPath, Token).GetAwaiter().GetResult(), "Concurrent user edit rejected");
            Check(File.ReadAllText(local) == "user edited while dialog open", "Concurrent user bytes preserved");
            File.WriteAllText(local, "destination");
            Check(client.ApplyMergeFileResolutionAsync(root, 3, "/conflict.txt", files.ResultPath, Token).GetAwaiter().GetResult().Succeeded, "Native repository-path resolution succeeds");
            Check(File.ReadAllText(local) == "reviewed result" && client.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult().Plan.FileConflicts.Single().Resolved, "Reviewed bytes and native resolution state agree");
            Check(Checkin(client, root).Succeeded && client.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult() == null, "Checkin retires completed session");
            Reset(root);
            client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            var failedFiles = client.PrepareMergeConflictAsync(root, 3, "/conflict.txt", Token).GetAwaiter().GetResult();
            File.WriteAllText(failedFiles.ResultPath, "second result");
            File.WriteAllText(Marker(root, "apply-fail"), "");
            Check(!client.ApplyMergeFileResolutionAsync(root, 3, "/conflict.txt", failedFiles.ResultPath, Token).GetAwaiter().GetResult().Succeeded, "Native failure surfaced after metadata may have changed");
            Check(File.ReadAllText(local) == "destination", "Native failure restores only our replacement bytes");
            Reject(() => Checkin(client, root), "Uncertain apply blocks checkin even if native preview says resolved");
            Check(client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root, Paths = new List<string> { root }, Recursive = true }, Token).GetAwaiter().GetResult().Succeeded, "Explicit full undo remains available for uncertain state");
            Check(client.GetMergeSessionAsync(root, Token).GetAwaiter().GetResult() == null, "Full clean undo retires failed session");
            Reset(root);
            client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult();
            Check(File.Exists(Marker(root, "plastic.mergeprogress")), "A new session starts after previous session retired");
            var timedFiles = client.PrepareMergeConflictAsync(root, 3, "/conflict.txt", Token).GetAwaiter().GetResult();
            File.WriteAllText(timedFiles.ResultPath, "timed result");
            File.WriteAllText(Marker(root, "apply-slow"), "");
            config.Timeout = TimeSpan.FromMilliseconds(500);
            Check(client.ApplyMergeFileResolutionAsync(root, 3, "/conflict.txt", timedFiles.ResultPath, Token).GetAwaiter().GetResult().TimedOut, "Native apply timeout surfaced");
            config.Timeout = TimeSpan.FromSeconds(10);
            Check(File.ReadAllText(local) == "destination", "Timeout preserves original local bytes");
            Reject(() => Checkin(client, root), "Timed out apply cannot check in");
            client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root, Paths = new List<string> { root }, Recursive = true }, Token).GetAwaiter().GetResult();
            Reset(root);
            File.WriteAllText(Marker(root, "begin-fail"), "");
            Reject(() => client.BeginMergeAsync(root, 3, Token).GetAwaiter().GetResult(), "Native begin partial failure surfaced");
            Reject(() => Checkin(client, root), "Incomplete begin cannot check in");
            Console.WriteLine("PASS: " + assertions + " merge safety assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            foreach (string file in Directory.GetFiles(temporary, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(temporary, true);
        }
    }
    private static PlasticCommandResult Checkin(PlasticClient client, string root)
    { return client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = root, Paths = new List<string> { root }, Comment = "merge test" }, Token).GetAwaiter().GetResult(); }
    private static string Marker(string root, string name) { return Path.Combine(root, ".plastic", name); }
    private static void Reset(string root)
    {
        foreach (string name in new[] { "resolved", "apply-fail", "apply-slow", "committed" }) File.Delete(Marker(root, name));
        File.Delete(Path.Combine(root, "automatic.txt")); File.WriteAllText(Path.Combine(root, "conflict.txt"), "destination");
    }
    private static string Preview(bool resolved)
    {
        return "CONTRIBUTOR|SRC|3|cs:3@test@server:8087|/source\nCONTRIBUTOR|DST|2|cs:2@test@server:8087|/main\nCONTRIBUTOR|BASE|0|cs:0@test@server:8087|/main\n" +
            (resolved ? "STATUS|ALREADY_CONNECTED\n" : "APPLY|ADD|/automatic.txt\nFILE_CONFLICT|/conflict.txt|1|3|2|42\n");
    }
    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string root = Environment.CurrentDirectory;
        bool active = File.Exists(Marker(root, "plastic.mergeprogress"));
        if (args[0] == "status")
        {
            var status = new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", 2))));
            if (!args.Contains("--header") && (active || File.Exists(Marker(root, "dirty"))))
                status.Add(new XElement("Changes", new XElement("Change", new XElement("Path", Path.Combine(root, "conflict.txt")), new XElement("Type", "CO"))));
            Console.WriteLine(status); return 0;
        }
        if (args[0] == "merge")
        {
            if (args.Contains("--keepdestination"))
            {
                if (!active || args[2] != "/conflict.txt") return 9;
                File.WriteAllText(Marker(root, "resolved"), "");
                if (File.Exists(Marker(root, "apply-slow"))) Thread.Sleep(5000);
                return File.Exists(Marker(root, "apply-fail")) ? 7 : 0;
            }
            if (args.Contains("--merge"))
            {
                File.WriteAllText(Marker(root, "plastic.mergeprogress"), "");
                File.WriteAllText(Path.Combine(root, "automatic.txt"), "automatic"); return File.Exists(Marker(root, "begin-fail")) ? 8 : 0;
            }
            Console.Write(Preview(File.Exists(Marker(root, "resolved")))); return 0;
        }
        if (args[0] == "ls")
        {
            Console.WriteLine(new XElement("LsResults", new XElement("LsItems", new XElement("LsItem", new XElement("CurrentPath", "/conflict.txt"), new XElement("Name", "conflict.txt"),
                new XElement("Type", "file"), new XElement("ItemId", File.Exists(Marker(root, "wrong-item")) ? 999 : 42))))); return 0;
        }
        if (args[0] == "cat")
        {
            string output = args.Single(a => a.StartsWith("--file=")).Substring(7);
            File.WriteAllText(output, File.Exists(Marker(root, "cat-fail")) ? "partial" : args[1].Contains("#cs:1@") ? "base" : args[1].Contains("#cs:2@") ? "destination" : "source");
            return File.Exists(Marker(root, "cat-fail")) ? 8 : 0;
        }
        if (args[0] == "checkin" || args[0] == "undo")
        {
            File.Delete(Marker(root, "plastic.mergeprogress")); File.Delete(Marker(root, "dirty"));
            Console.WriteLine("done"); return 0;
        }
        return 99;
    }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } catch (PlasticCommandException) { rejected = true; }
        Check(rejected, message);
    }
}
