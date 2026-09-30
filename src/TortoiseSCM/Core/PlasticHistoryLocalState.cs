// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    // Deliberately a root-workspace watermark, not per-file revision analysis.
    internal sealed class PlasticHistoryLocalState
    {
        internal PlasticWorkspace Workspace;
        internal string Scope;
        internal long LoadedThroughChangeset;
        internal bool IsWorkspaceRoot;
        internal bool IsApproximate;
        internal string WorkspaceIdentity;
    }

    internal sealed class PlasticHistoryRootUpdate
    {
        internal PlasticWorkspace Workspace;
        internal long? TargetChangeset;
        internal bool IsPartial;
        internal string WorkspaceIdentity;
    }

    public sealed partial class PlasticClient
    {
        internal async Task<PlasticHistoryLocalState> GetHistoryLocalStateAsync(string path, CancellationToken token)
        {
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            var workspace = DiscoverWorkspace(command.WorkingDirectory);
            string absolute = command.Arguments[1];
            var state = new PlasticHistoryLocalState { Workspace = workspace,
                IsWorkspaceRoot = SamePath(absolute, workspace.RootPath), LoadedThroughChangeset = 1,
                WorkspaceIdentity = HistoryWorkspaceIdentity(workspace),
                Scope = SamePath(absolute, workspace.RootPath) ? "/" : "/" +
                    absolute.Substring(workspace.RootPath.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/') };
            // Child histories never need a native status/read or a watermark.
            if (!state.IsWorkspaceRoot) return state;
            long loaded = await ReadHistoryWorkspaceChangesetAsync(workspace, token).ConfigureAwait(false);
            if (loaded >= 0) state.LoadedThroughChangeset = loaded;
            else if (!TryReadHistoryRootWatermark(workspace, out state.LoadedThroughChangeset, out state.IsApproximate))
            {
                // Existing Gluon workspaces have no whole-workspace changeset.
                // Use only the root revision as a one-time, explicitly approximate
                // initial value. Persist it so later child updates cannot raise it.
                state.LoadedThroughChangeset = await ReadApproximateHistoryRootAsync(workspace, token).ConfigureAwait(false);
                state.IsApproximate = true;
                ValidateHistoryLocalContext(state);
                TrySaveHistoryRootWatermark(workspace, state.LoadedThroughChangeset, true, state.WorkspaceIdentity);
                // An exact root update may have completed while fileinfo awaited.
                // Its record takes priority over this approximate initialization.
                long recorded; bool approximate;
                if (TryReadHistoryRootWatermark(workspace, out recorded, out approximate))
                { state.LoadedThroughChangeset = recorded; state.IsApproximate = approximate; }
            }
            token.ThrowIfCancellationRequested();
            ValidateHistoryLocalContext(state);
            return state;
        }

        internal Task<bool> IsHistoryNotLoadedAsync(PlasticHistoryLocalState state, PlasticHistoryItem entry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateHistoryLocalContext(state);
            return Task.FromResult(EvaluateHistoryRootRow(state, entry));
        }

        internal Task<Dictionary<long, bool>> GetHistoryNotLoadedAsync(PlasticHistoryLocalState state, IList<PlasticHistoryItem> entries, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateHistoryLocalContext(state);
            var rows = new Dictionary<long, bool>();
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                rows[entry.Changeset] = EvaluateHistoryRootRow(state, entry);
            }
            ValidateHistoryLocalContext(state);
            return Task.FromResult(rows);
        }

        private static bool EvaluateHistoryRootRow(PlasticHistoryLocalState state, PlasticHistoryItem entry)
        {
            if (entry.Repository != state.Workspace.Repository) throw new InvalidOperationException("历史记录的仓库已改变。");
            // Initial root/tree changesets are always ordinary, including Gluon.
            return state.IsWorkspaceRoot && entry.Changeset > 1 && entry.Changeset > state.LoadedThroughChangeset;
        }

        private async Task<long> ReadHistoryWorkspaceChangesetAsync(PlasticWorkspace workspace, CancellationToken token)
        {
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath,
                new[] { "status", workspace.RootPath, "--header", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var document = SafeXml.Load(result.Output);
            var status = document.Root == null ? null : document.Root.Element("WorkspaceStatus");
            status = status == null ? null : status.Element("Status");
            long changeset;
            if (status == null || !Int64.TryParse((string)status.Element("Changeset"), out changeset) || changeset < -1)
                throw new InvalidDataException("无法读取本地已加载版本。");
            var repository = status.Element("RepSpec");
            if (repository == null || (string)repository.Element("Name") + "@" + (string)repository.Element("Server") != workspace.Repository)
                throw new InvalidDataException("本地版本的仓库与历史记录不一致。");
            return changeset;
        }

        private async Task<long> ReadApproximateHistoryRootAsync(PlasticWorkspace workspace, CancellationToken token)
        {
            try
            {
                var result = await ExecuteAsync(RevisionCommand(workspace.RootPath,
                    new[] { "fileinfo", workspace.RootPath, "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
                if (!result.Succeeded) return 1;
                var item = SafeXml.Load(result.Output).Descendants("FileInfo").SingleOrDefault();
                long changeset;
                return item != null && SamePath((string)item.Element("ClientPath") ?? "", workspace.RootPath) &&
                    (string)item.Element("RepSpec") == workspace.Repository &&
                    Int64.TryParse((string)item.Element("RevisionChangeset"), out changeset) && changeset >= 0 ? changeset : 1;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Trace.TraceWarning("Cannot initialize approximate history watermark: " + error.Message); return 1; }
        }

        internal async Task<PlasticHistoryRootUpdate> PrepareHistoryRootUpdateAsync(PlasticProcessCommand command, CancellationToken token)
        {
            int verb = command.Arguments.Count > 0 && command.Arguments[0] == "partial" ? 1 : 0;
            if (command.Arguments.Count <= verb + 1 || command.Arguments[verb] != "update") return null;
            var workspace = DiscoverWorkspace(command.WorkingDirectory);
            if (workspace == null || !SamePath(command.Arguments[verb + 1], workspace.RootPath)) return null;
            var capture = new PlasticHistoryRootUpdate { Workspace = workspace, IsPartial = verb == 1,
                WorkspaceIdentity = HistoryWorkspaceIdentity(workspace) };
            if (!capture.IsPartial) return capture;
            try
            {
                string pinned = command.Arguments.FirstOrDefault(argument => argument.StartsWith("--changeset=", StringComparison.Ordinal));
                long changeset;
                if (pinned != null && Int64.TryParse(pinned.Substring("--changeset=".Length), out changeset) && changeset >= 0)
                    capture.TargetChangeset = changeset;
                else
                {
                    var configured = Regex.Matches(workspace.Selector, @"(?m)^\s*(?:smartbranch|branch|br)\s+""([^""]+)""\s*$");
                    if (configured.Count != 1) return capture;
                    var result = await ExecuteAsync(RevisionCommand(workspace.RootPath,
                        new[] { "find", "branch", "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
                    RequireSuccess(result);
                    var branch = ParseBranches(result.Output, workspace.Repository).SingleOrDefault(item => item.Name == configured[0].Groups[1].Value);
                    if (branch != null) capture.TargetChangeset = branch.HeadChangeset;
                }
                ValidateHistoryWorkspace(workspace, capture.WorkspaceIdentity);
                if (capture.TargetChangeset.HasValue && pinned == null)
                    command.Arguments = command.Arguments.Concat(new[] { "--changeset=" + capture.TargetChangeset.Value.ToString(CultureInfo.InvariantCulture) }).ToList();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { Trace.TraceWarning("Cannot prepare root history watermark: " + error.Message); }
            return capture;
        }

        internal async Task CompleteHistoryRootUpdateAsync(PlasticHistoryRootUpdate capture, PlasticCommandResult result, CancellationToken token = default(CancellationToken))
        {
            if (capture == null || !result.Succeeded || token.IsCancellationRequested) return;
            try
            {
                ValidateHistoryWorkspace(capture.Workspace, capture.WorkspaceIdentity);
                long? loaded = capture.TargetChangeset;
                if (!capture.IsPartial)
                    loaded = await ReadHistoryWorkspaceChangesetAsync(capture.Workspace, CancellationToken.None).ConfigureAwait(false);
                if (!token.IsCancellationRequested && loaded.HasValue && loaded.Value >= 0)
                    TrySaveHistoryRootWatermark(capture.Workspace, loaded.Value, false, capture.WorkspaceIdentity);
            }
            catch (Exception error) { Trace.TraceWarning("Cannot record successful root update: " + error.Message); }
        }

        internal void RecordHistoryRootLoaded(PlasticWorkspace workspace, long changeset)
        {
            if (workspace != null && changeset >= 0)
                TrySaveHistoryRootWatermark(workspace, changeset, false);
        }

        private static string HistoryRootWatermarkPath(PlasticWorkspace workspace)
        { return Path.Combine(workspace.RootPath, ".plastic", "tortoisescm-root-update.xml"); }

        private static string HistoryWorkspaceIdentity(PlasticWorkspace workspace)
        { return File.ReadAllText(Path.Combine(workspace.RootPath, ".plastic", "plastic.workspace")); }

        // XML normalizes line endings. Keep these exact metadata bindings intact.
        private static string HistoryMarkerIdentityText(string value)
        { return Convert.ToBase64String(Encoding.UTF8.GetBytes(value)); }

        private static bool TryReadHistoryRootWatermark(PlasticWorkspace workspace, out long changeset, out bool approximate)
        {
            changeset = 1; approximate = false;
            try
            {
                using (var gate = new HistoryRootMarkerGate(workspace.RootPath))
                    return ReadHistoryRootWatermarkLocked(workspace, out changeset, out approximate);
            }
            catch (Exception error) { Trace.TraceWarning("Cannot read root history watermark: " + error.Message); return false; }
        }

        private static bool ReadHistoryRootWatermarkLocked(PlasticWorkspace workspace, out long changeset, out bool approximate)
        {
            changeset = 1; approximate = false;
            try
            {
                string path = HistoryRootWatermarkPath(workspace);
                if (!File.Exists(path)) return false;
                RejectReparsePath(path);
                var root = SafeXml.Load(File.ReadAllText(path)).Root;
                long value;
                if (root == null || root.Name != "RootUpdate" || (string)root.Attribute("version") != "1" ||
                    (string)root.Element("Repository") != workspace.Repository || (string)root.Element("Selector") != HistoryMarkerIdentityText(workspace.Selector) ||
                    (string)root.Element("WorkspaceIdentity") != HistoryMarkerIdentityText(HistoryWorkspaceIdentity(workspace)) ||
                    !Int64.TryParse((string)root.Element("Changeset"), out value) || value < 0) return false;
                changeset = value; approximate = (string)root.Element("Approximate") == "true"; return true;
            }
            catch (Exception error) { Trace.TraceWarning("Cannot read root history watermark: " + error.Message); return false; }
        }

        private void TrySaveHistoryRootWatermark(PlasticWorkspace workspace, long changeset, bool approximate, string expectedIdentity = null)
        {
            string temporary = null;
            try
            {
                using (var gate = new HistoryRootMarkerGate(workspace.RootPath))
                {
                    if (expectedIdentity == null) expectedIdentity = HistoryWorkspaceIdentity(workspace);
                    ValidateHistoryWorkspace(workspace, expectedIdentity);
                    long existing; bool existingApproximate;
                    // The check and publish share the cross-process mutex. A
                    // late initializer must never overwrite a completed update.
                    if (approximate && ReadHistoryRootWatermarkLocked(workspace, out existing, out existingApproximate)) return;
                    string path = HistoryRootWatermarkPath(workspace);
                    RejectReparsePath(path);
                    var document = new XDocument(new XElement("RootUpdate", new XAttribute("version", "1"),
                        new XElement("Repository", workspace.Repository), new XElement("Selector", HistoryMarkerIdentityText(workspace.Selector)),
                        new XElement("WorkspaceIdentity", HistoryMarkerIdentityText(expectedIdentity)), new XElement("Changeset", changeset),
                        new XElement("Approximate", approximate ? "true" : "false")));
                    temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false))) document.Save(writer);
                    ValidateHistoryWorkspace(workspace, expectedIdentity);
                    if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
                    temporary = null;
                }
            }
            catch (Exception error) { Trace.TraceWarning("Cannot save root history watermark: " + error.Message); }
            finally { if (temporary != null) try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }

        private void ValidateHistoryWorkspace(PlasticWorkspace workspace, string expectedIdentity = null)
        {
            var current = DiscoverWorkspace(workspace.RootPath);
            if (current == null || current.Repository != workspace.Repository || current.Selector != workspace.Selector ||
                current.Name != workspace.Name || (expectedIdentity != null && HistoryWorkspaceIdentity(current) != expectedIdentity))
                throw new InvalidOperationException("工作区已切换，请刷新本地拉取状态。");
        }

        private void ValidateHistoryLocalContext(PlasticHistoryLocalState state)
        { ValidateHistoryWorkspace(state.Workspace, state.WorkspaceIdentity); }

        private sealed class HistoryRootMarkerGate : IDisposable
        {
            private readonly Mutex mutex;
            internal HistoryRootMarkerGate(string root)
            {
                string key;
                using (var hash = SHA256.Create()) key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(
                    Path.GetFullPath(root).TrimEnd('\\', '/').ToUpperInvariant()))).Replace("-", "");
                mutex = new Mutex(false, "Local\\TortoiseSCM-HistoryRoot-" + key);
                bool acquired;
                try { acquired = mutex.WaitOne(5000); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) { mutex.Dispose(); throw new IOException("Root history watermark is busy."); }
            }
            public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
        }
    }
}
