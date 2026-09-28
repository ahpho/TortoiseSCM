// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class NativeHistoryPreviewTests
{
    private static int assertions;
    private const string Repository = "test@server:8087";
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] != "--real") return Fake(args);
        if (args.Length > 0) return Real(args[1], args[2]);
        string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-native-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "preview\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"");
            string path = Path.Combine(root, "folder"); Directory.CreateDirectory(path);
            var parsed = PlasticClient.ParseNativeHistoryPreview(Xml(path, 1, 2, 3).ToString(), path, Repository);
            Check(parsed.Select(i => i.Changeset).SequenceEqual(new long[] { 3, 2, 1 }), "Native ascending results normalized newest first");
            Check(parsed.All(i => i.Repository == Repository && i.Path == path && i.RevisionSpec == "cs:" + i.Changeset), "Preview carries trusted repository and publication spec");
            Check(PlasticClient.ParseNativeHistoryPreview(Xml(path, Enumerable.Range(1, 20).ToArray()).ToString(), path, Repository).Count == 10, "Ignored native limit still bounds verification to ten");
            RejectXml(Xml(path, 1, 1), path, "Duplicate changeset rejected");
            RejectXml(Xml(path + "ish", 1), path, "Different item rejected");
            var xml = Xml(path, 1); xml.Descendants("Repository").Single().Value = "foreign"; RejectXml(xml, path, "Foreign repository rejected");
            xml = Xml(path, 1); xml.Descendants("RepositorySpec").Single().Element("Server").Value = "foreign"; RejectXml(xml, path, "Inconsistent repository spec rejected");
            xml = Xml(path, 1); xml.Descendants("ChangesetNumber").Single().Value = "-1"; RejectXml(xml, path, "Negative changeset rejected");
            xml = Xml(path, 1); xml.Descendants("ChangesetNumber").Single().Value = "999999999999999999999"; RejectXml(xml, path, "Overflow changeset rejected");
            xml = Xml(path, 1); xml.Descendants("RevisionSpec").Single().Value = "file#cs:2"; RejectXml(xml, path, "Revision mismatch rejected");
            xml = Xml(path, 1); xml.Descendants("Owner").Single().Remove(); RejectXml(xml, path, "Missing metadata rejected");
            xml = Xml(path, 1); xml.Descendants("Comment").Single().AddAfterSelf(new XElement("Comment", "ambiguous")); RejectXml(xml, path, "Duplicate metadata rejected");
            xml = Xml(path, 1); xml.Descendants("CreationDate").Single().Value = "invalid"; RejectXml(xml, path, "Bad date rejected");
            xml = Xml(path, 1); xml.Descendants("Branch").Single().Value = "/main@foreign"; RejectXml(xml, path, "Foreign branch syntax rejected");
            xml = Xml(path, 1); xml.Descendants("Revisions").Single().Add(new XElement("Unknown")); RejectXml(xml, path, "Unknown revision list member rejected");
            xml = Xml(path, 1); xml.Descendants("Comment").Single().Value = new string('x', 4 * 1024 * 1024); RejectXml(xml, path, "Oversized preview XML rejected before synchronous parsing");
            var page = Client().GetNativeHistoryPreviewAsync(path, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(page.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 3, 1 }), "Candidate foreign old path rejected by actual publication paths");
            Check(page.HasMore && page.ScannedChangesets == 0 && page.NextBeforeChangeset == null, "Preview can never mark publication scan complete or move its cursor");
            Check(File.ReadAllLines(Log(root)).First().Contains("--limit=10"), "Native command asks for bounded latest revisions");
            page = Client().GetNativeHistoryPreviewAsync(path, "/main/topic", CancellationToken.None).GetAwaiter().GetResult();
            Check(page.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 1 }), "Branch filter intersects actual publication path");
            bool rejected = false;
            try { Client().GetNativeHistoryPreviewAsync(path, "/missing", CancellationToken.None).GetAwaiter().GetResult(); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Unknown branch fails explicitly");
            int calls = File.ReadAllLines(Log(root)).Length;
            Check(Client().GetNativeHistoryPreviewAsync(root, null, CancellationToken.None).GetAwaiter().GetResult().Items.Count == 0, "Root does not perform redundant native preview");
            Check(Client().GetNativeHistoryPreviewAsync(Path.Combine(root, "deleted"), null, CancellationToken.None).GetAwaiter().GetResult().HasMore, "Missing path defers to complete publication scan");
            Check(File.ReadAllLines(Log(root)).Length == calls, "Root and absent path skip all native commands");
            Mode(root, "unsupported");
            Check(Client().GetNativeHistoryPreviewAsync(path, null, CancellationToken.None).GetAwaiter().GetResult().HasMore, "Unsupported native command falls back without false completion");
            Mode(root, "malformed");
            Check(Client().GetNativeHistoryPreviewAsync(path, null, CancellationToken.None).GetAwaiter().GetResult().Items.Count == 0, "Malformed native XML yields no speculative rows");
            Mode(root, "slow-history"); var watch = Stopwatch.StartNew();
            page = Client().GetNativeHistoryPreviewAsync(path, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(page.Items.Count == 0 && page.HasMore && watch.Elapsed.TotalSeconds < 6, "Native preview timeout promptly allows full scan");
            Mode(root, "slow-diff"); watch.Restart();
            page = Client().GetNativeHistoryPreviewAsync(path, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(page.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 3 }) && watch.Elapsed.TotalSeconds < 6, "Budget preserves completed validated rows only");
            Mode(root, "slow-history");
            using (var cancel = new CancellationTokenSource(250))
            {
                bool cancelled = false;
                try { Client().GetNativeHistoryPreviewAsync(path, null, cancel.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "User cancellation propagates rather than becoming fallback");
            }
            Mode(root, "repository-changed"); rejected = false;
            try { Client().GetNativeHistoryPreviewAsync(path, null, CancellationToken.None).GetAwaiter().GetResult(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Repository change during preview fails closed");
            Console.WriteLine("PASS: " + assertions + " native history preview assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static PlasticClient Client() { return new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(20) }); }
    private static string Log(string root) { return Path.Combine(root, ".plastic", "preview-calls.log"); }
    private static void Mode(string root, string value) { File.WriteAllText(Path.Combine(root, ".plastic", "preview-mode"), value); }
    private static XElement Xml(string path, params int[] numbers)
    {
        return new XElement("RevisionHistoriesResult", new XElement("RevisionHistories", new XElement("RevisionHistory", new XElement("ItemName", path),
            new XElement("Revisions", numbers.Select(i => new XElement("Revision", new XElement("ChangesetNumber", i), new XElement("RevisionSpec", "folder#cs:" + i),
                new XElement("Repository", "test"), new XElement("Server", "server:8087"), new XElement("RepositorySpec", new XElement("Name", "test"), new XElement("Server", "server:8087")),
                new XElement("Branch", i == 1 ? "/main/topic" : "/main"), new XElement("CreationDate", "2026-09-29T01:00:00+08:00"),
                new XElement("Owner", "tester"), new XElement("Comment", "")))))));
    }
    private static int Fake(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string root = Environment.CurrentDirectory;
        File.AppendAllText(Log(root), String.Join(" ", args) + "\n");
        string modePath = Path.Combine(root, ".plastic", "preview-mode"), mode = File.Exists(modePath) ? File.ReadAllText(modePath) : "";
        if (args[0] == "status")
        {
            Console.WriteLine(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", 3),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))), new XElement("WkConfigName", "/main@test@server:8087"))); return 0;
        }
        if (args[0] == "find")
        {
            Console.WriteLine(new XElement("PLASTICQUERY", new[] { "/main", "/main/topic" }.Select(branch => new XElement("BRANCH", new XElement("NAME", branch),
                new XElement("PARENT", branch == "/main" ? "" : "/main"), new XElement("CHANGESET", 3), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"))))); return 0;
        }
        if (args[0] == "history")
        {
            if (mode == "unsupported") return 2;
            if (mode == "slow-history") Thread.Sleep(10000);
            if (mode == "malformed") { Console.WriteLine("<broken>"); return 0; }
            Console.WriteLine(Xml(args[1], 1, 2, 3)); return 0;
        }
        if (args[0] == "diff")
        {
            string changeset = args[1];
            int repositoryStart = changeset.IndexOf('@');
            if (repositoryStart >= 0)
            {
                if (changeset.Substring(repositoryStart + 1) != Repository) return 98;
                changeset = changeset.Substring(0, repositoryStart);
            }
            if (mode == "slow-diff" && changeset == "cs:2") Thread.Sleep(10000);
            if (mode == "repository-changed") File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"other@server:8087\"");
            Console.WriteLine(changeset == "cs:2" ? "C|/old-folder/file.txt|F||" : "C|/folder/file.txt|F||"); return 0;
        }
        return 99;
    }
    private static int Real(string cm, string path)
    {
        try
        {
            var watch = Stopwatch.StartNew();
            var result = new PlasticClient(new PlasticClientConfig { CmPath = cm }).GetNativeHistoryPreviewAsync(path, null, CancellationToken.None).GetAwaiter().GetResult();
            Check(result.Items.Select(i => i.Changeset).SequenceEqual(new long[] { 886, 885, 883, 882 }), "Real native preview matches expected verified directory publications");
            Check(result.HasMore && result.ScannedChangesets == 0 && !result.NextBeforeChangeset.HasValue, "Real native preview still requires full publication scan");
            Console.WriteLine("PASS: real read-only preview [" + String.Join(",", result.Items.Select(i => i.Changeset)) + "] in " + watch.Elapsed.TotalSeconds.ToString("F3") + " seconds"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void RejectXml(XElement xml, string path, string message)
    {
        bool rejected = false; try { PlasticClient.ParseNativeHistoryPreview(xml.ToString(), path, Repository); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, message);
    }
}
