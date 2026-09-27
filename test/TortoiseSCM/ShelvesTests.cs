// GPL-2.0-or-later. Shelveset command and strict parser fixtures.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class ShelveTests
{
    private const string Repository = "test@server:8087";
    private static int assertions;
    private static string root;

    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-shelve-test-" + Guid.NewGuid().ToString("N"));
        string metadata = Path.Combine(root, ".plastic");
        Directory.CreateDirectory(metadata);
        try
        {
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "shelve-fixture\nguid\nStandard\n");
            Selector(); SetMode("changed");
            string file = Path.Combine(root, "selected 中文.txt"); File.WriteAllText(file, "local bytes");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(5) });
            var token = CancellationToken.None;

            var shelves = client.GetShelvesAsync(root, token).GetAwaiter().GetResult();
            Check(shelves.Count == 1 && shelves[0].ObjectId == 101 && shelves[0].ShelveId == 4 &&
                shelves[0].ParentChangeset == 22 && shelves[0].Repository == Repository, "Repository shelves parsed with immutable identities");
            Check(shelves[0].Comment == "saved | text\nline two", "XML preserves delimiters and multiline comment");
            var changes = client.GetShelveChangesAsync(root, 4, token).GetAwaiter().GetResult();
            Check(changes.Single().Path == "/selected 中文.txt" && changes.Single().Status == "C", "Shelveset details use strict repository diff parser");
            Check(File.ReadAllLines(Path.Combine(metadata, "calls")).Any(line => line == "diff\tsh:4@" + Repository +
                "\t--repositorypaths\t--encoding=utf-8\t--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}"),
                "Details qualify shelveset with captured repository and use literal argument array");
            Reject<ArgumentException>(() => client.GetShelveChangesAsync(root, 99, token).GetAwaiter().GetResult(), "Missing shelveset rejected before diff");
            Reject<ArgumentOutOfRangeException>(() => client.GetShelveChangesAsync(root, -1, token).GetAwaiter().GetResult(), "Negative shelveset rejected");
            string child = Path.Combine(root, "child"); Directory.CreateDirectory(child);
            Reject<ArgumentException>(() => client.GetShelvesAsync(child, token).GetAwaiter().GetResult(), "Listing requires explicit root");

            SetMode("changed"); byte[] before = File.ReadAllBytes(file);
            var created = client.CreateShelveAsync(root, new[] { file }, "literal \"quoted\" | 中文\nsecond", token).GetAwaiter().GetResult();
            Check(created.Succeeded && File.ReadAllBytes(file).SequenceEqual(before), "Standard create succeeds and preserves local content: " + created.Error);
            string mutation = File.ReadAllLines(Path.Combine(metadata, "calls")).Single(line => line.StartsWith("shelveset\tcreate\t"));
            Check(mutation == "shelveset\tcreate\t" + file + "\t--all\t-c=literal \"quoted\" | 中文\nsecond".Replace("\n", "\\n"),
                "Standard create sends only explicit file, --all and literal noninteractive comment");

            File.Delete(Path.Combine(metadata, "created")); SetMode("partial");
            Check(client.CreateShelveAsync(root, new[] { file }, "partial exact", token).GetAwaiter().GetResult().Succeeded,
                "Partial create succeeds through native Partial command");
            mutation = File.ReadAllLines(Path.Combine(metadata, "calls")).Single(line => line.StartsWith("partial\tshelveset\tcreate\t"));
            Check(mutation == "partial\tshelveset\tcreate\t" + file + "\t--applychanged\t-c=partial exact",
                "Partial create uses --applychanged without ignorefailed or dependencies");

            foreach (string bad in new[] { "", " ", "bad\0comment", "bad\u0001comment" })
                Reject<ArgumentException>(() => client.CreateShelveAsync(root, new[] { file }, bad, token).GetAwaiter().GetResult(), "Invalid comment rejected before mutation");
            SetMode("private"); Reject<ArgumentException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult(), "Private file refused");
            SetMode("ignored"); Reject<ArgumentException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult(), "Ignored file refused");
            SetMode("none"); Reject<ArgumentException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult(), "Nonpending file refused");
            SetMode("structural"); Reject<ArgumentException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult(), "Pending directory dependency refused before server mutation");
            SetMode("changed"); Reject<ArgumentException>(() => client.CreateShelveAsync(root, new[] { root }, "reason", token).GetAwaiter().GetResult(), "Recursive directory scope refused");
            Reject<ArgumentException>(() => client.CreateShelveAsync(root, new[] { file, file }, "reason", token).GetAwaiter().GetResult(), "Duplicate selection refused");
            Reject<InvalidOperationException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", "other@server:8087", File.ReadAllText(Path.Combine(metadata, "plastic.selector")), token).GetAwaiter().GetResult(), "Captured repository mismatch refused");
            string merge = Path.Combine(metadata, "plastic.mergeprogress"); File.WriteAllText(merge, "active");
            Reject<ArgumentException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult(), "Native merge session blocks create"); File.Delete(merge);
            var mergeGate = typeof(PlasticClient).GetMethod("OpenMergeGate", BindingFlags.Instance | BindingFlags.NonPublic);
            using ((IDisposable)mergeGate.Invoke(client, new object[] { root }))
                Reject<InvalidOperationException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult(), "Concurrent merge mutation gate blocks create");
            var structureGate = typeof(PlasticClient).GetMethod("StructureGate", BindingFlags.Static | BindingFlags.NonPublic);
            using ((IDisposable)structureGate.Invoke(null, new object[] { root }))
                Reject<InvalidOperationException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult(), "Concurrent structure mutation gate blocks create");
            SetMode("race");
            Reject<InvalidOperationException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult(), "Selector race before native create is rejected");
            Check(!File.Exists(Path.Combine(metadata, "created")), "Selector race starts no server mutation"); Selector();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Reject<OperationCanceledException>(() => client.CreateShelveAsync(root, new[] { file }, "reason", cancelled.Token).GetAwaiter().GetResult(), "Cancellation starts no mutation");
            }
            SetMode("create-no-result");
            var unverified = client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult();
            Check(!unverified.Succeeded && unverified.Error.Contains("may already exist"), "Native zero exit without list result is not success");
            SetMode("create-fail");
            var failed = client.CreateShelveAsync(root, new[] { file }, "reason", token).GetAwaiter().GetResult();
            Check(!failed.Succeeded && failed.Error.Contains("may already exist"), "Native create failure keeps server uncertainty advisory");

            string xml = ShelvesXml(false).ToString();
            Check(PlasticClient.ParseShelves("<PLASTICQUERY/>", Repository).Count == 0, "Empty repository list supported");
            foreach (string bad in new[] { "<other/>", xml.Replace("<SHELVEID>4</SHELVEID>", "<SHELVEID>-1</SHELVEID>"),
                xml.Replace("server:8087", "foreign:8087"), xml.Replace("<OWNER>tester</OWNER>", "<OWNER></OWNER>"),
                xml.Replace("</PLASTICQUERY>", ShelveRow(101, 4, "duplicate").ToString() + "</PLASTICQUERY>"),
                xml.Replace("</PLASTICQUERY>", "<WARNING>truncated</WARNING></PLASTICQUERY>") })
                Reject<InvalidDataException>(() => PlasticClient.ParseShelves(bad, Repository), "Malformed, duplicate or foreign shelves response rejected");
            Console.WriteLine("PASS: " + assertions + " shelveset assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void Selector()
    { File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"/main\"\n"); }
    private static void SetMode(string mode)
    {
        File.WriteAllText(Path.Combine(root, ".plastic", "mode"), mode);
        foreach (string name in new[] { "calls", "created", "created-comment", "status-count" }) { string path = Path.Combine(root, ".plastic", name); if (File.Exists(path)) File.Delete(path); }
    }
    private static XElement ShelvesXml(bool includeCreated)
    {
        var xml = new XElement("PLASTICQUERY", ShelveRow(101, 4, "saved | text\nline two"));
        if (includeCreated) xml.Add(ShelveRow(102, 5, File.ReadAllText(Path.Combine(Environment.CurrentDirectory, ".plastic", "created-comment"))));
        return xml;
    }
    private static XElement ShelveRow(long objectId, long shelveId, string comment)
    {
        return new XElement("SHELVE", new XElement("ID", objectId), new XElement("SHELVEID", shelveId), new XElement("COMMENT", comment),
            new XElement("DATE", "2026-09-27T12:00:00+08:00"), new XElement("OWNER", "tester"), new XElement("REPOSITORY", "test"),
            new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"), new XElement("PARENT", "22"), new XElement("GUID", Guid.NewGuid()));
    }
    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        string calls = Path.Combine(metadata, "calls");
        string rendered = String.Join("\t", args).Replace("\r", "\\r").Replace("\n", "\\n");
        File.AppendAllText(calls, rendered + Environment.NewLine);
        string mode = File.ReadAllText(Path.Combine(metadata, "mode"));
        if (args[0] == "status")
        {
            string code = mode == "private" ? "PR" : mode == "ignored" ? "IG" : mode == "none" ? "" : "CH";
            var output = new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", mode == "partial" ? -1 : 22),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))),
                new XElement("WkConfigType", "branch"), new XElement("WkConfigName", "/main@" + Repository));
            if (!args.Contains("--header") && code.Length != 0)
            {
                string countPath = Path.Combine(metadata, "status-count");
                int statusCount = File.Exists(countPath) ? Int32.Parse(File.ReadAllText(countPath)) : 0;
                File.WriteAllText(countPath, (statusCount + 1).ToString());
                if (mode == "race" && statusCount > 0) File.AppendAllText(Path.Combine(metadata, "plastic.selector"), "\n# changed during prepare\n");
                var changes = new XElement("Changes");
                if (mode == "structural") changes.Add(new XElement("Change", new XElement("Path", Environment.CurrentDirectory),
                    new XElement("Type", "AD"), new XElement("TypeVerbose", "AD"), new XElement("RevisionType", "Directory")));
                changes.Add(new XElement("Change", new XElement("Path", Path.Combine(Environment.CurrentDirectory, "selected 中文.txt")),
                    new XElement("Type", code), new XElement("TypeVerbose", code), new XElement("RevisionType", "File")));
                output.Add(changes);
            }
            Console.WriteLine(output); return 0;
        }
        if (args[0] == "find" && args[1] == "shelve")
        {
            bool include = File.Exists(Path.Combine(metadata, "created")) && mode != "create-no-result";
            Console.WriteLine(ShelvesXml(include)); return 0;
        }
        if (args[0] == "diff")
        { Console.WriteLine("C|\"/selected 中文.txt\"|F|\"\"|\"\""); return 0; }
        bool standard = args.Length > 1 && args[0] == "shelveset" && args[1] == "create";
        bool partial = args.Length > 2 && args[0] == "partial" && args[1] == "shelveset" && args[2] == "create";
        if (standard || partial)
        {
            string comment = args.Single(value => value.StartsWith("-c=", StringComparison.Ordinal)).Substring(3);
            File.WriteAllText(Path.Combine(metadata, "created-comment"), comment);
            File.WriteAllText(Path.Combine(metadata, "created"), "server object may exist");
            if (mode == "create-fail") { Console.Error.WriteLine("server failed after upload"); return 7; }
            Console.WriteLine("created"); return 0;
        }
        Console.Error.WriteLine("Unexpected fake command: " + String.Join(" ", args)); return 90;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
}
