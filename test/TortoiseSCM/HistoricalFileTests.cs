// GPL-2.0-or-later. Historical file read/export integration, isolated from user data.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class HistoricalFileTests
{
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] != "--real") return FakeCm(args);
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-history-test-" + Guid.NewGuid().ToString("N") + " 中文 & space");
        Directory.CreateDirectory(temporary);
        try
        {
            if (args.Length > 0)
            {
                var real = new PlasticClient(new PlasticClientConfig());
                var file = real.GetHistoricalFileAsync(args[1], args[2], Int64.Parse(args[3]), CancellationToken.None).GetAwaiter().GetResult();
                Check(file.Content.Length > 0, "Server historical file has bytes");
                string destination = Path.Combine(temporary, "real-export.bin");
                var realExport = real.ExportRevisionAsync(args[1], args[2], Int64.Parse(args[3]), destination, false, CancellationToken.None).GetAwaiter().GetResult();
                Check(realExport.Succeeded && File.ReadAllBytes(destination).SequenceEqual(file.Content), "Server export bytes match historical read");
                Check(real.GetRevisionDiffAsync(args[1], args[2], Int64.Parse(args[3]), Int64.Parse(args[4]), CancellationToken.None).GetAwaiter().GetResult().HasChanges, "Server two-revision comparison finds content changes");
                Console.WriteLine("PASS: " + assertions + " real historical file assertions"); return 0;
            }
            string workspace = Path.Combine(temporary, "workspace");
            Directory.CreateDirectory(Path.Combine(workspace, ".plastic"));
            File.WriteAllText(Path.Combine(workspace, ".plastic", "plastic.workspace"), "history\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(workspace, ".plastic", "plastic.selector"), "repository \"test@server:8087\"");
            string executable = Assembly.GetExecutingAssembly().Location;
            var config = new PlasticClientConfig { CmPath = executable, DiffToolPath = executable,
                DiffToolArguments = "--viewer \"{base}\" \"{local}\"", Timeout = TimeSpan.FromSeconds(10) };
            var client = new PlasticClient(config);
            var token = CancellationToken.None;
            string path = "/deleted 中文 & file.txt";
            var fileData = client.GetHistoricalFileAsync(workspace, path, 1, token).GetAwaiter().GetResult();
            Check(Encoding.UTF8.GetString(fileData.Content) == "before 中文\r\n", "History reads deleted local file from server path");
            Check(fileData.RevisionSpec == "serverpath:" + path + "#cs:1@test@server:8087", "Revision spec pins path, changeset and repository");
            Check(!File.Exists(Path.Combine(workspace, path.Substring(1))), "Historical reads never restore into workspace");
            var diff = client.GetRevisionDiffAsync(workspace, path, 1, 2, token).GetAwaiter().GetResult();
            Check(diff.HasChanges && !diff.IsBinary && diff.DiffText.Contains("-before 中文") && diff.DiffText.Contains("+after 中文"), "Text diff compares two selected revisions");
            Check(diff.DiffText.Contains("(cs:1)") && diff.DiffText.Contains("(cs:2)"), "Diff headers identify both revisions");
            Check(!client.GetRevisionDiffAsync(workspace, path, 1, 1, token).GetAwaiter().GetResult().HasChanges, "Identical historical revisions have no diff");
            var binary = client.GetRevisionDiffAsync(workspace, "/binary.bin", 1, 2, token).GetAwaiter().GetResult();
            Check(binary.IsBinary && binary.HasChanges, "Binary diff reports bytes changed without corrupt text");
            string exported = Path.Combine(temporary, "export 中文 &.bin");
            Check(client.ExportRevisionAsync(workspace, "/binary.bin", 1, exported, false, token).GetAwaiter().GetResult().Succeeded, "Binary export succeeds");
            Check(File.ReadAllBytes(exported).SequenceEqual(new byte[] { 0, 255, 1, 0, 13, 10 }), "Binary export is byte exact");
            Reject(() => client.ExportRevisionAsync(workspace, path, 2, exported, false, token).GetAwaiter().GetResult(), "Existing export requires explicit overwrite");
            Check(File.ReadAllBytes(exported).SequenceEqual(new byte[] { 0, 255, 1, 0, 13, 10 }), "Rejected overwrite preserves existing bytes");
            client.ExportRevisionAsync(workspace, path, 2, exported, true, token).GetAwaiter().GetResult();
            Check(File.ReadAllText(exported, Encoding.UTF8) == "after 中文\r\n", "Explicit overwrite atomically replaces content");
            bool failed = false;
            try { client.ExportRevisionAsync(workspace, path, 99, exported, true, token).GetAwaiter().GetResult(); }
            catch (PlasticCommandException) { failed = true; }
            Check(failed && File.ReadAllText(exported, Encoding.UTF8) == "after 中文\r\n", "Server error never replaces export target");
            Check(!Directory.GetFiles(temporary, ".tortoisescm-export-*").Any(), "No export staging files remain");
            foreach (string unsafePath in new[] { "/../x", "/a/../x", "/a//x", "/a/./x", "/.plastic/x", "/x#cs:2", "/x@other", "relative", "/a\\b", "/a/", "/a?", "/x. ", "/" })
                Reject(() => client.GetHistoricalFileAsync(workspace, unsafePath, 1, token).GetAwaiter().GetResult(), "Unsafe repository path rejected: " + unsafePath);
            Reject(() => client.GetHistoricalFileAsync(workspace, path, -1, token).GetAwaiter().GetResult(), "Negative changeset rejected");
            Reject(() => client.GetHistoricalFileAsync(workspace, "/directory", 1, token).GetAwaiter().GetResult(), "Historical directory rejected");
            Reject(() => client.GetHistoricalFileAsync(workspace, "/missing", 1, token).GetAwaiter().GetResult(), "Missing path is explicit error rather than empty file");
            Reject(() => client.GetHistoricalFileAsync(workspace, "/link", 1, token).GetAwaiter().GetResult(), "Historical symlink rejected");
            Reject(() => client.ExportRevisionAsync(workspace, path, 1, Path.Combine(workspace, ".plastic", "plastic.selector"), true, token).GetAwaiter().GetResult(), "Export cannot overwrite workspace metadata");
            Reject(() => client.ExportRevisionAsync(workspace, path, 1, exported + ":stream", true, token).GetAwaiter().GetResult(), "Export cannot write alternate data stream");
            Reject(() => client.ExportRevisionAsync(workspace, path, 1, Path.Combine(workspace, ".plastic.", "plastic.selector"), true, token).GetAwaiter().GetResult(), "Export rejects metadata directory spelling alias");
            Reject(() => client.ExportRevisionAsync(workspace, path, 1, Path.Combine(temporary, "CON.txt"), true, token).GetAwaiter().GetResult(), "Export rejects reserved Windows device");
            var viewer = client.OpenRevisionDiffToolAsync(workspace, path, 1, 2, token).GetAwaiter().GetResult();
            Check(viewer.Succeeded && viewer.Output.Contains("viewer bytes verified"), "External viewer receives both historical contents until exit");
            string[] viewerFiles = viewer.Output.Trim().Split('\n').Take(2).Select(s => s.TrimEnd('\r')).ToArray();
            Check(viewerFiles.Length == 2 && viewerFiles.All(p => !File.Exists(p)), "Historical viewer temporary files are removed after exit");
            config.DiffToolArguments = "--viewer-fail {base} {local}";
            Check(client.OpenRevisionDiffToolAsync(workspace, path, 1, 2, token).GetAwaiter().GetResult().ExitCode == 17, "Viewer exit code preserved");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); bool cancelled = false;
                try { client.ExportRevisionAsync(workspace, path, 1, Path.Combine(temporary, "cancelled"), false, cancel.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && !File.Exists(Path.Combine(temporary, "cancelled")), "Cancellation does not create export target");
            }
            Console.WriteLine("PASS: " + assertions + " historical file assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "ls")
        {
            if (args.Any(a => a.StartsWith("--tree=cs:99"))) { Console.Error.WriteLine("Deliberate server error"); return 8; }
            XElement item = args[1] == "/missing" ? null : new XElement("LsItem", new XElement("CurrentPath", args[1]),
                new XElement("Name", args[1] == "/directory" ? "." : Path.GetFileName(args[1])),
                new XElement("Type", args[1] == "/directory" ? "目录" : "文件"), new XElement("SymlinkTarget", args[1] == "/link" ? "/target" : ""));
            Console.WriteLine(new XElement("LsResults", new XElement("LsItems", item))); return 0;
        }
        if (args[0] == "cat")
        {
            string destination = args.Single(a => a.StartsWith("--file=")).Substring(7);
            bool first = args[1].Contains("#cs:1@");
            byte[] content = args[1].Contains("/binary.bin#") ? new byte[] { 0, 255, (byte)(first ? 1 : 2), 0, 13, 10 } : new UTF8Encoding(false).GetBytes(first ? "before 中文\r\n" : "after 中文\r\n");
            File.WriteAllBytes(destination, content); return 0;
        }
        if (args[0] == "--viewer")
        {
            Thread.Sleep(150);
            if (File.ReadAllText(args[1], Encoding.UTF8) != "before 中文\r\n" || File.ReadAllText(args[2], Encoding.UTF8) != "after 中文\r\n") return 9;
            Console.WriteLine(args[1]); Console.WriteLine(args[2]); Console.WriteLine("viewer bytes verified"); return 0;
        }
        if (args[0] == "--viewer-fail") return 17;
        return 20;
    }

    private static void Check(bool condition, string description) { assertions++; if (!condition) throw new Exception(description); }
    private static void Reject(Action action, string description)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, description); }
}
