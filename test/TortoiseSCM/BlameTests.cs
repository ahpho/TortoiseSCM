// GPL-2.0-or-later. Read-only Plastic annotate/blame parser and command tests.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using TortoiseSCM;

internal static class BlameTests
{
    private const string Repository = "test@server:8087";
    private static int assertions;
    private static string root;

    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-blame-test-" + Guid.NewGuid().ToString("N"));
        string metadata = Path.Combine(root, ".plastic");
        Directory.CreateDirectory(metadata);
        try
        {
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "blame-fixture\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"/main\"\n");
            string file = Path.Combine(root, "source 中文 & pipe.txt"); File.WriteAllText(file, "one\n\nthird\twith tab\n");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(5) });

            var lines = client.GetBlameAsync(file, new PlasticBlameOptions { Ignore = "eol&whitespaces" }, CancellationToken.None).GetAwaiter().GetResult();
            Check(lines.Count == 3 && lines[0].Line == 1 && lines[0].Content == "one|中文", "Blame preserves first line and pipe/Unicode content");
            Check(lines[1].Line == 2 && lines[1].Content == "" && lines[1].IsMergeRevision, "Blame preserves empty source lines and merge flag");
            Check(lines[2].Line == 3 && lines[2].Content == "third\twith tab" && lines[2].Comment == "comment | pipe", "Content-last parsing preserves tabs while comments retain pipes");
            Check(lines[0].Repository == Repository && lines[0].Revision == "br:/main#17" && lines[0].Changeset == 17, "Blame retains revision identity and repository");
            string call = File.ReadAllText(Path.Combine(metadata, "call"));
            Check(call.Contains("annotate\t" + file) && call.Contains("--encoding=utf-8") && call.Contains("--dateformat=o") && call.Contains("--ignore=eol&whitespaces"),
                "Blame passes a literal read-only annotate command and ignore mode");

            Reject<ArgumentException>(() => client.GetBlameAsync(root, CancellationToken.None).GetAwaiter().GetResult(), "Directory blame is rejected before native execution");
            File.WriteAllText(Path.Combine(metadata, "mode"), "binary");
            Reject<PlasticCommandException>(() => client.GetBlameAsync(file, CancellationToken.None).GetAwaiter().GetResult(), "Native binary rejection is surfaced as a command error");
            File.WriteAllText(Path.Combine(metadata, "mode"), "normal");
            Reject<ArgumentException>(() => client.GetBlameAsync(file, new PlasticBlameOptions { Ignore = "invalid" }, CancellationToken.None).GetAwaiter().GetResult(), "Invalid ignore mode is rejected before native execution");

            foreach (string bad in new[] {
                "1\talice\t17\tdate\tbr:/main\tbr:/main#17\tfalse\tother@server:8087\tcomment\tcontent",
                "2\talice\t17\tdate\tbr:/main\tbr:/main#17\tfalse\t" + Repository + "\tcomment\tcontent",
                "1\talice\t17\tdate\tbr:/main\tbr:/main#17\tunknown\t" + Repository + "\tcomment\tcontent",
                "1\talice\t17\tdate\tbr:/main\tbr:/main#17\tfalse\t" + Repository + "\tcomment"
            }) Reject<InvalidDataException>(() => PlasticClient.ParseBlame(bad, Repository), "Malformed or foreign annotate row is rejected");
            Check(PlasticClient.ParseBlame("", Repository).Count == 0, "Empty file annotate output is valid");
            Console.WriteLine("PASS: " + assertions + " blame assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        File.WriteAllText(Path.Combine(metadata, "call"), String.Join("\t", args), new UTF8Encoding(false));
        if (args.Length < 1 || args[0] != "annotate") { Console.Error.WriteLine("Unexpected fake cm invocation"); return 90; }
        if (File.Exists(Path.Combine(metadata, "mode")) && File.ReadAllText(Path.Combine(metadata, "mode")) == "binary")
        { Console.Error.WriteLine("Cannot annotate binary files."); return 7; }
        Console.Write("1\talice\t17\t2026-09-27T12:00:00.0000000+08:00\tbr:/main\tbr:/main#17\tfalse\t" + Repository + "\tcomment | pipe\tone|中文\n");
        Console.Write("2\tbob\t18\t2026-09-27T12:01:00.0000000+08:00\tbr:/main\tbr:/main#18\t是\t" + Repository + "\t\t\n");
        Console.Write("3\talice\t17\t2026-09-27T12:00:00.0000000+08:00\tbr:/main\tbr:/main#17\t否\t" + Repository + "\tcomment | pipe\tthird\twith tab\n");
        return 0;
    }

    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
}
