// GPL-2.0-or-later. Black-box label CLI contracts and guarded metadata writes.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;

internal static class LabelCliTests
{
    private const string Repository = "test@server:8087";
    private const string Name = "Release 中文 & 'quoted'";
    private static string root, executable;
    private static int assertions;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && new[] { "status", "find", "log", "label" }.Contains(args[0])) return FakeCm(args);
        if (args.Length != 1) { Console.Error.WriteLine("Usage: LabelCliTests.exe <TortoiseSCM.exe>"); return 2; }
        executable = args[0];
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-label-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Meta("plastic.workspace"), "labels\nguid\nStandard\n");
            Selector();
            Seed();
            var result = Run("labels");
            Check(result.ExitCode == 0, "List succeeds: " + result.Text);
            var rows = (IList)Data(result)["labels"];
            Check(rows.Count == 2, "List returns both labels");
            var label = (Dictionary<string, object>)rows.Cast<object>().Single(row => ((Dictionary<string, object>)row)["name"].ToString() == Name);
            Check(Convert.ToInt64(label["id"]) == 21 && Convert.ToInt64(label["changeset"]) == 7 && label["repository"].ToString() == Repository,
                "Label identity, fixed changeset and repository preserved");
            Check(label["branch"].ToString() == "/main/topic" && label["comment"].ToString() == "Approved 中文", "Label metadata preserved");
            Check(((IList)Data(Run("labels", "--filter", "approved"))["labels"]).Count == 1, "Filter matches comment without case sensitivity");
            Check(((IList)Data(Run("labels", "--filter", "中文"))["labels"]).Count == 1, "Filter matches Unicode names");
            Check(((IList)Data(Run("labels", "--filter", "missing"))["labels"]).Count == 0, "No matching labels is a successful empty result");
            result = Run("label-resolve", "--label", Name);
            Check(result.ExitCode == 0 && ((Dictionary<string, object>)Data(result)["label"])["name"].ToString() == Name, "Resolve preserves spaces, apostrophes and Unicode");
            Check(File.ReadAllText(Meta("calls")).Split('\n').Where(line => line.StartsWith("find\t", StringComparison.Ordinal)).All(line => !line.Contains(Name)), "Label name never enters query syntax");
            Check(Run("label-resolve", "--label", "missing").ExitCode != 0, "Missing label is an error");
            Check(Run("label-delete", "--label", Name, "--label-id", "20", "--changeset", "7", "--yes").ExitCode != 0, "Delete refuses stale identity");
            Check(Run("label-delete", "--label", Name, "--label-id", "21", "--changeset", "8", "--yes").ExitCode != 0, "Delete refuses moved target");
            Check(!File.ReadAllText(Meta("calls")).Contains("label\tdelete"), "Rejected deletes never reach native mutation");
            string originalSelector = File.ReadAllText(Meta("plastic.selector"));
            result = Run("label-delete", "--label", Name, "--label-id", "21", "--changeset", "7", "--yes");
            Check(result.ExitCode == 0 && Convert.ToInt64(Data(result)["id"]) == 21, "Reviewed deletion succeeds and returns precise identity: " + result.Text);
            Check(!XDocument.Load(Meta("labels.xml")).Descendants("MARKER").Any(row => row.Element("NAME").Value == Name), "Native label deleted");
            string comments = Path.Combine(root, "comment.txt");
            File.WriteAllText(comments, "Creation 中文\nsecond line", new UTF8Encoding(true));
            result = Run("label-create", "--label", Name, "--changeset", "0", "--commentsfile", comments, "--yes");
            Check(result.ExitCode == 0 && Convert.ToInt64(Data(result)["changeset"]) == 0, "Creation accepts fixed changeset zero and UTF-8 comment file: " + result.Text);
            label = (Dictionary<string, object>)Data(Run("label-resolve", "--label", Name))["label"];
            Check(label["comment"].ToString() == "Creation 中文\nsecond line", "Native creation receives decoded multiline comment");
            Check(File.ReadAllText(Meta("plastic.selector")) == originalSelector, "Metadata operations preserve selector");
            Check(!File.ReadAllText(Meta("calls")).Split('\n').Any(line => line.StartsWith("switch\t", StringComparison.Ordinal) || line.StartsWith("update\t", StringComparison.Ordinal)), "No implicit workspace switch/update");
            Check(Run("label-create", "--label", Name, "--changeset", "7", "--comment", "duplicate", "--yes").ExitCode != 0, "Duplicate create never repoints existing label");

            InvalidArguments();
            UnusualReadableNames();
            SetMode("partial");
            Check(Run("labels").ExitCode == 0 && Run("label-resolve", "--label", Name).ExitCode == 0, "Partial workspaces support repository reads");
            SetMode("fail");
            result = Run("labels");
            Check(result.ExitCode == 1 && !Convert.ToBoolean(Json(result)["success"]) && Json(result)["data"] == null && result.Text.Contains("Native denied"), "Native failure survives JSON transport");
            SetMode("malformed");
            Check(Run("labels").ExitCode == 1, "Malformed native XML fails closed");
            SetMode("race");
            result = Run("labels");
            Check(result.ExitCode == 1 && Json(result)["data"] == null, "Repository change rejects stale response");
            Selector();
            SetMode("");
            Console.WriteLine("PASS: " + assertions + " label CLI assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void InvalidArguments()
    {
        var cases = new[] {
            new[] { "labels", "--yes" }, new[] { "labels", "--label", Name }, new[] { "labels", "--changeset", "7" },
            new[] { "labels", "--path", Path.Combine(root, "second") }, new[] { "labels", "--recursive" },
            new[] { "labels", "--branch", "/main" }, new[] { "labels", "--item", "/file" }, new[] { "labels", "--external" },
            new[] { "labels", "--comment", "bad" }, new[] { "labels", "--shelve", "1" }, new[] { "labels", "--overwrite" },
            new[] { "labels", "--output", Path.Combine(root, "out") }, new[] { "labels", "--before", "1" },
            new[] { "label-resolve" }, new[] { "label-resolve", "--label", "" }, new[] { "label-resolve", "--label", Name, "--yes" },
            new[] { "label-resolve", "--label", Name, "--filter", "x" }, new[] { "label-resolve", "--label", Name, "--label-id", "21" },
            new[] { "label-create", "--label", "new", "--changeset", "1", "--comment", "x" },
            new[] { "label-create", "--label", "new", "--changeset", "1", "--yes" },
            new[] { "label-create", "--label", "new", "--changeset", "1", "--comment", " ", "--yes" },
            new[] { "label-create", "--label", "new", "--comment", "x", "--yes" },
            new[] { "label-create", "--label", "new", "--changeset", "-1", "--comment", "x", "--yes" },
            new[] { "label-delete", "--label", Name, "--changeset", "7", "--yes" },
            new[] { "label-delete", "--label", Name, "--label-id", "21", "--yes" },
            new[] { "label-delete", "--label", Name, "--label-id", "21", "--changeset", "7" },
            new[] { "label-delete", "--label", Name, "--label-id", "0", "--changeset", "7", "--yes" },
            new[] { "label-delete", "--label", Name, "--label-id", "-1", "--changeset", "7", "--yes" },
            new[] { "label-delete", "--label", Name, "--label-id", "21", "--changeset", "7", "--comment", "x", "--yes" },
            new[] { "status", "--label", Name }, new[] { "status", "--label-id", "21" }, new[] { "status", "--filter", "x" },
            new[] { "label-resolve", "--label", Name, "--label", "duplicate" }
        };
        foreach (var args in cases)
        {
            string calls = File.ReadAllText(Meta("calls"));
            var result = Run(args[0], args.Skip(1).ToArray());
            Check(result.ExitCode == 2 && !Convert.ToBoolean(Json(result)["success"]), "Invalid options rejected: " + String.Join(" ", args));
            Check(calls == File.ReadAllText(Meta("calls")), "Invalid arguments cannot invoke native client");
        }
        Check(Run("label-resolve", "--label", "bad\nname").ExitCode == 2, "Read names reject control characters");
    }

    private static void UnusualReadableNames()
    {
        var names = new[] { "release/path", "release@foreign", "release#cs:9", "lb:literal", "--json",
            " outer whitespace ", "quote\"label", "back\\slash", "x' or changeset = 0" };
        var xml = XDocument.Load(Meta("labels.xml"));
        for (int index = 0; index < names.Length; index++) xml.Root.Add(Row(200 + index, names[index], 7, "External client name"));
        xml.Root.Add(Row(300, "Case", 7, "Upper"), Row(301, "case", 0, "Lower"));
        xml.Save(Meta("labels.xml"));
        string before = File.ReadAllText(Meta("labels.xml"));
        string calls = File.ReadAllText(Meta("calls"));
        var result = Run("labels");
        Check(result.ExitCode == 0 && ((IList)Data(result)["labels"]).Count == 13, "Unusual existing names do not prevent repository listing");
        for (int index = 0; index < names.Length; index++)
        {
            result = Run("label-resolve", "--label", names[index]);
            var label = (Dictionary<string, object>)Data(result)["label"];
            Check(result.ExitCode == 0 && label["name"].ToString() == names[index] && Convert.ToInt64(label["id"]) == 200 + index,
                "Read names are matched verbatim: " + names[index]);
        }
        Check(Convert.ToInt64(((Dictionary<string, object>)Data(Run("label-resolve", "--label", "Case"))["label"])["id"]) == 300 &&
            Convert.ToInt64(((Dictionary<string, object>)Data(Run("label-resolve", "--label", "case"))["label"])["id"]) == 301,
            "Case-only label names resolve to distinct identities");
        string[] readCalls = File.ReadAllText(Meta("calls")).Substring(calls.Length).Split('\n');
        Check(readCalls.Where(line => line.StartsWith("find\t", StringComparison.Ordinal)).All(line =>
            line == "find\tlabel\ton repository '" + Repository + "'\t--xml\t--encoding=utf-8\t--nototal"),
            "Special read names never become find expressions or native object specifications");
        foreach (string name in names.Take(names.Length - 1))
        {
            Check(Run("label-create", "--label", name, "--changeset", "7", "--comment", "x", "--yes").ExitCode == 2,
                "Unsupported create name rejected: " + name);
            Check(Run("label-delete", "--label", name, "--label-id", "200", "--changeset", "7", "--yes").ExitCode == 2,
                "Unsupported delete name rejected: " + name);
        }
        Check(before == File.ReadAllText(Meta("labels.xml")), "Read and rejected actions preserve all external labels");
        Check(!File.ReadAllText(Meta("calls")).Substring(calls.Length).Split('\n').Any(line => line.StartsWith("label\t", StringComparison.Ordinal)),
            "Special-name reads and rejected writes do not invoke native label mutations");
    }

    private static Result Run(string command, params string[] extra)
    {
        var args = new[] { "--cli", "--command", command, "--path", root, "--json", "--cm", Assembly.GetExecutingAssembly().Location }.Concat(extra);
        var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, Arguments = String.Join(" ", args.Select(Quote)) };
        using (var process = Process.Start(start))
        {
            string text = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Check(error.Length == 0, "JSON mode writes no stderr: " + error);
            return new Result { ExitCode = process.ExitCode, Text = text };
        }
    }
    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        root = Environment.CurrentDirectory;
        string mode = File.Exists(Meta("mode")) ? File.ReadAllText(Meta("mode")) : "";
        File.AppendAllText(Meta("calls"), String.Join("\t", args) + "\n");
        if (args[0] == "status")
        {
            Console.Write(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status", new XElement("Changeset", mode == "partial" ? -1 : 7),
                new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))),
                new XElement("WkConfigType", "branch"), new XElement("WkConfigName", "/main@" + Repository)));
            return 0;
        }
        if (args[0] == "find")
        {
            if (mode == "fail") { Console.Error.Write("Native denied"); return 7; }
            if (mode == "malformed") { Console.Write("<bad/>"); return 0; }
            if (mode == "race") File.WriteAllText(Meta("plastic.selector"), File.ReadAllText(Meta("plastic.selector")).Replace(Repository, "other@server:8087"));
            Console.Write(File.ReadAllText(Meta("labels.xml"))); return 0;
        }
        if (args[0] == "log")
        {
            string value = args[1].Substring(3).Split('@')[0];
            Console.Write(new XElement("LogList", new XElement("Changeset", new XElement("ChangesetId", value)))); return 0;
        }
        if (args[0] == "label")
        {
            var xml = XDocument.Load(Meta("labels.xml"));
            string name = args[2].Substring(3); name = name.Substring(0, name.Length - Repository.Length - 1);
            var existing = xml.Descendants("MARKER").SingleOrDefault(row => row.Element("NAME").Value == name);
            if (args[1] == "create")
            {
                if (existing != null) { Console.Error.Write("Duplicate create"); return 91; }
                long cs = Int64.Parse(args[3].Substring(3).Split('@')[0], CultureInfo.InvariantCulture);
                string comment = args.Single(value => value.StartsWith("-c=", StringComparison.Ordinal)).Substring(3);
                xml.Root.Add(Row(100, name, cs, comment));
            }
            else if (args[1] == "rename")
            {
                if (existing == null || xml.Descendants("MARKER").Any(row => row.Element("NAME").Value == args[3])) return 92;
                existing.Element("NAME").Value = args[3];
            }
            else if (args[1] == "delete") { if (existing == null) return 93; existing.Remove(); }
            else return 94;
            xml.Save(Meta("labels.xml")); Console.Write("Done"); return 0;
        }
        return 90;
    }
    private static XElement Row(long id, string name, long cs, string comment)
    {
        return new XElement("MARKER", new XElement("ID", id), new XElement("NAME", name), new XElement("CHANGESET", cs),
            new XElement("DATE", "2026-09-28T12:00:00+08:00"), new XElement("OWNER", "tester"), new XElement("COMMENT", comment),
            new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"), new XElement("REPOSITORY", "test"),
            new XElement("BRANCH", "/main/topic"), new XElement("BRANCHID", 9));
    }
    private static void Seed() { new XDocument(new XElement("PLASTICQUERY", Row(21, Name, 7, "Approved 中文"), Row(22, "Earlier", 0, "Initial"))).Save(Meta("labels.xml")); }
    private static void Selector() { File.WriteAllText(Meta("plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n smartbranch \"/main\"\n"); }
    private static string Meta(string name) { return Path.Combine(root, ".plastic", name); }
    private static void SetMode(string value) { File.WriteAllText(Meta("mode"), value); }
    private static Dictionary<string, object> Json(Result result) { return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(result.Text); }
    private static Dictionary<string, object> Data(Result result) { return (Dictionary<string, object>)Json(result)["data"]; }
    private static string Quote(string value)
    {
        var output = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            output.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes); output.Append(ch); slashes = 0;
        }
        output.Append('\\', slashes * 2); return output.Append('"').ToString();
    }
    private sealed class Result { internal int ExitCode; internal string Text; }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
}
