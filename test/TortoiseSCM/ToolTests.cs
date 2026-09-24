// GPL-2.0-or-later. External tool integration tests, isolated from user settings.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TortoiseSCM;

internal static class ToolTests
{
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Child(args);
        string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-tools-" + Guid.NewGuid().ToString("N") + " 中文 & space");
        Directory.CreateDirectory(root);
        try
        {
            string exe = Assembly.GetExecutingAssembly().Location;
            string settings = Path.Combine(root, "settings.xml");
            var config = new PlasticClientConfig { SettingsPath = settings, CmPath = exe, DiffToolPath = exe, MergeToolPath = exe,
                DiffToolArguments = "--tool-diff \"{base}\" \"{local}\" \"literal & | % ! 中文\"",
                MergeToolArguments = "--tool-merge \"{base}\" \"{local}\" \"{remote}\" \"{merged}\"", Timeout = TimeSpan.FromSeconds(10) };
            config.Save();
            var loaded = PlasticClientConfig.Load(settings);
            Check(loaded.DiffToolPath == exe && loaded.MergeToolArguments == config.MergeToolArguments && loaded.SettingsPath == settings, "Custom tools persist in isolated settings");
            string before = File.ReadAllText(settings);
            config.MergeToolArguments = "{base} {local} {remote}";
            Reject(() => config.Save(), "Missing merged placeholder rejected");
            Check(File.ReadAllText(settings) == before, "Invalid settings do not overwrite saved configuration");
            config = loaded;
            Reject(() => PlasticToolArguments.Validate("\"{base} {local}", false), "Unclosed quotes rejected");
            Reject(() => PlasticToolArguments.Validate("{base} {local} {unknown}", false), "Unknown placeholder rejected");
            Reject(() => PlasticToolArguments.Validate("{base} {local} {", false), "Malformed placeholder rejected");
            var expanded = PlasticToolArguments.Expand("--base={base} \"{local}\" \"\" \"a\\\"b\"", new Dictionary<string, string> {
                { "base", "C:\\路径 a&b\\base" }, { "local", "C:\\{base}\\quoted \" x" } }, false);
            Check(expanded.SequenceEqual(new[] { "--base=C:\\路径 a&b\\base", "C:\\{base}\\quoted \" x", "", "a\"b" }), "Tokenization precedes single-pass placeholder substitution");
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "tools\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server\"");
            string local = Path.Combine(root, "local 中文 &.txt"), baseFile = Path.Combine(root, "base.txt"), remote = Path.Combine(root, "remote.txt"), output = Path.Combine(root, "merged 中文 &.txt");
            File.WriteAllText(local, "local"); File.WriteAllText(baseFile, "base"); File.WriteAllText(remote, "remote");
            var client = new PlasticClient(config);
            var diff = client.OpenDiffToolAsync(local, CancellationToken.None).GetAwaiter().GetResult();
            Check(diff.Succeeded && diff.Output.Contains("literal & | % ! 中文"), "Real external diff receives literal Unicode and shell metacharacters");
            string temporaryBase = diff.Output.Split('\n')[0].TrimEnd('\r');
            Check(!File.Exists(temporaryBase), "Diff baseline exists until tool exit and is then removed");
            var merged = client.RunMergeToolAsync(baseFile, local, remote, output, CancellationToken.None).GetAwaiter().GetResult();
            Check(merged.Succeeded && File.ReadAllText(output) == "local|remote", "Merge tool receives inputs and writes distinct output");
            Check(File.ReadAllText(baseFile) == "base" && File.ReadAllText(local) == "local" && File.ReadAllText(remote) == "remote", "Merge inputs are preserved");
            Reject(() => client.RunMergeToolAsync(baseFile, local, remote, local, CancellationToken.None).GetAwaiter().GetResult(), "Input cannot be chosen as output");
            Reject(() => client.RunMergeToolAsync(baseFile, local, Path.Combine(root, "absent"), output, CancellationToken.None).GetAwaiter().GetResult(), "Missing merge input rejected");
            string alias = Path.Combine(root, "hardlink.txt");
            Check(CreateHardLink(alias, local, IntPtr.Zero), "Hardlink alias fixture created");
            Reject(() => client.RunMergeToolAsync(baseFile, local, remote, alias, CancellationToken.None).GetAwaiter().GetResult(), "Hardlink output alias rejected");
            config.MergeToolArguments = "--tool-exit {base} {local} {remote} {merged}";
            var failure = client.RunMergeToolAsync(baseFile, local, remote, output, CancellationToken.None).GetAwaiter().GetResult();
            Check(failure.ExitCode == 17 && !failure.Succeeded, "External tool failure exit code is retained");
            config.MergeToolArguments = "--tool-sleep {base} {local} {remote} {merged}";
            config.Timeout = TimeSpan.FromMilliseconds(150);
            Check(client.RunMergeToolAsync(baseFile, local, remote, output, CancellationToken.None).GetAwaiter().GetResult().TimedOut, "External tool timeout terminates process");
            config.MergeToolPath = "";
            bool unconfigured = false;
            try { client.RunMergeToolAsync(baseFile, local, remote, output, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (InvalidOperationException) { unconfigured = true; }
            Check(unconfigured, "Unconfigured merge tool gives explicit error");
            Console.WriteLine("PASS: " + assertions + " external tool assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static int Child(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args[0] == "fileinfo") { Console.WriteLine("<FileInfos><FileInfo><RevisionChangeset>1</RevisionChangeset></FileInfo></FileInfos>"); return 0; }
        if (args[0] == "cat") { File.WriteAllText(args.Single(a => a.StartsWith("--file=")).Substring(7), "base"); return 0; }
        if (args[0] == "--tool-diff")
        {
            if (args.Length != 4 || !File.Exists(args[1]) || !File.Exists(args[2])) return 18;
            Thread.Sleep(150);
            if (!File.Exists(args[1])) return 19;
            Console.WriteLine(args[1]); Console.WriteLine(args[3]); return 0;
        }
        if (args[0] == "--tool-merge") { File.WriteAllText(args[4], File.ReadAllText(args[2]) + "|" + File.ReadAllText(args[3])); return 0; }
        if (args[0] == "--tool-exit") return 17;
        if (args[0] == "--tool-sleep") { Thread.Sleep(30000); return 0; }
        return 20;
    }

    private static void Check(bool value, string description) { assertions++; if (!value) throw new Exception(description); }
    private static void Reject(Action action, string description)
    { bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } Check(rejected, description); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
