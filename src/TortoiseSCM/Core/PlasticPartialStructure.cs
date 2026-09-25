// GPL-2.0-or-later. Explicit file structural decisions in Partial workspaces.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed class PlasticPartialStructureConflict
    {
        public string RepositoryPath { get; set; }
        public string OriginalPath { get; set; }
        public string IncomingPath { get; set; }
        public string Kind { get; set; }
        public string Reason { get; set; }
        public IList<string> ResolutionOptions { get; set; }
        public long BaseChangeset { get; set; }
        public long IncomingChangeset { get; set; }
        public long ItemId { get; set; }
        public long IncomingItemId { get; set; }
        public long IncomingRevisionChangeset { get; set; }
        public PlasticPartialStructureConflict() { ResolutionOptions = new List<string>(); }
    }
    public sealed class PlasticPartialStructureSession
    {
        public string SessionId { get; set; }
        public string WorkspaceRoot { get; set; }
        public PlasticPartialStructureConflict Conflict { get; set; }
        public string RecoveryDirectory { get; set; }
        public bool Ready { get; set; }
        public bool Applying { get; set; }
        public string Resolution { get; set; }
        public string RenamePath { get; set; }
    }
    public sealed partial class PlasticClient
    {
        private sealed class StructureState
        {
            internal PlasticPartialStructureSession Session;
            internal string Configuration, Repository, LocalHash, BaseHash, IncomingHash, PendingFingerprint;
        }
        private sealed class StructureTree
        {
            internal long Changeset;
            internal List<XElement> Items;
        }
        public bool HasSavedPartialStructureSession(string root) { return File.Exists(StructureIndex(Path.GetFullPath(root))); }
        public void ThrowIfPartialStructureActive(string root)
        {
            ThrowIfPartialDirectoryActive(root);
            if (HasSavedPartialStructureSession(root)) throw new ArgumentException("A Partial structural decision is active. Finish it, cancel an unapplied preparation, or explicitly recover to the incoming state first.");
        }
        private static string StructureIndex(string root) { return Path.Combine(root, ".plastic", "tortoisescm-structure.session"); }
        private static FileStream StructureGate(string root)
        {
            string path = Path.Combine(root, ".plastic", "tortoisescm-structure.lock"); RejectReparsePath(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new InvalidOperationException("Another structural decision is using this workspace.", error); }
        }
        public async Task<IList<PlasticPartialStructureConflict>> PreviewPartialStructureAsync(string root, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            var pending = await GetStatusAsync(root, token).ConfigureAwait(false);
            var tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            var result = new List<PlasticPartialStructureConflict>();
            foreach (var change in pending.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(item => item.StatusCode == "MV").First()))
            {
                if (change.IsDirectory || !new[] { "AD", "CH", "CO", "DE", "LD", "MV" }.Contains(change.StatusCode)) continue;
                string path = StructureRepositoryPath(root, change.Path);
                string original = change.StatusCode == "MV" ? StructureRepositoryPath(root, change.OldPath) : path;
                var conflict = new PlasticPartialStructureConflict { RepositoryPath = path, OriginalPath = original, IncomingChangeset = tree.Changeset, BaseChangeset = -1, ItemId = -1, IncomingItemId = -1, IncomingRevisionChangeset = -1, IncomingPath = "", Reason = "" };
                XElement incoming = null;
                if (change.StatusCode == "AD")
                {
                    incoming = tree.Items.SingleOrDefault(item => String.Equals((string)item.Element("CurrentPath"), path, StringComparison.OrdinalIgnoreCase));
                    if (incoming == null) continue;
                    conflict.Kind = "add-add";
                }
                else
                {
                    var infoResult = await ExecuteAsync(RevisionCommand(root, new[] { "fileinfo", change.Path, "--fields=RevisionChangeset,Type,IsUnderXlink", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(infoResult);
                    var info = SafeXml.Load(infoResult.Output).Descendants("FileInfo").Single();
                    long loaded;
                    if ((string)info.Element("IsUnderXlink") != "false" || ((string)info.Element("Type") != "txt" && (string)info.Element("Type") != "bin") ||
                        !Int64.TryParse((string)info.Element("RevisionChangeset"), out loaded) || loaded < 0)
                    {
                        if (change.StatusCode == "MV")
                        {
                            conflict.Kind = "local-move"; conflict.Reason = "The moved file's loaded revision or repository identity cannot be verified.";
                            result.Add(conflict);
                        }
                        continue;
                    }
                    conflict.BaseChangeset = loaded;
                    if (change.StatusCode == "DE")
                    {
                        try { conflict.ItemId = await StructureDeletedItemIdAsync(workspace, change.Path, loaded, token).ConfigureAwait(false); }
                        catch (ArgumentException error)
                        {
                            // Keep an unresolvable deletion scoped to its own
                            // path; it must not block preview/checkin elsewhere.
                            conflict.Kind = "local-delete"; conflict.Reason = error.Message;
                            result.Add(conflict); continue;
                        }
                    }
                    else conflict.ItemId = await StructureLocalItemIdAsync(root, change.Path, token).ConfigureAwait(false);
                    incoming = tree.Items.SingleOrDefault(item => (long?)item.Element("ItemId") == conflict.ItemId);
                    if (incoming == null) conflict.Kind = "incoming-delete";
                    else if ((string)incoming.Element("CurrentPath") != original) conflict.Kind = "incoming-move";
                    else if (change.StatusCode == "DE" || change.StatusCode == "LD")
                    { if ((long)incoming.Element("Changeset") == loaded) continue; conflict.Kind = "local-delete"; }
                    else if (change.StatusCode == "MV")
                    {
                        // A new server item can occupy the local destination even
                        // when the moved source itself has no incoming edits.
                        bool occupied = tree.Items.Any(item => String.Equals((string)item.Element("CurrentPath"), path, StringComparison.OrdinalIgnoreCase) && (long?)item.Element("ItemId") != conflict.ItemId);
                        if ((long)incoming.Element("Changeset") == loaded && !occupied) continue;
                        conflict.Kind = "local-move";
                    }
                    else continue;
                }
                if (incoming != null)
                {
                    conflict.IncomingPath = (string)incoming.Element("CurrentPath"); conflict.IncomingItemId = (long)incoming.Element("ItemId"); conflict.IncomingRevisionChangeset = (long)incoming.Element("Changeset");
                    if (!StructureRegularFile(incoming, workspace.Repository)) { conflict.Reason = "Directories, links and cross-repository targets require a separate decision."; result.Add(conflict); continue; }
                }
                if (conflict.Kind == "incoming-move" && change.StatusCode != "CH" && change.StatusCode != "CO") conflict.Reason = "An incoming move combined with a local move or deletion requires a separate structural decision.";
                else if (conflict.Kind == "incoming-delete" && (change.StatusCode == "MV" || change.StatusCode == "DE" || change.StatusCode == "LD")) conflict.Reason = "Combined local structure and incoming deletion are not supported by the file-only resolver.";
                else if (tree.Items.Any(item => (string)item.Element("CurrentPath") == original && (long?)item.Element("ItemId") != conflict.IncomingItemId)) conflict.Reason = "The original path is now occupied by a replacement item.";
                else if (conflict.Kind == "local-move" && tree.Items.Any(item => String.Equals((string)item.Element("CurrentPath"), path, StringComparison.OrdinalIgnoreCase))) conflict.Reason = "The local move destination is occupied at the incoming revision.";
                else
                {
                    if (conflict.Kind == "local-move")
                    {
                        try { await ValidateStructureParentsAsync(workspace, new[] { original, path }, tree, token).ConfigureAwait(false); }
                        catch (ArgumentException error) { conflict.Reason = error.Message; result.Add(conflict); continue; }
                    }
                    if (conflict.Kind == "incoming-move")
                    {
                        try { await ValidateIncomingMoveScopeAsync(workspace, conflict, token, tree).ConfigureAwait(false); }
                        catch (ArgumentException error) { conflict.Reason = error.Message; result.Add(conflict); continue; }
                    }
                    conflict.ResolutionOptions.Add("take-incoming"); conflict.ResolutionOptions.Add("keep-local");
                    if (conflict.Kind != "local-delete" && conflict.Kind != "incoming-move") conflict.ResolutionOptions.Add("rename");
                }
                result.Add(conflict);
            }
            return result;
        }
        public async Task<PlasticPartialStructureSession> PreparePartialStructureAsync(string root, string repositoryPath, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            ValidateMergeStorageLocation(workspace.RootPath);
            using (var gate = StructureGate(root))
            {
                ThrowIfPartialStructureActive(root);
                if (HasSavedPartialConflictSession(root)) throw new ArgumentException("Finish or cancel the prepared content conflict first.");
                var conflict = (await PreviewPartialStructureAsync(root, token).ConfigureAwait(false)).SingleOrDefault(item => item.RepositoryPath == repositoryPath);
                if (conflict == null || conflict.ResolutionOptions.Count == 0) throw new ArgumentException(conflict == null ? "The selected structural conflict no longer exists." : conflict.Reason);
                await ValidateStructureScopeAsync(workspace, conflict, token).ConfigureAwait(false);
                var state = new StructureState { Repository = workspace.Repository, Configuration = conflict.Kind == "incoming-move" ? IncomingMoveConfiguration(root) : PartialConfiguration(root), Session = new PlasticPartialStructureSession { SessionId = Guid.NewGuid().ToString("N"), WorkspaceRoot = root, Conflict = conflict, Ready = true, Resolution = "", RenamePath = "" } };
                state.Session.RecoveryDirectory = MergeSessionDirectory(state.Session.SessionId); CreatePrivateMergeDirectory(state.Session.RecoveryDirectory);
                string local = MergeLocalPath(root, repositoryPath);
                state.LocalHash = StructureFileHash(local);
                if (File.Exists(local)) { StructureRequireSingleLink(local); File.Copy(local, StructureBackup(state, "local"), false); }
                if (conflict.BaseChangeset >= 0) await DownloadPartialIdentityFileAsync(workspace, conflict.ItemId, conflict.BaseChangeset, StructureBackup(state, "base"), token).ConfigureAwait(false);
                if (conflict.IncomingItemId >= 0) await DownloadHistoricalFileAsync(workspace, conflict.IncomingPath, conflict.IncomingChangeset, StructureBackup(state, "incoming"), token).ConfigureAwait(false);
                state.BaseHash = StructureFileHash(StructureBackup(state, "base")); state.IncomingHash = StructureFileHash(StructureBackup(state, "incoming"));
                if (StructureFileHash(local) != state.LocalHash || (state.LocalHash != "missing" && MergeHash(StructureBackup(state, "local")) != state.LocalHash)) throw new IOException("The local contributor changed during preparation.");
                state.PendingFingerprint = await StructurePendingFingerprintAsync(root, conflict, token).ConfigureAwait(false);
                foreach (string file in new[] { "local", "base", "incoming" }.Select(name => StructureBackup(state, name)).Where(File.Exists)) File.SetAttributes(file, FileAttributes.ReadOnly);
                SaveStructureState(state); File.WriteAllText(StructureIndex(root), Path.Combine(state.Session.RecoveryDirectory, "structure.xml"));
                return state.Session;
            }
        }
        public async Task<PlasticPartialStructureSession> GetPartialStructureSessionAsync(string root, CancellationToken token)
        {
            await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var gate = StructureGate(root)) { var state = LoadStructureState(root, false); return state == null ? null : state.Session; }
        }
        public async Task CancelPartialStructureAsync(string root, CancellationToken token)
        {
            await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var gate = StructureGate(root))
            { ThrowIfPartialDirectoryActive(root); var state = LoadStructureState(root, true); if (!state.Session.Ready || state.Session.Applying) throw new ArgumentException("An applied or interrupted decision requires explicit recovery to the incoming state."); File.Delete(StructureIndex(root)); }
        }
        public async Task<PlasticCommandResult> ResolvePartialStructureAsync(string root, string resolution, string rename, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var gate = StructureGate(root))
            {
                var state = LoadStructureState(root, true); var conflict = state.Session.Conflict;
                ThrowIfPartialDirectoryActive(root);
                ValidateStructureConfiguration(state, workspace);
                if (!state.Session.Ready || state.Session.Applying) throw new ArgumentException("Use explicit recovery to the incoming state for an interrupted structural decision.");
                if (!conflict.ResolutionOptions.Contains(resolution)) throw new ArgumentException("Choose one of the offered structural resolutions.");
                if (resolution != "rename" && !String.IsNullOrEmpty(rename)) throw new ArgumentException("A destination is accepted only for rename.");
                if (resolution == "rename" && !String.IsNullOrEmpty(rename) && rename.IndexOfAny(new[] { '/', '\\' }) < 0)
                {
                    string relativeTo = conflict.Kind == "local-move" ? conflict.RepositoryPath : conflict.OriginalPath;
                    rename = relativeTo.Substring(0, relativeTo.LastIndexOf('/') + 1) + rename;
                }
                if (resolution == "rename") await ValidateStructureRenameAsync(workspace, conflict, rename, token).ConfigureAwait(false);
                var current = (await PreviewPartialStructureAsync(root, token).ConfigureAwait(false)).SingleOrDefault(item => item.RepositoryPath == conflict.RepositoryPath);
                if (current == null || StructureConflictKey(current) != StructureConflictKey(conflict) || StructureFileHash(MergeLocalPath(root, conflict.RepositoryPath)) != state.LocalHash ||
                    await StructurePendingFingerprintAsync(root, conflict, token).ConfigureAwait(false) != state.PendingFingerprint) throw new ArgumentException("The local or incoming structure changed since preparation. Cancel and prepare again; no change was applied.");
                await ValidateStructureScopeAsync(workspace, conflict, token).ConfigureAwait(false);
                state.Session.Resolution = resolution; state.Session.RenamePath = rename ?? ""; state.Session.Ready = false; state.Session.Applying = true; SaveStructureState(state);
                try
                {
                    await StructureTakeIncomingAsync(state, workspace, false, token).ConfigureAwait(false);
                    if (resolution != "take-incoming") await StructureApplyLocalAsync(state, workspace, token).ConfigureAwait(false);
                    ValidateStructureConfiguration(state, workspace);
                    if ((await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false)).Changeset != conflict.IncomingChangeset) throw new InvalidOperationException("The branch advanced during resolution. The decision remains recoverable; no checkin was performed.");
                    await VerifyStructureAppliedAsync(state, workspace, token).ConfigureAwait(false);
                    File.Delete(StructureIndex(root));
                    return new PlasticCommandResult { Output = "Applied the explicit structural decision. Local backups are retained in " + state.Session.RecoveryDirectory + ". Pending changes require a separate checkin." };
                }
                catch (Exception error) { throw new InvalidOperationException("Structural resolution did not finish. Other writes are blocked. Recover explicitly to the incoming state; original backups remain at " + state.Session.RecoveryDirectory + ". " + error.Message, error); }
            }
        }
        public async Task<PlasticCommandResult> RecoverPartialStructureAsync(string root, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var gate = StructureGate(root))
            {
                var state = LoadStructureState(root, true); ValidateStructureConfiguration(state, workspace);
                ThrowIfPartialDirectoryActive(root);
                if (state.Session.Ready && !state.Session.Applying) throw new ArgumentException("This preparation was not applied; cancel it instead.");
                var changes = await GetStatusAsync(root, token).ConfigureAwait(false);
                if (changes.Any(item => item.IsDirectory && StructurePaths(state).Any(path => IsWithin(MergeLocalPath(root, path), item.Path)))) throw new ArgumentException("A parent directory has pending structural changes. Preserve and finish those changes before file recovery.");
                // Preserve the current visible bytes as well, including edits made after failure.
                string observed = Path.Combine(state.Session.RecoveryDirectory, "recovery-" + Guid.NewGuid().ToString("N")); CreatePrivateMergeDirectory(observed);
                var recoveryHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in StructurePaths(state))
                {
                    string local = MergeLocalPath(root, path);
                    recoveryHashes[path] = StructureFileHash(local);
                    if (File.Exists(local)) { StructureRequireSingleLink(local); string backup = Path.Combine(observed, MergeKey(path) + ".bin"); File.Copy(local, backup, false); if (MergeHash(backup) != recoveryHashes[path]) throw new IOException("The recovery snapshot changed while being copied."); }
                }
                foreach (var entry in recoveryHashes) if (StructureFileHash(MergeLocalPath(root, entry.Key)) != entry.Value) throw new IOException("The local files changed during recovery backup; no native recovery was started.");
                await StructureTakeIncomingAsync(state, workspace, true, token, recoveryHashes).ConfigureAwait(false);
                ValidateStructureConfiguration(state, workspace); File.Delete(StructureIndex(root));
                return new PlasticCommandResult { Output = "Recovered the affected files to the pinned incoming state. Original and recovery-time local bytes remain in " + state.Session.RecoveryDirectory + ". No checkin was performed." };
            }
        }
        private async Task StructureTakeIncomingAsync(StructureState state, PlasticWorkspace workspace, bool recovery, CancellationToken token, Dictionary<string, string> recoveryHashes = null)
        {
            string root = workspace.RootPath; var conflict = state.Session.Conflict;
            await ValidatePartialLoadedDirectoriesAtAsync(workspace, conflict.IncomingChangeset, token).ConfigureAwait(false);
            if (conflict.Kind == "incoming-move")
            { await StructureTakeIncomingMoveAsync(state, workspace, recovery, token, recoveryHashes).ConfigureAwait(false); return; }
            var scope = new HashSet<string>(StructurePaths(state), StringComparer.OrdinalIgnoreCase);
            if (conflict.Kind == "local-move") await ValidateStructureParentsAsync(workspace, scope, await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false), token).ConfigureAwait(false);
            foreach (string path in scope) if (Directory.Exists(MergeLocalPath(root, path))) throw new ArgumentException("A conflict file path became a directory; file-only recovery will not recurse into it.");
            var expected = scope.ToDictionary(path => path, path => recovery ? recoveryHashes[path] :
                String.Equals(path, conflict.RepositoryPath, StringComparison.OrdinalIgnoreCase) ? state.LocalHash : "missing", StringComparer.OrdinalIgnoreCase);
            var pending = await GetStatusAsync(root, token).ConfigureAwait(false);
            foreach (var change in pending.Where(item => scope.Contains(StructureRepositoryPath(root, item.Path))).GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.OrderByDescending(item => item.StatusCode == "MV").First()).ToList())
            {
                if (change.StatusCode == "PR" || change.StatusCode == "IG") continue;
                if (change.IsDirectory || (!String.IsNullOrEmpty(change.OldPath) && !scope.Contains(StructureRepositoryPath(root, change.OldPath)))) throw new ArgumentException("A structural path now belongs to a different move or directory operation. Preserve it before recovery.");
                if (change.StatusCode != "AD")
                {
                    long identity;
                    if (change.StatusCode == "DE")
                    {
                        var info = await StructureFileInfoAsync(root, change.Path, token).ConfigureAwait(false);
                        long revision = (long?)info.Element("RevisionChangeset") ?? -1;
                        identity = revision < 0 ? -1 : await StructureDeletedItemIdAsync(workspace, change.Path, revision, token).ConfigureAwait(false);
                    }
                    else identity = await StructureLocalItemIdAsync(root, change.Path, token).ConfigureAwait(false);
                    if (identity != conflict.ItemId && identity != conflict.IncomingItemId) throw new ArgumentException("A structural path now belongs to a different controlled item; recovery did not overwrite it.");
                }
                ValidateStructureSnapshot(root, expected);
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "undo", change.Path }), token).ConfigureAwait(false));
                if (change.StatusCode != "AD")
                {
                    string restored = String.IsNullOrEmpty(change.OldPath) ? change.Path : change.OldPath;
                    string restoredHash = StructureFileHash(restored);
                    if (restoredHash != state.BaseHash && restoredHash != state.IncomingHash) throw new IOException("The restored file changed before the pinned update; its current bytes were preserved.");
                    expected[StructureRepositoryPath(root, restored)] = restoredHash;
                    if (!String.IsNullOrEmpty(change.OldPath)) expected[StructureRepositoryPath(root, change.Path)] = "missing";
                }
                ValidateStructureSnapshot(root, expected);
            }
            // Undoing an addition leaves a private file. Clear only these explicitly
            // scoped private paths, after their bytes have been durably backed up.
            foreach (string path in scope)
            {
                string local = MergeLocalPath(root, path); if (!File.Exists(local)) continue;
                var info = await StructureFileInfoAsync(root, local, token).ConfigureAwait(false);
                if ((string)info.Element("Status") == "private")
                {
                    if ((!recovery && StructureFileHash(local) != state.LocalHash) || (recovery && (!recoveryHashes.ContainsKey(path) || StructureFileHash(local) != recoveryHashes[path]))) throw new IOException("A private conflict path changed after backup.");
                    ValidateStructureSnapshot(root, expected);
                    StructureRequireSingleLink(local); File.Delete(local); expected[path] = "missing";
                }
            }
            string original = MergeLocalPath(root, conflict.OriginalPath);
            if (Directory.Exists(original)) throw new ArgumentException("The original file path became a directory; no recursive update was performed.");
            // Updating a tracked file applies a pinned delete, or advances it to the
            // exact incoming revision. Never select a parent directory here.
            if (File.Exists(original) || conflict.IncomingItemId >= 0)
            {
                ValidateStructureSnapshot(root, expected);
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "update", original, "--changeset=" + conflict.IncomingChangeset.ToString(CultureInfo.InvariantCulture), "--dontmerge", "--report" }), token).ConfigureAwait(false));
            }
            if (conflict.Kind == "add-add" && !File.Exists(original))
            {
                // A locally-added item need not be in the server load tree. Load
                // only the explicitly selected counterpart, never its parent.
                var beforeLoad = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
                var counterpart = beforeLoad.Items.SingleOrDefault(item => (string)item.Element("CurrentPath") == conflict.OriginalPath);
                if (beforeLoad.Changeset != conflict.IncomingChangeset || counterpart == null || !StructureRegularFile(counterpart, workspace.Repository) || (long?)counterpart.Element("ItemId") != conflict.IncomingItemId)
                    throw new IOException("The incoming file changed before loading its selected counterpart. No directory configure was requested.");
                if (File.Exists(original) || Directory.Exists(original)) throw new IOException("The selected counterpart path became occupied before loading.");
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "configure", "+" + conflict.OriginalPath }), token).ConfigureAwait(false));
                // Configure has no changeset parameter. A concurrent server path
                // replacement must never be followed by a recursive pinned update.
                if (Directory.Exists(original) || !File.Exists(original)) throw new IOException("The selected counterpart is no longer a regular file after configure. Recovery remains blocked for review; no recursive update was performed.");
                var loadedInfo = await StructureFileInfoAsync(root, original, token).ConfigureAwait(false);
                if ((string)loadedInfo.Element("Status") != "controlled" || (long?)loadedInfo.Element("RevisionChangeset") != conflict.IncomingRevisionChangeset ||
                    await StructureLocalItemIdAsync(root, original, token).ConfigureAwait(false) != conflict.IncomingItemId)
                    throw new IOException("The selected counterpart changed identity or revision while it was being loaded. No further update was performed.");
                StructureRequireSingleLink(original);
                if (StructureFileHash(original) != state.IncomingHash) throw new IOException("The newly loaded counterpart was edited; its current bytes were preserved.");
                ValidateStructureConfiguration(state, workspace);
            }
            if (conflict.IncomingItemId < 0)
            { if (File.Exists(original)) throw new IOException("Pinned incoming deletion was not applied."); }
            else
            {
                var info = await StructureFileInfoAsync(root, original, token).ConfigureAwait(false);
                if ((string)info.Element("Status") != "controlled" || (long?)info.Element("RevisionChangeset") != conflict.IncomingRevisionChangeset || StructureFileHash(original) != state.IncomingHash)
                    throw new IOException("The native update did not load the pinned incoming file.");
                long id = await StructureLocalItemIdAsync(root, original, token).ConfigureAwait(false);
                if (id != conflict.IncomingItemId) throw new IOException("The incoming item identity was replaced.");
            }
            foreach (string path in scope.Where(path => path != conflict.OriginalPath)) if (File.Exists(MergeLocalPath(root, path))) throw new IOException("A structural destination remains occupied after accepting incoming.");
        }
        private async Task StructureApplyLocalAsync(StructureState state, PlasticWorkspace workspace, CancellationToken token)
        {
            var conflict = state.Session.Conflict; string root = workspace.RootPath;
            string original = MergeLocalPath(root, conflict.OriginalPath);
            string targetPath = state.Session.Resolution == "rename" ? state.Session.RenamePath : conflict.RepositoryPath;
            string target = MergeLocalPath(root, targetPath);
            if (conflict.Kind == "incoming-move") StructureWriteBackup(state, MergeLocalPath(root, conflict.IncomingPath), state.IncomingHash);
            else if (conflict.Kind == "local-delete")
            {
                StructureRequireSingleLink(original);
                if (StructureFileHash(original) != state.IncomingHash) throw new IOException("The incoming file was edited before reapplying the deletion; its current bytes were preserved.");
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "remove", original }), token).ConfigureAwait(false));
            }
            else if (conflict.Kind == "local-move")
            {
                await ValidateStructureParentsAsync(workspace, new[] { conflict.OriginalPath, targetPath }, await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false), token).ConfigureAwait(false);
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("The rename destination became occupied before the native move.");
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "move", original, target }), token).ConfigureAwait(false));
                // A pure rename keeps incoming content. Explicit keep-local also
                // preserves any local content edit that accompanied the rename.
                if (state.LocalHash != state.BaseHash) StructureWriteBackup(state, target, state.IncomingHash);
            }
            else if (conflict.Kind == "incoming-delete" || state.Session.Resolution == "rename")
            {
                if (File.Exists(target) || Directory.Exists(target)) throw new IOException("The destination became occupied before restoring local content.");
                File.Copy(StructureBackup(state, "local"), target, false); File.SetAttributes(target, FileAttributes.Normal);
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "add", target }), token).ConfigureAwait(false));
            }
            else StructureWriteBackup(state, original, state.IncomingHash);
        }
        private static void ValidateStructureSnapshot(string root, IDictionary<string, string> expected)
        {
            foreach (var entry in expected)
            {
                string local = MergeLocalPath(root, entry.Key);
                if (Directory.Exists(local) || StructureFileHash(local) != entry.Value)
                    throw new IOException("A conflict path changed after backup; its newer bytes were preserved: " + entry.Key);
            }
        }
        private void StructureWriteBackup(StructureState state, string target, string expected)
        {
            StructureRequireSingleLink(target); if (MergeHash(target) != expected) throw new IOException("The target changed before applying the local decision.");
            string temporary = target + ".tscm-" + Guid.NewGuid().ToString("N");
            try { File.Copy(StructureBackup(state, "local"), temporary, false); File.SetAttributes(temporary, FileAttributes.Normal); if (MergeHash(target) != expected) throw new IOException("The target changed before replacement."); File.Replace(temporary, target, null); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private async Task VerifyStructureAppliedAsync(StructureState state, PlasticWorkspace workspace, CancellationToken token)
        {
            if (state.Session.Resolution == "take-incoming") return;
            var conflict = state.Session.Conflict; var pending = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
            string path = state.Session.Resolution == "rename" ? state.Session.RenamePath : conflict.Kind == "incoming-move" ? conflict.IncomingPath : conflict.RepositoryPath;
            string local = MergeLocalPath(workspace.RootPath, path);
            string code = conflict.Kind == "local-delete" ? "DE" : conflict.Kind == "local-move" ? "MV" : conflict.Kind == "incoming-delete" || state.Session.Resolution == "rename" ? "AD" : "CH";
            if (!pending.Any(item => SamePath(item.Path, local) && (item.StatusCode == code || (code == "CH" && item.StatusCode == "CO")))) throw new IOException("The native pending structure does not match the reviewed decision.");
            if (code != "DE")
            {
                string expected = conflict.Kind == "local-move" && state.LocalHash == state.BaseHash ? state.IncomingHash : state.LocalHash;
                if (StructureFileHash(local) != expected) throw new IOException("The structural result bytes failed verification.");
            }
        }
        private async Task ValidateStructureScopeAsync(PlasticWorkspace workspace, PlasticPartialStructureConflict conflict, CancellationToken token)
        {
            await ValidatePartialLoadedDirectoriesAtAsync(workspace, conflict.IncomingChangeset, token).ConfigureAwait(false);
            if (conflict.Kind == "incoming-move") await ValidateIncomingMoveScopeAsync(workspace, conflict, token).ConfigureAwait(false);
            foreach (string path in new[] { conflict.RepositoryPath, conflict.OriginalPath }.Distinct())
            {
                string local = MergeLocalPath(workspace.RootPath, path);
                if (File.Exists(local)) StructureRequireSingleLink(local);
                if (Directory.Exists(local)) throw new ArgumentException("Only individual files are supported.");
                var nested = DiscoverWorkspace(local); if (nested == null || !SamePath(nested.RootPath, workspace.RootPath)) throw new ArgumentException("Nested workspace paths are not supported.");
            }
            var pending = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
            if (pending.Any(item => item.IsDirectory && new[] { conflict.RepositoryPath, conflict.OriginalPath }.Any(path => IsWithin(MergeLocalPath(workspace.RootPath, path), item.Path)))) throw new ArgumentException("Complete the parent directory change before resolving this file.");
            if (conflict.Kind == "local-move") await ValidateStructureParentsAsync(workspace, new[] { conflict.RepositoryPath, conflict.OriginalPath }, await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false), token).ConfigureAwait(false);
            if (conflict.RepositoryPath != conflict.OriginalPath && File.Exists(MergeLocalPath(workspace.RootPath, conflict.OriginalPath))) throw new ArgumentException("The original rename path is occupied by an unrelated file.");
        }
        private async Task ValidateStructureRenameAsync(PlasticWorkspace workspace, PlasticPartialStructureConflict conflict, string rename, CancellationToken token)
        {
            ValidateRepositoryFilePath(rename);
            if ((conflict.Kind != "local-move" && Path.GetDirectoryName(rename) != Path.GetDirectoryName(conflict.OriginalPath)) || String.Equals(rename, conflict.RepositoryPath, StringComparison.OrdinalIgnoreCase) || String.Equals(rename, conflict.OriginalPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an unoccupied new filename; only a local move can choose another loaded, controlled directory.");
            string local = MergeLocalPath(workspace.RootPath, rename);
            ValidateHistoricalOutput(local, false);
            if (File.Exists(local) || Directory.Exists(local)) throw new ArgumentException("The rename destination already exists.");
            var tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            if (tree.Items.Any(item => String.Equals((string)item.Element("CurrentPath"), rename, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("The rename destination is already controlled at the incoming revision.");
            if (conflict.Kind == "local-move") await ValidateStructureParentsAsync(workspace, new[] { rename }, tree, token).ConfigureAwait(false);
        }
        private async Task ValidateStructureParentsAsync(PlasticWorkspace workspace, IEnumerable<string> paths, StructureTree tree, CancellationToken token)
        {
            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string parent = path.Substring(0, path.LastIndexOf('/'));
                while (!String.IsNullOrEmpty(parent)) { parents.Add(parent); parent = parent.Substring(0, parent.LastIndexOf('/')); }
            }
            var pending = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
            foreach (string parent in parents)
            {
                string local = MergeLocalPath(workspace.RootPath, parent); RejectReparsePath(local);
                if (!Directory.Exists(local)) throw new ArgumentException("The source and destination parent directories must already be loaded.");
                var nested = DiscoverWorkspace(local);
                if (nested == null || !SamePath(nested.RootPath, workspace.RootPath)) throw new ArgumentException("A move parent belongs to a nested workspace.");
                if (pending.Any(item => SamePath(item.Path, local) || (!String.IsNullOrEmpty(item.OldPath) && SamePath(item.OldPath, local)))) throw new ArgumentException("Complete pending parent directory changes before resolving this file move.");
                var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "fileinfo", local, "--fields=Type,Status,IsUnderXlink", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result);
                var info = SafeXml.Load(result.Output).Descendants("FileInfo").Single();
                // Moving a child implicitly checks out its parents without a
                // directory status row. Explicit pending parent changes were
                // rejected above; this native bookkeeping remains controlled.
                if ((string)info.Element("Type") != "dir" || !new[] { "controlled", "checked-out" }.Contains((string)info.Element("Status")) || (string)info.Element("IsUnderXlink") != "false") throw new ArgumentException("Move parents must be controlled directories outside Xlinks.");
                var incoming = tree.Items.SingleOrDefault(item => String.Equals((string)item.Element("CurrentPath"), parent, StringComparison.OrdinalIgnoreCase));
                if (incoming == null || !new[] { "dir", "directory", "目录" }.Contains((string)incoming.Element("Type"), StringComparer.OrdinalIgnoreCase) || !String.IsNullOrEmpty((string)incoming.Element("SymlinkTarget")) || (string)incoming.Element("Repository") != "rep:" + workspace.Repository) throw new ArgumentException("A move parent was removed, replaced or linked at the incoming revision.");
                var listed = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", local, "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(listed);
                var loaded = SafeXml.Load(listed.Output).Descendants("LsItem").SingleOrDefault(item => SamePath((string)item.Element("CurrentPath"), local));
                if (loaded == null || (long?)loaded.Element("ItemId") != (long?)incoming.Element("ItemId")) throw new ArgumentException("A move parent changed identity at the incoming revision.");
            }
        }
        private async Task<StructureTree> ReadStructureTreeAsync(PlasticWorkspace workspace, CancellationToken token)
        {
            var status = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "status", "--header", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(status);
            string spec = (string)SafeXml.Load(status.Output).Root.Element("WkConfigName"), suffix = "@" + workspace.Repository;
            if (String.IsNullOrEmpty(spec) || !spec.StartsWith("/", StringComparison.Ordinal) || !spec.EndsWith(suffix, StringComparison.Ordinal)) throw new InvalidDataException("The Partial branch is ambiguous.");
            string branch = spec.Substring(0, spec.Length - suffix.Length);
            if (branch.IndexOfAny(new[] { '\'', '\r', '\n' }) >= 0) throw new ArgumentException("This branch name cannot be safely queried.");
            var found = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "find", "changeset", "where branch = '" + branch + "' order by changesetid desc limit 1", "--xml", "--nototal", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(found);
            var changesets = SafeXml.Load(found.Output).Descendants("CHANGESET").ToList(); if (changesets.Count != 1) throw new InvalidDataException("The branch does not identify one head changeset.");
            long head = MergeNumber((string)changesets[0].Element("CHANGESETID"));
            var listed = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", "/", "--tree=cs:" + head.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(listed);
            return new StructureTree { Changeset = head, Items = SafeXml.Load(listed.Output).Descendants("LsItem").ToList() };
        }
        private static bool StructureRegularFile(XElement item, string repository)
        { string type = (string)item.Element("Type"); return new[] { "txt", "bin", "text", "binary", "Text file", "Binary file", "文本文件", "二进制文件" }.Contains(type, StringComparer.OrdinalIgnoreCase) && String.IsNullOrEmpty((string)item.Element("SymlinkTarget")) && (string)item.Element("Repository") == "rep:" + repository; }
        private static string StructureRepositoryPath(string root, string local)
        { if (String.IsNullOrEmpty(local) || !IsWithin(Path.GetFullPath(local), root)) throw new InvalidDataException("A structural path escaped its workspace."); return "/" + local.Substring(root.TrimEnd('\\').Length).TrimStart('\\').Replace('\\', '/'); }
        private static string StructureFileHash(string path) { RejectReparsePath(path); return File.Exists(path) ? MergeHash(path) : "missing"; }
        private static void StructureRequireSingleLink(string path)
        { using (var input = File.OpenRead(path)) { ToolFileInformation info; if (!GetFileInformationByHandle(input.SafeFileHandle, out info) || info.Links != 1) throw new ArgumentException("Structural resolution does not support files with hard-link aliases."); } }
        private static string StructureConflictKey(PlasticPartialStructureConflict c)
        { return String.Join("|", c.RepositoryPath, c.OriginalPath, c.IncomingPath, c.Kind, c.BaseChangeset, c.IncomingChangeset, c.ItemId, c.IncomingItemId, c.IncomingRevisionChangeset); }
        private static string StructureBackup(StructureState state, string kind) { return Path.Combine(state.Session.RecoveryDirectory, kind + ".bin"); }
        private static IEnumerable<string> StructurePaths(StructureState state)
        { return new[] { state.Session.Conflict.RepositoryPath, state.Session.Conflict.OriginalPath, state.Session.RenamePath,
            state.Session.Conflict.Kind == "incoming-move" ? state.Session.Conflict.IncomingPath : null }.Where(path => !String.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase); }
        private static void ValidateStructureConfiguration(StructureState state, PlasticWorkspace workspace)
        { if (state.Repository != workspace.Repository || state.Configuration != (state.Session.Conflict.Kind == "incoming-move" ? IncomingMoveConfiguration(workspace.RootPath) : PartialConfiguration(workspace.RootPath))) throw new ArgumentException("The workspace identity, selector or Partial load configuration changed. Restore that configuration before recovery."); }
        private async Task<XElement> StructureFileInfoAsync(string root, string path, CancellationToken token)
        { var result = await ExecuteAsync(RevisionCommand(root, new[] { "fileinfo", path, "--fields=RevisionChangeset,Status", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result); return SafeXml.Load(result.Output).Descendants("FileInfo").Single(); }
        private async Task<long> StructureLocalItemIdAsync(string root, string path, CancellationToken token)
        { var result = await ExecuteAsync(RevisionCommand(root, new[] { "ls", path, "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result); return (long)SafeXml.Load(result.Output).Descendants("LsItem").Single().Element("ItemId"); }
        private async Task<long> StructureDeletedItemIdAsync(PlasticWorkspace workspace, string path, long loadedRevision, CancellationToken token)
        {
            // Native DE omits the item from local ls. Paths cannot identify it:
            // pure directory/file moves preserve revision changesets, and names
            // can be swapped between two items created in the same changeset.
            // Use native loaded revision/hash metadata only when it identifies
            // exactly one regular item in that revision's complete history tree.
            string root = workspace.RootPath, parent = Path.GetDirectoryName(path);
            RejectReparsePath(parent);
            var nested = DiscoverWorkspace(parent);
            if (!Directory.Exists(parent) || nested == null || !SamePath(nested.RootPath, root)) throw new ArgumentException("The deleted file's loaded parent cannot be verified.");
            var pending = await GetStatusAsync(root, token).ConfigureAwait(false);
            if (pending.Any(item => item.IsDirectory && (IsWithin(path, item.Path) || (!String.IsNullOrEmpty(item.OldPath) && IsWithin(path, item.OldPath))))) throw new ArgumentException("Complete pending parent directory changes before resolving this deletion.");
            var result = await ExecuteAsync(RevisionCommand(root, new[] { "fileinfo", parent, "--fields=RevisionChangeset,Type,Status,IsUnderXlink,RepSpec", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result);
            var info = SafeXml.Load(result.Output).Descendants("FileInfo").Single();
            long parentRevision;
            if (!Int64.TryParse((string)info.Element("RevisionChangeset"), out parentRevision) || parentRevision < 0 || loadedRevision < 0 ||
                (string)info.Element("Type") != "dir" || !new[] { "controlled", "checked-out" }.Contains((string)info.Element("Status")) ||
                (string)info.Element("IsUnderXlink") != "false" || (string)info.Element("RepSpec") != workspace.Repository) throw new ArgumentException("The deleted file's parent must be a loaded directory in this repository.");
            result = await ExecuteAsync(RevisionCommand(root, new[] { "fileinfo", path, "--fields=RevisionChangeset,Hash,Status,Type,IsUnderXlink", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result);
            var deleted = SafeXml.Load(result.Output).Descendants("FileInfo").Single();
            string hash = (string)deleted.Element("Hash");
            if ((long?)deleted.Element("RevisionChangeset") != loadedRevision || String.IsNullOrEmpty(hash) ||
                (string)deleted.Element("Status") != "deleted" || !new[] { "txt", "bin" }.Contains((string)deleted.Element("Type")) ||
                (string)deleted.Element("IsUnderXlink") != "false") throw new ArgumentException("The deleted file's loaded revision and content identity cannot be verified.");
            string tree = "--tree=cs:" + loadedRevision.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository;
            result = await ExecuteAsync(RevisionCommand(root, new[] { "ls", "/", tree, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result);
            var historical = SafeXml.Load(result.Output).Descendants("LsItem").ToList();
            var matches = historical.Where(item => (long?)item.Element("Changeset") == loadedRevision && (string)item.Element("Hash") == hash && StructureRegularFile(item, workspace.Repository)).ToList();
            if (matches.Count != 1 || ((long?)matches[0].Element("ItemId") ?? -1) <= 0) throw new ArgumentException("The deleted file's identity is ambiguous: multiple items may share its loaded revision and content. Automatic structural resolution was refused; preserve the deletion and resolve it with the native client.");
            return (long)matches[0].Element("ItemId");
        }
        private async Task<string> StructurePendingFingerprintAsync(string root, PlasticPartialStructureConflict conflict, CancellationToken token)
        { return String.Join("\n", (await GetStatusAsync(root, token).ConfigureAwait(false)).Where(item => SamePath(item.Path, MergeLocalPath(root, conflict.RepositoryPath)) || SamePath(item.Path, MergeLocalPath(root, conflict.OriginalPath))).Select(item => item.StatusCode + "|" + item.Path.ToUpperInvariant() + "|" + item.OldPath).OrderBy(value => value)); }
        private void SaveStructureState(StructureState state)
        {
            var session = state.Session; var c = session.Conflict;
            var xml = new XElement("PartialStructure", new XAttribute("id", session.SessionId), new XAttribute("root", session.WorkspaceRoot), new XAttribute("repository", state.Repository),
                new XAttribute("configuration", state.Configuration), new XAttribute("localHash", state.LocalHash), new XAttribute("baseHash", state.BaseHash), new XAttribute("incomingHash", state.IncomingHash),
                new XAttribute("pending", state.PendingFingerprint), new XAttribute("ready", session.Ready), new XAttribute("applying", session.Applying), new XAttribute("resolution", session.Resolution), new XAttribute("rename", session.RenamePath),
                new XElement("Conflict", new XAttribute("path", c.RepositoryPath), new XAttribute("original", c.OriginalPath), new XAttribute("incoming", c.IncomingPath), new XAttribute("kind", c.Kind), new XAttribute("base", c.BaseChangeset),
                    new XAttribute("head", c.IncomingChangeset), new XAttribute("item", c.ItemId), new XAttribute("incomingItem", c.IncomingItemId), new XAttribute("incomingRevision", c.IncomingRevisionChangeset), c.ResolutionOptions.Select(value => new XElement("Option", value))));
            string target = Path.Combine(session.RecoveryDirectory, "structure.xml"), temporary = target + ".new"; RejectReparsePath(target); RejectReparsePath(temporary);
            new XDocument(xml).Save(temporary); if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        }
        private StructureState LoadStructureState(string root, bool required)
        {
            string index = StructureIndex(root); RejectReparsePath(index);
            if (!File.Exists(index)) { if (required) throw new ArgumentException("Prepare a structural decision first."); return null; }
            string target = File.ReadAllText(index); RejectReparsePath(target); var xml = SafeXml.Load(File.ReadAllText(target)).Root;
            if (xml == null || xml.Name != "PartialStructure") throw new InvalidDataException("Invalid structural session.");
            string id = (string)xml.Attribute("id"), directory = MergeSessionDirectory(id);
            if (!SamePath(target, Path.Combine(directory, "structure.xml")) || !SamePath((string)xml.Attribute("root"), root)) throw new InvalidDataException("Use the client settings that created this structural session.");
            var c = xml.Element("Conflict");
            var state = new StructureState { Repository = (string)xml.Attribute("repository"), Configuration = (string)xml.Attribute("configuration"), LocalHash = (string)xml.Attribute("localHash"), BaseHash = (string)xml.Attribute("baseHash"), IncomingHash = (string)xml.Attribute("incomingHash"), PendingFingerprint = (string)xml.Attribute("pending"),
                Session = new PlasticPartialStructureSession { SessionId = id, WorkspaceRoot = root, RecoveryDirectory = directory, Ready = (bool)xml.Attribute("ready"), Applying = (bool)xml.Attribute("applying"), Resolution = (string)xml.Attribute("resolution"), RenamePath = (string)xml.Attribute("rename"),
                    Conflict = new PlasticPartialStructureConflict { RepositoryPath = (string)c.Attribute("path"), OriginalPath = (string)c.Attribute("original"), IncomingPath = (string)c.Attribute("incoming"), Kind = (string)c.Attribute("kind"), BaseChangeset = (long)c.Attribute("base"), IncomingChangeset = (long)c.Attribute("head"), ItemId = (long)c.Attribute("item"), IncomingItemId = (long)c.Attribute("incomingItem"), IncomingRevisionChangeset = (long)c.Attribute("incomingRevision"), Reason = "", ResolutionOptions = c.Elements("Option").Select(value => value.Value).ToList() } } };
            foreach (string path in StructurePaths(state)) ValidateRepositoryFilePath(path);
            if (StructureFileHash(StructureBackup(state, "local")) != state.LocalHash || StructureFileHash(StructureBackup(state, "base")) != state.BaseHash || StructureFileHash(StructureBackup(state, "incoming")) != state.IncomingHash) throw new InvalidDataException("A structural contributor backup was changed or removed.");
            return state;
        }
    }
}
