// GPL-2.0-or-later. Headless shelveset comparison/export transport checks.
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

internal static class ShelveCompareExportCliTests
{
    private const string Repository = "test@server:8087";
    private static int assertions;
    private static string root, otherWorkspace;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && new[] { "status", "find", "diff", "ls", "cat", "shelveset" }.Contains(args[0])) return FakeCm(args);
        if (args.Length != 1) { Console.Error.WriteLine("Usage: ShelveCompareExportCliTests.exe <TortoiseSCM.exe>"); return 2; }
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-shelve-content-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "cli-shelve-content\nguid\nStandard\n");
            string selector = "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"/main\"\n";
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector);
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "normal");
            string application = Path.GetFullPath(args[0]), fake = Assembly.GetExecutingAssembly().Location;

            var compared = Run(application, "--cli", "--command", "shelve-diff", "--path", root, "--shelve", "4", "--json", "--cm", fake);
            Check(compared.ExitCode == 0, "shelve-diff succeeds: " + compared.Text + compared.Error);
            var comparison = Json(compared.Text);
            var comparisonData = (Dictionary<string, object>)comparison["data"];
            var rows = (IList)comparisonData["files"];
            var row = (Dictionary<string, object>)rows[0];
            var diff = (Dictionary<string, object>)row["diff"];
            Check(comparisonData["repository"].ToString() == Repository && Convert.ToInt64(comparisonData["parentChangeset"]) == 22,
                "comparison identifies repository and immutable parent changeset");
            Check(rows.Count == 1 && row["path"].ToString() == "/folder/one.txt" && diff["diffText"].ToString().Contains("-old") && diff["diffText"].ToString().Contains("+new"),
                "comparison returns structured per-file unified content diff");

            string output = Path.Combine(Path.GetDirectoryName(root), Path.GetFileName(root) + "-export");
            var exported = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", output, "--yes", "--json", "--cm", fake);
            Check(exported.ExitCode == 0, "shelve-export succeeds: " + exported.Text + exported.Error);
            string exportedFile = Path.Combine(output, "folder", "one.txt"), manifest = Path.Combine(output, "shelveset.manifest");
            Check(File.ReadAllText(exportedFile) == "new\n", "export writes exact shelveset bytes in repository-shaped directory");
            string manifestText = File.ReadAllText(manifest);
            Check(manifestText.Contains("sh:4") == false && manifestText.Contains("shelveset 4@" + Repository) && manifestText.Contains("C\tF\t/folder/one.txt"),
                "export records repository, parent and changed path in UTF-8 manifest");

            File.WriteAllText(exportedFile, "local bytes");
            var refused = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", output, "--yes", "--json", "--cm", fake);
            Check(refused.ExitCode == 2 && File.ReadAllText(exportedFile) == "local bytes", "existing output is refused without overwrite before changing bytes");
            var overwritten = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", output, "--yes", "--overwrite", "--json", "--cm", fake);
            Check(overwritten.ExitCode == 0 && File.ReadAllText(exportedFile) == "new\n", "explicit overwrite atomically replaces existing file bytes");

            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "mixed");
            var mixed = Run(application, "--cli", "--command", "shelve-diff", "--path", root, "--shelve", "4", "--json", "--cm", fake);
            Check(mixed.ExitCode == 0, "mixed addition, deletion, move and binary comparison succeeds: " + mixed.Text);
            var mixedRows = ((IList)((Dictionary<string, object>)Json(mixed.Text)["data"])["files"]).Cast<Dictionary<string, object>>().ToArray();
            Check(mixedRows.Length == 4 && mixedRows.Any(item => Convert.ToString(item["oldPath"]) == "/old.txt") &&
                mixedRows.Any(item => item["path"].ToString() == "/binary.dat" && (bool)((Dictionary<string, object>)item["diff"])["isBinary"]),
                "comparison preserves move origin and identifies binary bytes");
            var mixedExport = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", output + "-mixed", "--yes", "--json", "--cm", fake);
            Check(mixedExport.ExitCode == 0 && File.ReadAllBytes(Path.Combine(output + "-mixed", "binary.dat")).SequenceEqual(new byte[] { 0, 255, 2 }) &&
                File.Exists(Path.Combine(output + "-mixed", "added.txt")) && File.Exists(Path.Combine(output + "-mixed", "renamed.txt")) &&
                !File.Exists(Path.Combine(output + "-mixed", "gone.txt")) && !File.Exists(Path.Combine(output + "-mixed", "old.txt")),
                "mixed export writes exact binary, added and move-destination files, excluding deleted and old paths");
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "download-failure");
            var downloadFailure = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", output, "--yes", "--overwrite", "--json", "--cm", fake);
            Check(downloadFailure.ExitCode == 1 && File.ReadAllText(exportedFile) == "new\n" && File.ReadAllText(manifest) == manifestText,
                "failed download preserves every existing output file and manifest");
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "normal");

            var confirmation = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4", "--output", output, "--json", "--cm", fake);
            Check(confirmation.ExitCode == 2, "shelve-export requires explicit confirmation");
            var inside = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", Path.Combine(root, "unsafe-export"), "--yes", "--json", "--cm", fake);
            Check(inside.ExitCode == 2 && !Directory.Exists(Path.Combine(root, "unsafe-export")), "workspace output is rejected without creating a directory");
            otherWorkspace = Path.Combine(Path.GetDirectoryName(root), Path.GetFileName(root) + "-other-workspace");
            Directory.CreateDirectory(Path.Combine(otherWorkspace, ".plastic"));
            File.WriteAllText(Path.Combine(otherWorkspace, ".plastic", "plastic.workspace"), "other\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(otherWorkspace, ".plastic", "plastic.selector"), "repository \"other@server:8087\"\n path \"/\"\n  smartbranch \"/main\"\n");
            var other = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", Path.Combine(otherWorkspace, "unsafe-export"), "--yes", "--json", "--cm", fake);
            Check(other.ExitCode == 2 && !Directory.Exists(Path.Combine(otherWorkspace, "unsafe-export")), "output in another Plastic workspace is rejected");

            string nestedWorkspace = Path.Combine(output, "nested", ".plastic");
            Directory.CreateDirectory(nestedWorkspace);
            File.WriteAllText(Path.Combine(nestedWorkspace, "plastic.workspace"), "nested\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(nestedWorkspace, "plastic.selector"), "repository \"nested@server:8087\"\n path \"/\"\n  smartbranch \"/main\"\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "nested");
            File.Delete(Path.Combine(root, ".plastic", "calls"));
            var nested = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", output, "--yes", "--overwrite", "--json", "--cm", fake);
            string[] nestedCalls = File.ReadAllLines(Path.Combine(root, ".plastic", "calls"));
            Check(nested.ExitCode == 2 && !nestedCalls.Any(line => line.StartsWith("cat\t", StringComparison.Ordinal)),
                "target path inside a nested Plastic workspace is rejected before downloading content");

            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "deleted");
            File.Delete(Path.Combine(root, ".plastic", "calls"));
            string deletedOutput = output + "-deleted";
            var deleted = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", deletedOutput, "--yes", "--json", "--cm", fake);
            string[] deletedCalls = File.ReadAllLines(Path.Combine(root, ".plastic", "calls"));
            Check(deleted.ExitCode == 0 && File.ReadAllText(Path.Combine(deletedOutput, "shelveset.manifest")).Contains("D\tF\t/gone.txt") &&
                !deletedCalls.Any(line => line.StartsWith("cat\t", StringComparison.Ordinal)),
                "deleted item is recorded in manifest without requesting nonexistent shelveset content");

            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "manifest");
            File.Delete(Path.Combine(root, ".plastic", "calls"));
            string manifestOutput = output + "-manifest";
            var manifestCollision = Run(application, "--cli", "--command", "shelve-export", "--path", root, "--shelve", "4",
                "--output", manifestOutput, "--yes", "--json", "--cm", fake);
            string[] manifestCalls = File.ReadAllLines(Path.Combine(root, ".plastic", "calls"));
            Check(manifestCollision.ExitCode == 2 && !Directory.Exists(manifestOutput) &&
                !manifestCalls.Any(line => line.StartsWith("cat\t", StringComparison.Ordinal)),
                "reserved manifest collision is rejected before downloading content");

            foreach (string command in new[] { "shelve-apply", "shelve-delete" })
            {
                File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector);
                File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "captured-race");
                foreach (string name in new[] { "calls", "raced" })
                {
                    string path = Path.Combine(root, ".plastic", name); if (File.Exists(path)) File.Delete(path);
                }
                var raced = Run(application, "--cli", "--command", command, "--path", root, "--shelve", "4", "--yes", "--json", "--cm", fake);
                string[] raceCalls = File.ReadAllLines(Path.Combine(root, ".plastic", "calls"));
                Check(raced.ExitCode == 1 && !raceCalls.Any(line => line.StartsWith("shelveset\t", StringComparison.Ordinal)),
                    command + " refuses a selector change after CLI confirmation and starts no mutation");
            }

            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), selector);
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "unsafe");
            var unsafeDiff = Run(application, "--cli", "--command", "shelve-diff", "--path", root, "--shelve", "4", "--json", "--cm", fake);
            Check(unsafeDiff.ExitCode == 1 && unsafeDiff.Text.Contains("unsafe or ambiguous"), "unsafe repository path from cm is rejected as incomplete data");

            Console.WriteLine("PASS: " + assertions + " shelveset comparison/export CLI assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            string output = root == null ? null : Path.Combine(Path.GetDirectoryName(root), Path.GetFileName(root) + "-export");
            foreach (string path in new[] { output, output + "-deleted", output + "-manifest", output + "-mixed" })
                if (Directory.Exists(path)) Directory.Delete(path, true);
            if (Directory.Exists(otherWorkspace)) Directory.Delete(otherWorkspace, true);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static Result Run(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo { FileName = executable, WorkingDirectory = root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            Arguments = String.Join(" ", arguments.Select(Quote)) };
        using (var process = Process.Start(start))
        {
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Shelveset CLI test timed out."); }
            return new Result { ExitCode = process.ExitCode, Text = output.GetAwaiter().GetResult(), Error = error.GetAwaiter().GetResult() };
        }
    }

    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        string mode = File.ReadAllText(Path.Combine(metadata, "mode"));
        File.AppendAllText(Path.Combine(metadata, "calls"), String.Join("\t", args) + Environment.NewLine);
        if (args[0] == "status")
        {
            if (mode == "captured-race" && !File.Exists(Path.Combine(metadata, "raced")))
            {
                File.WriteAllText(Path.Combine(metadata, "raced"), "yes");
                File.AppendAllText(Path.Combine(metadata, "plastic.selector"), "# changed after confirmation\n");
            }
            Console.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><StatusOutput><WorkspaceStatus><Status><Changeset>22</Changeset><RepSpec><Name>test</Name><Server>server:8087</Server></RepSpec></Status></WorkspaceStatus><WkConfigType>branch</WkConfigType><WkConfigName>/main@" + Repository + "</WkConfigName></StatusOutput>");
            return 0;
        }
        if (args[0] == "find")
        {
            Console.Write(new XElement("PLASTICQUERY", new XElement("SHELVE", new XElement("ID", 101), new XElement("SHELVEID", 4),
                new XElement("COMMENT", "saved"), new XElement("DATE", "2026-09-28T12:00:00+08:00"), new XElement("OWNER", "tester"),
                new XElement("REPOSITORY", "test"), new XElement("REPNAME", "test"), new XElement("REPSERVER", "server:8087"),
                new XElement("PARENT", 22), new XElement("GUID", Guid.NewGuid())))); return 0;
        }
        if (args[0] == "diff")
        {
            if (mode == "mixed") {
                Console.WriteLine("A|\"/added.txt\"|F|\"\"|\"\"\nD|\"/gone.txt\"|F|\"\"|\"\"\nM|\"/old.txt\"|F|\"/old.txt\"|\"/renamed.txt\"\nC|\"/binary.dat\"|F|\"\"|\"\"");
                return 0;
            }
            Console.WriteLine(mode == "unsafe" ? "C|\"/../escape.txt\"|F|\"\"|\"\"" :
                mode == "deleted" ? "D|\"/gone.txt\"|F|\"\"|\"\"" :
                mode == "manifest" ? "A|\"/shelveset.manifest\"|F|\"\"|\"\"" :
                mode == "nested" ? "C|\"/nested/one.txt\"|F|\"\"|\"\"" :
                "C|\"/folder/one.txt\"|F|\"\"|\"\"");
            return 0;
        }
        if (args[0] == "ls")
        {
            Console.Write(new XElement("LsResults", new XElement("LsItem", new XElement("CurrentPath", args[1]),
                new XElement("Name", "one.txt"), new XElement("Type", "file"), new XElement("Repository", Repository)))); return 0;
        }
        if (args[0] == "cat")
        {
            string target = args.Single(value => value.StartsWith("--file=", StringComparison.Ordinal)).Substring(7);
            if (mode == "download-failure") { Console.Error.WriteLine("Simulated server download failure"); return 7; }
            if (args[1].Contains("/binary.dat#")) { File.WriteAllBytes(target, new byte[] { 0, 255, args[1].Contains("#cs:") ? (byte)1 : (byte)2 }); return 0; }
            if ((args[1].Contains("/added.txt#") && args[1].Contains("#cs:")) || (args[1].Contains("/gone.txt#") && args[1].Contains("#sh:"))) return 91;
            File.WriteAllText(target, args[1].Contains("#cs:22@") ? "old\n" : "new\n", new UTF8Encoding(false)); return 0;
        }
        Console.Error.WriteLine("Unexpected fake cm command: " + String.Join(" ", args)); return 90;
    }

    private static Dictionary<string, object> Json(string text)
    { return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(text); }

    private static string Quote(string argument)
    {
        var output = new StringBuilder("\""); int slashes = 0;
        foreach (char value in argument)
        {
            if (value == '\\') { slashes++; continue; }
            if (value == '"') output.Append('\\', slashes * 2 + 1).Append('"');
            else output.Append('\\', slashes).Append(value);
            slashes = 0;
        }
        return output.Append('\\', slashes * 2).Append('"').ToString();
    }

    private sealed class Result { public int ExitCode; public string Text; public string Error; }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
}
