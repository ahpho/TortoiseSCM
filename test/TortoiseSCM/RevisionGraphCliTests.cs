// GPL-2.0-or-later. Black-box revision graph CLI contracts.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Xml.Linq;

internal static class RevisionGraphCliTests
{
    private const string Repository = "test@server:8087";
    private static string root, executable;
    private static int assertions;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && new[] { "status", "find" }.Contains(args[0])) return FakeCm(args);
        if (args.Length != 1) { Console.Error.WriteLine("Usage: RevisionGraphCliTests.exe <TortoiseSCM.exe>"); return 2; }
        executable = args[0]; root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-graph-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "graph\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"\npath \"/\"\nsmartbranch \"/main\"\n");
            var result = Run("revision-graph", "--limit", "2");
            Check(result.ExitCode == 0, "Graph page succeeds: " + result.Text);
            var data = Data(result);
            Check(((IList)data["nodes"]).Count == 2 && ((IList)data["edges"]).Count == 4, "Page includes two nodes, parent and raw merge links");
            var edges = ((IList)data["edges"]).Cast<object>().Select(item => (Dictionary<string, object>)item).ToArray();
            Check(edges.Any(e => e["kind"].ToString() == "parent") && edges.Any(e => e["kind"].ToString() == "intervalcherrypicksubtractive"), "Parent and unknown native edge types remain distinct");
            Check(Convert.ToBoolean(data["hasMore"]) && Convert.ToInt64(data["nextBeforeChangeset"]) == 9, "Cursor is the last loaded changeset");
            Check(result.Text.Contains("source-unloaded") && result.Text.Contains("[intervalcherrypicksubtractive]"), "Text output marks unloaded endpoints and edge type");
            result = Run("revision-graph", "--before", "0");
            Check(result.ExitCode == 0 && ((IList)Data(result)["nodes"]).Count == 0 && !Convert.ToBoolean(Data(result)["hasMore"]), "Zero cursor is an explicit empty page");
            result = Run("revision-graph", "--before", "8", "--limit", "1");
            Check(result.ExitCode == 0 && ((IList)Data(result)["nodes"]).Count == 1, "Exclusive cursor and limit are passed to graph query");
            Check(Calls().Contains("where changesetid < 8 order by changesetid desc limit 2 on repository '" + Repository + "'"), "Cursor query is numeric, bounded and repository-qualified");
            result = Run("revision-graph");
            Check(result.ExitCode == 0 && Calls().Contains("order by changesetid desc limit 101 on repository '" + Repository + "'"), "Default page limit is 100 with sentinel");
            string selector = File.ReadAllText(Meta("plastic.selector"));
            File.WriteAllText(Meta("plastic.workspace"), "graph\nguid\nPartial\n");
            SetMode("partial");
            result = Run("revision-graph");
            Check(result.ExitCode == 0 && Convert.ToBoolean(((Dictionary<string, object>)Data(result)["workspace"])["isPartial"]), "Partial workspace graph is read-only and supported");
            Check(File.ReadAllText(Meta("plastic.selector")) == selector, "Graph reads preserve the selector");
            File.WriteAllText(Meta("plastic.workspace"), "graph\nguid\nStandard\n"); SetMode("");
            string file = Path.Combine(root, "file 中文.txt"); File.WriteAllText(file, "unchanged");
            result = RunAt(file, "revision-graph");
            Check(result.ExitCode == 0 && ((IList)Data(result)["nodes"]).Count == 3, "Existing file locates repository without filtering nodes");
            Check(File.ReadAllText(file) == "unchanged", "File locator content stays unchanged");
            InvalidArguments();
            foreach (string mode in new[] { "malformed", "malformed-merge", "duplicate", "over-cap", "fail", "race" })
            {
                SetMode(mode); result = Run("revision-graph", "--limit", "2");
                Check(result.ExitCode == 1 && DataOrNull(result) == null, mode + " graph failure returns no partial data");
                File.WriteAllText(Meta("plastic.selector"), selector);
            }
            SetMode("timeout"); result = Run("revision-graph", "--timeout", "1");
            Check(result.ExitCode == 124 && DataOrNull(result) == null, "Timed-out native graph query returns timeout code with no partial data");
            SetMode("future"); result = Run("revision-graph");
            Check(result.ExitCode == 0 && ((IList)Data(result)["edges"]).Cast<object>().Any(item => ((Dictionary<string, object>)item)["kind"].ToString() == "future_operation"), "Unrecognized native type is preserved verbatim");
            SetMode("");
            Check(Calls().Split('\n').Where(line => line.Length > 0).All(line => line.StartsWith("status\t", StringComparison.Ordinal) || line.StartsWith("find\t", StringComparison.Ordinal)), "Graph never invokes native mutation");
            Console.WriteLine("PASS: " + assertions + " revision graph CLI assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void InvalidArguments()
    {
        var cases = new[] {
            new[] { "revision-graph", "--yes" }, new[] { "revision-graph", "--limit", "0" },
            new[] { "revision-graph", "--limit", "101" }, new[] { "revision-graph", "--before", "-1" },
            new[] { "revision-graph", "--branch", "/main" }, new[] { "revision-graph", "--filter", "x" },
            new[] { "revision-graph", "--changeset", "7" }, new[] { "revision-graph", "--comment", "x" },
            new[] { "revision-graph", "--recursive" }, new[] { "revision-graph", "--item", "/file" },
            new[] { "revision-graph", "--external" }, new[] { "revision-graph", "--overwrite" },
            new[] { "revision-graph", "--label", "release" }, new[] { "revision-graph", "--shelve", "1" },
            new[] { "revision-graph", "--limit", "1", "--limit", "2" },
            new[] { "revision-graph", "--path", Path.Combine(root, "other") }
        };
        foreach (var item in cases)
        {
            string calls = Calls(); var result = Run(item[0], item.Skip(1).ToArray());
            Check(result.ExitCode == 2 && DataOrNull(result) == null, "Invalid graph options rejected: " + String.Join(" ", item));
            Check(calls == Calls(), "Invalid graph options do not invoke native client");
        }
    }

    private static Result Run(string command, params string[] extra)
    { return RunAt(root, command, extra); }
    private static Result RunAt(string path, string command, params string[] extra)
    {
        var args = new[] { "--cli", "--command", command, "--path", path, "--json", "--cm", Assembly.GetExecutingAssembly().Location }.Concat(extra);
        var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, Arguments = String.Join(" ", args.Select(Quote)) };
        using (var process = Process.Start(start))
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20000)) { process.Kill(); throw new TimeoutException("Graph CLI test process hung."); }
            string text = output.GetAwaiter().GetResult(), error = errors.GetAwaiter().GetResult();
            Check(error.Length == 0, "JSON mode writes no stderr: " + error); return new Result { ExitCode = process.ExitCode, Text = text };
        }
    }

    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        root = Environment.CurrentDirectory; string modePath = Path.Combine(root, ".plastic", "mode");
        string mode = File.Exists(modePath) ? File.ReadAllText(modePath) : "";
        File.AppendAllText(Path.Combine(root, ".plastic", "calls"), String.Join("\t", args) + "\n");
        if (args[0] == "status")
        {
            Console.Write(new XElement("StatusOutput", new XElement("WorkspaceStatus", new XElement("Status",
                new XElement("Changeset", mode == "partial" ? -1 : 7), new XElement("RepSpec", new XElement("Name", "test"), new XElement("Server", "server:8087")))),
                new XElement("WkConfigType", "branch"), new XElement("WkConfigName", "/main@" + Repository))); return 0;
        }
        if (args[0] != "find") return 90;
        if (mode == "timeout") Thread.Sleep(4000);
        if (mode == "fail") { Console.Error.Write("Graph access denied"); return 7; }
        if (mode == "race") File.WriteAllText(Meta("plastic.selector"), File.ReadAllText(Meta("plastic.selector")).Replace(Repository, "other@server:8087"));
        if (mode == "malformed") { Console.Write("<bad/>"); return 0; }
        string text = String.Join(" ", args);
        if (text.Contains(" merge "))
        {
            if (mode == "malformed-merge") { Console.Write("<bad/>"); return 0; }
            // Older pages have no merge rows in this fixture. Returning an empty
            // PLASTICQUERY keeps the parent boundary explicit without inventing a
            // destination that was not loaded in the page.
            var merges = text.Contains("dstchangeset = 7") ? new XElement("PLASTICQUERY") : Merges();
            if (mode == "future") merges.Elements("MERGE").Last().Element("TYPE").Value = "future_operation";
            Console.Write(merges); return 0;
        }
        if (text.Contains("< 8")) { Console.Write(Nodes(7)); return 0; }
        if (mode == "duplicate") { Console.Write(Nodes(10, 10, 8)); return 0; }
        if (mode == "over-cap") { Console.Write(Nodes(10, 9, 8, 7)); return 0; }
        Console.Write(Nodes(10, 9, 8)); return 0;
    }
    private static XElement Nodes(params long[] numbers)
    {
        var root = new XElement("PLASTICQUERY");
        for (int i = 0; i < numbers.Length; ++i)
        {
            long n = numbers[i]; long parent = n == 10 ? 9 : n == 9 ? 8 : n - 1;
            root.Add(new XElement("CHANGESET", new XElement("ID", n + 100), new XElement("CHANGESETID", n),
                new XElement("PARENT", parent), new XElement("GUID", new Guid((n + 1).ToString("D").PadLeft(32, '0')).ToString("D")),
                new XElement("BRANCH", "/main"), new XElement("OWNER", "tester"), new XElement("DATE", "2026-09-28T12:00:00+08:00"),
                new XElement("COMMENT", "cs " + n), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"), new XElement("REPOSITORY", "test")));
        }
        return root;
    }
    private static XElement Merges()
    {
        return new XElement("PLASTICQUERY",
            new XElement("MERGE", new XElement("ID", 501), new XElement("SRCCHANGESET", 9), new XElement("DSTCHANGESET", 10),
                new XElement("SRCID", 109), new XElement("DSTID", 110), new XElement("TYPE", "merge"), new XElement("BASECHANGESET", 8),
                new XElement("SRCBRANCH", "br:/main"), new XElement("DSTBRANCH", "br:/main")),
            new XElement("MERGE", new XElement("ID", 502), new XElement("SRCCHANGESET", 99), new XElement("DSTCHANGESET", 10),
                new XElement("SRCID", 199), new XElement("DSTID", 110), new XElement("TYPE", "intervalcherrypicksubtractive"), new XElement("BASECHANGESET", 98),
                new XElement("SRCBRANCH", "br:/main"), new XElement("DSTBRANCH", "br:/main")));
    }
    private static string Calls() { string path = Path.Combine(root, ".plastic", "calls"); return File.Exists(path) ? File.ReadAllText(path) : ""; }
    private static string Meta(string name) { return Path.Combine(root, ".plastic", name); }
    private static void SetMode(string value) { File.WriteAllText(Meta("mode"), value); }
    private static Dictionary<string, object> Data(Result result) { return (Dictionary<string, object>)Json(result)["data"]; }
    private static object DataOrNull(Result result) { var json = Json(result); return json.ContainsKey("data") ? json["data"] : null; }
    private static Dictionary<string, object> Json(Result result) { return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(result.Text); }
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
    private static void Check(bool condition, string message) { ++assertions; if (!condition) throw new InvalidOperationException(message); }
    private sealed class Result { public int ExitCode; public string Text; }
}
