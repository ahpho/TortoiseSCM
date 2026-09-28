// GPL-2.0-or-later. Identity-checked metadata rename, with an isolated fake cm process.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class BranchRenameTests
{
    private const string Repository = "test@server:8087";
    private const string Identity = "44ca6baf-aa3c-4454-b645-ac7e7de207bd";
    private static string root;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length != 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-branch-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(10) });
            Reset("");
            var parsed = PlasticClient.ParseBranches(Branches("", false).ToString(), Repository).Single(b => b.Name == "/main/topic");
            Check(parsed.BranchId == 2 && parsed.Guid == Identity, "Native ID/GUID retained");
            var old = Branches("", false); foreach (var item in old.Elements()) { item.Element("ID").Remove(); item.Element("GUID").Remove(); }
            Check(PlasticClient.ParseBranches(old.ToString(), Repository).All(b => b.BranchId == 0 && b.Guid == ""), "Legacy identity-less branch browsing retained");
            foreach (string bad in new[] { "0", "-1", "x", "9223372036854775808", "1" })
            {
                var xml = Branches("", false); xml.Elements().Last().Element("ID").Value = bad;
                Reject<InvalidDataException>(() => PlasticClient.ParseBranches(xml.ToString(), Repository), "Malformed/duplicate native ID rejected");
            }
            foreach (string bad in new[] { "", "x", Guid.Empty.ToString(), "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa" })
            {
                var xml = Branches("", false); xml.Elements().Last().Element("GUID").Value = bad;
                Reject<InvalidDataException>(() => PlasticClient.ParseBranches(xml.ToString(), Repository), "Malformed/duplicate native GUID rejected");
            }
            foreach (Action<PlasticBranch> change in new Action<PlasticBranch>[] {
                b => b.BranchId = 0, b => b.Guid = "", b => b.Guid = "bad", b => b.Guid = Guid.Empty.ToString(),
                b => b.Name = "/main", b => b.Parent = "", b => b.Parent = "/other", b => b.IsCurrent = true, b => b.HeadChangeset = -1 })
            {
                Reset(""); var branch = Expected(); change(branch);
                Reject<ArgumentException>(() => Rename(client, branch, "renamed"), "Invalid expected snapshot rejected"); NoMutation();
            }
            foreach (string name in new[] { "", " ", " x", "x ", ".", "..", "-option", "/main/new", "a/b", "a\\b", "a@b", "a#b", "a:b", "a?b", "a'b", "a\"b", "a\nb", "a\0b", "TOPIC" })
            {
                Reset(""); Reject<ArgumentException>(() => Rename(client, Expected(), name), "Invalid/colliding leaf rejected: " + name); NoMutation();
            }
            foreach (string mode in new[] { "id", "guid", "head", "parent", "missing", "late-id", "late-head" })
            {
                Reset(mode); Reject<InvalidOperationException>(() => Rename(client, Expected(), "renamed"), "Stale branch rejected: " + mode); NoMutation();
            }
            foreach (string mode in new[] { "child", "collision", "current", "checkout-current", "mapped-current" })
            {
                Reset(mode); Reject<ArgumentException>(() => Rename(client, Expected(), "renamed"), "Protected branch rejected: " + mode); NoMutation();
            }
            foreach (string mode in new[] { "selector-race", "workspace-race", "mode-race", "late-selector-race" })
            {
                Reset(mode); Reject<InvalidOperationException>(() => Rename(client, Expected(), "renamed"), "Changed workspace rejected: " + mode); NoMutation();
            }
            Reset("");
            Reject<InvalidOperationException>(() => client.RenameBranchAsync(root, Expected(), "renamed", "stale", CancellationToken.None).GetAwaiter().GetResult(), "Dialog selector mismatch rejected"); NoMutation();
            foreach (string gate in new[] { "StructureGate", "OpenMergeGate" })
            {
                Reset(""); var method = typeof(PlasticClient).GetMethod(gate, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
                using ((IDisposable)method.Invoke(method.IsStatic ? null : client, new object[] { root }))
                    Reject<InvalidOperationException>(() => Rename(client, Expected(), "renamed"), "Concurrent local gate rejected");
                NoMutation();
            }
            Reset("");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); Reject<OperationCanceledException>(() => client.RenameBranchAsync(root, Expected(), "renamed", Selector(), cancelled.Token), "Precancelled rename rejected"); NoMutation();
            }
            foreach (string mode in new[] { "", "partial", "pending" })
            {
                Reset(mode); string before = Selector(), metadata = File.ReadAllText(Meta("plastic.workspace"));
                Check(Rename(client, Expected(), "renamed 中文 & space").Succeeded, "Rename supports Standard/Partial/pending: " + mode);
                Check(File.ReadAllText(Meta("rename-args")) == "br:/main/topic@" + Repository + "\nrenamed 中文 & space", "Native command receives literal qualified old spec and leaf only");
                Check(Selector() == before && File.ReadAllText(Meta("plastic.workspace")) == metadata, "Metadata-only rename preserves local workspace");
            }
            Reset("");
            var mutable = Expected(); var pending = client.RenameBranchAsync(root, mutable, "renamed", Selector(), CancellationToken.None);
            mutable.BranchId = 999; mutable.Guid = "invalid"; mutable.Name = "/other"; mutable.Parent = "/different"; mutable.HeadChangeset = 100;
            Check(pending.GetAwaiter().GetResult().Succeeded, "Caller mutation after API return does not change captured snapshot");
            foreach (string mode in new[] { "post-id", "post-guid", "post-head", "post-parent", "post-old", "post-none", "post-selector", "post-workspace", "post-mode", "post-read-fail", "native-fail" })
            {
                Reset(mode); var result = Rename(client, Expected(), "renamed");
                Check(!result.Succeeded && result.Error.Contains("may already have been renamed") && result.Error.Contains("no automatic reverse rename"), "Uncertain server outcome needs refresh: " + mode);
                Check(File.ReadAllText(Meta("rename-count")) == "1", "Never automatically reverses uncertain rename");
            }
            Reset("slow");
            using (var cancelled = new CancellationTokenSource())
            {
                var task = client.RenameBranchAsync(root, Expected(), "renamed", Selector(), cancelled.Token);
                for (int wait = 0; wait < 150 && !File.Exists(Meta("rename-count")); wait++) Thread.Sleep(20);
                Check(File.Exists(Meta("rename-count")), "Cancellation fixture reaches native mutation"); cancelled.Cancel();
                try { task.GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException error) { Check(error.Message.Contains("may already have been renamed"), "Post-mutation cancellation reports uncertainty"); }
            }
            Console.WriteLine("PASS: " + assertions + " branch rename assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static PlasticCommandResult Rename(PlasticClient client, PlasticBranch branch, string leaf)
    { return client.RenameBranchAsync(root, branch, leaf, Selector(), CancellationToken.None).GetAwaiter().GetResult(); }
    private static PlasticBranch Expected()
    { return new PlasticBranch { BranchId = 2, Guid = Identity, Name = "/main/topic", Parent = "/main", Repository = Repository, HeadChangeset = 17 }; }
    private static string Meta(string leaf) { return Path.Combine(root, ".plastic", leaf); }
    private static string Selector() { return File.ReadAllText(Meta("plastic.selector")); }
    private static void Reset(string mode)
    {
        foreach (string file in Directory.GetFiles(Meta(""))) File.Delete(file);
        File.WriteAllText(Meta("plastic.workspace"), "rename-test\nguid\n" + (mode == "partial" ? "Partial" : "Standard") + "\n");
        File.WriteAllText(Meta("plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"" + (mode == "current" ? "/main/topic" : "/main") + "\"\n");
        if (mode == "checkout-current") File.AppendAllText(Meta("plastic.selector"), "  co \"/main/topic\"\n");
        if (mode == "mapped-current") File.AppendAllText(Meta("plastic.selector"), " path \"/sub\"\n  br \"/main/topic\"\n");
        File.WriteAllText(Meta("mode"), mode);
    }
    private static void NoMutation() { Check(!File.Exists(Meta("rename-count")), "Preflight prevents native rename"); }
    private static XElement Branch(string name, string parent, long id, string guid, long head)
    { return new XElement("BRANCH", new XElement("ID", id), new XElement("GUID", guid), new XElement("NAME", name), new XElement("PARENT", parent), new XElement("CHANGESET", head), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087")); }
    private static XElement Branches(string mode, bool renamed)
    {
        var tree = new XElement("PLASTICQUERY", Branch("/main", "", 1, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 2));
        if (mode == "missing" || (renamed && mode == "post-none")) return tree;
        string name = renamed ? "/main/" + File.ReadAllText(Meta("new-name")) : "/main/topic";
        var topic = Branch(name, mode == "parent" || (renamed && mode == "post-parent") ? "/other" : "/main",
            mode == "id" || (renamed && mode == "post-id") ? 99 : 2,
            mode == "guid" || (renamed && mode == "post-guid") ? "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" : Identity,
            mode == "head" || (renamed && mode == "post-head") ? 18 : 17);
        tree.Add(topic);
        if (mode == "child") tree.Add(Branch("/unrelated/path", "/main/topic", 3, "cccccccc-cccc-cccc-cccc-cccccccccccc", 17));
        if (mode == "collision") tree.Add(Branch("/main/RENAMED", "/main", 3, "cccccccc-cccc-cccc-cccc-cccccccccccc", 17));
        if (renamed && mode == "post-old") tree.Add(Branch("/main/topic", "/main", 3, "cccccccc-cccc-cccc-cccc-cccccccccccc", 17));
        return tree;
    }
    private static int FakeCm(string[] args)
    {
        root = Environment.CurrentDirectory; Console.OutputEncoding = new UTF8Encoding(false);
        string mode = File.ReadAllText(Meta("mode")); bool renamed = File.Exists(Meta("rename-count"));
        if (args[0] == "status")
        {
            if (args.Contains("--all")) { Console.Error.WriteLine("Rename must not require a clean workspace"); return 90; }
            Console.WriteLine(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", 2),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))),
                new XElement("WkConfigName", (mode == "current" ? "/main/topic" : "/main") + "@" + Repository),
                mode == "pending" ? new XElement("Changes", new XElement("Change", new XElement("Path", "local.txt"), new XElement("Type", "CH"))) : null)); return 0;
        }
        if (args[0] == "find" && args[1] == "branch")
        {
            int count = File.Exists(Meta("read-count")) ? Int32.Parse(File.ReadAllText(Meta("read-count"))) : 0;
            File.WriteAllText(Meta("read-count"), (count + 1).ToString());
            if (mode == "selector-race" || (mode == "late-selector-race" && count > 0)) File.AppendAllText(Meta("plastic.selector"), "\n# changed\n");
            if (mode == "workspace-race") File.WriteAllText(Meta("plastic.workspace"), "different\nguid\nStandard\n");
            if (mode == "mode-race") File.WriteAllText(Meta("plastic.workspace"), "rename-test\nguid\nPartial\n");
            if (renamed && mode == "post-read-fail") return 7;
            if (count > 0 && mode.StartsWith("late-")) mode = mode.Substring(5);
            Console.WriteLine(Branches(mode, renamed)); return 0;
        }
        if (args[0] == "branch" && args[1] == "rename")
        {
            if (args.Length != 4 || args[2] != "br:/main/topic@" + Repository) return 91;
            File.WriteAllText(Meta("rename-args"), args[2] + "\n" + args[3]); File.WriteAllText(Meta("new-name"), args[3]);
            int count = File.Exists(Meta("rename-count")) ? Int32.Parse(File.ReadAllText(Meta("rename-count"))) : 0;
            File.WriteAllText(Meta("rename-count"), (count + 1).ToString());
            if (mode == "slow") Thread.Sleep(10000);
            if (mode == "post-selector") File.AppendAllText(Meta("plastic.selector"), "\n# changed\n");
            if (mode == "post-workspace") File.WriteAllText(Meta("plastic.workspace"), "different\nguid\nStandard\n");
            if (mode == "post-mode") File.WriteAllText(Meta("plastic.workspace"), "rename-test\nguid\nPartial\n");
            if (mode == "native-fail") { Console.Error.WriteLine("Server failure after rename"); return 7; }
            Console.WriteLine("renamed"); return 0;
        }
        return 99;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
}
