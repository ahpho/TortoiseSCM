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
        string controlled = null, privateFile = null; byte[] original = null; DateTime originalTime = DateTime.MinValue; Process worker = null;
        try
        {
            string executable = Path.GetFullPath(args[0]), probe = Path.GetFullPath(args[1]);
            var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[2], Encoding.UTF8));
            artifacts = (string)manifest["runDirectory"]; string root = (string)manifest["producer"];
            Check(((string)manifest["branch"]).StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal) && root.StartsWith(artifacts + "\\", StringComparison.OrdinalIgnoreCase), "Overlay test uses an isolated branch workspace");
            var client = new PlasticClient(PlasticClientConfig.Load());
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
            Check(!withPrivate.ContainsKey(privateFile), "Private files receive no clean controlled overlay");
            File.AppendAllText(controlled, "\r\nTemporary overlay watcher edit.\r\n");
            WaitFor(store.SnapshotPath, controlled, PlasticOverlayState.Modified);
            WaitFor(store.SnapshotPath, root, PlasticOverlayState.Modified);
            Probe(probe, controlled, 2); Probe(probe, root, 2); Probe(probe, privateFile, 0);
            Check(true, "Filesystem watcher publishes changed file and parent folder to registered native handlers");
            var undo = client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Undo, WorkingDirectory = root, Paths = new[] { controlled } }, CancellationToken.None).GetAwaiter().GetResult();
            Check(undo.Succeeded && File.ReadAllBytes(controlled).SequenceEqual(original), "Native undo restores exact original controlled bytes");
            File.Delete(privateFile); privateFile = null;
            WaitFor(store.SnapshotPath, controlled, PlasticOverlayState.Normal); WaitFor(store.SnapshotPath, root, PlasticOverlayState.Normal);
            Probe(probe, root, 1); Check(true, "Restoring exact bytes returns file and root to normal");
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
            if (controlled != null && original != null && !File.ReadAllBytes(controlled).SequenceEqual(original))
            { File.WriteAllBytes(controlled, original); File.SetLastWriteTimeUtc(controlled, originalTime); }
            if (privateFile != null && File.Exists(privateFile)) File.Delete(privateFile);
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
