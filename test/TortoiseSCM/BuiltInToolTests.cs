// GPL-2.0-or-later. UI-independent routing and lifetime tests.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TortoiseSCM;

internal static class BuiltInToolTests
{
    private static int assertions;
    private sealed class Host : IPlasticToolHost
    {
        internal Func<string, string, CancellationToken, Task<PlasticCommandResult>> Diff;
        internal Func<string, string, string, string, CancellationToken, Task<PlasticCommandResult>> Merge;
        public Task<PlasticCommandResult> ShowDiffAsync(string a, string b, CancellationToken token) { return Diff(a, b, token); }
        public Task<PlasticCommandResult> ShowMergeAsync(string a, string b, string c, string d, CancellationToken token) { return Merge(a, b, c, d, token); }
    }

    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-builtin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "settings.xml");
            File.WriteAllText(settings, "<TortoiseSCM><DiffToolArguments>{base} {local}</DiffToolArguments></TortoiseSCM>");
            var config = PlasticClientConfig.Load(settings);
            Check(config.UseBeyondCompare && !config.UseBuiltInDiff && !config.UseBuiltInMerge, "Legacy settings activate Beyond Compare and retain dormant tool preferences");
            config.UseBuiltInDiff = config.UseBuiltInMerge = true;
            config.DiffToolPath = config.MergeToolPath = Path.Combine(root, "removed-tool.exe");
            config.Save();
            config = PlasticClientConfig.Load(settings);
            Check(config.UseBuiltInDiff && config.UseBuiltInMerge && config.DiffToolPath.EndsWith("removed-tool.exe"), "Built-in choices persist and preserve dormant external settings");
            config.UseBeyondCompare = false; // Exercise the retained editor adapter independently of the application profile.
            config.CmPath = Assembly.GetExecutingAssembly().Location;
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "test\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server\"");
            string local = Path.Combine(root, "renamed.txt"), before = Path.Combine(root, "base.txt"), remote = Path.Combine(root, "remote.txt"), output = Path.Combine(root, "output.txt");
            File.WriteAllText(local, "local"); File.WriteAllText(before, "base"); File.WriteAllText(remote, "remote");
            var client = new PlasticClient(config);
            var actualWorkspace = client.GetWorkspaceAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            Check(actualWorkspace.IsPartial && !client.DiscoverWorkspace(root).IsPartial, "Native Partial status overrides stale Standard metadata hint");
            var identityDownload = typeof(PlasticClient).GetMethod("DownloadPartialIdentityFileAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            string partialBase = Path.Combine(root, "partial-base.txt");
            Action downloadPartialBase = () => ((Task)identityDownload.Invoke(client,
                new object[] { actualWorkspace, 42L, 1L, partialBase, CancellationToken.None })).GetAwaiter().GetResult();
            downloadPartialBase();
            Check(File.ReadAllText(partialBase) == "original base", "Partial preparation identity download tolerates stale Standard metadata hint");
            File.Delete(partialBase);
            foreach (string changedSelector in new[] { "repository \"other@server\"", "repository \"test@server\"\npath \"/\" branch \"/other\"" })
            {
                File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), changedSelector);
                Reject<InvalidOperationException>(downloadPartialBase, "Partial historical download still rejects repository or selector changes");
                Check(!File.Exists(partialBase), "Context mismatch creates no downloaded baseline");
            }
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server\"");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "replacement\nguid\nStandard\n");
            Reject<InvalidOperationException>(downloadPartialBase, "Historical download rejects a replaced workspace name");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "test\nguid\nStandard\n");
            Reject<InvalidOperationException>(() => client.OpenDiffToolAsync(local, CancellationToken.None).GetAwaiter().GetResult(), "Missing host fails explicitly");
            Reject<InvalidOperationException>(() => client.RunMergeToolAsync(before, local, remote, output, CancellationToken.None).GetAwaiter().GetResult(), "Missing merge host fails explicitly");
            Reject<InvalidOperationException>(() => client.OpenRevisionDiffToolAsync(root, "/original.txt", 1, 2, CancellationToken.None).GetAwaiter().GetResult(), "Missing historical host fails explicitly");
            string temporaryBase = null;
            var host = new Host(); client.ToolHost = host;
            host.Diff = async (a, b, token) =>
            {
                temporaryBase = a;
                Check(File.ReadAllText(a) == "original base", "Renamed file baseline uses its native item identity");
                Check(b == local && File.ReadAllText(b) == "local", "Working file is the comparison target");
                Check((File.GetAttributes(a) & FileAttributes.ReadOnly) != 0, "Downloaded baseline is read-only");
                await Task.Delay(30, token);
                Check(File.Exists(a), "Temporary baseline stays alive throughout asynchronous host lifetime");
                return new PlasticCommandResult { ExitCode = 0, Output = "host diff" };
            };
            Check(client.OpenDiffToolAsync(local, CancellationToken.None).GetAwaiter().GetResult().Output == "host diff", "Configured built-in diff dispatches to host");
            Check(!File.Exists(temporaryBase) && !Directory.Exists(Path.GetDirectoryName(temporaryBase)), "Working diff removes temporary directory after closing");
            foreach (string mode in new[] { "Standard", "Partial" })
            {
                File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "test\nguid\n" + mode + "\n");
                Check(client.OpenDiffToolAsync(local, CancellationToken.None).GetAwaiter().GetResult().Succeeded, "Identity comparison supports workspace mode " + mode);
            }
            string secondTemporary = null;
            host.Diff = async (a, b, token) =>
            {
                temporaryBase = a; secondTemporary = b;
                Check(File.ReadAllText(a) == "original base" && File.ReadAllText(b) == "historical remote", "Historical host receives both downloaded revisions");
                Check((File.GetAttributes(b) & FileAttributes.ReadOnly) != 0, "Both historical endpoints are read-only");
                await Task.Delay(20, token);
                return new PlasticCommandResult { ExitCode = 7, Error = "host result" };
            };
            Check(client.OpenRevisionDiffToolAsync(root, "/original.txt", "/later.txt", 1, 2, CancellationToken.None).GetAwaiter().GetResult().ExitCode == 7, "Historical host result is preserved");
            Check(!File.Exists(temporaryBase) && !File.Exists(secondTemporary), "Both historical endpoints cleaned after host close");
            using (var cancel = new CancellationTokenSource())
            {
                host.Diff = async (a, b, token) => { temporaryBase = a; cancel.Cancel(); await Task.Delay(30, token); return new PlasticCommandResult(); };
                Reject<OperationCanceledException>(() => client.OpenDiffToolAsync(local, cancel.Token).GetAwaiter().GetResult(), "Cancellation reaches host");
                Check(!File.Exists(temporaryBase), "Cancellation removes downloaded baseline");
                Reject<OperationCanceledException>(() => client.OpenDiffToolAsync(local, cancel.Token).GetAwaiter().GetResult(), "Pre-cancellation dispatches no host");
            }
            host.Diff = (a, b, token) => { temporaryBase = a; throw new IOException("host failed"); };
            Reject<IOException>(() => client.OpenDiffToolAsync(local, CancellationToken.None).GetAwaiter().GetResult(), "Host failure propagates");
            Check(!File.Exists(temporaryBase), "Host failure still cleans baseline");
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "replace-identity");
            Reject<InvalidOperationException>(() => client.OpenDiffToolAsync(local, CancellationToken.None).GetAwaiter().GetResult(), "Identity replacement during preparation refuses viewer");
            File.Delete(Path.Combine(root, ".plastic", "mode"));
            File.Delete(Path.Combine(root, ".plastic", "downloaded"));
            File.WriteAllText(Path.Combine(root, ".plastic", "mode"), "selector-change");
            Reject<InvalidOperationException>(() => client.OpenDiffToolAsync(local, CancellationToken.None).GetAwaiter().GetResult(), "Selector change during download refuses viewer");
            File.Delete(Path.Combine(root, ".plastic", "mode"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server\"");
            foreach (string unsafeMode in new[] { "xlink", "symlink", "private", "directory" })
            {
                File.WriteAllText(Path.Combine(root, ".plastic", "mode"), unsafeMode);
                Reject<ArgumentException>(() => client.OpenDiffToolAsync(local, CancellationToken.None).GetAwaiter().GetResult(), "Unsafe working identity refuses viewer: " + unsafeMode);
            }
            File.Delete(Path.Combine(root, ".plastic", "mode"));
            int merges = 0;
            host.Merge = async (a, b, c, d, token) =>
            {
                merges++;
                Check(a == before && b == local && c == remote && d == output, "Merge receives validated distinct paths");
                Reject<IOException>(() => { using (File.Open(Path.Combine(root, ".plastic", "tortoisescm-structure.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }, "Output workspace gate held while editor is open");
                await Task.Delay(20, token);
                File.WriteAllText(d, "merged result");
                return new PlasticCommandResult { Output = "saved only" };
            };
            Check(client.RunMergeToolAsync(before, local, remote, output, CancellationToken.None).GetAwaiter().GetResult().Output == "saved only", "Built-in merge dispatches and returns host result");
            Check(File.ReadAllText(output) == "merged result" && File.ReadAllText(local) == "local", "Merge writes only separate output");
            using (File.Open(Path.Combine(root, ".plastic", "tortoisescm-structure.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { Check(true, "Editor close releases output gate"); }
            Reject<ArgumentException>(() => client.RunMergeToolAsync(before, local, remote, local, CancellationToken.None).GetAwaiter().GetResult(), "Merge cannot overwrite an input");
            string alias = Path.Combine(root, "alias.txt");
            Check(CreateHardLink(alias, local, IntPtr.Zero), "Hardlink fixture created");
            Reject<ArgumentException>(() => client.RunMergeToolAsync(before, local, remote, alias, CancellationToken.None).GetAwaiter().GetResult(), "Merge rejects hardlink alias output");
            Reject<ArgumentException>(() => client.RunMergeToolAsync(before, local, remote, output + ":stream", CancellationToken.None).GetAwaiter().GetResult(), "Merge rejects alternate streams");
            Reject<ArgumentException>(() => client.RunMergeToolAsync(before, local, remote, Path.Combine(root, ".plastic", "output"), CancellationToken.None).GetAwaiter().GetResult(), "Merge rejects metadata output");
            File.WriteAllText(Path.Combine(root, ".plastic", "tortoisescm-partial-directory.session"), "active");
            Reject<ArgumentException>(() => client.RunMergeToolAsync(before, local, remote, output, CancellationToken.None).GetAwaiter().GetResult(), "Active Partial directory decision blocks merge editor");
            Check(merges == 1 && File.ReadAllText(output) == "merged result", "Rejected editors never dispatch or alter result");
            File.Delete(Path.Combine(root, ".plastic", "tortoisescm-partial-directory.session"));
            using (var cancel = new CancellationTokenSource())
            {
                host.Merge = async (a, b, c, d, token) => { cancel.Cancel(); await Task.Delay(30, token); return new PlasticCommandResult(); };
                Reject<OperationCanceledException>(() => client.RunMergeToolAsync(before, local, remote, output, cancel.Token).GetAwaiter().GetResult(), "Merge host observes cancellation");
                using (File.Open(Path.Combine(root, ".plastic", "tortoisescm-structure.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { Check(true, "Cancelled merge releases workspace gate"); }
                Check(File.ReadAllText(output) == "merged result", "Cancelled merge leaves prior output unchanged");
            }
            Console.WriteLine("PASS: " + assertions + " built-in tool assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static int FakeCm(string[] args)
    {
        string metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        string mode = File.Exists(Path.Combine(metadata, "mode")) ? File.ReadAllText(Path.Combine(metadata, "mode")) : "";
        if (args[0] == "status") { Console.WriteLine("<StatusOutput><WorkspaceStatus><Status><Changeset>-1</Changeset></Status></WorkspaceStatus></StatusOutput>"); return 0; }
        if (args[0] == "fileinfo") { Console.WriteLine("<FileInfos><FileInfo><RevisionChangeset>1</RevisionChangeset><IsUnderXlink>false</IsUnderXlink><Type>txt</Type><Status>controlled</Status><RepSpec>test@server</RepSpec></FileInfo></FileInfos>"); return 0; }
        if (args[0] == "ls")
        {
            bool historical = args.Any(a => a.StartsWith("--tree="));
            string path = historical ? (args[1] == "/" ? "/original.txt" : args[1]) : "/renamed.txt";
            int id = !historical && mode == "replace-identity" && File.Exists(Path.Combine(metadata, "downloaded")) ? 99 : 42;
            Console.WriteLine(new XElement("LsResults", new XElement("LsItem", new XElement("CurrentPath", path), new XElement("ItemId", mode == "private" ? -1 : id),
                new XElement("Type", mode == "directory" ? "dir" : "txt"), new XElement("SymlinkTarget", mode == "symlink" ? "/other.txt" : ""),
                new XElement("Repository", mode == "xlink" ? "rep:other@server" : "rep:test@server")))); return 0;
        }
        if (args[0] == "cat")
        {
            if (!args[1].StartsWith("serverpath:/original.txt#cs:1@") && !args[1].StartsWith("serverpath:/later.txt#cs:2@")) return 19;
            File.WriteAllText(args.Single(a => a.StartsWith("--file=")).Substring(7), args[1].Contains("#cs:1@") ? "original base" : "historical remote");
            if (mode == "replace-identity") File.WriteAllText(Path.Combine(metadata, "downloaded"), "done");
            if (mode == "selector-change") File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"other@server\"");
            return 0;
        }
        return 20; // No mutation commands should ever be emitted by a tool adapter.
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { bool rejected = false; try { action(); } catch (T) { rejected = true; } Check(rejected, message); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
}
