// GPL-2.0-or-later. Label metadata and guarded publication/deletion tests.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class LabelTests
{
    private const string Repository = "test@server:8087";
    private const string LabelName = "release 中文 & O'Brien";
    private static string root, metadata;
    private static int assertions;
    private static PlasticClient client;
    private static readonly CancellationToken Token = CancellationToken.None;

    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-label-test-" + Guid.NewGuid().ToString("N"));
        metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
        try
        {
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "label-test\nguid\nStandard\n");
            WriteSelector(); SetRows();
            client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(5) });
            Check(client.GetLabelsAsync(root, Token).GetAwaiter().GetResult().Count == 0, "Empty valid label query");
            Check(Calls().Contains("find\tlabel\ton repository '" + Repository + "'\t--xml\t--encoding=utf-8\t--nototal"), "Query pins captured repository without name expression");
            SetRows(Row(7, LabelName, 17));
            var label = client.ResolveLabelAsync(root, LabelName, Repository, Token).GetAwaiter().GetResult();
            Check(label.Id == 7 && label.Name == LabelName && label.Changeset == 17 && label.Repository == Repository && label.Comment == "line one\nline two", "Exact label metadata decoded");
            Reject<ArgumentException>(() => client.ResolveLabelAsync(root, "release", Token).GetAwaiter().GetResult(), "Resolve is exact, never partial");
            Reject<InvalidOperationException>(() => client.GetLabelsAsync(root, "other@server:8087", Token).GetAwaiter().GetResult(), "Expected repository mismatch rejected");
            Reject<OperationCanceledException>(() => client.GetLabelsAsync(root, new CancellationToken(true)).GetAwaiter().GetResult(), "Canceled read rejected");
            foreach (string name in new[] { "", " ", "a\nb", "a\0b", "a\tb" })
                Reject<ArgumentException>(() => client.ResolveLabelAsync(root, name, Token).GetAwaiter().GetResult(), "Unsafe label name rejected");
            foreach (string name in new[] { " a", "a ", "-a", "a@other", "a#cs:4", "a:b", "a/b", "a\\b", "a\"b" })
            {
                SetRows(Row(7, name, 17), Row(8, LabelName, 17)); ClearCalls();
                Check(client.GetLabelsAsync(root, Token).GetAwaiter().GetResult().Count == 2, "Action-unsupported name does not hide repository labels");
                Check(client.ResolveLabelAsync(root, name, Token).GetAwaiter().GetResult().Name == name, "Read-only exact resolution preserves native name verbatim");
                Check(!Calls().Contains(name), "Read-only name never interpolated into native command");
                Reject<ArgumentException>(() => Create(name, 17, "comment"), "Unsupported create name still rejected");
                Reject<ArgumentException>(() => Delete(name, 7, 17), "Unsupported delete name still rejected");
            }
            SetRows(Row(7, "Release", 17), Row(8, "release", 18));
            Check(client.GetLabelsAsync(root, Token).GetAwaiter().GetResult().Count == 2, "Case-only distinct names remain readable");
            Check(client.ResolveLabelAsync(root, "Release", Token).GetAwaiter().GetResult().Id == 7 && client.ResolveLabelAsync(root, "release", Token).GetAwaiter().GetResult().Id == 8, "Case-only names resolve exact distinct identities");
            Reject<ArgumentException>(() => Create("RELEASE", 17, "comment"), "Case-insensitive creation collision still refused");
            SetRows(Row(7, LabelName, 17));
            Reject<InvalidDataException>(() => PlasticClient.ParseLabels("<wrong/>", Repository), "Unexpected query root rejected");
            Reject<InvalidDataException>(() => PlasticClient.ParseLabels("<PLASTICQUERY><BRANCH/></PLASTICQUERY>", Repository), "Unexpected object type rejected");
            Reject<InvalidDataException>(() => PlasticClient.ParseLabels(Wrap(Row(7, "a", 17), Row(7, "b", 17)), Repository), "Duplicate IDs rejected");
            Reject<InvalidDataException>(() => PlasticClient.ParseLabels(Wrap(Row(7, "a", 17), Row(8, "a", 17)), Repository), "Exact duplicate names rejected");
            foreach (string field in new[] { "ID", "NAME", "CHANGESET", "DATE", "OWNER", "COMMENT", "REPNAME", "REPSERVER", "REPOSITORY", "BRANCH" })
            {
                var row = Row(7, "a", 17); row.Element(field).Remove();
                Reject<InvalidDataException>(() => PlasticClient.ParseLabels(Wrap(row), Repository), "Missing field rejected: " + field);
                row = Row(7, "a", 17); row.Add(new XElement(row.Element(field)));
                Reject<InvalidDataException>(() => PlasticClient.ParseLabels(Wrap(row), Repository), "Duplicate field rejected: " + field);
            }
            foreach (var change in new[] { new[] { "ID", "0" }, new[] { "ID", "-1" }, new[] { "CHANGESET", "-1" }, new[] { "CHANGESET", "cs:17" },
                new[] { "DATE", "invalid" }, new[] { "REPOSITORY", "other" }, new[] { "REPNAME", "other" }, new[] { "REPSERVER", "other:8087" }, new[] { "NAME", "a\nb" } })
            {
                var row = Row(7, "a", 17); row.Element(change[0]).Value = change[1];
                Reject<InvalidDataException>(() => PlasticClient.ParseLabels(Wrap(row), Repository), "Malformed metadata rejected");
            }
            Mode("find-fail"); Reject<PlasticCommandException>(() => client.GetLabelsAsync(root, Token).GetAwaiter().GetResult(), "Native failure remains failure");
            Mode("find-switch"); Reject<InvalidOperationException>(() => client.GetLabelsAsync(root, Token).GetAwaiter().GetResult(), "Selector changed during list rejected");
            WriteSelector(); Mode("");
            File.WriteAllText(Path.Combine(metadata, "plastic.fullupdate"), "");
            Check(client.ResolveLabelAsync(root, LabelName, Token).GetAwaiter().GetResult().Id == 7, "Partial workspace uses repository metadata");
            File.Delete(Path.Combine(metadata, "plastic.fullupdate"));
            var details = client.GetLabelChangesetAsync(root, 17, Repository, Token).GetAwaiter().GetResult();
            Check(details.Changeset.Repository == Repository && details.Changeset.RevisionSpec == "cs:17@" + Repository && details.Files.Single().Path == "/file.txt", "Label details pin metadata and file list to captured target");
            Check(Calls().Contains("log\tcs:17@" + Repository) && Calls().Contains("diff\tcs:17@" + Repository), "Both details commands use explicit repository");
            Mode("log-switch"); Reject<InvalidOperationException>(() => client.GetLabelChangesetAsync(root, 17, Repository, Token).GetAwaiter().GetResult(), "Details reject selector switch during metadata");
            WriteSelector(); Mode("diff-switch"); Reject<InvalidOperationException>(() => client.GetLabelChangesetAsync(root, 17, Repository, Token).GetAwaiter().GetResult(), "Details reject repository switch during file list");
            WriteSelector(); Mode("diff-unsafe"); Reject<ArgumentException>(() => client.GetLabelChangesetAsync(root, 17, Repository, Token).GetAwaiter().GetResult(), "Details reject unsafe repository paths");
            Mode("");
            ClearCalls();
            Reject<ArgumentException>(() => Create(LabelName, 17, "comment"), "Existing label cannot be reapplied");
            Reject<ArgumentException>(() => Create(LabelName.ToUpperInvariant(), 17, "comment"), "Case-conflicting existing label cannot be reapplied");
            Check(!Calls().Contains("label\tcreate"), "Duplicate rejection sends no mutation");
            Reject<ArgumentException>(() => Create("new", 17, " "), "Empty comments cannot start editor");
            Reject<ArgumentException>(() => Create("new", 17, "x\0y"), "Invalid comments refused");
            Reject<ArgumentOutOfRangeException>(() => Create("new", -1, "comment"), "Negative source changeset refused");
            SetRows(); ClearCalls(); Mode("log-wrong");
            Reject<InvalidDataException>(() => Create("new", 17, "comment"), "Wrong log target rejected");
            Check(!Calls().Contains("label\tcreate"), "Wrong source sends no mutation");
            ClearCalls(); Mode("log-switch");
            Reject<InvalidOperationException>(() => Create("new", 17, "comment"), "Selector change before creation rejected");
            Check(!Calls().Contains("label\tcreate"), "Selector change sends no mutation"); WriteSelector(); Mode("");
            var created = Create(LabelName, 17, "line one\r\nline two");
            Check(created.Succeeded && created.Output.Contains(LabelName), "Safe staged create succeeds including multiline comments");
            label = client.ResolveLabelAsync(root, LabelName, Token).GetAwaiter().GetResult();
            Check(label.Id == 99 && label.Changeset == 17, "Publication keeps staged ID and explicit source");
            Check(Calls().Contains("label\tcreate\tlb:tortoisescm-label-pending-") && Calls().Contains("label\trename\tlb:tortoisescm-label-pending-"), "Create stages random name then uses collision-safe rename");
            Check(!Calls().Contains("label\tcreate\tlb:" + LabelName + "@"), "Native upsert never receives requested name");
            ClearCalls();
            Reject<InvalidOperationException>(() => Delete(LabelName, 98, 17), "Recreated label with new ID refused");
            Reject<InvalidOperationException>(() => Delete(LabelName, 99, 18), "Moved label target refused");
            Reject<ArgumentOutOfRangeException>(() => Delete(LabelName, 0, 17), "Missing reviewed ID refused");
            Reject<InvalidOperationException>(() => client.DeleteLabelAsync(root, LabelName, 99, 17, "foreign@server:8087", Token).GetAwaiter().GetResult(), "Foreign repository delete refused");
            Check(!Calls().Contains("label\tdelete"), "Stale confirmation never mutates");
            Check(Delete(LabelName, 99, 17).Succeeded, "Reviewed deletion succeeds");
            Check(client.GetLabelsAsync(root, Token).GetAwaiter().GetResult().Count == 0, "Deletion verified absent");
            ClearCalls(); Mode("rename-collision");
            var collision = Create("release-collision", 17, "comment");
            Check(!collision.Succeeded && collision.Error.Contains("tortoisescm-label-pending-") && collision.Error.Contains("release-collision"), "Concurrent create collision reports both possible names");
            var rows = client.GetLabelsAsync(root, Token).GetAwaiter().GetResult();
            Check(rows.Single(item => item.Name == "release-collision").Id == 200 && rows.Single(item => item.Name == "release-collision").Changeset == 22, "Concurrent creator's label is never repointed");
            Check(rows.Count == 2 && !Calls().Contains("label\tdelete"), "Temporary remains for reviewed recovery; no automatic deletion");
            SetRows(); Mode("create-fail-after");
            var failed = Create("tortoisescm-autotest-failure", 17, "comment");
            Check(!failed.Succeeded && failed.Error.Contains("tortoisescm-autotest-label-pending-"), "Uncertain create reports recoverable autotest temporary identity");
            SetRows(); Mode("find-fail-after-create");
            var uncertain = Reject<InvalidOperationException>(() => Create("uncertain", 17, "comment"), "Post-mutation read error returned");
            Check(uncertain.Message.Contains("tortoisescm-label-pending-") && uncertain.Message.Contains("uncertain"), "Post-mutation exceptions report possible success");
            SetRows(Row(7, "delete", 17)); Mode("delete-fail-after");
            var deleteFailure = Delete("delete", 7, 17);
            Check(!deleteFailure.Succeeded && deleteFailure.Error.Contains("may already have been deleted"), "Uncertain deletion reports possible success");
            SetRows(Row(7, "delete", 17)); Mode("delete-retains");
            var retained = Reject<InvalidOperationException>(() => Delete("delete", 7, 17), "Delete postcondition checked");
            Check(retained.Message.Contains("may already have been deleted"), "Post-delete verification retains uncertainty warning");
            Console.WriteLine("PASS: " + assertions + " label backend assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static PlasticCommandResult Create(string name, long changeset, string comment)
    { return client.CreateLabelAsync(root, name, changeset, comment, Repository, Token).GetAwaiter().GetResult(); }
    private static PlasticCommandResult Delete(string name, long id, long changeset)
    { return client.DeleteLabelAsync(root, name, id, changeset, Repository, Token).GetAwaiter().GetResult(); }
    private static void WriteSelector() { File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"/main\"\n"); }
    private static void Mode(string mode) { File.WriteAllText(Path.Combine(metadata, "mode"), mode); }
    private static void ClearCalls() { File.WriteAllText(Path.Combine(metadata, "calls"), ""); }
    private static string Calls() { return File.ReadAllText(Path.Combine(metadata, "calls")); }
    private static void SetRows(params XElement[] rows) { File.WriteAllText(Path.Combine(metadata, "rows"), Wrap(rows)); }
    private static string Wrap(params XElement[] rows) { return new XElement("PLASTICQUERY", rows).ToString(); }
    private static XElement Row(long id, string name, long changeset)
    {
        return new XElement("MARKER", new XElement("ID", id), new XElement("NAME", name), new XElement("CHANGESET", changeset),
            new XElement("DATE", "2026-09-28T10:00:00+08:00"), new XElement("OWNER", "tester"), new XElement("COMMENT", "line one\nline two"),
            new XElement("REPNAME", "test"), new XElement("REPOSITORY", "test"), new XElement("REPSERVER", "server:8087"), new XElement("BRANCH", "/main"));
    }

    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        File.AppendAllText(Path.Combine(metadata, "calls"), String.Join("\t", args) + "\n");
        string mode = File.Exists(Path.Combine(metadata, "mode")) ? File.ReadAllText(Path.Combine(metadata, "mode")) : "";
        var rows = XElement.Parse(File.ReadAllText(Path.Combine(metadata, "rows")));
        if (args[0] == "find")
        {
            if (args.Length != 6 || args[1] != "label" || args[2] != "on repository '" + Repository + "'") return 90;
            if (mode == "find-fail" || (mode == "find-fail-after-create" && rows.HasElements)) { Console.Error.Write("server offline"); return 7; }
            if (mode == "find-switch") File.AppendAllText(Path.Combine(metadata, "plastic.selector"), "\n changeset \"18\"\n");
            Console.Write(rows.ToString()); return 0;
        }
        if (args[0] == "log")
        {
            if (args[1] != "cs:17@" + Repository) return 94;
            if (mode == "log-switch") File.AppendAllText(Path.Combine(metadata, "plastic.selector"), "\n changeset \"18\"\n");
            Console.Write("<LogList><Changeset><ChangesetId>" + (mode == "log-wrong" ? "18" : "17") + "</ChangesetId><Owner>tester</Owner><Comment>Changeset comment</Comment><Branch>/main</Branch><Date>2026-09-28T10:00:00+08:00</Date></Changeset></LogList>"); return 0;
        }
        if (args[0] == "diff")
        {
            if (args[1] != "cs:17@" + Repository) return 95;
            if (mode == "diff-switch") File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"other@server:8087\"\n path \"/\"\n  smartbranch \"/main\"\n");
            Console.Write(mode == "diff-unsafe" ? "A|/../escape.txt|txt||/../escape.txt" : "A|/file.txt|txt||/file.txt"); return 0;
        }
        if (args[0] != "label") return 91;
        string name = args[2].Substring(3).Split('@')[0];
        if (args[1] == "create")
        {
            if (!name.StartsWith("tortoisescm-") || !name.Contains("label-pending-") || args[3] != "cs:17@" + Repository || !args[4].StartsWith("-c=")) return 92;
            var added = Row(99, name, 17); added.Element("COMMENT").Value = args[4].Substring(3); rows.Add(added);
            File.WriteAllText(Path.Combine(metadata, "rows"), rows.ToString()); return mode == "create-fail-after" ? 7 : 0;
        }
        var selected = rows.Elements("MARKER").Single(item => (string)item.Element("NAME") == name);
        if (args[1] == "rename")
        {
            if (mode == "rename-collision")
            { rows.Add(Row(200, args[3], 22)); File.WriteAllText(Path.Combine(metadata, "rows"), rows.ToString()); Console.Error.Write("destination already exists"); return 7; }
            selected.Element("NAME").Value = args[3];
        }
        else if (args[1] == "delete") { if (mode != "delete-retains") selected.Remove(); }
        else return 93;
        File.WriteAllText(Path.Combine(metadata, "rows"), rows.ToString()); return mode == "delete-fail-after" ? 7 : 0;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static T Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T error) { assertions++; return error; } throw new Exception(message); }
}
