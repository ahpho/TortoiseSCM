// GPL-2.0-or-later. Server-backed regression: unchanged original plus occupied move target.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class PartialMoveCollisionTests
{
    private static string CmPath = @"D:\Program Files\PlasticSCM5\client\cm.exe";
    private static readonly CancellationToken Token = CancellationToken.None;
    private static readonly XElement Evidence = new XElement("PartialMoveCollisionTests");
    private static int assertions;
    private static int Main(string[] args)
    {
        string run = null;
        try
        {
            if (args.Length < 1 || args.Length > 2) throw new ArgumentException("Pass a new isolated fixture manifest.json and optional cm.exe path.");
            string manifest = Path.GetFullPath(args[0]);
            if (!File.Exists(manifest) || Path.GetFileName(manifest) != "manifest.json") throw new ArgumentException("Pass a fixture manifest.json.");
            if (args.Length == 2) CmPath = Path.GetFullPath(args[1]);
            run = Path.GetDirectoryName(manifest); ValidateFixture(run);
            string producer = Path.Combine(run, "producer"), partial = Path.Combine(run, "partial"), consumer = Path.Combine(run, "consumer");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = CmPath, SettingsPath = Path.Combine(run, "move-collision-settings.xml") });
            Write(producer, "old.txt", "base A"); Write(producer, "safe-old.txt", "safe base");
            Native(producer, "add", "old.txt", "safe-old.txt"); Native(producer, "checkin", ".", "--all", "-c=move collision base");
            Native(partial, "partial", "configure", "+/old.txt", "+/safe-old.txt");
            long baseline = Revision(partial, "old.txt");
            Native(partial, "partial", "move", "old.txt", "new.txt"); Write(partial, "new.txt", "local moved and edited A");
            Native(partial, "partial", "move", "safe-old.txt", "safe-new.txt"); Write(partial, "safe-new.txt", "safe moved and edited content");
            Check(client.PreviewPartialStructureAsync(partial, Token).GetAwaiter().GetResult().Count == 0, "Unchanged server originals and unoccupied targets have no structural conflicts");
            Check(client.PreviewPartialConflictsAsync(partial, Token).GetAwaiter().GetResult().Count == 0, "MV plus CH rows do not create a false content conflict");
            var safe = Checkin(client, partial, Path.Combine(partial, "safe-new.txt"));
            Check(safe.Succeeded, "Unoccupied move plus edit can check in without structural preparation");
            Native(consumer, "update", ".", "--dontmerge");
            Check(!File.Exists(Path.Combine(consumer, "safe-old.txt")) && File.ReadAllText(Path.Combine(consumer, "safe-new.txt")) == "safe moved and edited content", "Independent consumer verifies safe move and edited bytes");
            Native(producer, "update", ".", "--dontmerge");
            Write(producer, "new.txt", "server added distinct B"); Native(producer, "add", "new.txt"); Native(producer, "checkin", "new.txt", "-c=occupy target without changing original");
            Check(Revision(producer, "old.txt") == baseline, "Server target addition leaves original item revision unchanged");
            string before = Snapshot(partial), selector = File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector"));
            var structures = client.PreviewPartialStructureAsync(partial, Token).GetAwaiter().GetResult();
            var collision = structures.Single(item => item.RepositoryPath == "/new.txt");
            Check(collision.Kind == "local-move" && collision.OriginalPath == "/old.txt" && collision.ResolutionOptions.Count == 0 && collision.Reason.IndexOf("occupied", StringComparison.OrdinalIgnoreCase) >= 0,
                "Structural preview reports unsupported destination collision despite unchanged original");
            var content = client.PreviewPartialConflictsAsync(partial, Token).GetAwaiter().GetResult();
            Check(content.Any(item => item.RepositoryPath == "/new.txt" && !item.CanResolve && item.Reason.Contains("local-move")), "Content preview retains structural collision instead of skipping MV/CH");
            Reject(() => client.PreparePartialStructureAsync(partial, "/new.txt", Token).GetAwaiter().GetResult(), "Structural prepare refuses occupied destination");
            Reject(() => Checkin(client, partial, Path.Combine(partial, "new.txt")), "Selected moved-file checkin refuses structural collision");
            Check(Snapshot(partial) == before && File.ReadAllText(Path.Combine(partial, ".plastic", "plastic.selector")) == selector, "Rejected operations preserve all working bytes and selector");
            Check(!client.HasSavedPartialStructureSession(partial) && !File.Exists(Path.Combine(partial, ".plastic", "tortoisescm-partial.session")), "Rejected preparation leaves no managed session");
            Native(consumer, "update", ".", "--dontmerge");
            Check(File.ReadAllText(Path.Combine(consumer, "old.txt")) == "base A" && File.ReadAllText(Path.Combine(consumer, "new.txt")) == "server added distinct B", "Independent consumer confirms rejected checkin did not replace either server item");
            Evidence.SetAttributeValue("passed", true); Evidence.SetAttributeValue("assertions", assertions);
            Console.WriteLine("PASS: " + assertions + " real Partial move collision assertions"); return 0;
        }
        catch (Exception error) { Evidence.Add(new XElement("Failure", error.ToString())); Console.Error.WriteLine(error); return 1; }
        finally { if (run != null && Evidence.Attribute("validated") != null) new XDocument(Evidence).Save(Path.Combine(run, "partial-move-collision-results.xml")); }
    }
    private static void ValidateFixture(string run)
    {
        string qa = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)), "qa"), id = Path.GetFileName(run);
        if (!String.Equals(Path.GetDirectoryName(run), qa, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(id, "^integration-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$")) throw new ArgumentException("Fixture must be under this checkout's qa/integration-* directory.");
        foreach (string role in new[] { "producer", "partial", "consumer" })
        {
            string root = Path.Combine(run, role);
            for (string path = root; !String.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Fixture paths cannot be redirected.");
            string selector = File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector"));
            if (!Regex.IsMatch(selector, "(?m)^\\s*smartbranch\\s+\"/main/tortoisescm-autotest-" + Regex.Escape(id) + "\"\\s*$") || !Regex.IsMatch(selector, "(?m)^repository \"TestSCM@[^\"\\r\\n]+\"\\s*$")) throw new ArgumentException("Unexpected fixture selector.");
            if (Directory.GetFileSystemEntries(root).Any(path => Path.GetFileName(path) != ".plastic")) throw new ArgumentException("Use a newly created empty fixture.");
        }
        Evidence.SetAttributeValue("validated", run);
    }
    private static PlasticCommandResult Checkin(PlasticClient client, string root, string path)
    { return client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = root, Paths = new List<string> { path }, Comment = "Partial move collision regression" }, Token).GetAwaiter().GetResult(); }
    private static long Revision(string root, string name)
    { return (long)XDocument.Parse(Native(root, "fileinfo", name, "--fields=RevisionChangeset", "--xml", "--encoding=utf-8")).Descendants("FileInfo").Single().Element("RevisionChangeset"); }
    private static string Native(string root, params string[] args)
    {
        var start = new ProcessStartInfo(CmPath) { WorkingDirectory = root, Arguments = String.Join(" ", args.Select(Quote)), UseShellExecute = false,
            CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false) };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Native test command timed out."); }
            string text = output.GetAwaiter().GetResult(), errors = error.GetAwaiter().GetResult();
            Evidence.Add(new XElement("Native", new XAttribute("cwd", root), new XAttribute("arguments", start.Arguments), new XAttribute("exit", process.ExitCode), new XElement("Output", text), new XElement("Error", errors)));
            if (process.ExitCode != 0) throw new IOException("Native command failed: " + start.Arguments + " " + text + " " + errors);
            return text;
        }
    }
    private static string Quote(string value) { return "\"" + Regex.Replace(Regex.Replace(value, "(\\\\*)\"", "$1$1\\\""), "(\\\\+)$", "$1$1") + "\""; }
    private static void Write(string root, string name, string value) { File.WriteAllText(Path.Combine(root, name), value, new UTF8Encoding(false)); }
    private static string Snapshot(string root)
    { return String.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + ".plastic" + Path.DirectorySeparatorChar)).OrderBy(path => path).Select(path => path.Substring(root.Length) + "|" + Convert.ToBase64String(File.ReadAllBytes(path)))); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; Evidence.Add(new XElement("Assert", message)); }
    private static void Reject(Action action, string message) { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, message); }
}
