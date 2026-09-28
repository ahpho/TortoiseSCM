// GPL-2.0-or-later. Real subprocess contract tests; the helper is not the BC product.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TortoiseSCM;

internal static class BeyondCompareProcessTests
{
    private static int assertions;
    private static string root;
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Child(args);
        root = Path.Combine(Path.GetTempPath(), "TSCM-BC-" + Guid.NewGuid().ToString("N") + " 中文 &");
        Directory.CreateDirectory(root);
        try { Run().GetAwaiter().GetResult(); Console.WriteLine("PASS: " + assertions + " Beyond Compare process assertions"); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Environment.SetEnvironmentVariable("TSCM_BC_TEST_ROOT", null); Directory.Delete(root, true); }
    }
    private static async Task Run()
    {
        string exe = Assembly.GetExecutingAssembly().Location, bc = Path.Combine(root, "BComp.exe");
        File.Copy(exe, bc); Environment.SetEnvironmentVariable("TSCM_BC_TEST_ROOT", root);
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "bc-test\nguid\nStandard\n");
        File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server\"");
        string local = Path.Combine(root, "renamed 中文 &.txt"), baseline = Path.Combine(root, "base.txt"), remote = Path.Combine(root, "remote.txt"), output = Path.Combine(root, "merged.txt");
        File.WriteAllText(local, "local"); File.WriteAllText(baseline, "base"); File.WriteAllText(remote, "remote");
        var config = new PlasticClientConfig { CmPath = exe, UseBeyondCompare = true, BeyondComparePath = bc, Timeout = TimeSpan.FromSeconds(10),
            UseBuiltInDiff = true, UseBuiltInMerge = true, DiffToolPath = "invalid", MergeToolPath = "invalid" };
        var client = new PlasticClient(config);
        Check((await client.OpenDiffToolAsync(local, CancellationToken.None)).Succeeded, "BC profile overrides dormant built-in/custom tools");
        var recorded = XDocument.Load(Path.Combine(root, "argv.xml")).Root.Elements("arg").Select(x => x.Value).ToArray();
        string downloaded = recorded.Single(File.Exists); // local only: downloaded baseline has been cleaned.
        Check(downloaded == local && recorded.Contains("/solo") && recorded.Contains("/readonly"), "Diff preserves Unicode argv, separate session and read-only sides");
        Check(File.ReadAllText(Path.Combine(root, "input.txt")) == "base|local", "Renamed work file resolves its original identity baseline");
        Check(!File.Exists(recorded[2]), "Temporary baseline removed only after normal tool completion");
        Check((await client.OpenRevisionDiffToolAsync(root, "/original.txt", "/later.txt", 1, 2, CancellationToken.None)).Succeeded, "History routes to BC with fixed cross-path endpoints");
        Check(File.ReadAllText(Path.Combine(root, "input.txt")) == "base|remote", "Historical BC receives both exact revisions");
        // A very short SCM timeout must not terminate a human editing session.
        config.Timeout = TimeSpan.FromMilliseconds(1);
        File.WriteAllText(Path.Combine(root, "mode"), "wait");
        File.Delete(Path.Combine(root, "opened"));
        using (var cancel = new CancellationTokenSource())
        {
            var pending = client.RunMergeToolAsync(baseline, local, remote, output, cancel.Token);
            await WaitForFile("opened");
            await Task.Delay(200);
            Check(!pending.IsCompleted, "BC session ignores SCM command timeout");
            cancel.Cancel(); await Task.Delay(200);
            Check(!pending.IsCompleted && !File.Exists(output), "Cancellation keeps editor and inputs alive until user closes it");
            File.WriteAllText(Path.Combine(root, "release"), "close");
            bool canceled = false; try { await pending; } catch (OperationCanceledException) { canceled = true; }
            Check(canceled && File.ReadAllText(output) == "local|remote|base", "Cancellation reports only after child exits and preserves its saved bytes");
        }
        File.WriteAllText(Path.Combine(root, "mode"), "");
        Check((await client.RunMergeToolAsync(baseline, local, remote, output, CancellationToken.None)).Succeeded, "BC merge succeeds with independent output");
        recorded = XDocument.Load(Path.Combine(root, "argv.xml")).Root.Elements("arg").Select(x => x.Value).ToArray();
        Check(recorded.Take(5).SequenceEqual(new[] { "/solo", "/readonly", local, remote, baseline }) && recorded.Contains("/mergeoutput=" + output), "Merge order is local, remote, base, explicit output");
        Check(File.ReadAllText(local) == "local" && File.ReadAllText(baseline) == "base" && File.ReadAllText(remote) == "remote", "Merge contributors unchanged");
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel(); File.Delete(Path.Combine(root, "opened"));
            bool rejected = false; try { await client.RunMergeToolAsync(baseline, local, remote, output, canceled.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(root, "opened")), "Pre-cancel never launches BC");
        }
        config.Timeout = TimeSpan.FromSeconds(10);
        foreach (int code in new[] { 1, 2, 11, 12, 13 })
        {
            File.WriteAllText(Path.Combine(root, "mode"), "exit" + code);
            var result = await client.OpenDiffToolAsync(local, CancellationToken.None);
            Check(result.Succeeded && result.Error.Length == 0 && result.Output.Contains(code.ToString()), "Comparison result is not an editor failure: " + code);
            result = await client.OpenRevisionDiffToolAsync(root, "/original.txt", "/later.txt", 1, 2, CancellationToken.None);
            Check(result.Succeeded, "Historical comparison result is not an editor failure: " + code);
            result = await client.RunMergeToolAsync(baseline, local, remote, output, CancellationToken.None);
            Check(!result.Succeeded && result.ExitCode == code, "Comparison status must not imply successful merge: " + code);
        }
        foreach (int code in new[] { 14, 100, 101, 103, 104, 105, 106, 107, 17 })
        {
            File.WriteAllText(Path.Combine(root, "mode"), "exit" + code);
            var result = await client.RunMergeToolAsync(baseline, local, remote, output, CancellationToken.None);
            Check(result.ExitCode == code && !result.Succeeded && result.Error.Length > 0, "Nonzero BC exit remains failure: " + code);
            result = await client.OpenDiffToolAsync(local, CancellationToken.None);
            Check(!result.Succeeded && result.ExitCode == code && result.Error.Length > 0, "Comparison errors remain failures: " + code);
        }
        config.Timeout = TimeSpan.FromSeconds(10);
        File.WriteAllText(Path.Combine(root, "mode"), "exit102");
        bool waitFailed = false;
        try { await client.OpenDiffToolAsync(local, CancellationToken.None); } catch (InvalidOperationException ex) { waitFailed = ex.Message.Contains("102"); }
        recorded = XDocument.Load(Path.Combine(root, "argv.xml")).Root.Elements("arg").Select(x => x.Value).ToArray();
        Check(waitFailed && File.Exists(recorded[2]), "Uncertain BC lifetime retains baseline and reports recovery directory");
        string retained = Path.GetDirectoryName(recorded[2]);
        Check(retained.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(retained).StartsWith("TortoiseSCM-history-"), "Retained cleanup belongs to this isolated test");
        File.SetAttributes(recorded[2], FileAttributes.Normal); File.Delete(recorded[2]); Directory.Delete(retained);
        File.WriteAllText(Path.Combine(root, "mode"), "");
        foreach (string mode in new[] { "identity", "selector" })
        {
            File.Delete(Path.Combine(root, "opened")); File.Delete(Path.Combine(root, "downloaded"));
            File.WriteAllText(Path.Combine(root, "cm-mode"), mode);
            bool rejected = false; try { await client.OpenDiffToolAsync(local, CancellationToken.None); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && !File.Exists(Path.Combine(root, "opened")), "Changed " + mode + " rejects before BC launch");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server\"");
        }
    }
    private static async Task WaitForFile(string name)
    {
        for (int i = 0; i < 100 && !File.Exists(Path.Combine(root, name)); i++) await Task.Delay(50);
        Check(File.Exists(Path.Combine(root, name)), "Helper session opened");
    }
    private static int Child(string[] args)
    {
        string testRoot = Environment.GetEnvironmentVariable("TSCM_BC_TEST_ROOT");
        if (args[0] == "/solo")
        {
            new XDocument(new XElement("args", args.Select(x => new XElement("arg", x)))).Save(Path.Combine(testRoot, "argv.xml"));
            string mode = File.Exists(Path.Combine(testRoot, "mode")) ? File.ReadAllText(Path.Combine(testRoot, "mode")) : "";
            File.WriteAllText(Path.Combine(testRoot, "opened"), "yes");
            if (mode.StartsWith("exit")) return Int32.Parse(mode.Substring(4));
            if (mode == "wait") for (int i = 0; i < 200 && !File.Exists(Path.Combine(testRoot, "release")); i++) Thread.Sleep(50);
            string output = args.FirstOrDefault(x => x.StartsWith("/mergeoutput="));
            string content = String.Join("|", args.Skip(2).Take(output == null ? 2 : 3).Select(File.ReadAllText));
            File.WriteAllText(Path.Combine(testRoot, "input.txt"), content);
            if (output != null) File.WriteAllText(output.Substring(13), content);
            return 0;
        }
        string cmMode = File.Exists(Path.Combine(testRoot, "cm-mode")) ? File.ReadAllText(Path.Combine(testRoot, "cm-mode")) : "";
        if (args[0] == "status") { Console.WriteLine("<StatusOutput><WorkspaceStatus><Status><Changeset>-1</Changeset></Status></WorkspaceStatus></StatusOutput>"); return 0; }
        if (args[0] == "fileinfo") { Console.WriteLine("<FileInfos><FileInfo><RevisionChangeset>1</RevisionChangeset><IsUnderXlink>false</IsUnderXlink><Type>txt</Type><Status>controlled</Status><RepSpec>test@server</RepSpec></FileInfo></FileInfos>"); return 0; }
        if (args[0] == "ls")
        {
            bool history = args.Any(x => x.StartsWith("--tree="));
            string path = history ? (args[1] == "/" ? "/original.txt" : args[1]) : "/renamed.txt";
            int id = !history && cmMode == "identity" && File.Exists(Path.Combine(testRoot, "downloaded")) ? 99 : 42;
            Console.WriteLine(new XElement("LsResults", new XElement("LsItem", new XElement("CurrentPath", path), new XElement("ItemId", id), new XElement("Type", "txt"), new XElement("Repository", "rep:test@server")))); return 0;
        }
        if (args[0] == "cat")
        {
            if (!args[1].StartsWith("serverpath:/original.txt#cs:1@") && !args[1].StartsWith("serverpath:/later.txt#cs:2@")) return 19;
            File.WriteAllText(args.Single(x => x.StartsWith("--file=")).Substring(7), args[1].Contains("#cs:1@") ? "base" : "remote");
            File.WriteAllText(Path.Combine(testRoot, "downloaded"), "yes");
            if (cmMode == "selector") File.WriteAllText(Path.Combine(testRoot, ".plastic", "plastic.selector"), "repository \"other@server\"");
            return 0;
        }
        return 20;
    }
    private static void Check(bool value, string text) { assertions++; if (!value) throw new Exception(text); }
}
