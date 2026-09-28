// GPL-2.0-or-later. Real Plastic revisions/shelves; a process helper captures the BC contract.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using TortoiseSCM;

internal static class BeyondCompareBrowsingIntegrationTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly List<object> Evidence = new List<object>();
    private static string run, cm;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "/solo") return Tool(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " Beyond Compare browsing live assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static async Task Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: BeyondCompareBrowsingIntegrationTests.exe <fresh-manifest.json> <cm.exe>");
        var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = Path.GetFullPath((string)manifest["runDirectory"]); cm = args[1];
        string producer = (string)manifest["producer"], partial = (string)manifest["partial"], reference = (string)manifest["referenceWorkspace"];
        string branch = (string)manifest["branch"];
        Check((bool)manifest["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Completed dedicated test branch fixture");
        foreach (string workspace in new[] { producer, partial })
        {
            Check(Path.GetFullPath(workspace).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) &&
                File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")).Contains(branch), "Isolated workspace selector");
            Check(!Directory.GetFileSystemEntries(workspace).Any(path => Path.GetFileName(path) != ".plastic"), "Fresh empty fixture");
        }
        string referenceBefore = Snapshot(reference), referenceSelector = Selector(reference);
        string referenceStatus = NativeRead(reference, "status", "--short", "--machinereadable");
        const string changed = "changed 中文 & file.txt", deleted = "deleted.txt", oldMove = "move old.txt", newMove = "move new.txt",
            added = "added 中文.txt", oldHistory = "history old.txt", newHistory = "history new.txt";
        byte[] baseBytes = Utf8.GetBytes("base 中文\r\nline two\r\n"), changedBytes = Utf8.GetBytes("shelved 中文\r\nline two\r\n"),
            deleteBytes = Utf8.GetBytes("deleted parent\n"), moveBase = Utf8.GetBytes("moved parent\n"), moveAfter = Utf8.GetBytes("moved shelf\n"),
            addBytes = Utf8.GetBytes("new shelf\n"), historyBefore = Utf8.GetBytes("history before\n"), historyAfter = Utf8.GetBytes("history after\n");
        Write(producer, changed, baseBytes); Write(producer, deleted, deleteBytes); Write(producer, oldMove, moveBase); Write(producer, oldHistory, historyBefore);
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "-c=BC browse isolated baseline");
        long baseCs = Revision(Path.Combine(producer, oldHistory));
        Native(producer, "move", Path.Combine(producer, oldHistory), Path.Combine(producer, newHistory)); Write(producer, newHistory, historyAfter);
        Native(producer, "checkin", producer, "--all", "-c=BC browse renamed historical revision"); long historyCs = Revision(Path.Combine(producer, newHistory));
        Native(partial, "partial", "update", partial, "--report");
        Write(producer, changed, changedBytes); Native(producer, "remove", Path.Combine(producer, deleted));
        Native(producer, "move", Path.Combine(producer, oldMove), Path.Combine(producer, newMove)); Write(producer, newMove, moveAfter);
        Write(producer, added, addBytes); Native(producer, "add", Path.Combine(producer, added));
        // Product shelving intentionally supports CH/CO only. Native fixture creation proves
        // browsing existing native shelves also handles structural file rows.
        string comment = "BC browse " + Guid.NewGuid().ToString("N");
        Native(producer, "shelveset", "create", producer, "--all", "-c=" + comment);
        string helper = Path.Combine(run, "BComp.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, helper);
        Environment.SetEnvironmentVariable("TSCM_BC_BROWSE_EXPECTATION", Path.Combine(run, "bc-browse-expectation.xml"));
        var client = new PlasticClient(new PlasticClientConfig { CmPath = cm, UseBeyondCompare = true, BeyondComparePath = helper, SettingsPath = Path.Combine(run, "browse-settings.xml") });
        var shelf = (await client.GetShelvesAsync(producer, Token)).Single(item => item.Comment == comment);
        Check(shelf.ParentChangeset == historyCs, "Native shelveset records actual parent changeset");
        var changes = await client.GetShelveChangesAsync(producer, shelf.ShelveId, Token);
        Evidence.Add(new { shelfId = shelf.ShelveId, parent = shelf.ParentChangeset, changes });
        Check(changes.Any(item => item.Path == "/" + added && item.Status == "A") && changes.Any(item => item.Path == "/" + deleted && item.Status == "D"), "Native shelf contains added and deleted files");
        Check(changes.Any(item => item.Path == "/" + newMove && item.OldPath == "/" + oldMove && item.Status == "M"), "Native shelf retains moved file source identity");
        var cases = new[] {
            new Case(changed, baseBytes, changedBytes), new Case(added, new byte[0], addBytes),
            new Case(deleted, deleteBytes, new byte[0]), new Case(newMove, moveBase, moveAfter) };
        foreach (string workspace in new[] { producer, partial })
        {
            string before = Snapshot(workspace), selector = Selector(workspace), status = Native(workspace, "status", "--short", "--machinereadable");
            foreach (Case item in cases)
            {
                Expect(item.Before, item.After);
                Check((await client.OpenShelveDiffToolAsync(workspace, shelf.ShelveId, "/" + item.Path, Token)).Succeeded, "Shelf BC launch succeeds: " + Path.GetFileName(workspace) + " / " + item.Path);
                Receipt("Shelf actual parent and stored bytes: " + item.Path, "Parent cs:" + historyCs, "Shelve sh:" + shelf.ShelveId);
            }
            Expect(historyBefore, historyAfter);
            Check((await client.OpenRevisionDiffToolAsync(workspace, "/" + oldHistory, "/" + newHistory, baseCs, historyCs, Token)).Succeeded, "Renamed historical endpoint BC launch succeeds");
            Receipt("Historical source/destination identities preserved", null, null);
            Check(Snapshot(workspace) == before && Selector(workspace) == selector && Native(workspace, "status", "--short", "--machinereadable") == status,
                "All browse operations preserve local files, pending status and selector: " + Path.GetFileName(workspace));
        }
        Native(producer, "checkin", producer, "--all", "-c=BC browse structural historical revisions");
        long structuralCs = Revision(Path.Combine(producer, changed));
        foreach (string workspace in new[] { producer, partial })
        {
            string before = Snapshot(workspace), selector = Selector(workspace), status = Native(workspace, "status", "--short", "--machinereadable");
            var comparison = await client.GetChangesetComparisonAsync(workspace, historyCs, structuralCs, Token);
            foreach (Case item in cases)
            {
                var file = comparison.Files.First(row => row.Path == "/" + item.Path);
                Expect(item.Before, item.After);
                Check((await client.OpenChangesetFileDiffToolAsync(workspace, comparison, file, Token)).Succeeded, "Structural historical BC launch succeeds: " + item.Path);
                string beforePath = item.Path == newMove ? "/" + oldMove : "/" + item.Path;
                Receipt("Structural history actual endpoint bytes: " + item.Path, "serverpath:" + beforePath + "#cs:" + historyCs,
                    "serverpath:/" + item.Path + "#cs:" + structuralCs);
            }
            Check(Snapshot(workspace) == before && Selector(workspace) == selector && Native(workspace, "status", "--short", "--machinereadable") == status,
                "Structural historical browsing preserves workspace: " + Path.GetFileName(workspace));
        }
        Check(Snapshot(reference) == referenceBefore && Selector(reference) == referenceSelector && NativeRead(reference, "status", "--short", "--machinereadable") == referenceStatus,
            "Original TestSCM files, selector and pending status remain unchanged");
        Check((await client.GetShelvesAsync(producer, Token)).Any(item => item.ShelveId == shelf.ShelveId && item.Comment == comment), "Read-only browsing retains shelveset for inspection");
    }
    private sealed class Case
    {
        internal readonly string Path; internal readonly byte[] Before, After;
        internal Case(string path, byte[] before, byte[] after) { Path = path; Before = before; After = after; }
    }
    private static void Expect(byte[] before, byte[] after)
    {
        File.Delete(Path.Combine(run, "bc-browse-receipt.xml"));
        new XDocument(new XElement("expected", new XElement("before", Convert.ToBase64String(before)), new XElement("after", Convert.ToBase64String(after))))
            .Save(Path.Combine(run, "bc-browse-expectation.xml"));
    }
    private static int Tool(string[] args)
    {
        string expectation = Environment.GetEnvironmentVariable("TSCM_BC_BROWSE_EXPECTATION");
        var expected = XDocument.Load(expectation).Root;
        bool matched = args.Length >= 4 && args.Contains("/readonly") && File.ReadAllBytes(args[2]).SequenceEqual(Convert.FromBase64String((string)expected.Element("before"))) &&
            File.ReadAllBytes(args[3]).SequenceEqual(Convert.FromBase64String((string)expected.Element("after")));
        bool readOnly = (File.GetAttributes(args[2]) & FileAttributes.ReadOnly) != 0 && (File.GetAttributes(args[3]) & FileAttributes.ReadOnly) != 0;
        // Both temp files must remain present until the child exits; exercise awaited lifetime.
        Thread.Sleep(120);
        bool present = File.Exists(args[2]) && File.Exists(args[3]);
        new XDocument(new XElement("receipt", new XElement("before", args[2]), new XElement("after", args[3]), new XElement("matched", matched),
            new XElement("readonly", readOnly), new XElement("present", present), new XElement("arguments", args.Select(value => new XElement("arg", value)))))
            .Save(Path.Combine(Path.GetDirectoryName(expectation), "bc-browse-receipt.xml"));
        return matched && readOnly && present ? 0 : 17;
    }
    private static void Receipt(string description, string leftTitle, string rightTitle)
    {
        var receipt = XDocument.Load(Path.Combine(run, "bc-browse-receipt.xml")).Root;
        string[] args = receipt.Element("arguments").Elements("arg").Select(item => item.Value).ToArray();
        Evidence.Add(new { description, receipt = receipt.ToString() });
        Check((bool)receipt.Element("matched") && (bool)receipt.Element("readonly") && (bool)receipt.Element("present") && args[0] == "/solo" && args[1] == "/readonly", description);
        Check(!File.Exists((string)receipt.Element("before")) && !File.Exists((string)receipt.Element("after")) &&
            !Directory.Exists(Path.GetDirectoryName((string)receipt.Element("before"))), "Temporary contributors cleaned only after helper exit");
        if (leftTitle != null) Check(args.Any(value => value.StartsWith("/lefttitle=" + leftTitle, StringComparison.Ordinal)) &&
            args.Any(value => value.StartsWith("/righttitle=" + rightTitle, StringComparison.Ordinal)), "Shelf titles identify immutable parent/shelve roles");
    }
    private static void Write(string workspace, string name, byte[] bytes) { File.WriteAllBytes(Path.Combine(workspace, name), bytes); }
    private static string Selector(string workspace) { return File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")); }
    private static string Snapshot(string workspace)
    {
        string metadata = Path.Combine(workspace, ".plastic") + "\\";
        using (var hash = SHA256.Create()) return String.Join("\n", Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(metadata, StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => path + "|" + Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)))));
    }
    private static long Revision(string path)
    { return (long)XDocument.Parse(Native(Path.GetDirectoryName(path), "fileinfo", path, "--xml")).Descendants("FileInfo").Single().Element("RevisionChangeset"); }
    private static string Native(string cwd, params string[] arguments)
    {
        if (!Path.GetFullPath(cwd).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture directory");
        return Execute(cwd, arguments);
    }
    private static string NativeRead(string cwd, params string[] arguments)
    {
        if (arguments.Length != 3 || arguments[0] != "status" || arguments[1] != "--short" || arguments[2] != "--machinereadable") throw new InvalidOperationException("Reference workspace is read-only");
        return Execute(cwd, arguments);
    }
    private static string Execute(string cwd, string[] arguments)
    {
        var start = new ProcessStartInfo(cm, String.Join(" ", arguments.Select(PlasticClient.QuoteArgument))) { WorkingDirectory = cwd,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("Native fixture timeout"); }
            string stdout = output.GetAwaiter().GetResult(), stderr = error.GetAwaiter().GetResult();
            Evidence.Add(new { cwd, arguments, exitCode = process.ExitCode, output = stdout, error = stderr });
            if (process.ExitCode != 0) throw new Exception("Native fixture failed: " + stdout + stderr); return stdout;
        }
    }
    private static void Check(bool value, string description)
    { assertions++; Evidence.Add(new { description, success = value }); if (!value) throw new Exception(description); }
    private static void Save(bool success, string error)
    { if (run != null) File.WriteAllText(Path.Combine(run, "bc-browsing-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
}
