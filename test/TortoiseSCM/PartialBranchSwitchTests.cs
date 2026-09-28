// GPL-2.0-or-later. Guarded Partial switching, using a deterministic native CLI peer.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class PartialBranchSwitchTests
{
    private const string Repository = "test@server:8087";
    private const string BranchGuid = "31e8f2fd-3ced-4829-ab09-f7733a98d4ad";
    private const string LoadRule = "7bc5ef28-aaf2-4380-98b5-5a0a891f6461:27\r\n";
    private static string root;
    private static PlasticClient client;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-partial-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(8) });
        try
        {
            foreach (string rules in new[] { LoadRule, "" })
            {
                Reset(""); Write("plastic.fullycheckeddirectories", rules);
                Check(Switch().Succeeded, "Clean Partial switch succeeds with scoped/empty rules");
                Check(Read("plastic.fullycheckeddirectories") == rules, "Exact loading bytes retained");
                Check(Read("plastic.wktree") == "new revision tree", "Post-switch tree is allowed to change");
                Check(Read("switched") == "partial|switch|br:/main/topic@" + Repository + "|--report", "Only root CWD partial switch, qualified branch and report used");
                Check(Read("plastic.workspace").Contains("Standard"), "Authoritative Partial mode overrides stale Standard hint");
            }
            Reset(""); Write("plastic.fullycheckeddirectories", ""); Write("plastic.fullupdate", "full-marker");
            Check(Switch().Succeeded && Read("plastic.fullupdate") == "full-marker", "Full-workspace loading marker retained exactly");
            foreach (string name in new[] { "plastic.fullycheckeddirectories", "plastic.wktree" })
            { Reset(""); File.Delete(Meta(name)); Block<ArgumentException>("Missing " + name); }
            Reset(""); Directory.CreateDirectory(Meta("plastic.fullupdate"));
            try { Block<ArgumentException>("Directory cannot impersonate metadata file"); } finally { Directory.Delete(Meta("plastic.fullupdate")); }
            foreach (string rules in new[] { "\n", "bad:27", BranchGuid + ":0", BranchGuid + ":-1", BranchGuid + ": 27", BranchGuid + ":27:28", LoadRule + LoadRule, Guid.Empty + ":27" })
            { Reset(""); Write("plastic.fullycheckeddirectories", rules); Block<ArgumentException>("Malformed rules rejected: " + rules); }
            Reset(""); Write("plastic.fullupdate", ""); Block<ArgumentException>("Mixed full/explicit rules rejected");
            foreach (string mode in new[] { "no-identity", "missing-target" }) { Reset(mode); Block<ArgumentException>(mode); }
            foreach (string mode in new[] { "pre-id", "pre-guid", "pre-head", "pre-parent", "pre-rules", "pre-tree", "pre-selector", "pre-name", "pre-mode", "late-tree", "late-mode", "pre-fullupdate" })
            { Reset(mode); Block<InvalidOperationException>(mode); }
            foreach (string code in new[] { "CH", "CO", "AD", "DE", "PR", "IG", "late-pending" })
            { Reset(code); Block<ArgumentException>("Dirty/private/ignored blocks switch: " + code); }
            foreach (string marker in new[] { "plastic.mergeprogress", "tortoisescm-partial.session", "tortoisescm-structure.session", "tortoisescm-partial-directory.session" })
            { Reset(""); Write(marker, "saved"); Block<ArgumentException>("Saved session blocks switch: " + marker); }
            Reset("");
            var index = typeof(PlasticClient).GetMethod("MergeIndex", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(client, new object[] { root }).ToString();
            Directory.CreateDirectory(Path.GetDirectoryName(index)); File.WriteAllText(index, "saved");
            try { Block<ArgumentException>("Saved Standard merge blocks switch"); } finally { File.Delete(index); }
            Reset(""); Directory.CreateDirectory(Path.Combine(root, "nested", ".plastic"));
            try { Block<ArgumentException>("Nested workspace blocks switch"); } finally { Directory.Delete(Path.Combine(root, "nested"), true); }
            foreach (string gate in new[] { "StructureGate", "OpenMergeGate" })
            {
                Reset(""); var method = typeof(PlasticClient).GetMethod(gate, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
                using ((IDisposable)method.Invoke(method.IsStatic ? null : client, new object[] { root })) Block<InvalidOperationException>("Gate blocks switch: " + gate);
            }
            Reset(""); Selector("br \"/main\"\n path \"/other\"\n br \"/main/topic\""); Block<ArgumentException>("Multiple mappings blocked");
            Reset(""); Directory.CreateDirectory(Path.Combine(root, "sub"));
            try { Reject<ArgumentException>(() => client.SwitchBranchAsync(Path.Combine(root, "sub"), "/main/topic", CancellationToken.None).GetAwaiter().GetResult(), "Explicit root required"); }
            finally { Directory.Delete(Path.Combine(root, "sub")); }
            foreach (string mode in new[] { "post-id", "post-guid", "post-head", "post-parent", "post-name", "post-mode", "post-rules", "post-rules-missing", "post-fullupdate", "post-current", "post-dirty", "post-repository" })
            {
                Reset(mode); var result = Switch();
                Check(!result.Succeeded && result.Error.Contains("verification failed") && result.Error.Contains("no automatic undo"), "Uncertain postflight retained: " + mode);
                Check(File.Exists(Meta("switched")) && Read("switch-count") == "1", "No reverse/retry: " + mode);
            }
            Reset("failure"); var failure = Switch();
            Check(!failure.Succeeded && failure.Error.Contains("native failure") && failure.Error.Contains("may have changed"), "Native failure retains diagnostics and recovery advisory");
            Check(Read("switch-count") == "1", "Native failure never retries");
            Reset("slow");
            var timeoutClient = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(1) });
            var timedOut = timeoutClient.SwitchBranchAsync(root, "/main/topic", CancellationToken.None).GetAwaiter().GetResult();
            Check(timedOut.TimedOut && !timedOut.Succeeded && timedOut.Error.Contains("may have changed"), "Native timeout reports uncertain outcome");
            Check(Read("switch-count") == "1", "Timeout never retries");
            Reset("post-fullupdate-bytes"); Write("plastic.fullycheckeddirectories", ""); Write("plastic.fullupdate", "initial");
            Check(!Switch().Succeeded && Read("plastic.fullupdate") == "changed", "Changed fullupdate bytes reported and never rolled back");
            Reset(""); using (var cancellation = new CancellationTokenSource())
            { cancellation.Cancel(); Reject<OperationCanceledException>(() => client.SwitchBranchAsync(root, "/main/topic", cancellation.Token).GetAwaiter().GetResult(), "Pre-cancel prevents switch"); Check(!File.Exists(Meta("switched")), "Pre-cancel no mutation"); }
            foreach (string mode in new[] { "slow", "post-slow" })
            {
              Reset(mode); using (var cancellation = new CancellationTokenSource())
              {
                var task = client.SwitchBranchAsync(root, "/main/topic", cancellation.Token);
                string marker = mode == "slow" ? "switched" : "verifying";
                for (int wait = 0; wait < 200 && !File.Exists(Meta(marker)); wait++) Thread.Sleep(20);
                Check(File.Exists(Meta(marker)), "Cancellation fixture reached switch/verification"); cancellation.Cancel();
                try { task.GetAwaiter().GetResult(); throw new Exception("Cancellation ignored"); }
                catch (OperationCanceledException error) { Check(error.Message.Contains("may have changed") && error.Message.Contains("no automatic undo"), "Cancellation retains uncertain outcome advisory"); }
                Check(Read("switch-count") == "1", "Cancelled switch never retried");
              }
            }
            Console.WriteLine("PASS: " + assertions + " Partial branch switch assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static string Meta(string name) { return Path.Combine(root, ".plastic", name); }
    private static string Read(string name) { return File.ReadAllText(Meta(name)); }
    private static void Write(string name, string value) { File.WriteAllText(Meta(name), value, new UTF8Encoding(false)); }
    private static void Selector(string rule) { Write("plastic.selector", "repository \"" + Repository + "\"\n path \"/\"\n " + rule + "\n"); }
    private static void Reset(string mode)
    {
        foreach (string file in Directory.GetFiles(Path.Combine(root, ".plastic"))) File.Delete(file);
        Write("plastic.workspace", "partial-test\nguid\nStandard\n"); Selector("br \"/main\"\n co \"/main\"");
        Write("plastic.wktree", "initial tree"); Write("plastic.fullycheckeddirectories", LoadRule); Write("mode", mode);
    }
    private static PlasticCommandResult Switch() { return client.SwitchBranchAsync(root, "/main/topic", CancellationToken.None).GetAwaiter().GetResult(); }
    private static void Block<T>(string message) where T : Exception
    { Reject<T>(() => Switch(), message); Check(!File.Exists(Meta("switched")), "Preflight leaves workspace unchanged: " + message); }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
    private static int Increment(string name)
    { int value = File.Exists(Meta(name)) ? Int32.Parse(Read(name)) + 1 : 1; Write(name, value.ToString()); return value; }

    private static int FakeCm(string[] args)
    {
        root = Environment.CurrentDirectory; Console.OutputEncoding = new UTF8Encoding(false);
        string mode = Read("mode"); bool switched = File.Exists(Meta("switched"));
        if (args[0] == "status")
        {
            bool all = args.Contains("--all"); int count = Increment(all ? "all-count" : "header-count"); string code = "";
            if (all)
            {
                if (!args.Contains("--ignored") || !args.Contains("--cutignored")) return 40;
                if (new[] { "CH", "CO", "AD", "DE", "PR", "IG" }.Contains(mode)) code = mode;
                if (mode == "late-pending" && count == 2 || mode == "post-dirty" && switched) code = "CH";
                if (mode == "late-tree" && count == 2) Write("plastic.wktree", "other load");
                if (mode == "late-mode" && count == 2) Write("native-standard", "yes");
            }
            bool standard = switched && mode == "post-mode" || mode == "pre-mode" && !all && count >= 4 || File.Exists(Meta("native-standard"));
            var xml = new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", standard ? 2 : -1),
                new XElement("RepSpec", new XElement("Name", switched && mode == "post-repository" ? "other" : "test"), new XElement("Server", "server:8087")))),
                new XElement("WkConfigName", (switched && mode != "post-current" ? "/main/topic" : "/main") + "@" + Repository));
            if (code != "") xml.Add(new XElement("Changes", new XElement("Change", new XElement("Path", "file.txt"), new XElement("Type", code))));
            Console.WriteLine(xml); return 0;
        }
        if (args[0] == "find" && args[1] == "branch")
        {
            if (switched && mode == "post-slow") { Write("verifying", "yes"); Thread.Sleep(10000); }
            int count = Increment("find-count"); bool pre = !switched && count == 2;
            if (pre && mode == "pre-rules") Write("plastic.fullycheckeddirectories", "");
            if (pre && mode == "pre-fullupdate") { Write("plastic.fullycheckeddirectories", ""); Write("plastic.fullupdate", ""); }
            if (pre && mode == "pre-tree") Write("plastic.wktree", "other load");
            if (pre && mode == "pre-selector") Selector("br \"/main/other\"");
            if (pre && mode == "pre-name") Write("plastic.workspace", "renamed\nguid\nStandard\n");
            bool alterId = pre && mode == "pre-id" || switched && mode == "post-id";
            bool alterGuid = pre && mode == "pre-guid" || switched && mode == "post-guid";
            bool alterHead = pre && mode == "pre-head" || switched && mode == "post-head";
            bool alterParent = pre && mode == "pre-parent" || switched && mode == "post-parent";
            var branch = new XElement("BRANCH", new XElement("NAME", "/main/topic"), new XElement("PARENT", alterParent ? "/main/other" : "/main"),
                new XElement("CHANGESET", alterHead ? 18 : 17), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"));
            if (mode != "no-identity") { branch.Add(new XElement("ID", alterId ? 42 : 41)); branch.Add(new XElement("GUID", alterGuid ? "0bb5eabf-1e04-4518-9066-10f8f3c7eeb9" : BranchGuid)); }
            Console.WriteLine(mode == "missing-target" ? new XElement("PLASTICQUERY") : new XElement("PLASTICQUERY", branch)); return 0;
        }
        if (args[0] == "ls")
        {
            bool local = args[1] == root;
            Console.WriteLine(new XElement("LsResults", new XElement("LsItems", new[] { "/", "/loaded" }.Select((path, index) => new XElement("LsItem",
                new XElement("CurrentPath", local ? root + path.TrimEnd('/').Replace('/', '\\') : path), new XElement("ItemId", index == 0 ? 3 : 27),
                new XElement("Type", "dir"), new XElement("Repository", "rep:" + Repository), new XElement("SymlinkTarget", "")))))); return 0;
        }
        if (args.SequenceEqual(new[] { "partial", "switch", "br:/main/topic@" + Repository, "--report" }))
        {
            Write("switched", String.Join("|", args)); Increment("switch-count");
            Selector("br \"/main/topic\"\n co \"/main/topic\""); Write("plastic.wktree", "new revision tree");
            if (mode == "post-name") Write("plastic.workspace", "renamed\nguid\nStandard\n");
            if (mode == "post-rules") Write("plastic.fullycheckeddirectories", "");
            if (mode == "post-rules-missing") File.Delete(Meta("plastic.fullycheckeddirectories"));
            if (mode == "post-fullupdate") Write("plastic.fullupdate", "unexpected");
            if (mode == "post-fullupdate-bytes") Write("plastic.fullupdate", "changed");
            if (mode == "slow") Thread.Sleep(10000);
            if (mode == "failure") { Console.Error.WriteLine("native failure after mutation"); return 7; }
            Console.WriteLine("Switched"); return 0;
        }
        Console.Error.WriteLine("Unexpected command: " + String.Join("|", args)); return 99;
    }
}
