// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TortoiseSCM;

internal static class BackendTests
{
    private static int assertions;
    private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message) { bool failed = false; try { action(); } catch (ArgumentException) { failed = true; } Assert(failed, message); }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "shell") return FakeShellCheckin(args);
        if (args.Length > 0 && args[0] == "status")
        {
            string changeset = File.Exists(Path.Combine(Environment.CurrentDirectory, "fake-partial")) ? "-1" : "0";
            Console.WriteLine("<StatusOutput><WorkspaceStatus><Status><Changeset>" + changeset + "</Changeset></Status></WorkspaceStatus></StatusOutput>");
            return 0;
        }
        if (args.Length > 1 && args[0] == "partial" && args[1] == "update")
        {
            File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "update-calls.log"), "partial " + args[2] + Environment.NewLine);
            Console.WriteLine("Updated " + args[2]);
            if (Path.GetFileName(args[2]) == "fail.txt") { Console.Error.WriteLine("Deliberate update failure"); return 7; }
            return 0;
        }
        if (args.Length > 0 && args[0] == "update")
        {
            // Fake cm: records the exact requested scope without touching repository data.
            Console.OutputEncoding = Encoding.UTF8;
            File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "update-calls.log"), args[1] + Environment.NewLine);
            Console.WriteLine("Updated " + args[1]);
            if (Path.GetFileName(args[1]) == "fail.txt") { Console.Error.WriteLine("Deliberate update failure"); return 7; }
            return 0;
        }
        if (args.Length > 0 && (args[0] == "checkin" || (args.Length > 1 && args[0] == "partial" && args[1] == "checkin")))
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "checkin-args.log"), String.Join("|", args));
            string clientConfig = args.FirstOrDefault(argument => argument.StartsWith("--clientconf=", StringComparison.OrdinalIgnoreCase));
            if (clientConfig != null)
                File.Copy(clientConfig.Substring("--clientconf=".Length), Path.Combine(Environment.CurrentDirectory, "checkin-config-copy.xml"), true);
            using (var input = Console.OpenStandardInput())
            using (var memory = new MemoryStream())
            { input.CopyTo(memory); File.WriteAllBytes(Path.Combine(Environment.CurrentDirectory, "checkin-stdin.bin"), memory.ToArray()); }
            if (args.Contains("-c=wait")) Thread.Sleep(30000);
            if (args.Contains("-c=fail")) return 17;
            return 0;
        }
        if (args.Length > 0 && args[0] == "--helper")
        {
            Console.OutputEncoding = Encoding.UTF8;
            if (args[1] == "wait") Thread.Sleep(30000);
            else if (args[1] == "output") { for (int i = 0; i < 20000; i++) { Console.Out.WriteLine("stdout 中文 " + i); Console.Error.WriteLine("stderr 中文 " + i); } }
            else if (args[1] == "stdin")
            {
                using (var input = Console.OpenStandardInput())
                using (var memory = new MemoryStream())
                { input.CopyTo(memory); Console.Write(Convert.ToBase64String(memory.ToArray())); }
            }
            else if (args[1] == "gbk-error")
            {
                // Simulate cm installations that write localized diagnostics
                // using the Windows Chinese console code page.
                byte[] diagnostic = Encoding.GetEncoding(936).GetBytes("\u6279\u91cf\u7b7e\u5165\u5931\u8d25\uff1a\u8def\u5f84\u592a\u957f");
                using (Stream error = Console.OpenStandardError()) error.Write(diagnostic, 0, diagnostic.Length);
                return 17;
            }
            else if (args[1] == "gbk-output")
            {
                byte[] diagnostic = Encoding.GetEncoding(936).GetBytes("批量签入失败：路径太长");
                using (Stream output = Console.OpenStandardOutput()) output.Write(diagnostic, 0, diagnostic.Length);
                return 17;
            }
            else if (args[1] == "utf8-error")
            {
                byte[] diagnostic = new UTF8Encoding(false).GetBytes("UTF-8 diagnostic \u4E2D\u6587");
                using (Stream error = Console.OpenStandardError()) error.Write(diagnostic, 0, diagnostic.Length);
                return 17;
            }
            else foreach (string value in args.Skip(2)) Console.WriteLine(value);
            return 0;
        }
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temporary, ".plastic"));
        File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "Test\r\n00000000-0000-0000-0000-000000000000\r\nStandard\r\n");
        try
        {
            var config = new PlasticClientConfig();
            var client = new PlasticClient(config);
            Assert(client.DiscoverWorkspace(Path.Combine(temporary, "deleted.txt")).RootPath == temporary, "Deleted path workspace discovery");
            Assert(client.DiscoverWorkspace(temporary + "\\").RootPath == temporary, "Trailing separator normalized");
            Assert(client.DiscoverWorkspace(Path.Combine(temporary, ".plastic", "plastic.workspace")) == null, "Metadata workspace discovery rejected");
            var request = new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = temporary, Comment = "quoted \" 中文 & | comment" };
            Reject(delegate { client.Build(request); }, "Empty checkin selection rejected");
            request.Paths.Add(Path.Combine(temporary, "中文 space.txt"));
            var built = client.Build(request);
            Assert(built.Arguments.Contains("-c=" + request.Comment), "Checkin comment remains one argument");
            Assert(built.Arguments.Contains("中文 space.txt"), "Selected path retained relative to the workspace process directory");
            Assert(!built.Arguments.Contains("--all"), "Explicit file checkin does not request recursive discovery");
            request.Paths[0] = Path.Combine(temporary, "-c=selected.txt");
            built = client.Build(request);
            Assert(built.Arguments.Contains(".\\-c=selected.txt") && !built.Arguments.Contains("-c=selected.txt"),
                "Leading-dash selected filenames stay paths rather than cm options");
            request.Paths.Clear();
            for (int i = 0; i < 300; i++) request.Paths.Add(Path.Combine(temporary,
                "bulk-" + i.ToString("D4") + "-" + new String('x', 80) + " 中文.txt"));
            built = client.Build(request);
            Assert(built.Arguments.SequenceEqual(new[] { "checkin", "-", "-c=" + request.Comment }),
                "Large standard checkin switches to stdin without splitting the changeset");
            Assert(built.StandardInput.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .SequenceEqual(request.Paths.Select(path => path.Substring(temporary.TrimEnd('\\').Length).TrimStart('\\'))),
                "Large standard checkin writes every exact workspace-relative path to stdin");
            request.Paths.Clear();
            for (int i = 0; i < 194; i++) request.Paths.Add(Path.Combine(temporary, "bulk-194-" + i.ToString("D3") + ".txt"));
            built = client.Build(request);
            Assert(built.Arguments.SequenceEqual(new[] { "checkin", "-", "-c=" + request.Comment }),
                "Automatic 194-file checkin uses one stdin invocation");
            request.CheckinInputMode = PlasticCheckinInputMode.Paths;
            built = client.Build(request);
            Assert(String.IsNullOrEmpty(built.StandardInput) && !built.Arguments.Contains("-") &&
                built.Arguments.Skip(1).Take(request.Paths.Count).SequenceEqual(request.Paths.Select(Path.GetFileName)),
                "Forced path mode keeps all 194 relative arguments in one checkin");
            request.CheckinInputMode = PlasticCheckinInputMode.Automatic;
            request.Paths.Clear();
            for (int i = 0; i < 65; i++) request.Paths.Add(Path.Combine(temporary, "bulk-065-" + i.ToString("D3") + ".txt"));
            built = client.Build(request);
            Assert(String.IsNullOrEmpty(built.StandardInput) && built.Arguments.Count == 67,
                "65 selected files do not imply a native parser limit");
            request.CheckinInputMode = PlasticCheckinInputMode.StandardInput;
            request.Paths.Clear(); request.Paths.Add(Path.Combine(temporary, "中文 space.txt"));
            built = client.Build(request);
            Assert(built.Arguments.SequenceEqual(new[] { "checkin", "-", "-c=" + request.Comment }) &&
                built.StandardInput == "中文 space.txt" + Environment.NewLine + Environment.NewLine,
                "Forced stdin accepts an exact single-file selection");
            request.CheckinInputMode = PlasticCheckinInputMode.Automatic;
            request.Paths.Clear(); request.Paths.Add(Path.Combine(temporary, "中文 space.txt"));
            request.Paths[0] = Path.GetTempPath(); Reject(delegate { client.Build(request); }, "Outside workspace rejected");
            request.Paths[0] = Path.Combine(temporary, ".plastic", "plastic.workspace"); Reject(delegate { client.Build(request); }, "Metadata rejected");
            request.Paths[0] = Path.Combine(temporary, "*.txt"); Reject(delegate { client.Build(request); }, "Wildcard rejected");
            request.Paths[0] = Path.Combine(temporary, "file.txt");
            File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "Test\r\nguid\r\nPartial\r\n");
            Assert(client.DiscoverWorkspace(temporary).IsPartial, "Partial workspace detection");
            built = client.Build(request); Assert(built.Arguments[0] == "partial" && built.Arguments[1] == "checkin", "Partial command routing");
            request.Paths.Clear();
            for (int i = 0; i < 300; i++) request.Paths.Add(Path.Combine(temporary,
                "partial-bulk-" + i.ToString("D4") + "-" + new String('y', 80) + " 中文.txt"));
            built = client.Build(request);
            Assert(built.Arguments.SequenceEqual(new[] { "partial", "checkin", "-", "-c=" + request.Comment }) &&
                built.StandardInput.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                    .SequenceEqual(request.Paths),
                "Large partial checkin uses absolute stdin paths without splitting the changeset");
            string xml = "<StatusOutput><Changes><Change><Type>MV</Type><TypeVerbose>Moved</TypeVerbose><Path>new.txt</Path><OldPath>old.txt</OldPath><RevisionType>enTextFile</RevisionType></Change><Change><Type>DE</Type><Path>gone</Path><RevisionType>enDirectory</RevisionType></Change></Changes></StatusOutput>";
            var parsed = PlasticClient.ParseStatus(xml, temporary);
            Assert(parsed[0].Status == "MV" && parsed[0].OldPath == Path.Combine(temporary, "old.txt"), "Moved status preserves source and destination");
            Assert(parsed[1].IsDirectory, "Deleted directory parsed from revision type");
            bool rejectedPath = false;
            try { PlasticClient.ParseStatus("<StatusOutput><Changes><Change><Type>AD</Type><Path>..\\outside.txt</Path></Change></Changes></StatusOutput>", temporary); }
            catch (InvalidDataException) { rejectedPath = true; }
            Assert(rejectedPath, "Status paths cannot escape workspace");
            bool rejectedXml = false; try { PlasticClient.ParseStatus("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///does-not-exist'>]><StatusOutput>&e;</StatusOutput>", temporary); } catch (System.Xml.XmlException) { rejectedXml = true; }
            Assert(rejectedXml, "DTD prohibited");
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            var difficult = new [] { "space 中文", "trailing\\", "embed\"quote", "& | % !", "" };
            var helperArgs = new List<string> { "--helper", "args" }; helperArgs.AddRange(difficult);
            var result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = helperArgs }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded && result.Output.TrimStart('\uFEFF') == String.Join(Environment.NewLine, difficult) + Environment.NewLine, "Windows argument quoting round trip");
            string stdin = "first path\r\n中文 path\r\n\r\n";
            result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary,
                Arguments = new[] { "--helper", "stdin" }, StandardInput = stdin }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded && result.Output.TrimStart('\uFEFF') == Convert.ToBase64String(new UTF8Encoding(false).GetBytes(stdin)),
                "UTF-8 standard input round trip has no BOM before the first path");
            Encoding englishInput = PlasticClient.SelectCmStandardInputEncoding("en", 936);
            Assert(englishInput.CodePage == 936 && englishInput.GetPreamble().Length == 0 &&
                englishInput.GetBytes("中文").SequenceEqual(Encoding.GetEncoding(936).GetBytes("中文")),
                "English cm UI on Chinese Windows uses CP936 stdin without a BOM");
            foreach (string language in new [] { "zh-Hans", "zh-Hant", "ja", "ko" })
                Assert(PlasticClient.SelectCmStandardInputEncoding(language, 936).CodePage == 65001 &&
                    PlasticClient.SelectCmStandardInputEncoding(language, 936).GetPreamble().Length == 0,
                    "cm " + language + " UI uses BOM-less UTF-8 stdin");
            bool invalidInput = false;
            try { englishInput.GetBytes("\uD83D\uDE80.txt"); } catch (EncoderFallbackException) { invalidInput = true; }
            Assert(invalidInput, "cm legacy stdin rejects unrepresentable paths instead of replacing characters");
            result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "output" } }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Output.Contains("stdout 中文 19999") && result.Error.Contains("stderr 中文 19999"), "Both redirected streams drain without deadlock");
            result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "gbk-error" } }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.ExitCode == 17 && result.Error.Contains("\u6279\u91cf\u7b7e\u5165\u5931\u8d25") && result.Error.Contains("\u8def\u5f84\u592a\u957f"),
                "Localized GBK diagnostics remain readable");
            result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "utf8-error" } }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.ExitCode == 17 && result.Error.Contains("UTF-8 diagnostic 中文"),
                "Explicit UTF-8 diagnostics remain readable on localized Windows");
            result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "gbk-output" } }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.ExitCode == 17 && result.Output.Contains("\u6279\u91cf\u7b7e\u5165\u5931\u8d25") && result.Output.Contains("\u8def\u5f84\u592a\u957f"),
                "Localized GBK stdout diagnostics remain readable");
            config.Timeout = TimeSpan.FromMilliseconds(200);
            result = client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "wait" } }, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.TimedOut && !result.Succeeded, "Timeout terminates process");
            config.Timeout = TimeSpan.FromSeconds(30);
            using (var cancellation = new CancellationTokenSource(200))
            {
                bool cancelled = false;
                try { client.ExecuteAsync(new PlasticProcessCommand { FileName = exe, WorkingDirectory = temporary, Arguments = new [] { "--helper", "wait" } }, cancellation.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled, "Cancellation terminates process");
            }
            UpdateTests(temporary, exe);
            CheckinTransportTests(temporary, exe);
            ReadOperationTests();
            if (args.Length > 0 && args[0] == "--read-only")
            {
                string path = args[1];
                var detected = client.GetWorkspaceAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("Live workspace IsPartial=" + detected.IsPartial);
                var history = client.GetHistoryAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                Assert(history.Count > 0 && history[0].Changeset >= 0 && !String.IsNullOrEmpty(history[0].RevisionSpec), "Live structured history");
                var diff = client.GetDiffTextAsync(path, CancellationToken.None).GetAwaiter().GetResult();
                Assert(diff.Path == Path.GetFullPath(path) && !String.IsNullOrEmpty(diff.BaseRevision), "Live headless base comparison");
                Assert(!diff.HasChanges && !diff.IsBinary && diff.DiffText == "", "Unchanged live text has no diff");
            }
            else if (args.Length > 0) LiveTests(client, args[0]);
            Console.WriteLine("PASS: " + assertions + " assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static int FakeShellCheckin(string[] args)
    {
        // The real cm shell returns zero even when its inner checkin fails.
        Console.OutputEncoding = new UTF8Encoding(false);
        string input;
        using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true))) input = reader.ReadToEnd();
        File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "checkin-shell-input.txt"), input, new UTF8Encoding(false));
        using (var reader = new StringReader(input))
        {
            string header = reader.ReadLine();
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "checkin-shell-command.log"), header);
            var inner = new List<string>();
            var word = new StringBuilder(); bool quoted = false;
            foreach (char character in header)
            {
                if (character == '"') { quoted = !quoted; continue; }
                if (character == ' ' && !quoted) { if (word.Length > 0) { inner.Add(word.ToString()); word.Clear(); } }
                else word.Append(character);
            }
            if (word.Length > 0) inner.Add(word.ToString());
            bool isCheckin = inner.Count > 0 && (inner[0] == "checkin" || (inner.Count > 1 && inner[0] == "partial" && inner[1] == "checkin"));
            if (!isCheckin || quoted || !inner.Contains("-")) return 23;
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "checkin-args.log"), String.Join("|", args));
            string clientConfig = args.FirstOrDefault(argument => argument.StartsWith("--clientconf=", StringComparison.OrdinalIgnoreCase));
            if (clientConfig != null)
                File.Copy(clientConfig.Substring("--clientconf=".Length), Path.Combine(Environment.CurrentDirectory, "checkin-config-copy.xml"), true);
            string commentArgument = inner.Single(argument => argument.StartsWith("-commentsfile=", StringComparison.Ordinal));
            string comment = File.ReadAllText(commentArgument.Substring("-commentsfile=".Length), new UTF8Encoding(false, true));
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "checkin-comment.log"), comment, new UTF8Encoding(false));
            File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "checkin-shell-calls.log"), "checkin" + Environment.NewLine);
            if (comment == "early-fail")
            {
                Console.Error.WriteLine("Deliberate failure before reading paths"); Console.WriteLine("CommandResult 17");
                string residual;
                while ((residual = reader.ReadLine()) != null && residual != "exit")
                {
                    if (residual == "") continue;
                    if (!Path.IsPathRooted(residual)) File.AppendAllText(Path.Combine(Environment.CurrentDirectory, "checkin-shell-calls.log"), "unexpected-command" + Environment.NewLine);
                    Console.WriteLine("CommandResult 1");
                }
                return 0;
            }
            var paths = new List<string>(); string line;
            while ((line = reader.ReadLine()) != null && line != "") paths.Add(line);
            File.WriteAllBytes(Path.Combine(Environment.CurrentDirectory, "checkin-stdin.bin"),
                new UTF8Encoding(false).GetBytes(String.Join(Environment.NewLine, paths) + Environment.NewLine + Environment.NewLine));
            if (line == null || reader.ReadLine() != "exit" || reader.ReadLine() != null) return 23;
            if (comment == "wait") Thread.Sleep(30000);
            if (comment == "no-result") return 0;
            if (comment == "wrong-count") Console.WriteLine("CommandResult 0");
            if (comment == "fail") { Console.Error.WriteLine("Deliberate shell checkin failure 中文"); Console.WriteLine("CommandResult 17"); }
            else Console.WriteLine("CommandResult 0");
            return 0;
        }
    }

    private static void CheckinTransportTests(string temporary, string exe)
    {
        string root = Path.Combine(temporary, "native-cm-transport"); Directory.CreateDirectory(root);
        string fakeCm = Path.Combine(root, "cm.exe"); File.Copy(exe, fakeCm);
        string configPath = Path.Combine(root, "client.conf");
        string original = "<ClientConfigData><Language>en</Language><SecurityConfig>opaque-existing-auth</SecurityConfig><WorkspaceServer>server:8087</WorkspaceServer><PlasticProtoEnableLz4>no</PlasticProtoEnableLz4><UnknownSetting>preserve</UnknownSetting></ClientConfigData>";
        File.WriteAllText(configPath, original);
        var config = new PlasticClientConfig { CmPath = fakeCm, Timeout = TimeSpan.FromSeconds(10) };
        var client = new PlasticClient(config);
        var command = new PlasticProcessCommand { FileName = fakeCm, WorkingDirectory = root,
            Arguments = new [] { "partial", "checkin", "-", "--all", "-c=success" }, StandardInput = "中文.txt\r\n\r\n" };
        var result = client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        string[] args = File.ReadAllText(Path.Combine(root, "checkin-args.log")).Split('|');
        string copiedConfigPath = args.Single(argument => argument.StartsWith("--clientconf=")).Substring("--clientconf=".Length);
        string copiedConfig = File.ReadAllText(Path.Combine(root, "checkin-config-copy.xml"));
        Assert(result.Succeeded && copiedConfig.Contains("<PlasticProtoEnableLz4>yes</PlasticProtoEnableLz4>") &&
            copiedConfig.Contains("<Language>en</Language>") && copiedConfig.Contains("opaque-existing-auth") && copiedConfig.Contains("<UnknownSetting>preserve</UnknownSetting>"),
            "Native checkin enables supported protocol compression in a copy while preserving client settings");
        Assert(File.ReadAllText(configPath) == original && !File.Exists(copiedConfigPath) && !Directory.Exists(Path.GetDirectoryName(copiedConfigPath)),
            "Successful checkin leaves the original config unchanged and removes its temporary copy");
        Assert(File.ReadAllBytes(Path.Combine(root, "checkin-stdin.bin")).SequenceEqual(
            new UTF8Encoding(false).GetBytes(Path.Combine(root, "中文.txt") + "\r\n\r\n")) && args.Take(3).SequenceEqual(new [] { "shell", "--encoding=utf-8", "--enablestderr" }),
            "Native stdin uses the supported shell UTF-8 reader and absolute paths with no BOM");
        Assert(command.Arguments.SequenceEqual(new [] { "partial", "checkin", "-", "--all", "-c=success" }),
            "Temporary transport preparation does not mutate the planned command or change selection");
        foreach (string comment in new [] { "fail", "wait", "no-result", "wrong-count" })
        {
            command.Arguments = new [] { "checkin", "-", "-c=" + comment };
            config.Timeout = comment == "wait" ? TimeSpan.FromMilliseconds(250) : TimeSpan.FromSeconds(10);
            result = client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult();
            args = File.ReadAllText(Path.Combine(root, "checkin-args.log")).Split('|');
            copiedConfigPath = args.Single(argument => argument.StartsWith("--clientconf=")).Substring("--clientconf=".Length);
            Assert(!result.Succeeded && !File.Exists(copiedConfigPath) && !Directory.Exists(Path.GetDirectoryName(copiedConfigPath)) && File.ReadAllText(configPath) == original,
                "Temporary native checkin config is removed after " + comment);
        }
        config.Timeout = TimeSpan.FromSeconds(10);
        using (var cancellation = new CancellationTokenSource(250))
        {
            command.Arguments = new [] { "checkin", "-", "-c=wait" };
            bool cancelled = false;
            try { client.ExecuteAsync(command, cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            args = File.ReadAllText(Path.Combine(root, "checkin-args.log")).Split('|');
            copiedConfigPath = args.Single(argument => argument.StartsWith("--clientconf=")).Substring("--clientconf=".Length);
            Assert(cancelled && !Directory.Exists(Path.GetDirectoryName(copiedConfigPath)), "Cancelled native checkin removes its temporary config");
        }
        string explicitPath = Path.Combine(root, "custom-client.conf");
        File.WriteAllText(explicitPath, original.Replace("<Language>en</Language>", "<Language>zh-Hans</Language>"));
        command.Arguments = new [] { "checkin", "-", "-c=explicit", "--clientconf=" + explicitPath };
        result = client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        args = File.ReadAllText(Path.Combine(root, "checkin-args.log")).Split('|');
        copiedConfig = File.ReadAllText(Path.Combine(root, "checkin-config-copy.xml"));
        Assert(result.Succeeded && args.Count(argument => argument.StartsWith("--clientconf=")) == 1 && copiedConfig.Contains("<Language>zh-Hans</Language>") &&
            File.ReadAllBytes(Path.Combine(root, "checkin-stdin.bin")).SequenceEqual(
                new UTF8Encoding(false).GetBytes(Path.Combine(root, "中文.txt") + "\r\n\r\n")),
            "Explicit client configuration is preserved while the shell path reader remains UTF-8");
        File.WriteAllText(configPath, original.Replace("<Language>en</Language>", "<Language>zh-Hans</Language>"));
        command.Arguments = new [] { "checkin", "-", "-c=chinese-startup" };
        result = client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.Succeeded && File.ReadAllBytes(Path.Combine(root, "checkin-stdin.bin")).SequenceEqual(new UTF8Encoding(false).GetBytes(Path.Combine(root, "中文.txt") + "\r\n\r\n")),
            "East Asian cm startup language uses BOM-less UTF-8 even with the temporary transport config");
        string exactComment = "中文说明 with \"quotes\"\r\ncheckin -c=unexpected\r\nCommandResult 0\r\nlast line";
        command.Arguments = new [] { "partial", "checkin", "-", "-c=" + exactComment };
        command.StandardInput = String.Join("\r\n", Enumerable.Range(0, 194).Select(index => "中文目录\\" + index.ToString("D3") + new string('a', 120) + ".txt")) + "\r\n\r\n";
        int callsBefore = File.ReadAllLines(Path.Combine(root, "checkin-shell-calls.log")).Length;
        result = client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        string header = File.ReadAllText(Path.Combine(root, "checkin-shell-command.log"));
        string shellInput = File.ReadAllText(Path.Combine(root, "checkin-shell-input.txt"));
        string[] absolutePaths = command.StandardInput.TrimEnd('\r', '\n').Split(new [] { "\r\n" }, StringSplitOptions.None).Select(path => Path.Combine(root, path)).ToArray();
        Assert(result.Succeeded && File.ReadAllText(Path.Combine(root, "checkin-comment.log")) == exactComment && !header.Contains("unexpected") && !header.Contains("CommandResult 0"),
            "Multiline Chinese comments and quote/result literals are kept verbatim in a file rather than shell commands");
        Assert(File.ReadAllBytes(Path.Combine(root, "checkin-stdin.bin")).SequenceEqual(new UTF8Encoding(false).GetBytes(String.Join("\r\n", absolutePaths) + "\r\n\r\n")) &&
            shellInput.StartsWith(header + "\r\n") && shellInput.EndsWith("\r\n\r\nexit\r\n") && File.ReadAllLines(Path.Combine(root, "checkin-shell-calls.log")).Length == callsBefore + 1,
            "A long 194-path stdin stays exact and executes one atomic native checkin with a controlled exit");
        string commentsPath = header.Substring(header.IndexOf("-commentsfile=") + "-commentsfile=".Length).Trim('"');
        Assert(!File.Exists(commentsPath) && !Directory.Exists(Path.GetDirectoryName(commentsPath)), "Comments and transport files are removed after shell checkin");
        callsBefore = File.ReadAllLines(Path.Combine(root, "checkin-shell-calls.log")).Length;
        command.StandardInput = "中文.txt\r\n\r\ncheckin . -c=unexpected\r\n\r\n";
        Reject(delegate { client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult(); }, "An embedded blank path cannot inject another shell command");
        Assert(File.ReadAllLines(Path.Combine(root, "checkin-shell-calls.log")).Length == callsBefore, "Invalid path-list input never starts native checkin");
        command.StandardInput = "..\\outside.txt\r\n\r\n";
        Reject(delegate { client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult(); }, "Shell stdin cannot escape its workspace");
        command.Arguments = new [] { "checkin", "-", "-c=early-fail" };
        command.StandardInput = "checkin\r\nexit\r\nundo . --recursive\r\n\r\n";
        result = client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        Assert(!result.Succeeded && result.Error.Contains("inspect history") && File.ReadAllLines(Path.Combine(root, "checkin-shell-calls.log")).Length == callsBefore + 1 &&
            !File.ReadAllText(Path.Combine(root, "checkin-shell-calls.log")).Contains("unexpected-command"),
            "An early checkin failure leaves only absolute path tokens and cannot execute basename commands");
        header = File.ReadAllText(Path.Combine(root, "checkin-shell-command.log"));
        commentsPath = header.Substring(header.IndexOf("-commentsfile=") + "-commentsfile=".Length).Trim('"');
        Assert(!Directory.Exists(Path.GetDirectoryName(commentsPath)), "Uncertain early shell failure removes temporary comments and configuration");
        ShellResultTests();
        File.WriteAllText(configPath, original.Replace("<PlasticProtoEnableLz4>no</PlasticProtoEnableLz4>", "<PlasticProtoEnableLz4>yes</PlasticProtoEnableLz4>"));
        command.Arguments = new [] { "checkin", "selected.txt", "-c=already-enabled" }; command.StandardInput = null;
        result = client.ExecuteAsync(command, CancellationToken.None).GetAwaiter().GetResult();
        args = File.ReadAllText(Path.Combine(root, "checkin-args.log")).Split('|');
        Assert(result.Succeeded && !args.Any(argument => argument.StartsWith("--clientconf=")),
            "Already-enabled native compression uses the original config directly");
    }

    private static void ShellResultTests()
    {
        var result = PlasticClient.ReadShellCheckinResult(new PlasticCommandResult { ExitCode = 0, Output = "Created cs:12\r\nCommandResult 0\r\n" });
        Assert(result.Succeeded && result.Output.Contains("cs:12") && !result.Output.Contains("CommandResult"), "One native success marker is required and removed from user output");
        result = PlasticClient.ReadShellCheckinResult(new PlasticCommandResult { ExitCode = 0, Output = "CommandResult 17\r\n", Error = "Readable 中文 failure" });
        Assert(result.ExitCode == 17 && result.Error.Contains("Readable 中文"), "Inner checkin failure overrides cm shell process success");
        result = PlasticClient.ReadShellCheckinResult(new PlasticCommandResult { ExitCode = 7, Output = "CommandResult 0\r\n", Error = "Native process failed" });
        Assert(result.ExitCode == 7 && result.Error.Contains("Native process failed"), "Native process failure cannot be hidden by a success marker");
        foreach (string output in new [] { "no marker", "CommandResult 0\r\nCommandResult 0\r\n", "CommandResult 0\r\nCommandResult 1\r\n", "CommandResult 99999999999999\r\n" })
        {
            result = PlasticClient.ReadShellCheckinResult(new PlasticCommandResult { ExitCode = 0, Output = output });
            Assert(!result.Succeeded && result.Error.Contains("inspect history"), "Missing, multiple, or invalid checkin markers are an uncertain failure");
        }
    }

    private static void ReadOperationTests()
    {
        var diff = PlasticClient.CompareContent("中文 file.txt", "cs:1", Encoding.UTF8.GetBytes("first\nold\nlast\n"), Encoding.UTF8.GetBytes("first\nnew\nlast\n"), false);
        Assert(diff.HasChanges && !diff.IsBinary && diff.DiffText.Contains("-old\n+new\n") && diff.DiffText.Contains("@@ -1,3 +1,3 @@"), "Headless unified text diff");
        diff = PlasticClient.CompareContent("file", "cs:1", new byte[0], Encoding.UTF8.GetBytes("added"), false);
        Assert(diff.DiffText.Contains("@@ -0,0 +1,1 @@") && diff.DiffText.Contains("+added\n\\ No newline"), "Empty base and missing final newline");
        diff = PlasticClient.CompareContent("file", "cs:1", Encoding.UTF8.GetBytes("gone\n"), new byte[0], false);
        Assert(diff.DiffText.Contains("@@ -1,1 +0,0 @@") && diff.DiffText.Contains("-gone"), "Deleted text diff");
        diff = PlasticClient.CompareContent("bin", "cs:1", new byte[] { 0, 1 }, new byte[] { 0, 2 }, false);
        Assert(diff.IsBinary && diff.HasChanges && diff.DiffText.StartsWith("Binary files differ:"), "Binary content marked without replacement decoding");
        diff = PlasticClient.CompareContent("same", "cs:1", Encoding.UTF8.GetBytes("same"), Encoding.UTF8.GetBytes("same"), false);
        Assert(!diff.HasChanges && diff.DiffText == "", "Identical bytes produce no diff");
        byte[] utf16Old = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("旧\n")).ToArray();
        byte[] utf16New = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("新\n")).ToArray();
        diff = PlasticClient.CompareContent("utf16", "cs:1", utf16Old, utf16New, false);
        Assert(!diff.IsBinary && diff.DiffText.Contains("-旧\n+新\n"), "UTF-16 BOM text is decoded explicitly");
        string xml = "<RevisionHistoriesResult><RevisionHistories><RevisionHistory><ItemName>中文.txt</ItemName><Revisions><Revision><ChangesetNumber>42</ChangesetNumber><RevisionSpec>rev:x#cs:42</RevisionSpec><CreationDate>2026-09-24T12:00:00+08:00</CreationDate><Owner>user</Owner><Branch>/main</Branch><Comment>one &amp; two</Comment><Repository>repo</Repository></Revision></Revisions></RevisionHistory></RevisionHistories></RevisionHistoriesResult>";
        var history = PlasticClient.ParseHistory(xml);
        Assert(history.Count == 1 && history[0].Changeset == 42 && history[0].Comment == "one & two" && history[0].Path == "中文.txt", "Structured history retains identity and decoded comment");
        bool rejected = false;
        try { PlasticClient.ParseHistory("<Wrong />"); } catch (InvalidDataException) { rejected = true; }
        Assert(rejected, "Unexpected history XML rejected instead of empty success");
    }

    private static void UpdateTests(string temporary, string exe)
    {
        File.WriteAllText(Path.Combine(temporary, ".plastic", "plastic.workspace"), "Test\r\nguid\r\nStandard\r\n");
        File.WriteAllText(Path.Combine(temporary, "fake-partial"), "");
        var client = new PlasticClient(new PlasticClientConfig { CmPath = exe });
        string first = Path.Combine(temporary, "first.txt"), second = Path.Combine(temporary, "second.txt"), failed = Path.Combine(temporary, "fail.txt");
        string log = Path.Combine(temporary, "update-calls.log");
        var request = new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = temporary, Paths = new List<string> { first, second } };
        var result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.Succeeded && File.ReadAllLines(log).SequenceEqual(new [] { "partial " + first, "partial " + second }), "Partial multi-select update preserves each exact scope");
        File.Delete(log);
        request.Paths = new List<string> { first, failed, second };
        result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.ExitCode == 7 && File.ReadAllLines(log).SequenceEqual(new [] { "partial " + first, "partial " + failed }), "Multi-select update stops on first failure");
        Assert(result.Output.Contains(first) && result.Output.Contains(failed) && result.Error.Contains("Deliberate"), "Failed multi-update preserves completed output");
        File.Delete(log);
        request.Paths = new List<string> { first, Path.Combine(Path.GetTempPath(), "outside.txt") };
        Reject(delegate { client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult(); }, "Multi-update validates later paths before first execution");
        Assert(!File.Exists(log), "No update runs before complete selection validation");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel(); bool cancelled = false;
            request.Paths = new List<string> { first, second };
            try { client.RunAsync(request, cancellation.Token).GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !File.Exists(log), "Cancelled build never starts an update");
        }
        File.WriteAllText(Path.Combine(temporary, "fake-partial"), "");
        Assert(!client.DiscoverWorkspace(temporary).IsPartial && client.GetWorkspaceAsync(temporary, CancellationToken.None).GetAwaiter().GetResult().IsPartial,
            "Actual partial status overrides stale Standard metadata");
        request.Paths = new List<string> { first };
        result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.Succeeded && File.ReadAllLines(log).SequenceEqual(new [] { "partial " + first }), "Mutation routes through actual partial mode");
        request = new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = temporary, Comment = "stale metadata bulk", Recursive = true,
            CheckinInputMode = PlasticCheckinInputMode.StandardInput };
        for (int i = 0; i < 65; i++) request.Paths.Add(Path.Combine(temporary, "stale-bulk-" + i.ToString("D3") + ".txt"));
        result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        string[] checkinArgs = File.ReadAllText(Path.Combine(temporary, "checkin-args.log")).Split('|');
        string[] checkinPaths = Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(temporary, "checkin-stdin.bin")))
            .TrimEnd('\r', '\n').Split(new [] { "\r\n" }, StringSplitOptions.None);
        Assert(result.Succeeded && checkinArgs.Take(4).SequenceEqual(new [] { "partial", "checkin", "--all", "-" }),
            "Stale Standard metadata rebuilds a large checkin as partial stdin");
        Assert(checkinPaths.SequenceEqual(request.Paths), "Stale metadata partial stdin uses absolute paths");
        File.Delete(Path.Combine(temporary, "checkin-args.log")); File.Delete(Path.Combine(temporary, "checkin-stdin.bin"));
        File.Delete(Path.Combine(temporary, "fake-partial"));
        File.Delete(log);
        request = new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = temporary, Paths = new List<string> { first } };
        request.Paths = new List<string> { first };
        Reject(delegate { client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult(); }, "Standard file update requires explicit root scope");
        Assert(!File.Exists(log), "Rejected standard file update performs no mutation");
        request.Paths = new List<string> { temporary, second };
        Reject(delegate { client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult(); }, "Standard mixed root and file selection rejected before execution");
        Assert(!File.Exists(log), "No root update executes before later non-root rejection");
        request.Paths = new List<string> { temporary };
        result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
        Assert(result.Succeeded && File.ReadAllLines(log).SequenceEqual(new [] { temporary }), "Standard update accepts explicitly selected workspace root");
        File.Delete(log);
    }

    private static void LiveTests(PlasticClient client, string workspacePath)
    {
        var workspace = client.DiscoverWorkspace(workspacePath);
        Assert(workspace != null, "Live workspace found");
        var before = client.GetStatusAsync(workspace.RootPath, CancellationToken.None).GetAwaiter().GetResult();
        string file = Path.Combine(workspace.RootPath, "TortoiseSCM-test-" + Guid.NewGuid().ToString("N") + " 中文 & 空格.txt");
        bool added = false;
        try
        {
            using (var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false))) writer.Write("Disposable TortoiseSCM integration test.");
            var items = client.GetStatusAsync(file, CancellationToken.None).GetAwaiter().GetResult();
            Assert(items.Count == 1 && items[0].StatusCode == "PR" && items[0].Path.Equals(file, StringComparison.OrdinalIgnoreCase), "Live private UTF-8 path status");
            var request = new PlasticCommandRequest { Command = PlasticCommand.Add, WorkingDirectory = workspace.RootPath, Paths = new List<string> { file } };
            added = true;
            var result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded, "Live add: " + result.Error);
            items = client.GetStatusAsync(file, CancellationToken.None).GetAwaiter().GetResult();
            Assert(items.Count == 1 && items[0].StatusCode == "AD", "Live added status");
            request.Command = PlasticCommand.Undo;
            result = client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult();
            Assert(result.Succeeded, "Live undo add: " + result.Error); added = false;
            items = client.GetStatusAsync(file, CancellationToken.None).GetAwaiter().GetResult();
            Assert(items.Count == 1 && items[0].StatusCode == "PR", "Undo add returns private file");
        }
        finally
        {
            if (added) client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = workspace.RootPath, Paths = new List<string> { file } }, CancellationToken.None).GetAwaiter().GetResult();
            File.Delete(file);
        }
        var after = client.GetStatusAsync(workspace.RootPath, CancellationToken.None).GetAwaiter().GetResult();
        Assert(String.Join("\n", before.Select(x => x.StatusCode + " " + x.Path).OrderBy(x => x)) == String.Join("\n", after.Select(x => x.StatusCode + " " + x.Path).OrderBy(x => x)), "Live workspace status restored");
    }
}
