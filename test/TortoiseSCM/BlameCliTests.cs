// GPL-2.0-or-later. Headless CLI annotate/blame transport checks.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;
using TortoiseSCM;

internal static class BlameCliTests
{
    private const string Repository = "test@server:8087";
    private static int assertions;
    private static string root;

    private static int Main(string[] args)
    {
        if (args.Length > 0 && (args[0] == "annotate" || args[0] == "status")) return FakeCm(args);
        if (args.Length != 1) { Console.Error.WriteLine("Usage: BlameCliTests.exe <TortoiseSCM.exe>"); return 2; }
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-blame-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "cli-blame\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"/main\"\n");
            string file = Path.Combine(root, "cli 中文.txt"); File.WriteAllText(file, "bytes");
            var response = Run(args[0], "--cli", "--command", "blame", "--path", file, "--ignore", "eol", "--json", "--cm", Assembly.GetExecutingAssembly().Location);
            Check(response.ExitCode == 0, "Blame CLI succeeds: " + response.Text);
            var json = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(response.Text);
            Check(Convert.ToBoolean(json["success"]) && Convert.ToInt32(json["exitCode"]) == 0, "Blame CLI JSON reports success");
            var data = (Dictionary<string, object>)json["data"];
            var rows = (IList)data["lines"];
            Check(rows.Count == 1 && ((Dictionary<string, object>)rows[0])["content"].ToString() == "cli|中文", "Blame CLI returns structured Unicode line data");
            Check(data["path"].ToString() == file && data["ignore"].ToString() == "eol", "Blame CLI retains selected file and ignore mode");
            var rejected = Run(args[0], "--cli", "--command", "blame", "--path", root, "--json", "--cm", Assembly.GetExecutingAssembly().Location);
            Check(rejected.ExitCode == 2, "Blame CLI rejects a directory at operation time");
            Console.WriteLine("PASS: " + assertions + " blame CLI assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static Result Run(string executable, params string[] arguments)
    {
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
        if (args.Length > 0 && args[0] == "status")
        {
            Console.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><StatusOutput><WorkspaceStatus><Status><Changeset>22</Changeset><RepSpec><Name>test</Name><Server>server:8087</Server></RepSpec></Status></WorkspaceStatus><WkConfigType>branch</WkConfigType><WkConfigName>/main@" + Repository + "</WkConfigName></StatusOutput>");
            return 0;
        }
        if (args.Length > 0 && args[0] == "annotate")
        {
            Console.Write("1\talice\t17\t2026-09-27T12:00:00.0000000+08:00\tbr:/main\tbr:/main#17\tfalse\t" + Repository + "\tcomment\tcli|中文\n");
            return 0;
        }
        Console.Error.WriteLine("Unexpected fake cm command: " + String.Join(" ", args)); return 90;
    }

    private sealed class Result { public int ExitCode; public string Text; public string Error; }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
}
