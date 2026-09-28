// GPL-2.0-or-later. Real Plastic workfile states; helper observes the Beyond Compare process contract.
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

internal static class WorkingBeyondCompareIntegrationTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly List<object> Evidence = new List<object>();
    private static string run, cm;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "/solo") return Tool(args);
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " Beyond Compare workfile live assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static async Task Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: WorkingBeyondCompareIntegrationTests.exe <fresh-manifest.json> <cm.exe>");
        var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = Path.GetFullPath((string)manifest["runDirectory"]); cm = args[1];
        string producer = (string)manifest["producer"], partial = (string)manifest["partial"], reference = (string)manifest["referenceWorkspace"];
        string branch = (string)manifest["branch"];
        Check((bool)manifest["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Completed dedicated test branch fixture");
        foreach (string workspace in new[] { producer, partial })
        {
            Check(Path.GetFullPath(workspace).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && Selector(workspace).Contains(branch), "Isolated workspace selector");
            Check(!Directory.GetFileSystemEntries(workspace).Any(path => Path.GetFileName(path) != ".plastic"), "Fresh empty fixture");
        }
        string referenceBefore = Snapshot(reference), referenceSelector = Selector(reference);
        string referenceStatus = NativeRead(reference, "status", "--short", "--machinereadable");
        const string changed = "changed 中文 & file.txt", deleted = "deleted.txt", lost = "lost.txt", oldMove = "move old.txt", newMove = "renamed.txt",
            added = "added.txt", binary = "added 中文 & binary.bin", duplicate = "duplicate deleted.txt", privateFile = "private.txt";
        byte[] baseBytes = Utf8.GetBytes("base 中文\r\nline two\r\n"), changedBytes = Utf8.GetBytes("working 中文\r\nline two\r\n"),
            deleteBytes = Utf8.GetBytes("base deleted.txt"), lostBytes = Utf8.GetBytes("base lost.txt"), moveBase = Utf8.GetBytes("base moved.txt"),
            moveAfter = Utf8.GetBytes("moved working\n"), addBytes = Utf8.GetBytes("new content"), binaryBytes = new byte[] { 0, 255, 1, 128, 13, 10, 0 },
            duplicateBytes = Utf8.GetBytes("identical baseline requires identity\n");
        Write(producer, changed, baseBytes); Write(producer, deleted, deleteBytes); Write(producer, lost, lostBytes); Write(producer, oldMove, moveBase);
        Write(producer, duplicate, duplicateBytes); Write(producer, "duplicate retained.txt", duplicateBytes);
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "-c=BC workfile isolated baseline");
        // Commit baseline exactly once: both trees now refer to the same native items.
        Native(partial, "partial", "update", partial, "--report");
        string helper = Path.Combine(run, "BComp.exe"); File.Copy(Assembly.GetExecutingAssembly().Location, helper);
        Environment.SetEnvironmentVariable("TSCM_BC_WORKING_EXPECTATION", Path.Combine(run, "bc-working-expectation.xml"));
        var client = new PlasticClient(new PlasticClientConfig { CmPath = cm, UseBeyondCompare = true, BeyondComparePath = helper, SettingsPath = Path.Combine(run, "working-settings.xml") });
        foreach (string workspace in new[] { producer, partial })
        {
            Write(workspace, changed, changedBytes);
            Mutate(workspace, workspace == partial, "remove", Path.Combine(workspace, deleted));
            File.Delete(Path.Combine(workspace, lost));
            Mutate(workspace, workspace == partial, "move", Path.Combine(workspace, oldMove), Path.Combine(workspace, newMove)); Write(workspace, newMove, moveAfter);
            Write(workspace, added, addBytes); Write(workspace, binary, binaryBytes);
            Mutate(workspace, workspace == partial, "add", Path.Combine(workspace, added), Path.Combine(workspace, binary));
            Write(workspace, privateFile, Utf8.GetBytes("private content\n"));
            Mutate(workspace, workspace == partial, "remove", Path.Combine(workspace, duplicate));
            var cases = new[] {
                new Case(changed, "CH", baseBytes, changedBytes, false), new Case(added, "AD", new byte[0], addBytes, false),
                new Case(binary, "AD", new byte[0], binaryBytes, false), new Case(deleted, "DE", deleteBytes, new byte[0], true),
                new Case(lost, "LD", lostBytes, new byte[0], true), new Case(newMove, "MV", moveBase, moveAfter, false) };
            string before = Snapshot(workspace), selector = Selector(workspace), status = Native(workspace, "status", "--short", "--machinereadable");
            var rows = await client.GetStatusAsync(workspace, Token);
            Evidence.Add(new { workspace, rows });
            foreach (Case item in cases)
            {
                string path = Path.Combine(workspace, item.Path);
                Check(rows.Any(row => row.Path.Equals(path, StringComparison.OrdinalIgnoreCase) && row.StatusCode == item.Status), "Native status " + item.Status + ": " + item.Path);
                Expect(item.Before, item.After, item.Missing ? "" : path);
                Check((await client.OpenDiffToolAsync(path, Token)).Succeeded, "Working BC launch succeeds: " + Path.GetFileName(workspace) + " / " + item.Path);
                Receipt(item);
            }
            await Reject(client, Path.Combine(workspace, privateFile), "Private file rejected");
            await Reject(client, Path.Combine(workspace, "never controlled.txt"), "Unknown missing file rejected");
            await Reject(client, Path.Combine(workspace, duplicate), "Ambiguous deleted revision/hash rejected");
            Check(Snapshot(workspace) == before && Selector(workspace) == selector && Native(workspace, "status", "--short", "--machinereadable") == status,
                "Comparisons/rejections preserve files, attributes, pending status and selector: " + Path.GetFileName(workspace));
            Write(workspace, deleted, Utf8.GetBytes("recreated private file must survive\n"));
            string recreated = Snapshot(workspace), recreatedStatus = Native(workspace, "status", "--short", "--machinereadable");
            await Reject(client, Path.Combine(workspace, deleted), "Recreated scheduled-deletion path rejected");
            Check(Snapshot(workspace) == recreated && Native(workspace, "status", "--short", "--machinereadable") == recreatedStatus && Selector(workspace) == selector,
                "Rejected recreated deletion preserves replacement file and status");
            File.Delete(Path.Combine(workspace, deleted));
        }
        Check(Snapshot(reference) == referenceBefore && Selector(reference) == referenceSelector && NativeRead(reference, "status", "--short", "--machinereadable") == referenceStatus,
            "Original TestSCM files, selector and pending status remain unchanged");
    }
    private sealed class Case
    {
        internal readonly string Path, Status; internal readonly byte[] Before, After; internal readonly bool Missing;
        internal Case(string path, string status, byte[] before, byte[] after, bool missing) { Path = path; Status = status; Before = before; After = after; Missing = missing; }
    }
    private static void Expect(byte[] before, byte[] after, string local)
    {
        File.Delete(Path.Combine(run, "bc-working-receipt.xml"));
        new XDocument(new XElement("expected", new XElement("before", Convert.ToBase64String(before)), new XElement("after", Convert.ToBase64String(after)), new XElement("local", local)))
            .Save(Path.Combine(run, "bc-working-expectation.xml"));
    }
    private static int Tool(string[] args)
    {
        string expectation = Environment.GetEnvironmentVariable("TSCM_BC_WORKING_EXPECTATION");
        var expected = XDocument.Load(expectation).Root;
        string local = (string)expected.Element("local");
        bool matched = args.Length >= 4 && args.Contains("/readonly") && File.ReadAllBytes(args[2]).SequenceEqual(Convert.FromBase64String((string)expected.Element("before"))) &&
            File.ReadAllBytes(args[3]).SequenceEqual(Convert.FromBase64String((string)expected.Element("after")));
        bool readOnly = (File.GetAttributes(args[2]) & FileAttributes.ReadOnly) != 0 && (local.Length > 0 || (File.GetAttributes(args[3]) & FileAttributes.ReadOnly) != 0);
        bool localIdentity = local.Length == 0 || String.Equals(local, args[3], StringComparison.OrdinalIgnoreCase);
        Thread.Sleep(120);
        bool present = File.Exists(args[2]) && File.Exists(args[3]);
        new XDocument(new XElement("receipt", new XElement("before", args[2]), new XElement("after", args[3]), new XElement("matched", matched),
            new XElement("readonly", readOnly), new XElement("localIdentity", localIdentity), new XElement("present", present), new XElement("arguments", args.Select(value => new XElement("arg", value)))))
            .Save(Path.Combine(Path.GetDirectoryName(expectation), "bc-working-receipt.xml"));
        return matched && readOnly && localIdentity && present ? 0 : 17;
    }
    private static void Receipt(Case item)
    {
        var receipt = XDocument.Load(Path.Combine(run, "bc-working-receipt.xml")).Root;
        string[] args = receipt.Element("arguments").Elements("arg").Select(value => value.Value).ToArray();
        Evidence.Add(new { item.Path, receipt = receipt.ToString() });
        Check((bool)receipt.Element("matched") && (bool)receipt.Element("readonly") && (bool)receipt.Element("localIdentity") && (bool)receipt.Element("present") && args[0] == "/solo" && args[1] == "/readonly", "BC sees exact baseline/current bytes and correct readonly/lifetime contract: " + item.Path);
        Check(!File.Exists((string)receipt.Element("before")) && !Directory.Exists(Path.GetDirectoryName((string)receipt.Element("before"))) &&
            (item.Missing ? !File.Exists((string)receipt.Element("after")) : File.Exists((string)receipt.Element("after"))), "Only temporary contributors are removed after helper exit");
        Check(args.Any(value => value.StartsWith("/lefttitle=", StringComparison.Ordinal) && value.Contains(item.Status == "AD" ? "empty" : "base cs:")) &&
            args.Any(value => value.StartsWith("/righttitle=", StringComparison.Ordinal) && value.Contains(item.Missing ? "empty" : "working")), "BC titles distinguish empty, base and working sides");
    }
    private static async Task Reject(PlasticClient client, string path, string description)
    {
        File.Delete(Path.Combine(run, "bc-working-receipt.xml")); bool rejected = false;
        try { await client.OpenDiffToolAsync(path, Token); }
        catch (ArgumentException) { rejected = true; }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(run, "bc-working-receipt.xml")), description + " before BC launch");
    }
    private static void Mutate(string workspace, bool partial, params string[] arguments)
    { Native(workspace, partial ? new[] { "partial" }.Concat(arguments).ToArray() : arguments); }
    private static void Write(string workspace, string name, byte[] bytes) { File.WriteAllBytes(Path.Combine(workspace, name), bytes); }
    private static string Selector(string workspace) { return File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")); }
    private static string Snapshot(string workspace)
    {
        string metadata = Path.Combine(workspace, ".plastic") + "\\";
        using (var hash = SHA256.Create()) return String.Join("\n", Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(metadata, StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => path + "|" + File.GetAttributes(path) + "|" + Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)))));
    }
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
    { if (run != null) File.WriteAllText(Path.Combine(run, "bc-working-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
}
