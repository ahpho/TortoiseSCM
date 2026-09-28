// GPL-2.0-or-later. Deterministic native peer for read-only Partial switch reviews.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class PartialBranchSwitchPreviewTests
{
    private const string Repository = "test@server:8087", Namespace = "7bc5ef28-aaf2-4380-98b5-5a0a891f6461", BranchGuid = "31e8f2fd-3ced-4829-ab09-f7733a98d4ad";
    private static string root;
    private static PlasticClient client;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-switch-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(8) });
        try
        {
            Reset(""); var safe = Preview();
            Check(safe.CanSwitch && safe.HeadChangeset == 17 && safe.Branch == "/main/topic" && safe.Repository == Repository, "Preview captures pinned target");
            Check(safe.LoadedDirectoryCount == 3 && safe.LoadingRuleCount == 1 && !safe.IsFullyLoaded && safe.Directories.All(item => item.Change == "Unchanged"), "Loaded scope summarized");
            Check(Read("tree-argument") == "--tree=cs:17@" + Repository, "Tree pinned to captured target head"); NoWrite();
            safe.Branch = "/evil"; safe.HeadChangeset = 900; safe.Directories.Clear();
            Check(client.SwitchBranchAsync(root, safe, CancellationToken.None).GetAwaiter().GetResult().Succeeded && Read("switched").Contains("br:/main/topic@"), "Caller-edited display cannot retarget captured evidence");
            foreach (string mode in new[] { "moved", "case-moved", "deleted", "replaced", "added-loaded", "link", "xlink", "unknown-type", "file-link", "file-directory", "sparse-file-directory", "sparse-file-move" })
            {
                Reset(mode); var preview = Preview(); Check(!preview.CanSwitch && preview.Directories.Any(item => item.Change != "Unchanged"), "Structural blocker displayed: " + mode); NoWrite();
                preview.CanSwitch = true; preview.Directories.Clear();
                Reject<ArgumentException>(() => client.SwitchBranchAsync(root, preview, CancellationToken.None).GetAwaiter().GetResult(), "Edited blocked preview cannot bypass: " + mode);
                Reject<ArgumentException>(() => client.SwitchBranchAsync(root, "/main/topic", CancellationToken.None).GetAwaiter().GetResult(), "Direct legacy switch also guards: " + mode); NoWrite();
            }
            foreach (string mode in new[] { "added-unloaded", "sparse-added", "file-change", "file-delete", "safe-file-move" })
            { Reset(mode); Check(Preview().CanSwitch, "Unchanged directory loading allows: " + mode); NoWrite(); }
            Reset("safe-file-move"); Check(client.SwitchBranchAsync(root, Preview(), CancellationToken.None).GetAwaiter().GetResult().Succeeded, "File move within retained loaded directories can switch");
            Reset("added-unloaded"); Write("plastic.fullycheckeddirectories", ""); Write("plastic.fullupdate", "full");
            Check(!Preview().CanSwitch, "Full-workspace rules include new directories anywhere"); NoWrite();
            Reset("added-unloaded"); Write("plastic.fullycheckeddirectories", Namespace + ":3\r\n");
            Check(!Preview().CanSwitch, "Explicit root rule includes new directories anywhere"); NoWrite();
            foreach (string mode in new[] { "duplicate-id", "duplicate-path", "missing-id", "negative-id", "duplicate-field", "missing-root", "unsafe-path", "missing-parent", "wrong-xml", "missing-link-field" })
            { Reset(mode); Reject<Exception>(() => Preview(), "Malformed listing rejected: " + mode); NoWrite(); }
            foreach (string rules in new[] { Namespace + ":999\r\n", Namespace + ":29\r\n", Namespace + ":27\r\n" + BranchGuid + ":28\r\n", Namespace + ":27\r\n" + BranchGuid + ":27\r\n" })
            { Reset(""); Write("plastic.fullycheckeddirectories", rules); Reject<Exception>(() => Preview(), "Unknown/file/mixed/duplicate loading rule blocked"); NoWrite(); }
            foreach (string mode in new[] { "head-race", "id-race", "selector-race", "loading-race", "tree-race", "mode-race", "dirty-race" })
            { Reset(mode); Reject<Exception>(() => Preview(), "Concurrent preview state drift rejected: " + mode); NoWrite(); }
            foreach (string change in new[] { "rules", "tree", "selector", "branch", "workspace", "late-rules" })
            {
                Reset(""); var preview = Preview();
                if (change == "rules") Write("plastic.fullycheckeddirectories", "");
                if (change == "tree") Write("plastic.wktree", "changed");
                if (change == "selector") Selector("/main/other");
                if (change == "branch") Write("mode", "changed-head");
                if (change == "workspace") Write("plastic.workspace", "renamed\nguid\nStandard\n");
                if (change == "late-rules") Write("mode", "late-reviewed-rules");
                Reject<InvalidOperationException>(() => client.SwitchBranchAsync(root, preview, CancellationToken.None).GetAwaiter().GetResult(), "Reviewed evidence cannot silently change: " + change); NoWrite();
            }
            Reset(""); Reject<ArgumentException>(() => client.SwitchBranchAsync(root, new PlasticPartialBranchSwitchPreview { CanSwitch = true }, CancellationToken.None).GetAwaiter().GetResult(), "Fabricated preview rejected"); NoWrite();
            foreach (string gate in new[] { "StructureGate", "OpenMergeGate" })
            {
                Reset(""); var method = typeof(PlasticClient).GetMethod(gate, BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic);
                using ((IDisposable)method.Invoke(method.IsStatic ? null : client, new object[] { root })) Reject<InvalidOperationException>(() => Preview(), "Preview respects " + gate);
                NoWrite();
            }
            Reset(""); using (var cancellation = new CancellationTokenSource())
            { cancellation.Cancel(); Reject<OperationCanceledException>(() => client.PreviewPartialBranchSwitchAsync(root, "/main/topic", cancellation.Token).GetAwaiter().GetResult(), "Precancel preview"); NoWrite(); }
            Reset("cancel-ls"); using (var cancellation = new CancellationTokenSource())
            {
                var pending = client.PreviewPartialBranchSwitchAsync(root, "/main/topic", cancellation.Token);
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!File.Exists(Meta("ls-started")) && DateTime.UtcNow < deadline) Thread.Sleep(10);
                Check(File.Exists(Meta("ls-started")), "Preview reaches active native tree read");
                cancellation.Cancel();
                Reject<OperationCanceledException>(() => pending.GetAwaiter().GetResult(), "Cancel active native read before any switch"); NoWrite();
            }
            Reset(""); Check(Preview().CanSwitch, "Cancelled read releases workspace gates for a fresh preview");
            Console.WriteLine("PASS: " + assertions + " Partial branch switch preview assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static PlasticPartialBranchSwitchPreview Preview() { return client.PreviewPartialBranchSwitchAsync(root, "/main/topic", CancellationToken.None).GetAwaiter().GetResult(); }
    private static string Meta(string name) { return Path.Combine(root, ".plastic", name); }
    private static string Read(string name) { return File.ReadAllText(Meta(name)); }
    private static void Write(string name, string value) { File.WriteAllText(Meta(name), value, new UTF8Encoding(false)); }
    private static void Selector(string branch) { Write("plastic.selector", "repository \"" + Repository + "\"\n path \"/\"\n br \"" + branch + "\"\n co \"" + branch + "\"\n"); }
    private static void Reset(string mode)
    {
        foreach (string file in Directory.GetFiles(Path.Combine(root, ".plastic"))) File.Delete(file);
        Write("mode", mode); Write("plastic.workspace", "preview-test\nguid\nStandard\n"); Selector("/main"); Write("plastic.wktree", "initial"); Write("plastic.fullycheckeddirectories", Namespace + ":27\r\n");
        if (mode == "sparse-added" || mode == "sparse-file-directory") Write("plastic.fullycheckeddirectories", Namespace + ":28\r\n");
        if (mode == "sparse-file-move" || mode == "safe-file-move") Write("plastic.fullycheckeddirectories", "");
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
    private static void NoWrite() { Check(!File.Exists(Meta("switched")), "No native mutation"); Check(Read("plastic.wktree") == "initial" || Read("plastic.wktree") == "changed", "No automatic tree restore/write"); }
    private static int Increment(string name) { int count = File.Exists(Meta(name)) ? Int32.Parse(Read(name)) + 1 : 1; Write(name, count.ToString()); return count; }
    private static XElement Item(string path, long id, string type, bool local)
    { return new XElement("LsItem", new XElement("CurrentPath", local ? root + path.TrimEnd('/').Replace('/', '\\') : path), new XElement("ItemId", id), new XElement("Type", type), new XElement("Repository", "rep:" + Repository), new XElement("SymlinkTarget", "")); }
    private static int FakeCm(string[] args)
    {
        root = Environment.CurrentDirectory; Console.OutputEncoding = new UTF8Encoding(false); string mode = Read("mode"); bool switched = File.Exists(Meta("switched"));
        if (args[0] == "status")
        {
            var xml = new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", mode == "mode-race" && File.Exists(Meta("tree-argument")) ? 17 : -1),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))), new XElement("WkConfigName", (switched ? "/main/topic" : "/main") + "@" + Repository));
            if (mode == "dirty-race" && File.Exists(Meta("tree-argument"))) xml.Add(new XElement("Changes", new XElement("Change", new XElement("Path", "file.txt"), new XElement("Type", "CH"))));
            if (mode == "late-reviewed-rules") Write("plastic.fullycheckeddirectories", "");
            Console.WriteLine(xml); return 0;
        }
        if (args[0] == "find" && args[1] == "branch")
        {
            int count = Increment("find-count");
            Console.WriteLine(new XElement("PLASTICQUERY", new XElement("BRANCH", new XElement("NAME", "/main/topic"), new XElement("PARENT", "/main"),
                new XElement("CHANGESET", mode == "changed-head" || mode == "head-race" && count >= 2 ? 18 : 17), new XElement("ID", mode == "id-race" && count >= 2 ? 42 : 41), new XElement("GUID", BranchGuid),
                new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087")))); return 0;
        }
        if (args[0] == "ls")
        {
            bool local = args[1] == root;
            var items = new[] { Item("/", 3, "目录", local), Item("/loaded", 27, "dir", local), Item("/loaded/sub", 28, "directory", local), Item("/loaded/file.txt", 29, "文本文件", local) }.ToList();
            if (!local)
            {
                if (mode == "cancel-ls") { Write("ls-started", "true"); Thread.Sleep(30000); }
                Write("tree-argument", args.Single(arg => arg.StartsWith("--tree=")));
                if (mode == "moved" || mode == "case-moved") foreach (var item in items.Skip(1)) item.Element("CurrentPath").Value = item.Element("CurrentPath").Value.Replace("/loaded", mode == "moved" ? "/renamed" : "/LOADED");
                if (mode == "deleted") items.RemoveRange(1, 3);
                if (mode == "replaced") items[1].Element("ItemId").Value = "70";
                if (mode == "added-loaded") items.Add(Item("/loaded/new", 50, "dir", false));
                if (mode == "added-unloaded") items.Add(Item("/outside", 50, "dir", false));
                if (mode == "sparse-added") items.Add(Item("/loaded/new", 50, "dir", false));
                if (mode == "link") items[1].Element("SymlinkTarget").Value = "/elsewhere";
                if (mode == "xlink") items[1].Element("Repository").Value = "rep:other@server:8087";
                if (mode == "unknown-type") items[2].Element("Type").Value = "xlink";
                if (mode == "file-link") items[3].Element("SymlinkTarget").Value = "/outside";
                if (mode == "file-directory" || mode == "sparse-file-directory") { items[3].Element("Type").Value = "dir"; items[3].Element("ItemId").Value = "90"; }
                if (mode == "sparse-file-move") { items.Add(Item("/new", 90, "dir", false)); items[3].Element("CurrentPath").Value = "/new/file.txt"; }
                if (mode == "safe-file-move") items[3].Element("CurrentPath").Value = "/loaded/sub/file.txt";
                if (mode == "file-delete") items.RemoveAt(3);
                if (mode == "duplicate-id") items.Add(Item("/duplicate", 27, "dir", false));
                if (mode == "duplicate-path") items.Add(Item("/LOADED", 77, "dir", false));
                if (mode == "missing-id") items[1].Element("ItemId").Remove();
                if (mode == "negative-id") items[1].Element("ItemId").Value = "-1";
                if (mode == "duplicate-field") items[1].Add(new XElement("Type", "dir"));
                if (mode == "missing-root") items.RemoveAt(0);
                if (mode == "unsafe-path") items[3].Element("CurrentPath").Value = "/loaded/../outside";
                if (mode == "missing-parent") items[3].Element("CurrentPath").Value = "/absent/file";
                if (mode == "missing-link-field") items[3].Element("SymlinkTarget").Remove();
                if (mode == "selector-race") Selector("/other");
                if (mode == "loading-race") Write("plastic.fullycheckeddirectories", "");
                if (mode == "tree-race") Write("plastic.wktree", "changed");
            }
            Console.WriteLine(new XElement(mode == "wrong-xml" ? "Unexpected" : "LsResults", new XElement("LsItems", items))); return 0;
        }
        if (args.SequenceEqual(new[] { "partial", "switch", "br:/main/topic@" + Repository, "--report" }))
        { Write("switched", String.Join("|", args)); Selector("/main/topic"); Write("plastic.wktree", "new"); Console.WriteLine("Switched"); return 0; }
        Console.Error.WriteLine("Unexpected native command: " + String.Join("|", args)); return 99;
    }
}
