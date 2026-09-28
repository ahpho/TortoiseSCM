// GPL-2.0-or-later. Empty leaf branch deletion with an isolated fake cm process.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class BranchDeleteTests
{
    private const string Repository = "test@server:8087";
    private const string Identity = "44ca6baf-aa3c-4454-b645-ac7e7de207bd";
    private static string root;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length != 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-branch-delete-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(10) });
            foreach (Action<PlasticBranch> change in new Action<PlasticBranch>[] {
                b => b.BranchId = 0, b => b.Guid = "", b => b.Guid = "bad", b => b.Guid = Guid.Empty.ToString(),
                b => b.Name = "/main", b => b.Parent = "", b => b.Parent = "/other", b => b.IsCurrent = true,
                b => b.HeadChangeset = -1, b => b.Name = "/main/a'b", b => b.Repository = "a'b@server", b => b.Repository = "a\\b@server" })
            {
                Reset(""); var branch = Expected(); change(branch);
                Reject<ArgumentException>(() => Delete(client, branch), "Invalid expected snapshot rejected"); NoMutation();
            }
            foreach (string mode in new[] { "id", "guid", "head", "parent", "missing", "no-parent", "late-id", "late-guid", "late-head", "late-parent", "late-missing" })
            {
                Reset(mode); Reject<InvalidOperationException>(() => Delete(client, Expected()), "Stale branch rejected: " + mode); NoMutation();
            }
            foreach (string mode in new[] { "child", "late-child", "current", "checkout-current", "mapped-current", "branch-current" })
            {
                Reset(mode); Reject<ArgumentException>(() => Delete(client, Expected()), "Protected branch rejected: " + mode); NoMutation();
            }
            foreach (string mode in new[] { "selector-race", "workspace-race", "mode-race", "late-selector-race", "reference-selector-race" })
            {
                Reset(mode); Reject<InvalidOperationException>(() => Delete(client, Expected()), "Changed workspace rejected: " + mode); NoMutation();
            }
            foreach (string type in new[] { "changeset", "attribute", "shelve" })
            {
                foreach (string prefix in new[] { "", "late-" })
                {
                    Reset(prefix + type); Reject<ArgumentException>(() => Delete(client, Expected()), "Reference prevents deletion: " + prefix + type); NoMutation();
                }
                Reset(type + "-fail"); Reject<PlasticCommandException>(() => Delete(client, Expected()), "Failed reference query blocks mutation"); NoMutation();
                foreach (string kind in new[] { "root", "namespace", "text", "xml" })
                {
                    Reset(type + "-" + kind); Reject<Exception>(() => Delete(client, Expected()), "Malformed reference query blocks mutation: " + kind); NoMutation();
                }
            }
            Reset("");
            Reject<InvalidOperationException>(() => client.DeleteBranchAsync(root, Expected(), "stale", CancellationToken.None).GetAwaiter().GetResult(), "Dialog selector mismatch rejected"); NoMutation();
            foreach (string gate in new[] { "StructureGate", "OpenMergeGate" })
            {
                Reset(""); var method = typeof(PlasticClient).GetMethod(gate, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
                using ((IDisposable)method.Invoke(method.IsStatic ? null : client, new object[] { root }))
                    Reject<InvalidOperationException>(() => Delete(client, Expected()), "Concurrent local gate rejected");
                NoMutation();
            }
            Reset("");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); Reject<OperationCanceledException>(() => client.DeleteBranchAsync(root, Expected(), Selector(), cancelled.Token), "Precancelled deletion rejected"); NoMutation();
            }
            foreach (string mode in new[] { "", "partial", "pending", "zero-head" })
            {
                Reset(mode); string before = Selector(), metadata = File.ReadAllText(Meta("plastic.workspace"));
                var expected = Expected(); if (mode == "zero-head") expected.HeadChangeset = 0;
                Check(Delete(client, expected).Succeeded, "Delete supports Standard/Partial/pending/inherited zero head: " + mode);
                Check(File.ReadAllText(Meta("delete-args")) == "br:/main/topic@" + Repository, "Native command receives exact qualified branch spec");
                Check(Selector() == before && File.ReadAllText(Meta("plastic.workspace")) == metadata, "Deletion preserves local workspace");
                Check(new[] { "changeset", "attribute", "shelve" }.All(type => File.ReadAllText(Meta(type + "-count")) == "2"), "Every reference queried twice before deletion");
            }
            Reset("");
            var mutable = Expected(); var pending = client.DeleteBranchAsync(root, mutable, Selector(), CancellationToken.None);
            mutable.BranchId = 999; mutable.Guid = "invalid"; mutable.Name = "/other"; mutable.Parent = "/different"; mutable.HeadChangeset = 100;
            Check(pending.GetAwaiter().GetResult().Succeeded, "Caller mutation does not change captured snapshot");
            foreach (string mode in new[] { "post-name", "post-id", "post-guid", "post-original", "post-identity-missing", "post-selector", "post-workspace", "post-mode", "post-read-fail", "native-fail" })
            {
                Reset(mode); var result = Delete(client, Expected());
                Check(!result.Succeeded && result.Error.Contains("may already have been deleted") && result.Error.Contains("no automatic retry"), "Uncertain server outcome needs refresh: " + mode);
                Check(File.ReadAllText(Meta("delete-count")) == "1", "Never automatically retries uncertain deletion");
            }
            Reset("slow");
            using (var cancelled = new CancellationTokenSource())
            {
                var task = client.DeleteBranchAsync(root, Expected(), Selector(), cancelled.Token);
                for (int wait = 0; wait < 250 && !File.Exists(Meta("delete-count")); wait++) Thread.Sleep(20);
                Check(File.Exists(Meta("delete-count")), "Cancellation fixture reaches native mutation"); cancelled.Cancel();
                try { task.GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); }
                catch (OperationCanceledException error) { Check(error.Message.Contains("may already have been deleted"), "Post-mutation cancellation reports uncertainty"); }
            }
            Console.WriteLine("PASS: " + assertions + " branch delete assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static PlasticCommandResult Delete(PlasticClient client, PlasticBranch branch)
    { return client.DeleteBranchAsync(root, branch, Selector(), CancellationToken.None).GetAwaiter().GetResult(); }
    private static PlasticBranch Expected()
    { return new PlasticBranch { BranchId = 2, Guid = Identity, Name = "/main/topic", Parent = "/main", Repository = Repository, HeadChangeset = 17 }; }
    private static string Meta(string leaf) { return Path.Combine(root, ".plastic", leaf); }
    private static string Selector() { return File.ReadAllText(Meta("plastic.selector")); }
    private static void Reset(string mode)
    {
        foreach (string file in Directory.GetFiles(Meta(""))) File.Delete(file);
        File.WriteAllText(Meta("plastic.workspace"), "delete-test\nguid\n" + (mode == "partial" ? "Partial" : "Standard") + "\n");
        File.WriteAllText(Meta("plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"" + (mode == "current" ? "/main/topic" : "/main") + "\"\n");
        if (mode == "checkout-current") File.AppendAllText(Meta("plastic.selector"), "  co \"/main/topic\"\n");
        if (mode == "mapped-current") File.AppendAllText(Meta("plastic.selector"), " path \"/sub\"\n  br \"/main/topic\"\n");
        if (mode == "branch-current") File.AppendAllText(Meta("plastic.selector"), "  branch \"/main/topic\"\n");
        File.WriteAllText(Meta("mode"), mode);
    }
    private static void NoMutation() { Check(!File.Exists(Meta("delete-count")), "Preflight prevents native deletion"); }
    private static XElement Branch(string name, string parent, long id, string guid, long head)
    { return new XElement("BRANCH", new XElement("ID", id), new XElement("GUID", guid), new XElement("NAME", name), new XElement("PARENT", parent), new XElement("CHANGESET", head), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087")); }
    private static XElement Branches(string mode, bool deleted)
    {
        var tree = new XElement("PLASTICQUERY");
        if (mode != "no-parent") tree.Add(Branch("/main", "", 1, "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", 2));
        if (mode == "missing" || (deleted && !new[] { "post-name", "post-id", "post-guid", "post-original" }.Contains(mode))) return tree;
        tree.Add(Branch(deleted && (mode == "post-id" || mode == "post-guid") ? "/main/renamed" : "/main/topic",
            mode == "parent" ? "/other" : "/main", mode == "id" || (deleted && (mode == "post-name" || mode == "post-guid")) ? 99 : 2,
            mode == "guid" || (deleted && (mode == "post-name" || mode == "post-id")) ? "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" : Identity,
            mode == "head" ? 18 : mode == "zero-head" ? 0 : 17));
        if (mode == "child") tree.Add(Branch("/unrelated/path", "/main/topic", 3, "cccccccc-cccc-cccc-cccc-cccccccccccc", 17));
        return tree;
    }
    private static int Increment(string name)
    {
        int count = File.Exists(Meta(name)) ? Int32.Parse(File.ReadAllText(Meta(name))) : 0;
        File.WriteAllText(Meta(name), (count + 1).ToString()); return count;
    }
    private static int FakeCm(string[] args)
    {
        root = Environment.CurrentDirectory; Console.OutputEncoding = new UTF8Encoding(false);
        string mode = File.ReadAllText(Meta("mode")); bool deleted = File.Exists(Meta("delete-count"));
        if (args[0] == "status")
        {
            if (args.Contains("--all")) { Console.Error.WriteLine("Deletion must not require a clean workspace"); return 90; }
            Console.WriteLine(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", 2),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))),
                new XElement("WkConfigName", (mode == "current" ? "/main/topic" : "/main") + "@" + Repository),
                mode == "pending" ? new XElement("Changes", new XElement("Change", new XElement("Path", "local.txt"), new XElement("Type", "CH"))) : null)); return 0;
        }
        if (args[0] == "find" && args[1] == "branch")
        {
            int count = Increment("read-count");
            if (mode == "selector-race" || (mode == "late-selector-race" && count > 0)) File.AppendAllText(Meta("plastic.selector"), "\n# changed\n");
            if (mode == "workspace-race") File.WriteAllText(Meta("plastic.workspace"), "different\nguid\nStandard\n");
            if (mode == "mode-race") File.WriteAllText(Meta("plastic.workspace"), "delete-test\nguid\nPartial\n");
            if (deleted && mode == "post-read-fail") return 7;
            if (count > 0 && mode.StartsWith("late-")) mode = mode.Substring(5);
            var branches = Branches(mode, deleted);
            if (deleted && mode == "post-identity-missing") foreach (var branch in branches.Elements()) { branch.Element("ID").Remove(); branch.Element("GUID").Remove(); }
            Console.WriteLine(branches); return 0;
        }
        if (args[0] == "find" && new[] { "changeset", "attribute", "shelve" }.Contains(args[1]))
        {
            string type = args[1]; int count = Increment(type + "-count");
            string query = type == "shelve" ? "where parent = " + (mode == "zero-head" ? "0" : "17") :
                "where " + (type == "changeset" ? "branch" : "srcobj") + " = 'br:/main/topic@" + Repository + "'";
            if (args.Length != 6 || args[2] != query + " limit 1" || args[3] != "--xml" || args[4] != "--nototal" || args[5] != "--encoding=utf-8") return 92;
            if (mode == "reference-selector-race" && type == "shelve" && count > 0) File.AppendAllText(Meta("plastic.selector"), "\n# changed\n");
            if (mode == type + "-fail") { Console.Error.WriteLine("Reference query failed"); return 7; }
            if (mode == type + "-root") { Console.WriteLine("<WRONG/>"); return 0; }
            if (mode == type + "-namespace") { Console.WriteLine("<PLASTICQUERY xmlns='wrong'/>"); return 0; }
            if (mode == type + "-text") { Console.WriteLine("<PLASTICQUERY>unexpected</PLASTICQUERY>"); return 0; }
            if (mode == type + "-xml") { Console.WriteLine("<broken"); return 0; }
            Console.WriteLine(new XElement("PLASTICQUERY", mode == type || (mode == "late-" + type && count > 0) ? new XElement("ENTRY", "reference") : null)); return 0;
        }
        if (args[0] == "branch" && args[1] == "delete")
        {
            if (args.Length != 3 || args[2] != "br:/main/topic@" + Repository) return 91;
            File.WriteAllText(Meta("delete-args"), args[2]); Increment("delete-count");
            if (mode == "slow") Thread.Sleep(10000);
            if (mode == "post-selector") File.AppendAllText(Meta("plastic.selector"), "\n# changed\n");
            if (mode == "post-workspace") File.WriteAllText(Meta("plastic.workspace"), "different\nguid\nStandard\n");
            if (mode == "post-mode") File.WriteAllText(Meta("plastic.workspace"), "delete-test\nguid\nPartial\n");
            if (mode == "native-fail") { Console.Error.WriteLine("Server failure after deletion"); return 7; }
            Console.WriteLine("deleted"); return 0;
        }
        return 99;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
}
