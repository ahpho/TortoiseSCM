// GPL-2.0-or-later. Prepared checkin on fresh, isolated real Plastic workspaces.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using TortoiseSCM;

internal static class CheckinPreflightIntegrationTests
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly List<object> Evidence = new List<object>();
    private static string run, cm, branch;
    private static int assertions;
    private static int Main(string[] args)
    {
        try { Run(args).GetAwaiter().GetResult(); Save(true, null); Console.WriteLine("PASS: " + assertions + " prepared checkin live assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); Save(false, error.ToString()); return 1; }
    }
    private static async Task Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Usage: CheckinPreflightIntegrationTests.exe <fresh-manifest.json> <cm.exe>");
        var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
        run = Path.GetFullPath((string)manifest["runDirectory"]); cm = args[1]; branch = (string)manifest["branch"];
        string producer = (string)manifest["producer"], partial = (string)manifest["partial"], consumer = (string)manifest["consumer"], reference = (string)manifest["referenceWorkspace"];
        Check((bool)manifest["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Fresh dedicated test branch fixture");
        foreach (string workspace in new[] { producer, partial, consumer })
            Check(Path.GetFullPath(workspace).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && Selector(workspace).Contains(branch) &&
                !Directory.GetFileSystemEntries(workspace).Any(path => Path.GetFileName(path) != ".plastic"), "Isolated empty fixture workspace");
        string referenceBefore = Snapshot(reference), referenceSelector = Selector(reference), referenceStatus = NativeRead(reference);
        foreach (string relative in new[] { "selected 中文.txt", "outside.txt", "folder/one.txt", "folder/two.txt" }) Write(producer, relative, "base\n");
        Native(producer, "add", producer, "-R"); Native(producer, "checkin", producer, "--all", "-c=Prepared checkin common baseline");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = cm, SettingsPath = Path.Combine(run, "prepared-settings.xml") });
        foreach (string workspace in new[] { producer, partial })
        {
            if (workspace == partial) Native(partial, "partial", "update", partial, "--report");
            var context = client.DiscoverWorkspace(workspace);
            string role = Path.GetFileName(workspace), selector = Selector(workspace);
            string selected = Path.Combine(workspace, "selected 中文.txt"), outside = Path.Combine(workspace, "outside.txt"), folder = Path.Combine(workspace, "folder");
            Write(workspace, "selected 中文.txt", role + " reviewed\n"); Write(workspace, "outside.txt", role + " excluded\n");
            var preview = await client.PrepareCheckinAsync(workspace, new[] { selected }, context.Repository, context.Selector, Token);
            Check(preview.IsPartial == (workspace == partial), role + " authoritative workspace mode in preview");
            Check(preview.Files.Count == 1 && Same(preview.Files[0].Path, selected), role + " preview includes only selected changed file");
            Write(workspace, "selected 中文.txt", role + " changed after review\n");
            string beforeHead = BranchHead(workspace), beforeStatus = Status(workspace), beforeSnapshot = Snapshot(workspace);
            await Reject(() => client.CheckinPreparedAsync(preview, role + " stale rejected", Token), role + " post-preview content change blocks submit");
            Check(BranchHead(workspace) == beforeHead && Status(workspace) == beforeStatus && Snapshot(workspace) == beforeSnapshot && Selector(workspace) == selector,
                role + " stale rejection preserves server head, pending status, all bytes and selector");
            preview = await client.PrepareCheckinAsync(workspace, new[] { selected }, context.Repository, context.Selector, Token);
            Check((await client.CheckinPreparedAsync(preview, role + " reviewed selected checkin", Token)).Succeeded, role + " fresh explicit file submits successfully");
            var pending = await client.GetStatusAsync(workspace, Token);
            Check(!pending.Any(item => Same(item.Path, selected)) && pending.Any(item => Same(item.Path, outside)), role + " selected file submitted, excluded file remains pending");
            Check(File.ReadAllText(outside, Utf8) == role + " excluded\n" && Selector(workspace) == selector, role + " excluded bytes and selector preserved");
            Native(consumer, "update", consumer, "--dontmerge");
            Check(File.ReadAllText(Path.Combine(consumer, "selected 中文.txt"), Utf8) == role + " changed after review\n" &&
                File.ReadAllText(Path.Combine(consumer, "outside.txt"), Utf8) == "base\n", role + " independent consumer confirms exact selected-only server bytes");

            Write(workspace, "folder/one.txt", role + " one\n"); Write(workspace, "folder/two.txt", role + " two\n");
            preview = await client.PrepareCheckinAsync(workspace, new[] { folder }, context.Repository, context.Selector, Token);
            Check(preview.Files.Count(item => !item.IsDirectory) == 2 && preview.Files.Any(item => Same(item.Path, Path.Combine(folder, "one.txt"))) &&
                preview.Files.Any(item => Same(item.Path, Path.Combine(folder, "two.txt"))), role + " recursive directory preview lists actual pending children");
            string newName = role + "-new.txt";
            string newPath = Path.Combine(folder, newName); Write(workspace, "folder/" + newName, role + " new\n");
            Native(workspace, "add", newPath);
            beforeHead = BranchHead(workspace); beforeStatus = Status(workspace); beforeSnapshot = Snapshot(workspace);
            await Reject(() => client.CheckinPreparedAsync(preview, role + " expanded scope rejected", Token), role + " new controlled pending child blocks stale recursive submit");
            Check(BranchHead(workspace) == beforeHead && Status(workspace) == beforeStatus && Snapshot(workspace) == beforeSnapshot && Selector(workspace) == selector,
                role + " expanded-scope rejection preserves server, pending files and selector");
            preview = await client.PrepareCheckinAsync(workspace, new[] { folder }, context.Repository, context.Selector, Token);
            Check(preview.Files.Any(item => Same(item.Path, newPath)), role + " refreshed directory preview includes new child");
            Check((await client.CheckinPreparedAsync(preview, role + " reviewed directory checkin", Token)).Succeeded, role + " refreshed directory scope submits successfully");
            pending = await client.GetStatusAsync(workspace, Token);
            Check(pending.Count == 1 && Same(pending[0].Path, outside), role + " directory submit leaves only excluded pending file");
            Native(consumer, "update", consumer, "--dontmerge");
            Check(File.ReadAllText(Path.Combine(consumer, "folder", "one.txt"), Utf8) == role + " one\n" &&
                File.ReadAllText(Path.Combine(consumer, "folder", "two.txt"), Utf8) == role + " two\n" &&
                File.ReadAllText(Path.Combine(consumer, "folder", newName), Utf8) == role + " new\n", role + " consumer receives exact reviewed directory bytes");
            Check(Selector(workspace) == selector, role + " all prepared operations retain selector");
        }
        Check(Snapshot(reference) == referenceBefore && Selector(reference) == referenceSelector && NativeRead(reference) == referenceStatus,
            "Original TestSCM files, selector and pending status unchanged");
    }
    private static async Task Reject(Func<Task<PlasticCommandResult>> operation, string description)
    {
        try { var result = await operation(); Check(!result.Succeeded, description); }
        catch (InvalidOperationException error) { Evidence.Add(new { rejection = error.Message }); Check(true, description); }
    }
    private static string BranchHead(string workspace)
    {
        string xml = Native(workspace, "find", "changeset", "where branch = '" + branch + "' order by changesetid desc limit 1", "--xml", "--nototal", "--encoding=utf-8");
        return XDocument.Parse(xml).ToString(SaveOptions.DisableFormatting);
    }
    private static bool Same(string a, string b) { return String.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
    private static void Write(string workspace, string name, string content)
    { string path = Path.Combine(workspace, name.Replace('/', '\\')); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, content, Utf8); }
    private static string Selector(string workspace) { return File.ReadAllText(Path.Combine(workspace, ".plastic", "plastic.selector")); }
    private static string Status(string workspace)
    { return NormalizeStatus(Native(workspace, "status", "--short", "--machinereadable")); }
    private static string NormalizeStatus(string text)
    { return String.Join("\n", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).OrderBy(line => line, StringComparer.Ordinal)); }
    private static string Snapshot(string workspace)
    {
        string metadata = Path.Combine(workspace, ".plastic") + "\\";
        using (var hash = SHA256.Create()) return String.Join("\n", Directory.GetFiles(workspace, "*", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(metadata, StringComparison.OrdinalIgnoreCase)).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => path + "|" + Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)))));
    }
    private static string Native(string cwd, params string[] arguments)
    {
        if (!Path.GetFullPath(cwd).StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Unsafe fixture directory");
        return Execute(cwd, arguments);
    }
    private static string NativeRead(string cwd) { return NormalizeStatus(Execute(cwd, new[] { "status", "--short", "--machinereadable" })); }
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
    { if (run != null) File.WriteAllText(Path.Combine(run, "checkin-preflight-results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, evidence = Evidence }), Utf8); }
}
