// GPL-2.0-or-later. Headless fixed snapshot browser CLI transport checks.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using System.Xml.Linq;
using TortoiseSCM;

internal static class RepositoryBrowserCliTests
{
    private const string Repository = "test@server:8087";
    private static int assertions;
    private static string root, executable;
    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "ls" || args[0] == "status")) return FakeCm(args);
        if (args.Length != 1) { Console.Error.WriteLine("Usage: RepositoryBrowserCliTests.exe <TortoiseSCM.exe>"); return 2; }
        executable = args[0];
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-browser-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "cli-browser\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"/main\"\n");
            var response = Run("--changeset", "17");
            Check(response.ExitCode == 0, "Browser CLI succeeds: " + response.Text);
            var json = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(response.Text);
            var data = (Dictionary<string, object>)json["data"];
            var rows = (IList)data["entries"];
            Check(Convert.ToBoolean(json["success"]) && Convert.ToInt32(data["changeset"]) == 17 && data["directoryPath"].ToString() == "/", "CLI returns fixed snapshot and default root");
            Check(rows.Count == 1 && ((Dictionary<string, object>)rows[0])["name"].ToString() == "中文 & space.txt", "CLI structured Unicode entries preserved");
            Check(File.ReadAllText(Path.Combine(root, ".plastic", "call")).Contains("--tree=cs:17@" + Repository), "CLI pins repository snapshot");
            var nested = Run("--changeset", "0", "--item", "/folder");
            Check(nested.ExitCode == 0, "CLI accepts changeset zero and a directory selection");
            var nestedData = (Dictionary<string, object>)((Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(nested.Text))["data"];
            Check(nestedData["directoryPath"].ToString() == "/folder" && ((IList)nestedData["entries"]).Count == 0, "CLI represents verified empty directory");
            foreach (var bad in new[] {
                new string[0], new[] { "--changeset", "17", "--yes" }, new[] { "--changeset", "17", "--recursive" },
                new[] { "--changeset", "17", "--path", Path.Combine(root, "other") }, new[] { "--changeset", "17", "--item", "/../x" },
                new[] { "--changeset", "17", "--comment", "bad" }, new[] { "--changeset", "17", "--external" },
                new[] { "--changeset", "17", "--from", "1" }, new[] { "--changeset", "-1" },
                new[] { "--changeset", "17", "--item", "" }, new[] { "--changeset", "17", "--output", Path.Combine(root, "out") },
                new[] { "--changeset", "17", "--shelve", "1" } })
                Check(Run(bad).ExitCode == 2, "Invalid browser arguments rejected: " + String.Join(" ", bad));
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "fail");
            var failed = Run("--changeset", "17");
            Check(failed.ExitCode != 0 && failed.Text.Contains("\"success\":false"), "Native failure reaches JSON as failure");
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "switch-status");
            var switched = Run("--changeset", "17");
            var switchedJson = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(switched.Text);
            Check(switched.ExitCode == 1 && !Convert.ToBoolean(switchedJson["success"]) && switchedJson["data"] == null,
                "CLI rejects repository changed between workspace header and directory listing");
            Check(switchedJson["error"].ToString().Contains("listing preparation"), "Context mismatch reports an explicit refresh error");
            Console.WriteLine("PASS: " + assertions + " repository browser CLI assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Result Run(params string[] extra)
    {
        var arguments = new[] { "--cli", "--command", "repository-list", "--path", root, "--json", "--cm", Assembly.GetExecutingAssembly().Location }.Concat(extra);
        var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            Arguments = String.Join(" ", arguments.Select(PlasticClient.QuoteArgument)) };
        using (var process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd(); string error = process.StandardError.ReadToEnd();
            process.WaitForExit(); return new Result { ExitCode = process.ExitCode, Text = output, Error = error };
        }
    }
    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string modePath = Path.Combine(Environment.CurrentDirectory, ".plastic", "mode");
        string mode = File.Exists(modePath) ? File.ReadAllText(modePath) : "";
        if (args[0] == "status")
        {
            if (mode == "switch-status")
            {
                string selector = Path.Combine(Environment.CurrentDirectory, ".plastic", "plastic.selector");
                File.WriteAllText(selector, File.ReadAllText(selector).Replace(Repository, "other@server:8087"));
            }
            Console.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><StatusOutput><WorkspaceStatus><Status><Changeset>22</Changeset><RepSpec><Name>test</Name><Server>server:8087</Server></RepSpec></Status></WorkspaceStatus><WkConfigType>branch</WkConfigType><WkConfigName>/main@" + Repository + "</WkConfigName></StatusOutput>");
            return 0;
        }
        if (args[0] == "ls")
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, ".plastic", "call"), String.Join("\t", args));
            if (mode == "fail") { Console.Error.Write("Missing directory"); return 7; }
            var entries = new XElement("LsItems", Item(".", args[1], "dir", 3));
            if (args[1] == "/") entries.Add(Item("中文 & space.txt", "/中文 & space.txt", "txt", 4));
            if (mode == "switch-status")
                foreach (var repository in entries.Descendants("Repository")) repository.Value = "rep:other@server:8087";
            Console.Write(new XElement("LsResults", entries).ToString());
            return 0;
        }
        return 90;
    }
    private static XElement Item(string name, string path, string type, long id)
    {
        return new XElement("LsItem", new XElement("Name", name), new XElement("CurrentPath", path),
            new XElement("Type", type), new XElement("ItemId", id), new XElement("Size", 42),
            new XElement("Repository", "rep:" + Repository), new XElement("SymlinkTarget", ""));
    }
    private sealed class Result { public int ExitCode; public string Text; public string Error; }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
}
