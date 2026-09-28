// GPL-2.0-or-later. Native-state and subprocess contracts; BComp helper is not the BC product.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TortoiseSCM;

internal static class WorkingBeyondCompareTests
{
    private static string root, local;
    private static int assertions;
    private static readonly byte[] BaseBytes = { 0, 255, 7, 9 }, LocalBytes = { 0, 254, 8, 10 };
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Child(args);
        root = Path.Combine(Path.GetTempPath(), "TSCM-working-bc-" + Guid.NewGuid().ToString("N") + " 中文 &");
        Directory.CreateDirectory(root);
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + assertions + " working Beyond Compare assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Environment.SetEnvironmentVariable("TSCM_WORKING_BC_ROOT", null); Directory.Delete(root, true); }
    }
    private static async Task Run()
    {
        string exe = Assembly.GetExecutingAssembly().Location, bc = Path.Combine(root, "BComp.exe");
        File.Copy(exe, bc); Environment.SetEnvironmentVariable("TSCM_WORKING_BC_ROOT", root);
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "working-bc\nguid\nStandard\n");
        local = Path.Combine(root, "工作 文件 &.bin");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = exe, BeyondComparePath = bc, UseBeyondCompare = true, Timeout = TimeSpan.FromSeconds(10) });
        foreach (string state in new[] { "AD", "DE", "LD", "CH", "MV", "" })
        {
            Prepare(state, "");
            Check((await client.OpenDiffToolAsync(local, Token)).Succeeded, "Native state supports BC: " + state);
            var recording = XDocument.Load(Path.Combine(root, "viewer.xml")).Root;
            var argv = recording.Elements("arg").Select(item => item.Value).ToArray();
            bool deleted = state == "DE" || state == "LD";
            Check(argv[0] == "/solo" && argv[1] == "/readonly", "Independent read-only viewer session");
            Check(Convert.FromBase64String((string)recording.Element("left")).SequenceEqual(state == "AD" ? new byte[0] : BaseBytes), "Exact binary base or proven empty AD side");
            Check(Convert.FromBase64String((string)recording.Element("right")).SequenceEqual(deleted ? new byte[0] : LocalBytes), "Exact live bytes or proven empty deleted side");
            Check((bool)recording.Element("leftReadonly") && (!deleted || (bool)recording.Element("rightReadonly")), "Temporary inputs are read-only");
            Check(deleted ? argv[3] != local && argv[5].Contains("empty") : argv[3] == local, "Working target remains live, only deletion gets empty temp");
            Check(state != "AD" || argv[4].Contains("empty"), "Addition title identifies absent base");
            Check(argv[4].Contains(local) && argv[5].Contains(local), "Titles preserve actual Unicode path");
            Check(!Directory.Exists(Path.GetDirectoryName(argv[2])) && (!deleted || !File.Exists(argv[3])), "All temporary inputs removed on viewer completion");
            Check(File.Exists(local) == !deleted && (deleted || File.ReadAllBytes(local).SequenceEqual(LocalBytes)), "Comparison preserves working state");
            Check(File.ReadAllLines(Path.Combine(root, "commands")).Count(line => line == "cat") == (state == "AD" ? 0 : 1), "Only proven existing baseline is downloaded");
        }
        foreach (string specification in new[] {
            "AD:status-fail", "AD:fileinfo-fail", "AD:ls-fail", "AD:bad-revision", "AD:bad-status", "AD:positive-added-id", "AD:state-change", "AD:identity-change",
            "AD:xlink", "AD:symlink", "AD:directory", "AD:wrong-repository", "AD:duplicate-state", "AD:info-xlink", "AD:info-directory", "AD:missing-file",
            "DE:cat-fail", "DE:ambiguous", "DE:no-hash", "DE:parent-xlink", "DE:parent-move", "DE:recreated", "DE:state-change", "DE:revision-change",
            "LD:cat-fail", "LD:identity-change", "LD:recreated", "LD:symlink", "CH:cat-fail", "CH:selector-change", "CH:identity-change", "CH:revision-change", "PR:", "IG:" })
        {
            var parts = specification.Split(':'); Prepare(parts[0], parts[1]);
            await Reject<Exception>(() => client.OpenDiffToolAsync(local, Token), "Unsafe endpoint rejected: " + specification);
            Check(!File.Exists(Path.Combine(root, "viewer.xml")), "Unsafe endpoint never launches viewer: " + specification);
            string downloads = Path.Combine(root, "downloads");
            Check(!File.Exists(downloads) || File.ReadAllLines(downloads).All(path => !File.Exists(path) && !Directory.Exists(Path.GetDirectoryName(path))), "Failed preparation cleans any downloaded files");
        }
        Prepare("", "missing-file");
        await Reject<ArgumentException>(() => client.OpenDiffToolAsync(local, Token), "Unproven absent file is not deletion");
        Prepare("DE", "exit17");
        Check((await client.OpenDiffToolAsync(local, Token)).ExitCode == 17, "BC failure result preserved");
        string[] closed = ViewerArgs();
        Check(!File.Exists(closed[2]) && !File.Exists(closed[3]), "BC failure cleans both deleted inputs");
        foreach (string state in new[] { "AD", "DE" })
        {
            Prepare(state, "wait");
            using (var cancel = new CancellationTokenSource())
            {
                var task = client.OpenDiffToolAsync(local, cancel.Token);
                for (int i = 0; i < 160 && !File.Exists(Path.Combine(root, "opened")); i++) await Task.Delay(25);
                Check(File.Exists(Path.Combine(root, "opened")), "BC wait session opened");
                var argv = ViewerArgs(); cancel.Cancel(); await Task.Delay(80);
                Check(!task.IsCompleted && File.Exists(argv[2]) && File.Exists(argv[3]), "Cancellation retains inputs until viewer closes");
                File.WriteAllText(Path.Combine(root, "release"), "close");
                await Reject<OperationCanceledException>(() => task, "Cancellation reported after close");
                Check(!Directory.Exists(Path.GetDirectoryName(argv[2])), "Cancellation cleans entire comparison directory");
            }
        }
        Prepare("DE", "exit102");
        await Reject<InvalidOperationException>(() => client.OpenDiffToolAsync(local, Token), "Uncertain BC wait retains both inputs");
        string[] retained = ViewerArgs();
        Check(File.Exists(retained[2]) && File.Exists(retained[3]), "Both deletion inputs survive uncertain BC ownership");
        string retainedDirectory = Path.GetDirectoryName(retained[2]);
        Check(retainedDirectory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(retainedDirectory).StartsWith("TortoiseSCM-history-"), "Cleanup verifies isolated temporary directory");
        foreach (string path in retained.Skip(2).Take(2)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        Directory.Delete(retainedDirectory);
        Prepare("AD", "");
        using (var cancel = new CancellationTokenSource())
        { cancel.Cancel(); await Reject<OperationCanceledException>(() => client.OpenDiffToolAsync(local, cancel.Token), "Pre-cancel rejects before native read"); }
        Check(!File.Exists(Path.Combine(root, "commands")) && !File.Exists(Path.Combine(root, "viewer.xml")), "Pre-cancel launches no subprocess");
    }
    private static void Prepare(string state, string mode)
    {
        foreach (string file in new[] { "viewer.xml", "commands", "downloads", "downloaded", "opened", "release", "listed" }) File.Delete(Path.Combine(root, file));
        File.WriteAllText(Path.Combine(root, "state"), state); File.WriteAllText(Path.Combine(root, "mode"), mode);
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server\"");
        File.Delete(local);
        if ((state != "DE" && state != "LD" && mode != "missing-file") || mode == "recreated") File.WriteAllBytes(local, LocalBytes);
    }
    private static string[] ViewerArgs() { return XDocument.Load(Path.Combine(root, "viewer.xml")).Root.Elements("arg").Select(item => item.Value).ToArray(); }
    private static int Child(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        root = Environment.GetEnvironmentVariable("TSCM_WORKING_BC_ROOT"); local = Path.Combine(root, "工作 文件 &.bin");
        string mode = File.ReadAllText(Path.Combine(root, "mode")), state = File.ReadAllText(Path.Combine(root, "state"));
        if (args[0] == "/solo")
        {
            var document = new XDocument(new XElement("viewer", args.Select(arg => new XElement("arg", arg)),
                new XElement("left", Convert.ToBase64String(File.ReadAllBytes(args[2]))), new XElement("right", Convert.ToBase64String(File.ReadAllBytes(args[3]))),
                new XElement("leftReadonly", (File.GetAttributes(args[2]) & FileAttributes.ReadOnly) != 0),
                new XElement("rightReadonly", (File.GetAttributes(args[3]) & FileAttributes.ReadOnly) != 0)));
            document.Save(Path.Combine(root, "viewer.xml")); File.WriteAllText(Path.Combine(root, "opened"), "yes");
            if (mode == "wait") for (int i = 0; i < 300 && !File.Exists(Path.Combine(root, "release")); i++) Thread.Sleep(25);
            return mode.StartsWith("exit") ? Int32.Parse(mode.Substring(4)) : 0;
        }
        File.AppendAllText(Path.Combine(root, "commands"), args[0] + "\n");
        bool downloaded = File.Exists(Path.Combine(root, "downloaded")), listed = File.Exists(Path.Combine(root, "listed"));
        if (mode == args[0] + "-fail") return 23;
        if (args[0] == "status")
        {
            string actual = mode == "state-change" && listed ? "PR" : state;
            var document = new XElement("StatusOutput");
            if (actual.Length > 0) document.Add(new XElement("Change", new XElement("Path", local), new XElement("Type", actual), new XElement("RevisionType", "enFile"), new XElement("OldPath", state == "MV" ? Path.Combine(root, "old.bin") : "")));
            if (mode == "duplicate-state") document.Add(new XElement("Change", new XElement("Path", local), new XElement("Type", "CH")));
            if (mode == "parent-move") document.Add(new XElement("Change", new XElement("Path", root), new XElement("Type", "MV"), new XElement("RevisionType", "enDirectory")));
            Console.WriteLine(document); return 0;
        }
        if (args[0] == "fileinfo")
        {
            bool parent = args[1] == root;
            Console.WriteLine(new XElement("FileInfos", new XElement("FileInfo", new XElement("RevisionChangeset", state == "AD" && !parent && mode != "bad-revision" ? -1 : mode == "revision-change" && downloaded ? 2 : 1),
                new XElement("Status", parent ? "controlled" : mode == "bad-status" ? "controlled" : state == "AD" ? "added" : state == "DE" ? "deleted" : state == "MV" ? "moved" : "controlled"),
                new XElement("Hash", mode == "no-hash" ? "" : "original-hash"), new XElement("Type", parent || mode == "info-directory" ? "dir" : "bin"),
                new XElement("IsUnderXlink", mode == "info-xlink" || (parent && mode == "parent-xlink") ? "true" : "false"),
                new XElement("RepSpec", state == "DE" && !parent ? "" : "test@server")))); return 0;
        }
        if (args[0] == "ls")
        {
            bool historical = args.Any(arg => arg.StartsWith("--tree="));
            int id = state == "AD" && !historical && mode != "positive-added-id" ? -11 : 42;
            if (!historical && mode == "identity-change" && listed) id += 1;
            string path = historical ? (args[1] == "/" ? "/original.bin" : args[1]) : "/工作 文件 &.bin";
            var item = new XElement("LsItem", new XElement("CurrentPath", path), new XElement("ItemId", id), new XElement("Changeset", state == "AD" && !historical ? -1 : 1),
                new XElement("Type", mode == "directory" ? "dir" : "bin"), new XElement("Hash", "original-hash"),
                new XElement("SymlinkTarget", mode == "symlink" ? "/other.bin" : ""), new XElement("Repository", mode == "xlink" || mode == "wrong-repository" ? "rep:other@server" : "rep:test@server"));
            var document = new XElement("LsResults");
            if (historical || state != "DE") document.Add(item);
            if (historical && mode == "ambiguous") { var duplicate = new XElement(item); duplicate.SetElementValue("ItemId", 43); document.Add(duplicate); }
            File.WriteAllText(Path.Combine(root, "listed"), "yes"); Console.WriteLine(document); return 0;
        }
        if (args[0] == "cat")
        {
            if (!args[1].StartsWith("serverpath:/original.bin#cs:1@")) return 24;
            string destination = args.Single(arg => arg.StartsWith("--file=")).Substring(7);
            File.AppendAllText(Path.Combine(root, "downloads"), destination + "\n"); File.WriteAllBytes(destination, BaseBytes);
            File.WriteAllText(Path.Combine(root, "downloaded"), "yes");
            if (mode == "selector-change") File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"other@server\"");
            return 0;
        }
        return 25;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static async Task Reject<T>(Func<Task<PlasticCommandResult>> action, string message) where T : Exception
    { bool rejected = false; try { await action(); } catch (T) { rejected = true; } Check(rejected, message); }
}
