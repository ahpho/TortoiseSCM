// GPL-2.0-or-later. Live worker and registered COM checks in an isolated test workspace.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using TortoiseSCM;

internal static class OverlayIntegrationTests
{
    private static int assertions;
    private static readonly List<string> results = new List<string>();
    private static string artifacts;
    private static void Check(bool condition, string description)
    { if (!condition) throw new Exception(description); assertions++; results.Add(description); Console.WriteLine("PASS: " + description); }

    private static int Main(string[] args)
    {
        string controlled = null, privateFile = null, root = null; byte[] original = null; DateTime originalTime = DateTime.MinValue; Process worker = null;
        PlasticClient client = null;
        try
        {
            string executable = Path.GetFullPath(args[0]), probe = Path.GetFullPath(args[1]);
            var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[2], Encoding.UTF8));
            artifacts = Path.GetFullPath((string)manifest["runDirectory"]); root = Path.GetFullPath((string)manifest["producer"]);
            Check(((string)manifest["branch"]).StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal) &&
                root.Equals(Path.Combine(artifacts, "producer"), StringComparison.OrdinalIgnoreCase), "Overlay test uses an isolated branch workspace");
            client = new PlasticClient(PlasticClientConfig.Load());
            Check(client.GetStatusAsync(root, CancellationToken.None).GetAwaiter().GetResult().Count == 0, "Overlay fixture starts clean");
            var initial = client.GetOverlayStatesAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            controlled = initial.Keys.First(path => File.Exists(path)); original = File.ReadAllBytes(controlled); originalTime = File.GetLastWriteTimeUtc(controlled);
            Check(initial.ContainsKey(root) && initial[root] == PlasticOverlayState.Normal, "Clean workspace root is explicitly normal");
            var store = new OverlayCacheStore(OverlayCacheStore.DefaultDirectory); store.Track(root);
            worker = Process.Start(new ProcessStartInfo(executable, "--cache-worker") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden });
            Thread.Sleep(300);
            Check(!worker.HasExited, "Standalone hidden cache worker owns the session");
            WaitFor(store.SnapshotPath, controlled, PlasticOverlayState.Normal);
            Probe(probe, controlled, 1); Check(true, "Registered native DLL consumes managed normal-file snapshot");
            privateFile = Path.Combine(root, "overlay-private-" + Guid.NewGuid().ToString("N") + ".txt"); File.WriteAllText(privateFile, "private overlay fixture");
            var withPrivate = client.GetOverlayStatesAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            Check(withPrivate.ContainsKey(privateFile) && withPrivate[privateFile] == PlasticOverlayState.Unversioned && withPrivate[root] == PlasticOverlayState.Normal,
                "Private files have unversioned overlays without changing controlled parent");
            WaitFor(store.SnapshotPath, privateFile, PlasticOverlayState.Unversioned);
            Probe(probe, privateFile, 8);
            File.AppendAllText(controlled, "\r\nTemporary overlay watcher edit.\r\n");
            WaitFor(store.SnapshotPath, controlled, PlasticOverlayState.Modified);
            WaitFor(store.SnapshotPath, root, PlasticOverlayState.Modified);
            Probe(probe, controlled, 2); Probe(probe, root, 2); Probe(probe, privateFile, 8);
            Check(true, "Filesystem watcher publishes changed file and parent folder to registered native handlers");
            var undo = client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root, Paths = new[] { controlled } }, CancellationToken.None).GetAwaiter().GetResult();
            Check(undo.Succeeded && File.ReadAllBytes(controlled).SequenceEqual(original), "Native undo restores exact original controlled bytes");
            File.Delete(privateFile); privateFile = null;
            WaitFor(store.SnapshotPath, controlled, PlasticOverlayState.Normal); WaitFor(store.SnapshotPath, root, PlasticOverlayState.Normal);
            Probe(probe, root, 1); Check(true, "Restoring exact bytes returns file and root to normal");

            Run(client, root, PlasticCommand.Checkout, controlled);
            Check(HasStatus(client, root, controlled, "CO") && File.ReadAllBytes(controlled).SequenceEqual(original),
                "Real checkout records CO without changing file contents");
            AssertPendingOverlay(client, root, controlled, PlasticOverlayState.Modified, store, probe);
            Check(true, "Checkout displays Modified for file and ancestors instead of inventing a server lock");
            Run(client, root, PlasticCommand.Undo, controlled);
            WaitFor(store.SnapshotPath, controlled, PlasticOverlayState.Normal); WaitFor(store.SnapshotPath, root, PlasticOverlayState.Normal);

            privateFile = Path.Combine(Path.GetDirectoryName(controlled), "overlay-added-" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(privateFile, "temporary pending add overlay fixture");
            Run(client, root, PlasticCommand.Add, privateFile);
            Check(HasStatus(client, root, privateFile, "AD"), "Real add records AD without checkin");
            AssertPendingOverlay(client, root, privateFile, PlasticOverlayState.Added, store, probe);
            Check(true, "Added file displays Added while controlled ancestors display Modified");
            Run(client, root, PlasticCommand.Undo, privateFile);
            if (File.Exists(privateFile)) File.Delete(privateFile);
            privateFile = null;
            WaitFor(store.SnapshotPath, root, PlasticOverlayState.Normal);

            var removed = client.RemoveAsync(controlled, CancellationToken.None).GetAwaiter().GetResult();
            Check(removed.Succeeded && !File.Exists(controlled) && HasStatus(client, root, controlled, "DE"),
                "Real remove records DE for the controlled fixture without checkin");
            AssertPendingOverlay(client, root, controlled, PlasticOverlayState.Deleted, store, probe);
            Check(true, "Deleted file snapshot retains Deleted while controlled ancestors display Modified");
            Run(client, root, PlasticCommand.Undo, controlled);
            Check(File.ReadAllBytes(controlled).SequenceEqual(original), "Undo deletion restores exact controlled fixture bytes");
            WaitFor(store.SnapshotPath, controlled, PlasticOverlayState.Normal); WaitFor(store.SnapshotPath, root, PlasticOverlayState.Normal);
            Probe(probe, controlled, 1); Probe(probe, root, 1);
            using (var stop = Process.Start(new ProcessStartInfo(executable, "--cache-stop") { UseShellExecute = false, CreateNoWindow = true })) stop.WaitForExit(5000);
            Check(worker.WaitForExit(30000) && worker.ExitCode == 0, "Cache worker stops gracefully");
            Probe(probe, root, 0); Check(true, "Stopping the worker invalidates its workspace overlays");
            Check(client.GetStatusAsync(root, CancellationToken.None).GetAwaiter().GetResult().Count == 0, "Overlay test leaves fixture clean without server writes");
            File.WriteAllLines(Path.Combine(artifacts, "overlay-integration-results.txt"), results.Concat(new[] { "PASS: " + assertions }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (artifacts != null) File.WriteAllLines(Path.Combine(artifacts, "overlay-integration-results.txt"), results.Concat(new[] { "FAIL: " + error }));
            return 1;
        }
        finally
        {
            try
            {
                // Undo only the exact fixture paths, including pending CO/AD/DE.
                // Restoring bytes alone would leave checkout/delete metadata behind.
                try
                {
                    if (controlled != null && original != null)
                    {
                        UndoPending(client, root, controlled);
                        if (!File.Exists(controlled) || !File.ReadAllBytes(controlled).SequenceEqual(original)) File.WriteAllBytes(controlled, original);
                        File.SetLastWriteTimeUtc(controlled, originalTime);
                    }
                }
                finally
                {
                    if (privateFile != null)
                    {
                        UndoPending(client, root, privateFile);
                        if (File.Exists(privateFile)) File.Delete(privateFile);
                    }
                }
            }
            finally
            {
                if (worker != null)
                {
                    if (!worker.HasExited)
                    {
                        using (var stop = Process.Start(new ProcessStartInfo(Path.GetFullPath(args[0]), "--cache-stop") { UseShellExecute = false, CreateNoWindow = true })) stop.WaitForExit(5000);
                        worker.WaitForExit(30000);
                    }
                    worker.Dispose();
                }
            }
        }
    }

    private static void Run(PlasticClient client, string root, PlasticCommand command, string path)
    {
        var result = client.RunAsync(new PlasticCommandRequest { Command = command, WorkingDirectory = root, Paths = new[] { path } }, CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Succeeded) throw new Exception(command + " failed for fixture path " + path + ": " + result.Error + result.Output);
    }

    private static bool HasStatus(PlasticClient client, string root, string path, string code)
    {
        return client.GetStatusAsync(root, CancellationToken.None).GetAwaiter().GetResult().Any(item =>
            String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) && item.StatusCode == code);
    }

    private static void UndoPending(PlasticClient client, string root, string path)
    {
        if (client.GetStatusAsync(root, CancellationToken.None).GetAwaiter().GetResult().Any(item =>
            String.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase) && item.StatusCode != "PR" && item.StatusCode != "IG"))
            Run(client, root, PlasticCommand.Undo, path);
    }

    private static void AssertPendingOverlay(PlasticClient client, string root, string path, PlasticOverlayState expected, OverlayCacheStore store, string probe)
    {
        string parent = Path.GetDirectoryName(path);
        var states = client.GetOverlayStatesAsync(root, CancellationToken.None).GetAwaiter().GetResult();
        Check(states[path] == expected && states[parent] == PlasticOverlayState.Modified && states[root] == PlasticOverlayState.Modified,
            "Managed " + expected + " overlay preserves Modified parent/root summaries");
        WaitFor(store.SnapshotPath, path, expected);
        WaitFor(store.SnapshotPath, parent, PlasticOverlayState.Modified);
        WaitFor(store.SnapshotPath, root, PlasticOverlayState.Modified);
        Probe(probe, path, (int)expected); Probe(probe, parent, 2); Probe(probe, root, 2);
    }

    private static void WaitFor(string snapshot, string path, PlasticOverlayState expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                PlasticOverlayState actual;
                if (OverlaySnapshot.Decode(File.ReadAllBytes(snapshot), DateTime.UtcNow).Entries.TryGetValue(path, out actual) && actual == expected) return;
            }
            catch (IOException) { } catch (InvalidDataException) { } catch (ArgumentException) { }
            Thread.Sleep(200);
        }
        throw new Exception("Worker did not publish " + expected + " for " + path);
    }

    private static void Probe(string executable, string path, int state)
    {
        using (var process = Process.Start(new ProcessStartInfo(executable, "--overlay-probe " + PlasticClient.QuoteArgument(path) + " " + state) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
        {
            string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(15000) || process.ExitCode != 0) throw new Exception("Native overlay probe failed: " + output + error);
        }
    }
}
