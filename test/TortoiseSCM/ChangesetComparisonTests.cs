// GPL-2.0-or-later. Read-only complete tree comparison and renamed file tests.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class ChangesetComparisonTests
{
    private static int assertions;
    private const string Repository = "test@server:8087";
    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-tree-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "comparison\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"");
            var config = new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(10) };
            var client = new PlasticClient(config);
            var token = CancellationToken.None;
            var comparison = client.GetChangesetComparisonAsync(temporary, 1, 2, token).GetAwaiter().GetResult();
            Check(comparison.Repository == Repository && comparison.RootPath == temporary && comparison.FromChangeset == 1 && comparison.ToChangeset == 2, "Comparison pins repository and both endpoints");
            Check(comparison.Files.Count == 8, "All statuses and multiple changes to same path retained");
            Check(comparison.Files.Any(f => f.Status == "A" && f.Path == "/added 中文.txt"), "Unicode added path parsed");
            Check(comparison.Files.Any(f => f.Status == "D" && f.Path == "/deleted.txt"), "Deleted path retained as existing source");
            Check(comparison.Files.Any(f => f.Status == "M" && f.Path == "/new.txt" && f.OldPath == "/old.txt"), "File rename endpoints retained");
            Check(comparison.Files.Any(f => f.Status == "C" && f.Path == "/new.txt" && f.OldPath == "/old.txt"), "Changed rename row has original source path");
            Check(comparison.Files.Any(f => f.Status == "C" && f.Path == "/new dir/child.txt" && f.OldPath == "/old dir/child.txt"), "Changed descendant follows moved parent");
            Check(comparison.Files.Any(f => f.Status == "M" && f.ItemType == "D"), "Directory structural changes distinguished");
            Check(comparison.Files.Any(f => f.ItemType == "X") && comparison.Files.Any(f => f.ItemType == "S"), "Links retain explicit type rather than being mislabeled files");
            Check(client.GetChangesetComparisonAsync(temporary, 2, 2, token).GetAwaiter().GetResult().Files.Count == 0, "Equal valid snapshots produce empty comparison");
            Check(client.GetChangesetComparisonAsync(temporary, 0, 1, token).GetAwaiter().GetResult().Files.Count == 1, "Initial empty changeset is accepted");
            Check(client.GetChangesetComparisonAsync(temporary, 2, 1, token).GetAwaiter().GetResult().Files.Single().Status == "D", "Reverse comparison preserves endpoint direction");
            Reject<PlasticCommandException>(() => client.GetChangesetComparisonAsync(temporary, 99, 99, token).GetAwaiter().GetResult(), "Missing equal endpoint is not short-circuited");
            Reject<ArgumentException>(() => client.GetChangesetComparisonAsync(temporary, -1, 2, token).GetAwaiter().GetResult(), "Negative endpoint rejected");
            Reject<InvalidDataException>(() => client.GetChangesetComparisonAsync(temporary, 1, 7, token).GetAwaiter().GetResult(), "Malformed complete output rejected");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Reject<OperationCanceledException>(() => client.GetChangesetComparisonAsync(temporary, 1, 2, cancellation.Token).GetAwaiter().GetResult(), "Pre-cancelled comparison rejected");
            }
            using (var cancellation = new CancellationTokenSource(150))
                Reject<OperationCanceledException>(() => client.GetChangesetComparisonAsync(temporary, 1, 6, cancellation.Token).GetAwaiter().GetResult(), "Running cm comparison cancels");

            var diff = client.GetRevisionDiffAsync(temporary, "/old.txt", "/new.txt", 1, 2, token).GetAwaiter().GetResult();
            Check(diff.HasChanges && diff.DiffText.Contains("--- /old.txt (cs:1)") && diff.DiffText.Contains("+++ /new.txt (cs:2)"), "Cross-path comparison labels both historical endpoints");
            Check(diff.DiffText.Contains("-before") && diff.DiffText.Contains("+after"), "Cross-path comparison reads both bytes");
            Reject<ArgumentException>(() => client.GetRevisionDiffAsync(temporary, "/new.txt", "/new.txt", 1, 2, token).GetAwaiter().GetResult(), "Missing source is not invented as empty content");
            Reject<ArgumentException>(() => client.GetRevisionDiffAsync(temporary, "/old.txt", "/old.txt", 1, 2, token).GetAwaiter().GetResult(), "Missing destination is not invented as empty content");
            Reject<ArgumentException>(() => client.GetRevisionDiffAsync(temporary, "/old.txt", "/new.txt@foreign", 1, 2, token).GetAwaiter().GetResult(), "Second path cannot inject repository");
            config.DiffToolPath = config.CmPath; config.DiffToolArguments = "--viewer {base} {local}";
            var viewed = client.OpenRevisionDiffToolAsync(temporary, "/old.txt", "/new.txt", 1, 2, token).GetAwaiter().GetResult();
            Check(viewed.Succeeded && viewed.Output.Contains("verified"), "Configured viewer gets moved file content from correct endpoints");
            config.DiffToolPath = "";
            Check(client.OpenRevisionDiffToolAsync(temporary, "/old.txt", "/new.txt", 1, 2, token).GetAwaiter().GetResult().Succeeded, "Native viewer receives pinned cross-path revision specs");

            foreach (string invalid in new[] { "Warning: truncated", "Q|/a|F||", "A|/a|unknown||", "A|/a|F", "M|/a|F||/b", "C|/a|F|/b|/a",
                "A|/a|b|F||", "A|/a\nmore|F||", "A|/../a|F||", "A|/.plastic/x|F||", "A|/a#cs:3|F||", "A|\"\"/a\"|F||",
                "A|/a@foreign|F||", "A|relative|F||", "A|/a//b|F||", "A|/a?|F||", "A|/a\t|F||" })
                Reject<InvalidDataException>(() => PlasticClient.ParseChangesetComparisonFiles(invalid), "Malformed or unsafe row rejected: " + invalid);
            Reject<InvalidDataException>(() => PlasticClient.ParseChangesetComparisonFiles("M|/old|F|/old|/new\nM|/other|F|/other|/new\nC|/new|F||"), "Ambiguous rename source is rejected");
            Check(PlasticClient.ParseChangesetComparisonFiles("A|\"/space & 中文.txt\"|F|\"\"|\"\"\r\n").Single().Path == "/space & 中文.txt", "Quoted native format supported");
            Check(PlasticClient.ParseChangesetComparisonFiles("C|/|D||").Single().Path == "/", "Root directory change remains a structural row");
            Reject<InvalidDataException>(() => PlasticClient.ParseChangesetComparisonFiles("C|/|F||"), "Repository root is never a file endpoint");
            var replacement = PlasticClient.ParseChangesetComparisonFiles("M|/old|D|/old|/new\nD|/old/child|F||\nA|/new/child|F||");
            Check(replacement.Single(f => f.Status == "A").OldPath == "", "Replaced descendant is not falsely paired with deleted identity");
            Reject<ArgumentException>(() => client.GetRevisionDiffAsync(temporary, "/old.txt", "/foreign.txt", 1, 2, token).GetAwaiter().GetResult(), "Historical Xlink target from another repository is rejected");
            Reject<InvalidOperationException>(() => client.GetChangesetComparisonAsync(temporary, 1, 8, token).GetAwaiter().GetResult(), "Repository selector race rejects result");
            Check(!Directory.GetFiles(temporary).Any(), "Read-only comparison never writes workspace content");
            Console.WriteLine("PASS: " + assertions + " changeset comparison assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "diff" && args[1].StartsWith("cs:"))
        {
            if (!args[1].EndsWith("@" + Repository) || !args[2].EndsWith("@" + Repository) || !args.Contains("--repositorypaths") ||
                !args.Contains("--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}")) return 20;
            if (args[1].StartsWith("cs:99@")) return 8;
            if (args[2].StartsWith("cs:7@")) { Console.WriteLine("Warning: partial output"); return 0; }
            if (args[2].StartsWith("cs:6@")) { Thread.Sleep(10000); return 0; }
            if (args[2].StartsWith("cs:8@")) File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".plastic", "plastic.selector"), "repository \"other@server:8087\"");
            if (args[1] == args[2]) return 0;
            if (args[1].StartsWith("cs:0@")) { Console.WriteLine("A|/initial|F||"); return 0; }
            if (args[1].StartsWith("cs:2@")) { Console.WriteLine("D|/added 中文.txt|F||"); return 0; }
            Console.WriteLine("A|\"/added 中文.txt\"|F|\"\"|\"\"\nD|/deleted.txt|F||\nM|/old.txt|F|/old.txt|/new.txt\nC|/new.txt|F||\nM|/old dir|D|/old dir|/new dir\nC|/new dir/child.txt|F||\nA|/link|S||\nA|/mount|X||"); return 0;
        }
        if (args[0] == "ls")
        {
            bool before = args.Contains("--tree=cs:1@" + Repository), after = args.Contains("--tree=cs:2@" + Repository);
            if (!before && !after) return 20;
            bool exists = before ? args[1] == "/old.txt" : args[1] == "/new.txt" || args[1] == "/foreign.txt";
            Console.WriteLine(new XElement("LsResults", new XElement("LsItems", exists ? new XElement("LsItem", new XElement("CurrentPath", args[1]),
                new XElement("Name", Path.GetFileName(args[1])), new XElement("Type", "file"), new XElement("Repository", args[1] == "/foreign.txt" ? "rep:other@server:8087" : "rep:" + Repository), new XElement("SymlinkTarget", "")) : null))); return 0;
        }
        if (args[0] == "cat")
        {
            bool before = args[1] == "serverpath:/old.txt#cs:1@" + Repository;
            if (!before && args[1] != "serverpath:/new.txt#cs:2@" + Repository) return 20;
            File.WriteAllText(args.Single(a => a.StartsWith("--file=")).Substring(7), before ? "before\n" : "after\n"); return 0;
        }
        if (args[0] == "--viewer")
        {
            if (File.ReadAllText(args[1]) != "before\n" || File.ReadAllText(args[2]) != "after\n") return 20;
            if ((File.GetAttributes(args[1]) & FileAttributes.ReadOnly) == 0 || (File.GetAttributes(args[2]) & FileAttributes.ReadOnly) == 0) return 20;
            Console.WriteLine("verified"); return 0;
        }
        if (args[0] == "diff") return args[1] == "serverpath:/old.txt#cs:1@" + Repository && args[2] == "serverpath:/new.txt#cs:2@" + Repository ? 0 : 20;
        return 20;
    }

    private static void Check(bool condition, string description) { assertions++; if (!condition) throw new Exception(description); }
    private static void Reject<T>(Action action, string description) where T : Exception
    { bool rejected = false; try { action(); } catch (T) { rejected = true; } Check(rejected, description); }
}
