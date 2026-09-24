// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    internal static class OverlayCacheHost
    {
        private static string Name { get { return "Local\\TortoiseSCM.OverlayCache." + WindowsIdentity.GetCurrent().User.Value; } }

        internal static void TrackAndStart(string root)
        {
            try
            {
                using (var current = Process.GetCurrentProcess())
                {
                    string executable = current.MainModule.FileName;
                    if (!Path.GetFileName(executable).Equals("TortoiseSCM.exe", StringComparison.OrdinalIgnoreCase)) return;
                    new OverlayCacheStore(OverlayCacheStore.DefaultDirectory).Track(root);
                    // Starting a second copy is harmless: the worker owns a per-user mutex.
                    using (var child = Process.Start(new ProcessStartInfo(executable, "--cache-worker") { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden })) { }
                }
            }
            catch (Exception error) { Log(error.Message); }
        }

        internal static async Task<int> RefreshAsync(PlasticClientConfig config, string root, CancellationToken token)
        {
            var client = new PlasticClient(config);
            var workspace = client.DiscoverWorkspace(root);
            if (workspace == null || !String.Equals(Path.GetFullPath(root).TrimEnd('\\'), workspace.RootPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Overlay refresh requires the explicit workspace root.");
            root = workspace.RootPath;
            var store = new OverlayCacheStore(OverlayCacheStore.DefaultDirectory);
            store.Track(root); store.Invalidate(root);
            try
            {
                var entries = await client.GetOverlayStatesAsync(root, token).ConfigureAwait(false);
                store.Publish(root, entries); Notify(root); return entries.Count;
            }
            catch { store.Invalidate(root); Notify(root); throw; }
        }

        internal static int Stop()
        {
            try { using (var stop = EventWaitHandle.OpenExisting(Name + ".Stop")) stop.Set(); return 0; }
            catch (WaitHandleCannotBeOpenedException) { return 0; }
        }

        internal static int Run()
        {
            bool created;
            using (var singleton = new Mutex(true, Name, out created))
            {
                if (!created) return 0;
                try
                {
                    using (var stop = new EventWaitHandle(false, EventResetMode.ManualReset, Name + ".Stop"))
                    {
                        var store = new OverlayCacheStore(OverlayCacheStore.DefaultDirectory);
                        var watches = new Dictionary<string, Watch>(StringComparer.OrdinalIgnoreCase);
                        DateTime nextDiscovery = DateTime.MinValue;
                        try
                        {
                            while (!stop.WaitOne(500))
                            {
                                if (DateTime.UtcNow >= nextDiscovery)
                                {
                                    var roots = store.TrackedRoots();
                                    foreach (string root in watches.Keys.Where(root => !roots.Contains(root, StringComparer.OrdinalIgnoreCase)).ToArray())
                                    { store.Invalidate(root); watches[root].Dispose(); watches.Remove(root); }
                                    foreach (string root in roots)
                                        if (!watches.ContainsKey(root) && Directory.Exists(Path.Combine(root, ".plastic")))
                                            try { watches.Add(root, new Watch(root)); } catch (Exception error) { Log(error.Message); }
                                    nextDiscovery = DateTime.UtcNow.AddSeconds(10);
                                }
                                foreach (var pair in watches)
                                {
                                    if (stop.WaitOne(0)) break;
                                    var watch = pair.Value;
                                    long version = Interlocked.Read(ref watch.Version);
                                    bool changed = version != watch.PublishedVersion;
                                    if (!changed && DateTime.UtcNow < watch.NextRefresh) continue;
                                    // Expire immediately before rescanning; a failed or changing tree never remains green.
                                    try
                                    {
                                        store.Invalidate(pair.Key); Notify(pair.Key);
                                        if (changed && DateTime.UtcNow.Ticks - Interlocked.Read(ref watch.LastEventTicks) < TimeSpan.FromMilliseconds(750).Ticks) continue;
                                        var config = PlasticClientConfig.Load(); config.Timeout = TimeSpan.FromSeconds(20);
                                        using (var cancellation = new CancellationTokenSource())
                                        {
                                            var registration = ThreadPool.RegisterWaitForSingleObject(stop, delegate { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } }, null, Timeout.Infinite, true);
                                            try
                                            {
                                                var states = new PlasticClient(config).GetOverlayStatesAsync(pair.Key, cancellation.Token).GetAwaiter().GetResult();
                                                if (!stop.WaitOne(0) && Interlocked.Read(ref watch.Version) == version)
                                                { store.Publish(pair.Key, states); watch.PublishedVersion = version; Notify(pair.Key); }
                                            }
                                            finally { registration.Unregister(null); }
                                        }
                                        watch.NextRefresh = DateTime.UtcNow.AddSeconds(30);
                                    }
                                    catch (Exception error)
                                    { Log(pair.Key + ": " + error.Message); watch.PublishedVersion = version; watch.NextRefresh = DateTime.UtcNow.AddSeconds(30); }
                                }
                            }
                        }
                        finally
                        {
                            foreach (var pair in watches) { pair.Value.Dispose(); try { store.Invalidate(pair.Key); Notify(pair.Key); } catch (IOException) { } }
                        }
                    }
                    return 0;
                }
                catch (Exception error) { Log(error.Message); return 1; }
                finally { singleton.ReleaseMutex(); }
            }
        }

        private sealed class Watch : IDisposable
        {
            private readonly FileSystemWatcher watcher;
            internal long Version = 1;
            internal long PublishedVersion;
            internal long LastEventTicks;
            internal DateTime NextRefresh;
            internal Watch(string root)
            {
                watcher = new FileSystemWatcher(root) { IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 32768 };
                watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
                watcher.Renamed += delegate(object sender, RenamedEventArgs e) { Changed(sender, e); };
                watcher.Error += delegate { Mark(); };
                watcher.EnableRaisingEvents = true;
            }
            private void Changed(object sender, FileSystemEventArgs e)
            {
                string relative = e.FullPath.Substring(watcher.Path.Length).TrimStart('\\');
                if (relative.Equals(".plastic", StringComparison.OrdinalIgnoreCase)) return;
                if (relative.StartsWith(".plastic\\", StringComparison.OrdinalIgnoreCase) &&
                    !relative.Equals(".plastic\\plastic.selector", StringComparison.OrdinalIgnoreCase) &&
                    !relative.Equals(".plastic\\plastic.mergeprogress", StringComparison.OrdinalIgnoreCase)) return;
                Mark();
            }
            private void Mark() { Interlocked.Exchange(ref LastEventTicks, DateTime.UtcNow.Ticks); Interlocked.Increment(ref Version); }
            public void Dispose() { watcher.Dispose(); }
        }

        private static void Log(string message)
        {
            try
            {
                string path = Path.Combine(OverlayCacheStore.DefaultDirectory, "overlay-worker.log");
                Directory.CreateDirectory(OverlayCacheStore.DefaultDirectory);
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.WriteAllText(path, "");
                File.AppendAllText(path, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        private static void Notify(string root) { SHChangeNotify(0x00001000, 0x0005, root, IntPtr.Zero); }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern void SHChangeNotify(uint events, uint flags, string path, IntPtr second);
    }
}
