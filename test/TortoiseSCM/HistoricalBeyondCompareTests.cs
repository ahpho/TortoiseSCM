// GPL-2.0-or-later. Historical BC contract tests with isolated cm/BComp subprocesses.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TortoiseSCM;

internal static class HistoricalBeyondCompareTests
{
    private static int assertions;
    private static string root;
    private static readonly CancellationToken Token = CancellationToken.None;
    private const string Repository = "test@server:8087";
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Child(args);
        root = Path.Combine(Path.GetTempPath(), "TSCM-history-bc-" + Guid.NewGuid().ToString("N") + " 中文 &");
        Directory.CreateDirectory(root);
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + assertions + " historical Beyond Compare assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Environment.SetEnvironmentVariable("TSCM_HISTORY_BC_ROOT", null); Directory.Delete(root, true); }
    }

    private static async Task Run()
    {
        string executable = Assembly.GetExecutingAssembly().Location, bc = Path.Combine(root, "BComp.exe");
        File.Copy(executable, bc); Environment.SetEnvironmentVariable("TSCM_HISTORY_BC_ROOT", root);
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "history-bc\nguid\nStandard\n");
        RestoreSelector();
        var client = new PlasticClient(new PlasticClientConfig { CmPath = executable, BeyondComparePath = bc, UseBeyondCompare = true,
            Timeout = TimeSpan.FromSeconds(10) });
        var parentComparison = await client.GetChangesetParentComparisonAsync(root, 10, Repository, Token);
        Check(parentComparison.FromChangeset == 1 && parentComparison.ToChangeset == 10,
            "Default file diff reads actual non-adjacent parent, never changeset minus one");
        Check(File.ReadAllText(Path.Combine(root, "parent-query")).Contains("where changesetid = 10 on repository '" + Repository + "'"),
            "Parent metadata query pins selected changeset and repository");
        Check(parentComparison.Files.Single(f => f.Status == "M").OldPath == "/old 中文 &.txt",
            "Parent comparison preserves authoritative historical source path");
        foreach (string mode in new[] { "parent-root", "parent-wrong", "parent-malformed", "find-fail", "find-selector" })
        {
            File.WriteAllText(Path.Combine(root, "mode"), mode);
            await Reject<Exception>(() => client.GetChangesetParentComparisonAsync(root, 10, Repository, Token),
                "Parent lookup rejects invalid identity, absence, server error or context race: " + mode);
            RestoreSelector();
        }
        File.WriteAllText(Path.Combine(root, "mode"), "");
        await Reject<InvalidOperationException>(() => client.GetChangesetParentComparisonAsync(root, 10, "other@server", Token),
            "Parent lookup rejects stale expected repository");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Reject<OperationCanceledException>(() => client.GetChangesetParentComparisonAsync(root, 10, Repository, cancelled.Token),
                "Cancelled parent lookup cannot start comparison");
        }
        var comparison = await client.GetChangesetComparisonAsync(root, 1, 2, Token);
        foreach (var row in comparison.Files)
        {
            File.Delete(Path.Combine(root, "commands"));
            Check((await client.OpenChangesetFileDiffToolAsync(root, comparison, row, Token)).Succeeded, "Supported row launches BC: " + row.Status);
            XElement recording = XDocument.Load(Path.Combine(root, "viewer.xml")).Root;
            var argv = recording.Elements("arg").Select(x => x.Value).ToArray();
            string fromPath = String.IsNullOrEmpty(row.OldPath) ? row.Path : row.OldPath;
            Check(argv[0] == "/solo" && argv[1] == "/readonly", "History uses independent read-only BC session");
            Check(argv[4] == "/lefttitle=serverpath:" + fromPath + "#cs:1@" + Repository + (row.Status == "A" ? " (不存在 / empty)" : "") &&
                argv[5] == "/righttitle=serverpath:" + row.Path + "#cs:2@" + Repository + (row.Status == "D" ? " (不存在 / empty)" : ""), "History titles identify exact revision/path and absent side");
            byte[] left = Convert.FromBase64String((string)recording.Element("left")), right = Convert.FromBase64String((string)recording.Element("right"));
            Check(left.SequenceEqual(row.Status == "A" ? new byte[0] : Bytes(fromPath, true)) &&
                right.SequenceEqual(row.Status == "D" ? new byte[0] : Bytes(row.Path, false)), "Exact contents, including empty and binary sides: " + row.Status);
            Check((bool)recording.Element("readonly"), "Both downloaded and synthetic empty files are read-only");
            Check(!File.Exists(argv[2]) && !File.Exists(argv[3]) && !Directory.Exists(Path.GetDirectoryName(argv[2])), "Historical temporary inputs removed after BC completes");
            string commands = File.ReadAllText(Path.Combine(root, "commands"));
            Check(commands.Split('\n').First() == "diff", "Selected row is re-proved by native tree comparison before reads");
            Check(commands.Split('\n').Count(x => x == "cat") == (row.Status == "A" || row.Status == "D" ? 1 : 2), "Only proven existing endpoints are downloaded");
        }
        var added = comparison.Files.Single(x => x.Status == "A");
        foreach (var spoof in new[] {
            new PlasticChangesetFile { Status = "D", Path = added.Path, ItemType = "F" },
            new PlasticChangesetFile { Status = "A", Path = "/invented.txt", ItemType = "F" },
            new PlasticChangesetFile { Status = "A", Path = added.Path, OldPath = "/wrong.txt", ItemType = "F" },
            new PlasticChangesetFile { Status = "A", Path = added.Path, ItemType = "B" } })
        {
            File.Delete(Path.Combine(root, "viewer.xml"));
            await Reject<InvalidOperationException>(() => client.OpenChangesetFileDiffToolAsync(root, comparison, spoof, Token), "Forged row rejected before BC");
            Check(!File.Exists(Path.Combine(root, "viewer.xml")), "Forged row cannot launch BC");
        }
        foreach (string type in new[] { "D", "S", "X" })
            await Reject<ArgumentException>(() => client.OpenChangesetFileDiffToolAsync(root, comparison,
                new PlasticChangesetFile { Status = "A", Path = added.Path, ItemType = type }, Token), "Unsupported row type rejected: " + type);

        foreach (string mode in new[] { "different-row", "duplicate-row", "diff-fail", "ls-fail", "cat-fail", "diff-selector", "cat-selector" })
        {
            File.WriteAllText(Path.Combine(root, "mode"), mode); File.Delete(Path.Combine(root, "viewer.xml"));
            await Reject<Exception>(() => client.OpenChangesetFileDiffToolAsync(root, comparison, added, Token), "Failure or context race rejects selected comparison: " + mode);
            Check(!File.Exists(Path.Combine(root, "viewer.xml")), "Failure never substitutes absent content or launches BC: " + mode);
            RestoreSelector();
        }
        File.WriteAllText(Path.Combine(root, "mode"), "");
        comparison.Repository = "other@server:8087";
        await Reject<InvalidOperationException>(() => client.OpenChangesetFileDiffToolAsync(root, comparison, added, Token), "Reviewed repository mismatch rejects comparison");
        comparison.Repository = Repository;
        comparison.RootPath = Path.Combine(root, "elsewhere");
        await Reject<InvalidOperationException>(() => client.OpenChangesetFileDiffToolAsync(root, comparison, added, Token), "Reviewed root mismatch rejects comparison");
        comparison.RootPath = root;
        await Reject<ArgumentException>(() => client.OpenRevisionDiffToolAsync(root, added.Path, 1, 2, Token), "Unproven ordinary history missing endpoint remains an error");
        await Reject<InvalidOperationException>(() => client.OpenRevisionDiffToolAsync(root, "/old 中文 &.txt", "/new 中文 &.txt", 1, 2, "other@server", Token),
            "Ordinary marked comparison rejects changed expected repository");
        Check((await client.OpenRevisionDiffToolAsync(root, "/old 中文 &.txt", "/new 中文 &.txt", 1, 2, Token)).Succeeded, "Ordinary cross-path historical comparison still works");
        var ordinary = XDocument.Load(Path.Combine(root, "viewer.xml")).Root.Elements("arg").Select(x => x.Value).ToArray();
        Check(ordinary[4] == "/lefttitle=serverpath:/old 中文 &.txt#cs:1@" + Repository && ordinary[5] == "/righttitle=serverpath:/new 中文 &.txt#cs:2@" + Repository, "Ordinary history has real endpoint titles too");

        File.WriteAllText(Path.Combine(root, "mode"), "exit17");
        var failed = await client.OpenChangesetFileDiffToolAsync(root, comparison, added, Token);
        Check(!failed.Succeeded && failed.ExitCode == 17, "BC failure exit remains failure");
        var failureArgs = XDocument.Load(Path.Combine(root, "viewer.xml")).Root.Elements("arg").Select(x => x.Value).ToArray();
        Check(!File.Exists(failureArgs[2]) && !File.Exists(failureArgs[3]), "Confirmed failure still cleans comparison inputs");

        File.WriteAllText(Path.Combine(root, "mode"), "wait"); File.Delete(Path.Combine(root, "opened"));
        using (var cancel = new CancellationTokenSource())
        {
            var operation = client.OpenChangesetFileDiffToolAsync(root, comparison, added, cancel.Token);
            for (int i = 0; i < 100 && !File.Exists(Path.Combine(root, "opened")); i++) await Task.Delay(50);
            Check(File.Exists(Path.Combine(root, "opened")), "BC helper opened with comparison inputs");
            cancel.Cancel(); await Task.Delay(150);
            var pendingArgs = XDocument.Load(Path.Combine(root, "viewer.xml")).Root.Elements("arg").Select(x => x.Value).ToArray();
            Check(!operation.IsCompleted && File.Exists(pendingArgs[2]) && File.Exists(pendingArgs[3]), "Cancelled comparison retains inputs while BC is open");
            File.WriteAllText(Path.Combine(root, "release"), "done");
            await Reject<OperationCanceledException>(() => operation, "Cancellation reports after BC closes");
            Check(!File.Exists(pendingArgs[2]) && !File.Exists(pendingArgs[3]), "Cancelled comparison cleans only after BC closes");
        }
        File.WriteAllText(Path.Combine(root, "mode"), "exit102");
        await Reject<InvalidOperationException>(() => client.OpenChangesetFileDiffToolAsync(root, comparison, added, Token), "Uncertain BC lifetime fails explicitly");
        var retained = XDocument.Load(Path.Combine(root, "viewer.xml")).Root.Elements("arg").Select(x => x.Value).ToArray();
        Check(File.Exists(retained[2]) && File.Exists(retained[3]), "Uncertain lifetime retains both real and synthetic inputs");
        foreach (string path in retained.Skip(2).Take(2)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        Directory.Delete(Path.GetDirectoryName(retained[2]));
        Check(!Directory.GetFiles(root, "*.txt").Any(), "Historical comparison never restores files into workspace");
    }

    private static byte[] Bytes(string path, bool first)
    { return path == "/binary.bin" ? new byte[] { 0, 255, (byte)(first ? 1 : 2), 13, 10 } : Encoding.UTF8.GetBytes((first ? "before " : "after ") + path + "\r\n"); }
    private static void RestoreSelector()
    { File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\""); }
    private static int Child(string[] args)
    {
        string testRoot = Environment.GetEnvironmentVariable("TSCM_HISTORY_BC_ROOT");
        string mode = File.Exists(Path.Combine(testRoot, "mode")) ? File.ReadAllText(Path.Combine(testRoot, "mode")) : "";
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "/solo")
        {
            new XDocument(new XElement("viewer", args.Select(a => new XElement("arg", a)),
                new XElement("left", Convert.ToBase64String(File.ReadAllBytes(args[2]))),
                new XElement("right", Convert.ToBase64String(File.ReadAllBytes(args[3]))),
                new XElement("readonly", args.Skip(2).Take(2).All(p => (File.GetAttributes(p) & FileAttributes.ReadOnly) != 0))))
                .Save(Path.Combine(testRoot, "viewer.xml"));
            File.WriteAllText(Path.Combine(testRoot, "opened"), "yes");
            if (mode == "wait") for (int i = 0; i < 200 && !File.Exists(Path.Combine(testRoot, "release")); i++) Thread.Sleep(50);
            return mode.StartsWith("exit") ? Int32.Parse(mode.Substring(4)) : 0;
        }
        File.AppendAllText(Path.Combine(testRoot, "commands"), args[0] + "\n");
        if (mode == args[0] + "-fail") { Console.Error.WriteLine("Deliberate server failure"); return 19; }
        if (mode == args[0] + "-selector") File.WriteAllText(Path.Combine(testRoot, ".plastic", "plastic.selector"), "repository \"other@server\"");
        if (args[0] == "find")
        {
            File.WriteAllText(Path.Combine(testRoot, "parent-query"), String.Join("\n", args));
            Console.WriteLine(new XElement("PLASTICQUERY", new XElement("CHANGESET",
                new XElement("ID", 100), new XElement("CHANGESETID", mode == "parent-wrong" ? 9 : 10),
                new XElement("PARENT", mode == "parent-root" ? "-1" : mode == "parent-malformed" ? "invalid" : "1"),
                new XElement("GUID", "9bbdadde-8a92-4537-b233-715a2646e879"), new XElement("BRANCH", "/main"),
                new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"), new XElement("REPOSITORY", "test"),
                new XElement("DATE", "2026-09-28T10:00:00Z"), new XElement("OWNER", "tester"), new XElement("COMMENT", "parent fixture"))));
            return 0;
        }
        if (args[0] == "diff")
        {
            Console.WriteLine((mode == "different-row" ? "C" : "A") + "|/added 中文 &.txt|F||");
            if (mode == "duplicate-row") Console.WriteLine("A|/added 中文 &.txt|F||");
            Console.WriteLine("D|/deleted.txt|F||\nC|/changed.txt|F||\nM|/new 中文 &.txt|F|/old 中文 &.txt|/new 中文 &.txt\nC|/binary.bin|B||"); return 0;
        }
        if (args[0] == "ls")
        {
            bool first = args.Any(a => a.StartsWith("--tree=cs:1@"));
            bool missing = first && args[1] == "/added 中文 &.txt" || !first && args[1] == "/deleted.txt";
            Console.WriteLine(new XElement("LsResults", missing ? null : new XElement("LsItem", new XElement("CurrentPath", args[1]),
                new XElement("Type", args[1] == "/binary.bin" ? "bin" : "txt"), new XElement("Repository", "rep:" + Repository)))); return 0;
        }
        if (args[0] == "cat")
        {
            string path = args[1].Substring("serverpath:".Length).Split('#')[0];
            File.WriteAllBytes(args.Single(a => a.StartsWith("--file=")).Substring(7), Bytes(path, args[1].Contains("#cs:1@"))); return 0;
        }
        return 20;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task> action, string message) where T : Exception
    { bool rejected = false; try { await action(); } catch (T) { rejected = true; } Check(rejected, message); }
}
