// GPL-2.0-or-later. Isolated fake-cm tests for first checkout safety and recovery.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TortoiseSCM;

internal static class WorkspaceCreationTests
{
    private static string Server { get { return Read("mode") == "ssl" ? "ssl://server:8088" : "server:8087"; } }
    private static string Repository { get { return "demo@" + Server; } }
    private const string RepositoryGuid = "6b08125e-83fa-4ef3-9708-336fd7e7c19c";
    private const string BranchGuid = "de5f12c5-99ef-4b92-b5bb-bd9ed0bcd65f";
    private static string root;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length != 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-create-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("TORTOISESCM_CREATE_FIXTURE", root);
        try
        {
            var client = Client(10);
            Reset(""); Check(client.GetRegisteredWorkspacesAsync(CancellationToken.None).GetAwaiter().GetResult().Count == 0, "Registered workspace query accepts empty list");
            Reset("workspace-name"); var registered = client.GetRegisteredWorkspacesAsync(CancellationToken.None).GetAwaiter().GetResult().Single();
            Check(registered.Name == "NEW-WK" && registered.RootPath == Path.Combine(root, "elsewhere"), "Registered workspace query preserves names and paths");
            foreach (string mode in new[] { "workspace-malformed", "workspace-list-fail" })
            { Reset(mode); Reject(() => client.GetRegisteredWorkspacesAsync(CancellationToken.None).GetAwaiter().GetResult(), "Failed registered workspace query rejected " + mode); NoWrite(); }
            Reset(""); var repo = client.GetRepositoriesAsync("server:8087", CancellationToken.None).GetAwaiter().GetResult().Single();
            Check(repo.Name == "demo" && repo.Server == "server:8087" && repo.Specification == Repository && repo.Id == 7 && repo.Guid == RepositoryGuid, "Native repo fields retained");
            Reset("ssl");
            repo = client.GetRepositoriesAsync(Server, CancellationToken.None).GetAwaiter().GetResult().Single();
            Check(repo.Server == Server && repo.Specification == Repository, "SSL server address retained");
            Check(Create(client, repo, "new-wk", Target(), "/main").Succeeded, "SSL repository creation and identity checks");
            foreach (string mode in new[] { "bad-repo-id", "bad-repo-guid", "duplicate-repo", "repo-fields", "repo-fail" })
            { Reset(mode); Reject(() => client.GetRepositoriesAsync("server:8087", CancellationToken.None).GetAwaiter().GetResult(), "Malformed repository response rejected " + mode); NoWrite(); }
            foreach (string bad in new[] { "", " ", "-option", "a\nb", "a\tb", "a'b", "a\"b", "a#b", "a\\b" })
            { Reset(""); Reject(() => client.GetRepositoriesAsync(bad, CancellationToken.None).GetAwaiter().GetResult(), "Server validation " + bad); NoWrite(); }
            foreach (string bad in new[] { "", " ", ".", "..", "a/b", "a\\b", "a:b", "a@b", "-option", "a\nb" })
            { Reset(""); Reject(() => Create(client, Expected(), bad, Target(), "/main"), "Workspace name validation " + bad); NoWrite(); }
            foreach (string bad in new[] { "", ".", "relative", "C:relative", "\\relative", "C:\\", "C:\\a\\..\\b", "C:\\a.\\b", "C:\\a \\b", "C:\\a:stream", "\\\\?\\C:\\x" })
            { Reset(""); Reject(() => Create(client, Expected(), "new-wk", bad, "/main"), "Target path validation " + bad); NoWrite(); }
            foreach (string branch in new[] { "main", "/", "/main@else", "/main/..", "/main\nother" })
            { Reset(""); Reject(() => Create(client, Expected(), "new-wk", Target(), branch), "Branch validation " + branch); NoWrite(); }
            foreach (Action<PlasticRepositoryInfo> mutate in new Action<PlasticRepositoryInfo>[] { r => r.Id = 0, r => r.Guid = "bad", r => r.Specification = "other@server", r => r.Name = "demo' where 1=1" })
            { Reset(""); var invalid = Expected(); mutate(invalid); Reject(() => Create(client, invalid, "new-wk", Target(), "/main"), "Invalid repository snapshot"); NoWrite(); }
            foreach (string mode in new[] { "repo-changed", "repo-late-changed", "branch-missing", "branch-changed", "workspace-name", "workspace-parent", "workspace-child", "workspace-malformed", "late-content" })
            { Reset(mode); Reject(() => Create(client, Expected(), "new-wk", Target(), "/main"), "Preflight rejects " + mode); NoWrite(); }
            Reset(""); Directory.CreateDirectory(Target()); File.WriteAllText(Path.Combine(Target(), "valuable.txt"), "preserve");
            Reject(() => Create(client, Expected(), "new-wk", Target(), "/main"), "Nonempty directory rejected"); Check(File.ReadAllText(Path.Combine(Target(), "valuable.txt")) == "preserve", "Existing bytes preserved"); NoWrite();
            Reset(""); File.WriteAllText(Target(), "preserve"); Reject(() => Create(client, Expected(), "new-wk", Target(), "/main"), "File destination rejected"); NoWrite();
            Reset(""); Directory.CreateDirectory(Path.Combine(root, ".plastic")); File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "parent");
            Reject(() => Create(client, Expected(), "new-wk", Target(), "/main"), "Unregistered parent metadata rejected"); NoWrite(); Directory.Delete(Path.Combine(root, ".plastic"), true);
            Reset(""); using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); Reject(() => client.CreateWorkspaceAsync(Expected(), "new-wk", Target(), "/main", null, cancel.Token), "Precancelled request"); NoWrite(); }
            Reset("");
            var gate = typeof(PlasticClient).GetMethod("CreationGate", BindingFlags.Static | BindingFlags.NonPublic);
            using ((IDisposable)gate.Invoke(null, new object[] { "all-workspace-creations" }))
            {
                Reject(() => Create(client, Expected(), "child-name", Path.Combine(Target(), "child"), "/main"), "Global gate protects parent-child paths before metadata exists");
                Check(!Directory.Exists(Target()) && !File.Exists(Path.Combine(root, "repository-count")), "Blocked creation cannot start native preflight or create directory"); NoWrite();
            }
            foreach (bool existing in new[] { false, true })
            {
                Reset(""); if (existing) Directory.CreateDirectory(Target());
                var result = Create(client, Expected(), "new-wk", Target(), "/main/topic 中文 & literal");
                Check(result.Succeeded && result.WorkspaceCreated && result.UpdateCompleted && !result.OutcomeUncertain, "Successful checkout " + existing);
                Check(File.ReadAllText(Path.Combine(root, "create-args")) == "new-wk\n" + Target() + "\nrep:" + Repository, "Explicit repo create args");
                Check(File.ReadAllText(Path.Combine(root, "switch-args")) == "br:/main/topic 中文 & literal@" + Repository + "\n--workspace=" + Target(), "Explicit branch literal args, no default download");
                Check(File.ReadAllText(Path.Combine(Target(), "downloaded.txt")) == "selected", "Selected files downloaded");
                Check(Read("create-count") == "1" && Read("switch-count") == "1", "Exactly one create/download");
            }
            foreach (string selectedBranch in new[] { "/main", "/main/topic 中文 & literal" })
            {
                Reset("");
                var partialResult = client.CreateWorkspaceAsync(Expected(), "new-wk", Target(), selectedBranch, true, null, CancellationToken.None).GetAwaiter().GetResult();
                Check(partialResult.Succeeded && client.GetWorkspaceAsync(Target(), CancellationToken.None).GetAwaiter().GetResult().IsPartial, "Requested Gluon creation verifies actual partial tree");
                Check(Read("configure-count") == "1" && Read("partial-update-count") == "1", "Gluon initializes exactly once after full download");
                Check(Read("branch") == selectedBranch && File.ReadAllText(Path.Combine(Target(), "downloaded.txt")) == "selected", "Gluon retains requested branch and first-download bytes");
                Check(File.ReadAllText(Path.Combine(Target(), ".plastic", "plastic.workspace")).EndsWith("Standard"), "Mode verification uses actual tree, not stale metadata label");
            }
            foreach (string mode in new[] { "configure-fail", "partial-update-fail", "partial-mode-wrong", "partial-branch-wrong", "partial-repo-changed" })
            {
                Reset(mode);
                var partialResult = client.CreateWorkspaceAsync(Expected(), "new-wk", Target(), "/main", true, null, CancellationToken.None).GetAwaiter().GetResult();
                Check(!partialResult.Succeeded && partialResult.WorkspaceCreated && !partialResult.UpdateCompleted && partialResult.OutcomeUncertain, "Gluon failure preserves uncertain outcome " + mode);
                Check(partialResult.Stage.Contains("Gluon") && partialResult.RecoveryInstructions.Contains(partialResult.Stage), "Gluon failure reports exact recovery stage " + mode);
                Check(Read("create-count") == "1" && Read("switch-count") == "1" && Read("configure-count") == "1" && File.ReadAllText(Path.Combine(Target(), "downloaded.txt")) == "selected", "Gluon failure preserves bytes and never retries writes " + mode);
                if (mode == "configure-fail") Check(!File.Exists(Path.Combine(root, "partial-update-count")), "Failed configure stops before partial update");
            }
            foreach (string mode in new[] { "create-fail", "create-identity", "create-partial", "create-extra-file", "switch-fail", "post-branch", "post-repo" })
            {
                Reset(mode); var result = Create(client, Expected(), "new-wk", Target(), "/main");
                Check(!result.Succeeded && !result.UpdateCompleted && result.OutcomeUncertain && result.RecoveryInstructions.Contains(Target()), "Recovery details for " + mode);
                Check(Directory.Exists(Target()) && Read("create-count") == "1", "Failure never cleans or recreates " + mode);
                Check(result.WorkspaceCreated == (mode.StartsWith("switch", StringComparison.Ordinal) || mode.StartsWith("post", StringComparison.Ordinal) || mode == "create-extra-file"), "Created confirmation phase " + mode);
                Check(!File.Exists(Path.Combine(root, "switch-count")) || Read("switch-count") == "1", "Failure never repeats switch " + mode);
                if (mode == "create-extra-file") Check(File.ReadAllText(Path.Combine(Target(), "valuable.txt")) == "preserve" && !File.Exists(Path.Combine(root, "switch-count")), "Late user file preserved without download");
            }
            Reset("slow-create"); var timed = Create(Client(0.3), Expected(), "new-wk", Target(), "/main");
            Check(!timed.Succeeded && timed.OutcomeUncertain && !timed.WorkspaceCreated && Directory.Exists(Target()), "Create timeout preserves uncertain metadata");
            Reset("slow-switch"); timed = Create(Client(0.3), Expected(), "new-wk", Target(), "/main");
            Check(!timed.Succeeded && timed.OutcomeUncertain && timed.WorkspaceCreated && !timed.UpdateCompleted, "Download timeout distinguishes confirmed workspace");
            Reset("slow-switch");
            using (var cancel = new CancellationTokenSource())
            {
                var captured = Expected(); var task = client.CreateWorkspaceAsync(captured, "new-wk", Target(), "/main", null, cancel.Token);
                captured.Name = "changed"; captured.Id = 999;
                for (int i = 0; i < 300 && !File.Exists(Path.Combine(root, "switch-count")); i++) Thread.Sleep(20);
                Check(File.Exists(Path.Combine(root, "switch-count")), "Download stage reached"); cancel.Cancel();
                Reject(() => Create(client, Expected(), "different-name", Target(), "/main"), "Concurrent same destination locked");
                Reject(() => Create(client, Expected(), "child-name", Path.Combine(Target(), "child"), "/main"), "Concurrent nested destination locked");
                Check(task.GetAwaiter().GetResult().Succeeded, "Caller snapshot captured; cancellation cannot interrupt mutation");
            }
            Console.WriteLine("PASS: " + assertions + " workspace creation assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); Environment.SetEnvironmentVariable("TORTOISESCM_CREATE_FIXTURE", null); }
    }
    private static PlasticClient Client(double seconds) { return new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(seconds) }); }
    private static PlasticRepositoryInfo Expected() { return new PlasticRepositoryInfo { Name = "demo", Server = Server, Specification = Repository, Id = 7, Guid = RepositoryGuid }; }
    private static string Target() { return Path.Combine(root, "new-workspace"); }
    private static string Read(string file) { return File.ReadAllText(Path.Combine(root, file)); }
    private static PlasticWorkspaceCreationResult Create(PlasticClient client, PlasticRepositoryInfo repo, string name, string path, string branch)
    { return client.CreateWorkspaceAsync(repo, name, path, branch, null, CancellationToken.None).GetAwaiter().GetResult(); }
    private static void Reset(string mode)
    {
        foreach (string path in Directory.GetFileSystemEntries(root)) { if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path); }
        File.WriteAllText(Path.Combine(root, "mode"), mode);
    }
    private static void Check(bool value, string name) { assertions++; if (!value) throw new Exception("FAILED: " + name); }
    private static void Reject(Action action, string name) { try { action(); } catch (Exception) { assertions++; return; } throw new Exception("FAILED: expected rejection " + name); }
    private static void NoWrite() { Check(!File.Exists(Path.Combine(root, "create-count")) && !File.Exists(Path.Combine(root, "switch-count")), "Rejected request performs no native writes"); }
    private static int Count(string name) { string file = Path.Combine(root, name); int value = File.Exists(file) ? Int32.Parse(File.ReadAllText(file)) : 0; File.WriteAllText(file, (++value).ToString()); return value; }
    private static int FakeCm(string[] args)
    {
        root = Environment.GetEnvironmentVariable("TORTOISESCM_CREATE_FIXTURE"); string mode = Read("mode"); Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "repository")
        {
            int count = Count("repository-count"); if (mode == "repo-fail") return 1;
            string id = mode == "bad-repo-id" ? "0" : (mode == "repo-changed" || (mode == "repo-late-changed" && count > 1) || (mode == "post-repo" && File.Exists(Path.Combine(root, "switch-count"))) || (mode == "partial-repo-changed" && File.Exists(Path.Combine(root, "partial-update-count"))) ? "8" : "7");
            string line = id + "\tdemo\t" + Server + "\t" + (mode == "bad-repo-guid" ? "invalid" : RepositoryGuid);
            Console.WriteLine(mode == "repo-fields" ? line + "\textra" : line); if (mode == "duplicate-repo") Console.WriteLine(line); return 0;
        }
        if (args[0] == "find")
        {
            int count = Count("branch-count"); string name = File.Exists(Path.Combine(root, "branch")) ? Read("branch") : "/main";
            bool changed = mode == "branch-changed" && count > 1 || mode == "post-branch" && File.Exists(Path.Combine(root, "switch-count"));
            string branches = BranchXml("/main", changed ? "22" : "2", BranchGuid) + BranchXml("/main/topic 中文 & literal", "3", "cb213f9b-9d9f-410b-8e49-b1f1e22c155c");
            Console.WriteLine("<PLASTICQUERY>" + (mode == "branch-missing" ? "" : branches) + "</PLASTICQUERY>"); GC.KeepAlive(name); return 0;
        }
        if (args[0] == "workspace" && args[1] == "list")
        {
            if (mode == "workspace-list-fail") return 1;
            int count = Count("workspace-list-count");
            if (mode == "workspace-malformed") Console.WriteLine("malformed");
            if (mode == "workspace-name") Console.WriteLine("NEW-WK\t" + Path.Combine(root, "elsewhere") + "\t" + RepositoryGuid);
            if (mode == "workspace-parent") Console.WriteLine("other\t" + root + "\t" + RepositoryGuid);
            if (mode == "workspace-child") Console.WriteLine("other\t" + Path.Combine(Target(), "child") + "\t" + RepositoryGuid);
            if (mode == "late-content" && count == 2) { Directory.CreateDirectory(Target()); File.WriteAllText(Path.Combine(Target(), "late.txt"), "preserve"); } return 0;
        }
        if (args[0] == "workspace" && args[1] == "create")
        {
            Count("create-count"); File.WriteAllText(Path.Combine(root, "create-args"), String.Join("\n", args.Skip(2)));
            Directory.CreateDirectory(Path.Combine(Target(), ".plastic"));
            File.WriteAllText(Path.Combine(Target(), ".plastic", "plastic.workspace"), (mode == "create-identity" ? "other" : args[2]) + "\nserver\nStandard");
            WriteSelector("/main");
            if (mode == "create-extra-file") File.WriteAllText(Path.Combine(Target(), "valuable.txt"), "preserve");
            if (mode == "slow-create") Thread.Sleep(3000);
            if (mode == "create-fail") { Console.Error.WriteLine("metadata partially created"); return 1; } return 0;
        }
        if (args[0] == "switch")
        {
            Count("switch-count"); File.WriteAllText(Path.Combine(root, "switch-args"), String.Join("\n", args.Skip(1)));
            string branch = args[1].Substring(3); branch = branch.Substring(0, branch.IndexOf('@')); WriteSelector(branch); File.WriteAllText(Path.Combine(root, "branch"), branch);
            File.WriteAllText(Path.Combine(Target(), "downloaded.txt"), "selected"); if (mode == "slow-switch") Thread.Sleep(3000);
            if (mode == "switch-fail") { Console.Error.WriteLine("network interrupted"); return 1; } return 0;
        }
        if (args[0] == "partial")
        {
            if (Environment.CurrentDirectory != Target()) throw new Exception("Partial initialization must use new workspace root CWD");
            if (args.SequenceEqual(new[] { "partial", "configure", "-/", "+/" }))
            {
                Check(File.Exists(Path.Combine(root, "switch-count")), "Configure follows full branch download");
                Count("configure-count"); return mode == "configure-fail" ? 1 : 0;
            }
            if (args.SequenceEqual(new[] { "partial", "update", ".", "--report" }))
            {
                Check(File.Exists(Path.Combine(root, "configure-count")), "Partial update follows configure");
                Count("partial-update-count");
                if (mode == "partial-branch-wrong") { WriteSelector("/main/other"); File.WriteAllText(Path.Combine(root, "branch"), "/main/other"); }
                return mode == "partial-update-fail" ? 1 : 0;
            }
        }
        if (args[0] == "status")
        {
            string branch = File.Exists(Path.Combine(root, "branch")) ? Read("branch") : "/main";
            Console.WriteLine("<StatusOutput><WkConfigName>" + Escape(branch + "@" + Repository) + "</WkConfigName><WorkspaceStatus><Status><Changeset>" + (mode == "create-partial" || (File.Exists(Path.Combine(root, "partial-update-count")) && mode != "partial-mode-wrong") ? "-1" : "0") + "</Changeset><RepSpec><Name>demo</Name><Server>" + Server + "</Server></RepSpec></Status></WorkspaceStatus></StatusOutput>"); return 0;
        }
        Console.Error.WriteLine("Unsupported fake cm command: " + String.Join(" ", args)); return 1;
    }
    private static string Escape(string value) { return System.Security.SecurityElement.Escape(value); }
    private static string BranchXml(string name, string id, string guid) { return "<BRANCH><ID>" + id + "</ID><GUID>" + guid + "</GUID><NAME>" + Escape(name) + "</NAME><PARENT></PARENT><CHANGESET>0</CHANGESET><REPNAME>demo</REPNAME><REPSERVER>" + Server + "</REPSERVER></BRANCH>"; }
    private static void WriteSelector(string branch) { File.WriteAllText(Path.Combine(Target(), ".plastic", "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  br \"" + branch + "\"\n  co \"" + branch + "\""); }
}
