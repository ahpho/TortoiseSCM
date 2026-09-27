// GPL-2.0-or-later. Branch queries and guarded workspace switching.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class BranchTests
{
    private const string Repository = "test@server:8087";
    private static int assertions;
    private static string root;
    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-branch-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "branches\nguid\nStandard\n");
            Selector("smartbranch \"/main\"");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(8) });
            var token = CancellationToken.None;
            var branches = client.GetBranchesAsync(root, token).GetAwaiter().GetResult();
            Check(branches.Count == 3 && branches.Single(b => b.IsCurrent).Name == "/main", "Listing identifies branch without localized type text");
            Check(branches.Single(b => b.Name == "/main/topic").HeadChangeset == 17, "Branch heads come from one native branch listing");
            Check(branches.Single(b => b.Name == "/main/topic").Parent == "/main" && branches[0].Repository == Repository, "Branch ancestry and repository retained");
            Check(client.ResolveBranchHeadAsync(root, "/main/topic", token).GetAwaiter().GetResult() == 17, "Resolve pins head");
            Check(client.ResolveBranchHeadAsync(root, "/main/Unicode 中文 'quoted'", token).GetAwaiter().GetResult() == 2, "Quoted names remain literal and are never a find expression");
            Reject<ArgumentException>(() => client.ResolveBranchHeadAsync(root, "/missing", token).GetAwaiter().GetResult(), "Missing branch rejected");
            Selector("changeset \"2\"");
            Check(!client.GetBranchesAsync(root, token).GetAwaiter().GetResult().Any(b => b.IsCurrent), "Changeset selector is not marked as current branch");
            Selector("label \"/main\"");
            Check(!client.GetBranchesAsync(root, token).GetAwaiter().GetResult().Any(b => b.IsCurrent), "Same-named label is not a branch");
            Selector("smartbranch \"/main\"");
            SetMode("partial");
            Selector("br \"/main\"\n co \"/main\"");
            Check(client.GetBranchesAsync(root, token).GetAwaiter().GetResult().Single(b => b.IsCurrent).Name == "/main", "Partial br/co selector identifies current branch");
            Reject<ArgumentException>(() => client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult(), "Partial switch rejected");
            SetMode("");
            Selector("smartbranch \"/main\"");
            foreach (string branch in new[] { "main", "br:/main", "/main@other", "/main#cs:2", "/main\n/topic", "/main//x", "/main/../x", "/main/", "/main\\x", "/main\"x" })
                Reject<ArgumentException>(() => client.SwitchBranchAsync(root, branch, token).GetAwaiter().GetResult(), "Unsafe spec rejected: " + branch);
            string sub = Path.Combine(root, "sub"); Directory.CreateDirectory(sub);
            Reject<ArgumentException>(() => client.SwitchBranchAsync(sub, "/main/topic", token).GetAwaiter().GetResult(), "Subdirectory switch rejected");
            Directory.CreateDirectory(Path.Combine(sub, ".plastic"));
            File.WriteAllText(Path.Combine(sub, ".plastic", "plastic.workspace"), "nested\nguid\nStandard\n");
            Reject<ArgumentException>(() => client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult(), "Nested workspace blocks root mutation");
            File.Delete(Path.Combine(sub, ".plastic", "plastic.workspace")); Directory.Delete(Path.Combine(sub, ".plastic"));
            Directory.Delete(sub);
            foreach (string mode in new[] { "CH", "CO", "AD", "DE", "PR", "IG", "late-pending" })
            {
                SetMode(mode);
                Reject<ArgumentException>(() => client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult(), "Pending/ignored or late changes block switch: " + mode);
            }
            SetMode("");
            var mergeGateMethod = typeof(PlasticClient).GetMethod("OpenMergeGate", BindingFlags.Instance | BindingFlags.NonPublic);
            using ((IDisposable)mergeGateMethod.Invoke(client, new object[] { root }))
                Reject<InvalidOperationException>(() => client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult(), "Concurrent merge gate blocks switch");
            var structureGateMethod = typeof(PlasticClient).GetMethod("StructureGate", BindingFlags.Static | BindingFlags.NonPublic);
            using ((IDisposable)structureGateMethod.Invoke(null, new object[] { root }))
                Reject<InvalidOperationException>(() => client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult(), "Concurrent structure gate blocks switch");
            string merge = Path.Combine(root, ".plastic", "plastic.mergeprogress"); File.WriteAllText(merge, "pending");
            Reject<ArgumentException>(() => client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult(), "Native merge blocks switch"); File.Delete(merge);
            Selector("smartbranch \"/main\"\n path \"/sub\"\n smartbranch \"/main/topic\"");
            Reject<ArgumentException>(() => client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult(), "Multi-mapping selector rejected");
            Selector("smartbranch \"/main\"");
            SetMode("race");
            Reject<InvalidOperationException>(() => client.GetBranchesAsync(root, token).GetAwaiter().GetResult(), "Selector race rejects list");
            Selector("smartbranch \"/main\""); SetMode("race");
            Reject<InvalidOperationException>(() => client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult(), "Selector race blocks mutation");
            Selector("smartbranch \"/main\""); SetMode("foreign");
            Reject<InvalidDataException>(() => client.GetBranchesAsync(root, token).GetAwaiter().GetResult(), "Status repository mismatch fails closed");
            SetMode("");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Reject<OperationCanceledException>(() => client.SwitchBranchAsync(root, "/main/topic", cancelled.Token).GetAwaiter().GetResult(), "Pre-cancelled switch never executes");
            }
            Check(!File.Exists(Path.Combine(root, ".plastic", "switched")), "Every rejection preserved workspace without a native switch");
            SetMode("no-selector-update");
            var unverified = client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult();
            Check(!unverified.Succeeded && unverified.Error.Contains("verification failed"), "Native zero exit without selector update is not success");
            SetMode("");
            Check(client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult().Succeeded, "Clean root switch succeeds");
            Check(File.ReadAllText(Path.Combine(root, ".plastic", "switched")) == "br:/main/topic@" + Repository, "Switch pins repository and never requests force or auto-shelve");
            SetMode("failure");
            var failed = client.SwitchBranchAsync(root, "/main/topic", token).GetAwaiter().GetResult();
            Check(!failed.Succeeded && failed.Error.Contains("Refresh workspace status"), "Native failure retains error and advises refresh");
            string xml = BranchXml();
            foreach (string bad in new[] { "<other/>", xml.Replace("<CHANGESET>17</CHANGESET>", "<CHANGESET>-1</CHANGESET>"), xml.Replace("server:8087", "foreign"),
                xml.Replace("/main/topic", "/main@foreign"), xml.Replace("/main/topic", "/main"), xml.Replace("</PLASTICQUERY>", "<WARNING>truncated</WARNING></PLASTICQUERY>") })
                Reject<InvalidDataException>(() => PlasticClient.ParseBranches(bad, Repository), "Malformed branch response rejected");
            Check(PlasticClient.ParseBranches("<PLASTICQUERY/>", Repository).Count == 0, "Empty branch result supported");
            Console.WriteLine("PASS: " + assertions + " branch assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Selector(string rule)
    { File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  " + rule + "\n"); }
    private static void SetMode(string mode)
    {
        File.WriteAllText(Path.Combine(root, ".plastic", "mode"), mode);
        string count = Path.Combine(root, ".plastic", "status-count"); if (File.Exists(count)) File.Delete(count);
    }
    private static string BranchXml()
    {
        return new XElement("PLASTICQUERY", new[] { "/main", "/main/topic", "/main/Unicode 中文 'quoted'" }.Select(name => new XElement("BRANCH",
            new XElement("NAME", name), new XElement("PARENT", name == "/main" ? "" : "/main"), new XElement("OWNER", "tester"),
            new XElement("DATE", "2026-09-27"), new XElement("COMMENT", "Unicode 中文"), new XElement("REPNAME", "test"),
            new XElement("REPSERVER", "server:8087"), new XElement("CHANGESET", name == "/main/topic" ? 17 : 2)))).ToString();
    }
    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        string modePath = Path.Combine(metadata, "mode"), mode = File.Exists(modePath) ? File.ReadAllText(modePath) : "";
        if (args[0] == "status")
        {
            string code = "";
            if (args.Contains("--all"))
            {
                if (!args.Contains("--ignored") || !args.Contains("--cutignored")) return 31;
                code = new[] { "CH", "CO", "AD", "DE", "PR", "IG" }.Contains(mode) ? mode : "";
                string countPath = Path.Combine(metadata, "status-count");
                int count = File.Exists(countPath) ? Int32.Parse(File.ReadAllText(countPath)) : 0;
                File.WriteAllText(countPath, (count + 1).ToString());
                if (mode == "late-pending" && count > 0) code = "CH";
            }
            var status = new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status",
                new XElement("Changeset", mode == "partial" ? -1 : 2), new XElement("RepSpec", new XElement("Name", mode == "foreign" ? "other" : "test"), new XElement("Server", "server:8087")))),
                new XElement("WkConfigType", "localized irrelevant"), new XElement("WkConfigName", (File.Exists(Path.Combine(metadata, "current-branch")) ? File.ReadAllText(Path.Combine(metadata, "current-branch")) : "/main") + "@" + Repository));
            if (code != "") status.Add(new XElement("Changes", new XElement("Change", new XElement("Path", "file.txt"), new XElement("Type", code))));
            Console.WriteLine(status); return 0;
        }
        if (args[0] == "find" && args[1] == "branch")
        {
            if (args.Length != 5 || args.Any(a => a.StartsWith("where"))) return 32;
            if (mode == "race") File.AppendAllText(Path.Combine(metadata, "plastic.selector"), "\n# altered\n");
            Console.WriteLine(BranchXml()); return 0;
        }
        if (args[0] == "switch")
        {
            if (args.Length != 3 || args[2] != "--workspace=" + Environment.CurrentDirectory) return 33;
            File.WriteAllText(Path.Combine(metadata, "switched"), args[1]);
            if (mode == "failure") { Console.Error.WriteLine("native failure"); return 2; }
            if (mode != "no-selector-update")
            {
                string branch = args[1].Substring(3, args[1].Length - 3 - Repository.Length - 1);
                File.WriteAllText(Path.Combine(metadata, "current-branch"), branch);
                File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n smartbranch \"" + branch + "\"\n");
            }
            Console.WriteLine("Switched"); return 0;
        }
        Console.Error.WriteLine("Unexpected fake command: " + String.Join(" ", args)); return 99;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
}
