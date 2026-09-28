// GPL-2.0-or-later. Real subprocess contract tests, using an isolated fake cm/BComp.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class ShelveBeyondCompareTests
{
    private static int assertions;
    private static string root;
    private const string Selector = "repository \"repo@server\"";
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Child(args);
        root = Path.Combine(Path.GetTempPath(), "TSCM-shelve-bc-" + Guid.NewGuid().ToString("N") + " 中文 &");
        Directory.CreateDirectory(root);
        try { Run(); Console.WriteLine("PASS: " + assertions + " shelveset Beyond Compare assertions"); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Environment.SetEnvironmentVariable("TSCM_SHELVE_BC_ROOT", null); Directory.Delete(root, true); }
    }
    private static void Run()
    {
        string cm = Assembly.GetExecutingAssembly().Location, bc = Path.Combine(root, "BComp.exe");
        File.Copy(cm, bc); Environment.SetEnvironmentVariable("TSCM_SHELVE_BC_ROOT", root);
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        var config = new PlasticClientConfig { CmPath = cm, UseBeyondCompare = true, BeyondComparePath = bc, Timeout = TimeSpan.FromSeconds(10) };
        var client = new PlasticClient(config);
        foreach (string path in new[] { "/changed.txt", "/added.txt", "/deleted.txt", "/renamed 中文 &.txt", "/binary.dat" })
        {
            Reset("");
            Check(Open(client, path).Succeeded, "Open " + path);
            var argv = Arguments();
            Check(argv[0] == "/solo" && argv[1] == "/readonly", "Independent read-only session " + path);
            Check(File.ReadAllText(Path.Combine(root, "readonly")) == "True", "Both input attributes read-only " + path);
            byte[] before = File.ReadAllBytes(Path.Combine(root, "left.bin")), after = File.ReadAllBytes(Path.Combine(root, "right.bin"));
            Check(before.SequenceEqual(path == "/added.txt" ? new byte[0] : ParentBytes(path)), "Exact parent bytes " + path);
            Check(after.SequenceEqual(path == "/deleted.txt" ? new byte[0] : ShelveBytes(path)), "Exact shelved bytes " + path);
            Check(!Directory.Exists(Path.GetDirectoryName(argv[2])) && !File.Exists(argv[3]), "Only this session's temp directory removed " + path);
            Check(argv.Any(a => a.StartsWith("/lefttitle=Parent cs:4 ")) && argv.Any(a => a.StartsWith("/righttitle=Shelve sh:7 " + path)), "Explicit revision/path titles " + path);
            if (path == "/added.txt") Check(argv.Any(a => a.Contains("empty: added")), "Added side label explains empty input");
            if (path == "/deleted.txt") Check(argv.Any(a => a.Contains("empty: deleted")), "Deleted side label explains empty input");
            if (path.Contains("renamed")) Check(File.ReadAllText(Path.Combine(root, "cats")).Contains("serverpath:/original.txt#cs:4@repo@server"), "M+C resolves exact old path");
        }
        foreach (string path in new[] { "/folder", "/link", "/xlink", "/Changed.txt", "/missing.txt", "/../bad.txt", "/.plastic/bad", "/bad#sh:9" })
        { Reset(""); Reject(() => Open(client, path), "Unsafe, missing, case-mismatched or unsupported path " + path); }
        foreach (string mode in new[] { "duplicate", "conflicting-moves", "selector", "workspace", "object", "parent", "details", "removed", "cat-failed", "parent-link", "parent-foreign" })
        { Reset(mode); Reject(() => Open(client, mode == "conflicting-moves" ? "/renamed 中文 &.txt" : "/changed.txt"), "Reject changed or invalid preparation: " + mode); }
        Reset(""); Reject(() => client.OpenShelveDiffToolAsync(root, 7, "/changed.txt", "other@server", Selector, CancellationToken.None).GetAwaiter().GetResult(), "Expected repository mismatch");
        Reset(""); Reject(() => client.OpenShelveDiffToolAsync(root, 7, "/changed.txt", "repo@server", Selector + "\nbranch \"/other\"", CancellationToken.None).GetAwaiter().GetResult(), "Expected selector mismatch");
        Reset(""); Reject(() => client.OpenShelveDiffToolAsync(root, -1, "/changed.txt", CancellationToken.None).GetAwaiter().GetResult(), "Negative shelveset ID");
        Reset(""); Reject(() => client.OpenShelveDiffToolAsync(root, 7, "/changed.txt", new CancellationToken(true)).GetAwaiter().GetResult(), "Pre-cancel never starts BC");
        Reset("exit17"); var failed = Open(client, "/changed.txt");
        Check(!failed.Succeeded && failed.ExitCode == 17 && failed.Error.Length != 0, "BC failure remains explicit");
        Check(!Directory.Exists(Path.GetDirectoryName(Arguments()[2])), "Known nonzero BC exit cleans both inputs");
        Reset("exit102"); bool uncertain = false;
        try { Open(client, "/changed.txt"); } catch (InvalidOperationException ex) { uncertain = ex.Message.Contains("102"); }
        var retained = Arguments();
        Check(uncertain && File.Exists(retained[2]) && File.Exists(retained[3]), "Uncertain editor lifetime retains both inputs");
        string directory = Path.GetDirectoryName(retained[2]);
        Check(Path.GetDirectoryName(retained[3]) == directory && directory.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(directory).StartsWith("TortoiseSCM-history-"), "Retained cleanup is isolated");
        foreach (string path in retained.Skip(2).Take(2)) { File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); }
        Directory.Delete(directory);
    }
    private static PlasticCommandResult Open(PlasticClient client, string path)
    { return client.OpenShelveDiffToolAsync(root, 7, path, "repo@server", Selector, CancellationToken.None).GetAwaiter().GetResult(); }
    private static string[] Arguments()
    { return XDocument.Load(Path.Combine(root, "argv.xml")).Root.Elements("arg").Select(x => x.Value).ToArray(); }
    private static void Reset(string mode)
    {
        File.WriteAllText(Path.Combine(root, "mode"), mode);
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "shelve\nguid\nStandard\n");
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), Selector);
        foreach (string name in new[] { "opened", "downloaded", "cats", "argv.xml" }) File.Delete(Path.Combine(root, name));
    }
    private static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; } catch (IOException) { rejected = true; } catch (InvalidDataException) { rejected = true; } catch (OperationCanceledException) { rejected = true; } catch (PlasticCommandException) { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(root, "opened")), message);
    }
    private static byte[] ParentBytes(string path)
    { return path == "/binary.dat" ? new byte[] { 0, 255, 13, 10, 128 } : System.Text.Encoding.UTF8.GetBytes("parent\r\n"); }
    private static byte[] ShelveBytes(string path)
    { return path == "/binary.dat" ? new byte[] { 255, 0, 10, 128, 127 } : System.Text.Encoding.UTF8.GetBytes(path == "/added.txt" ? "added\n" : "shelved\n"); }
    private static int Child(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        root = Environment.GetEnvironmentVariable("TSCM_SHELVE_BC_ROOT");
        string mode = File.ReadAllText(Path.Combine(root, "mode"));
        bool downloaded = File.Exists(Path.Combine(root, "downloaded"));
        if (args[0] == "/solo")
        {
            new XDocument(new XElement("args", args.Select(a => new XElement("arg", a)))).Save(Path.Combine(root, "argv.xml"));
            File.WriteAllText(Path.Combine(root, "opened"), "yes");
            File.WriteAllBytes(Path.Combine(root, "left.bin"), File.ReadAllBytes(args[2]));
            File.WriteAllBytes(Path.Combine(root, "right.bin"), File.ReadAllBytes(args[3]));
            File.WriteAllText(Path.Combine(root, "readonly"), args.Skip(2).Take(2).All(p => (File.GetAttributes(p) & FileAttributes.ReadOnly) != 0).ToString());
            return mode.StartsWith("exit") ? Int32.Parse(mode.Substring(4)) : 0;
        }
        if (args[0] == "status") { Console.WriteLine("<StatusOutput><WorkspaceStatus><Status><Changeset>4</Changeset></Status></WorkspaceStatus></StatusOutput>"); return 0; }
        if (args[0] == "ls")
        {
            Console.WriteLine(new XElement("LsResults", new XElement("LsItem", new XElement("Name", "file.txt"), new XElement("CurrentPath", args[1]),
                new XElement("Type", "txt"), new XElement("Repository", mode == "parent-foreign" ? "rep:other@server" : "rep:repo@server"),
                new XElement("SymlinkTarget", mode == "parent-link" ? "/elsewhere" : "")))); return 0;
        }
        if (args[0] == "find" && args[1] == "shelve")
        {
            if (downloaded && mode == "removed") { Console.WriteLine("<PLASTICQUERY />"); return 0; }
            Console.WriteLine("<PLASTICQUERY><SHELVE><ID>" + (downloaded && mode == "object" ? 2 : 1) + "</ID><SHELVEID>7</SHELVEID><PARENT>" +
                (downloaded && mode == "parent" ? 5 : 4) + "</PARENT><OWNER>tester</OWNER><DATE>2026-09-28</DATE><COMMENT>fixture</COMMENT><REPNAME>repo</REPNAME><REPSERVER>server</REPSERVER><REPOSITORY>repo</REPOSITORY></SHELVE></PLASTICQUERY>"); return 0;
        }
        if (args[0] == "diff")
        {
            Console.WriteLine((downloaded && mode == "details" ? "A" : "C") + "|/changed.txt|F||\nA|/added.txt|F||\nD|/deleted.txt|F||\nC|/folder|D||\nC|/link|S||\nC|/xlink|X||\nC|/binary.dat|B||\nM|/original.txt|F|/original.txt|/renamed 中文 &.txt\nC|/renamed 中文 &.txt|F||");
            if (mode == "duplicate") Console.WriteLine("C|/changed.txt|F||");
            if (mode == "conflicting-moves") Console.WriteLine("M|/other.txt|F|/other.txt|/renamed 中文 &.txt");
            return 0;
        }
        if (args[0] == "cat")
        {
            File.AppendAllText(Path.Combine(root, "cats"), args[1] + "\n");
            if (mode == "cat-failed") return 19;
            string path = args[1].Substring("serverpath:".Length).Split('#')[0];
            if (path.StartsWith("/renamed") && args[1].Contains("#cs:")) return 20;
            File.WriteAllBytes(args.Single(a => a.StartsWith("--file=")).Substring(7), args[1].Contains("#sh:7@") ? ShelveBytes(path) : ParentBytes(path));
            File.WriteAllText(Path.Combine(root, "downloaded"), "yes");
            if (mode == "selector") File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"other@server\"");
            if (mode == "workspace") File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "changed\nguid\nStandard\n");
            return 0;
        }
        return 21;
    }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
}
