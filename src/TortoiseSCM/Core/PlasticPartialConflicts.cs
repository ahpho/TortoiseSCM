// GPL-2.0-or-later. Reviewed per-file incoming conflict resolution for Partial workspaces.
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
    public sealed class PlasticPartialConflict
    {
        public string RepositoryPath { get; set; }
        public long BaseChangeset { get; set; }
        public long IncomingChangeset { get; set; }
        public long ItemId { get; set; }
        public bool CanResolve { get; set; }
        public string Reason { get; set; }
        public bool Resolved { get; set; }
    }
    public sealed class PlasticPartialConflictSession
    {
        public string SessionId { get; set; }
        public string WorkspaceRoot { get; set; }
        public string RecoveryDirectory { get; set; }
        public IList<PlasticPartialConflict> Conflicts { get; set; }
        public bool Applying { get; set; }
        public bool Ready { get; set; }
        public PlasticPartialConflictSession() { Conflicts = new List<PlasticPartialConflict>(); }
    }

    public sealed partial class PlasticClient
    {
        private sealed class PartialState
        {
            internal PlasticPartialConflictSession Session;
            internal string Repository, Configuration, ApplyingPath;
            internal readonly Dictionary<string, string> LocalHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> ResultHashes = new Dictionary<string, string>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> InputDirectories = new Dictionary<string, string>(StringComparer.Ordinal);
        }
        public async Task<IList<PlasticPartialConflict>> PreviewPartialConflictsAsync(string root, CancellationToken cancellationToken)
        {
            var workspace = await PartialWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            var conflicts = new List<PlasticPartialConflict>();
            var changes = await GetStatusAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false);
            var structures = changes.Count == 0 ? new List<PlasticPartialStructureConflict>() : await PreviewPartialStructureAsync(root, cancellationToken).ConfigureAwait(false);
            foreach (var structural in structures) conflicts.Add(new PlasticPartialConflict { RepositoryPath = structural.RepositoryPath, BaseChangeset = structural.BaseChangeset,
                IncomingChangeset = structural.IncomingChangeset, ItemId = structural.ItemId, CanResolve = false, Reason = "Use structural conflict decisions: " + structural.Kind + ". " + structural.Reason });
            var addedPaths = changes.Where(item => item.StatusCode == "AD").ToList();
            HashSet<string> headPaths = addedPaths.Count == 0 ? null : await PartialHeadPathsAsync(workspace, cancellationToken).ConfigureAwait(false);
            foreach (var pending in changes)
            {
                string path = "/" + pending.Path.Substring(workspace.RootPath.TrimEnd('\\').Length).TrimStart('\\').Replace('\\', '/');
                if (structures.Any(item => String.Equals(item.RepositoryPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                // Plastic emits separate MV and CH rows for an edited moved file.
                // Its uncommitted destination has no historical revision yet;
                // the structural preview already checks the original identity.
                if (changes.Any(item => item.StatusCode == "MV" && SamePath(item.Path, pending.Path))) continue;
                if (pending.StatusCode == "AD" && headPaths.Contains(path))
                {
                    conflicts.Add(new PlasticPartialConflict { RepositoryPath = path, BaseChangeset = -1, IncomingChangeset = -1,
                        CanResolve = false, Reason = "A server item now occupies this locally added path. Preserve the local file and choose a different name or undo the addition before updating." });
                    continue;
                }
                if (pending.IsDirectory || (pending.StatusCode != "CH" && pending.StatusCode != "CO"))
                {
                    continue;
                }
                var conflict = await ReadPartialConflictAsync(workspace, path, cancellationToken).ConfigureAwait(false);
                if (conflict.BaseChangeset != conflict.IncomingChangeset || !conflict.CanResolve) conflicts.Add(conflict);
            }
            return conflicts;
        }
        private async Task<HashSet<string>> PartialHeadPathsAsync(PlasticWorkspace workspace, CancellationToken token)
        {
            var header = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "status", workspace.RootPath, "--header", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(header);
            var document = SafeXml.Load(header.Output); string branch = document.Root == null ? null : (string)document.Root.Element("WkConfigName");
            if (String.IsNullOrEmpty(branch) || !branch.StartsWith("/", StringComparison.Ordinal) || !branch.EndsWith("@" + workspace.Repository, StringComparison.Ordinal))
                throw new ArgumentException("The Partial branch cannot be identified for an added-path collision check. No checkin was performed.");
            var listing = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", "/", "--tree=br:" + branch, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(listing);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in SafeXml.Load(listing.Output).Descendants("LsItem"))
            {
                string path = (string)entry.Element("CurrentPath");
                if (String.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid incoming tree path.");
                paths.Add(path);
            }
            return paths;
        }
        public async Task<PlasticPartialConflictSession> GetPartialConflictSessionAsync(string root, CancellationToken cancellationToken)
        {
            var workspace = await PartialWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadPartialState(workspace.RootPath, false);
                return state == null ? null : state.Session;
            }
        }
        public async Task<PlasticMergeConflictFiles> PreparePartialConflictAsync(string root, string repositoryPath, CancellationToken cancellationToken)
        {
            ThrowIfPartialStructureActive(root);
            var workspace = await PartialWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            ValidateMergeStorageLocation(workspace.RootPath);
            string local = MergeLocalPath(workspace.RootPath, repositoryPath);
            using (var structureGate = StructureGate(root))
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                ThrowIfPartialStructureActive(root);
                var state = LoadPartialState(workspace.RootPath, false);
                if (state != null) ValidatePartialConfiguration(state, workspace);
                if (state != null && (!state.Session.Ready || state.Session.Applying)) throw new ArgumentException("A Partial resolution was interrupted. Preserve its backups and explicitly undo the affected file before retrying.");
                var conflict = await ReadPartialConflictAsync(workspace, repositoryPath, cancellationToken).ConfigureAwait(false);
                if (!conflict.CanResolve) throw new ArgumentException(conflict.Reason);
                if (conflict.BaseChangeset == conflict.IncomingChangeset) throw new ArgumentException("The file has no incoming revision conflict.");
                await ValidatePartialLoadedDirectoriesAtAsync(workspace, conflict.IncomingChangeset, cancellationToken).ConfigureAwait(false);
                await RequirePartialContentChangeAsync(workspace, local, cancellationToken).ConfigureAwait(false);
                if (state == null) state = new PartialState { Repository = workspace.Repository, Configuration = PartialConfiguration(workspace.RootPath),
                    Session = new PlasticPartialConflictSession { SessionId = Guid.NewGuid().ToString("N"), WorkspaceRoot = workspace.RootPath, Ready = true } };
                var existing = state.Session.Conflicts.SingleOrDefault(item => item.RepositoryPath == repositoryPath);
                if (existing != null)
                {
                    ValidatePartialInputs(state, existing);
                    if (!existing.Resolved && existing.BaseChangeset == conflict.BaseChangeset && existing.IncomingChangeset == conflict.IncomingChangeset && existing.ItemId == conflict.ItemId && state.LocalHashes[repositoryPath] == MergeHash(local))
                        return PartialFiles(state, repositoryPath);
                }
                string hash = MergeHash(local);
                // Explicit re-preparation snapshots current contributors into a new directory.
                // Earlier backups remain untouched even if this preparation later fails.
                state.InputDirectories[repositoryPath] = Guid.NewGuid().ToString("N");
                var files = PartialFiles(state, repositoryPath);
                CreatePrivateMergeDirectory(Path.GetDirectoryName(files.BasePath));
                await DownloadHistoricalFileAsync(workspace, repositoryPath, conflict.BaseChangeset, files.BasePath, cancellationToken).ConfigureAwait(false);
                await DownloadHistoricalFileAsync(workspace, repositoryPath, conflict.IncomingChangeset, files.RemotePath, cancellationToken).ConfigureAwait(false);
                File.Copy(local, files.LocalPath, false);
                if (hash != MergeHash(local) || hash != MergeHash(files.LocalPath)) throw new ArgumentException("The local file changed while preparing the conflict. No workspace bytes were changed.");
                foreach (string input in new[] { files.BasePath, files.LocalPath, files.RemotePath }) File.SetAttributes(input, File.GetAttributes(input) | FileAttributes.ReadOnly);
                File.Copy(files.LocalPath, files.ResultPath, false); File.SetAttributes(files.ResultPath, FileAttributes.Normal);
                if (existing != null) state.Session.Conflicts.Remove(existing);
                state.Session.Conflicts.Add(conflict); state.LocalHashes[repositoryPath] = hash; state.ResultHashes.Remove(repositoryPath);
                SavePartialState(state); WritePartialIndex(workspace.RootPath, state.Session.SessionId);
                return files;
            }
        }
        public async Task<PlasticCommandResult> ResolvePartialConflictAsync(string root, string repositoryPath, string resultPath, CancellationToken cancellationToken)
        {
            ThrowIfPartialStructureActive(root);
            var workspace = await PartialWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            string local = MergeLocalPath(workspace.RootPath, repositoryPath);
            string result = Path.GetFullPath(resultPath); RejectReparsePath(result);
            if (!File.Exists(result) || SamePath(result, local) || SameExistingFile(result, local)) throw new ArgumentException("Save the reviewed result to a separate file.");
            using (var structureGate = StructureGate(root))
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                ThrowIfPartialStructureActive(root);
                var state = LoadPartialState(workspace.RootPath, true); ValidatePartialConfiguration(state, workspace);
                if (!state.Session.Ready || state.Session.Applying) throw new ArgumentException("An interrupted resolution requires explicit file undo before continuing. Backups remain in the session.");
                var conflict = state.Session.Conflicts.SingleOrDefault(item => item.RepositoryPath == repositoryPath);
                if (conflict == null || conflict.Resolved) throw new ArgumentException("Prepare this unresolved Partial conflict first.");
                ValidatePartialInputs(state, conflict);
                var files = PartialFiles(state, repositoryPath);
                foreach (string input in new[] { files.BasePath, files.LocalPath, files.RemotePath })
                    if (SamePath(result, input) || SameExistingFile(result, input)) throw new ArgumentException("An immutable contributor cannot be used as the result. Save a separate reviewed file.");
                string approved = Path.Combine(Path.GetDirectoryName(files.BasePath), "approved.bin");
                RejectReparsePath(approved); File.Copy(result, approved, true); string approvedHash = MergeHash(approved);
                await ValidatePartialBeforeApplyAsync(state, conflict, workspace, cancellationToken).ConfigureAwait(false);
                await ValidatePartialLoadedDirectoriesAtAsync(workspace, conflict.IncomingChangeset, cancellationToken).ConfigureAwait(false);
                state.Session.Applying = true; state.Session.Ready = false; state.ApplyingPath = repositoryPath; SavePartialState(state);
                // Persist originals and an approved snapshot before the first native mutation.
                // No broad update, workspace conversion, client config edit or implicit checkin.
                try
                {
                    if (MergeHash(local) != state.LocalHashes[repositoryPath] || MergeHash(approved) != approvedHash)
                        throw new IOException("The local or reviewed file changed during preflight. Its current bytes were preserved; no undo was performed.");
                    RequireSuccess(await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "partial", "undo", local }), cancellationToken).ConfigureAwait(false));
                    if (MergeHash(local) != MergeHash(files.BasePath)) throw new InvalidOperationException("Partial undo did not restore the pinned base bytes.");
                    RequireSuccess(await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "partial", "update", local,
                        "--changeset=" + conflict.IncomingChangeset.ToString(CultureInfo.InvariantCulture), "--dontmerge", "--report" }), cancellationToken).ConfigureAwait(false));
                    ValidatePartialConfiguration(state, await PartialWorkspaceAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false));
                    var updated = await ReadPartialConflictAsync(workspace, repositoryPath, cancellationToken).ConfigureAwait(false);
                    if (!updated.CanResolve || updated.BaseChangeset != conflict.IncomingChangeset || updated.IncomingChangeset != conflict.IncomingChangeset || updated.ItemId != conflict.ItemId || MergeHash(local) != MergeHash(files.RemotePath))
                        throw new InvalidOperationException("The incoming revision changed during resolution. The original and reviewed bytes remain in the session; explicitly undo this file before restarting.");
                    cancellationToken.ThrowIfCancellationRequested();
                    if (approvedHash != MergeHash(approved)) throw new InvalidDataException("The approved result snapshot was modified.");
                    // Exclusive file handle prevents an editor from racing the replacement.
                    using (var output = new FileStream(local, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        ToolFileInformation information;
                        if (!GetFileInformationByHandle(output.SafeFileHandle, out information) || information.Links != 1) throw new IOException("The workspace file has multiple hard links; the result was not written.");
                        using (var hash = System.Security.Cryptography.SHA256.Create())
                            if (BitConverter.ToString(hash.ComputeHash(output)).Replace("-", "") != MergeHash(files.RemotePath)) throw new IOException("The local file changed before applying the reviewed result.");
                        output.Position = 0;
                        byte[] bytes = File.ReadAllBytes(approved); output.Write(bytes, 0, bytes.Length); output.SetLength(bytes.Length); output.Flush(true);
                    }
                    if (MergeHash(local) != approvedHash) throw new IOException("The applied result changed before verification.");
                    var after = await ReadPartialConflictAsync(workspace, repositoryPath, cancellationToken).ConfigureAwait(false);
                    if (after.IncomingChangeset != conflict.IncomingChangeset || after.BaseChangeset != conflict.IncomingChangeset || after.ItemId != conflict.ItemId)
                        throw new InvalidOperationException("A newer incoming revision arrived during resolution. Recheck the saved result before continuing.");
                    ValidatePartialConfiguration(state, workspace);
                    conflict.Resolved = true; state.ResultHashes[repositoryPath] = approvedHash;
                    state.Session.Applying = false; state.Session.Ready = true; state.ApplyingPath = ""; SavePartialState(state);
                    return new PlasticCommandResult { ExitCode = 0, Output = "Applied the reviewed result over the pinned incoming revision. Changes remain pending for explicit checkin." };
                }
                catch (Exception error)
                {
                    // Fail closed, do not rewrite native metadata or guess how far cm got.
                    throw new InvalidOperationException("Partial resolution did not finish. Checkin is blocked for this session. Original and reviewed backups: " + Path.GetDirectoryName(files.BasePath) + ". " + error.Message, error);
                }
            }
        }
        public async Task CancelPartialConflictPreparationAsync(string root, CancellationToken cancellationToken)
        {
            var workspace = await PartialWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadPartialState(workspace.RootPath, true);
                if (state.Session.Applying || !state.Session.Ready || state.Session.Conflicts.Any(item => item.Resolved))
                    throw new ArgumentException("Only an unapplied preparation can be cancelled. Explicitly undo affected files to discard applied or interrupted resolutions.");
                File.Delete(PartialIndex(workspace.RootPath));
            }
        }
        private async Task<PlasticCommandResult> ExecuteWithPartialConflictGuardAsync(PlasticProcessCommand command, PlasticCommandRequest request, CancellationToken cancellationToken)
        {
            if (request.Command == PlasticCommand.Add || request.Command == PlasticCommand.Checkout || request.Command == PlasticCommand.Checkin || request.Command == PlasticCommand.Update || request.Command == PlasticCommand.Undo)
            {
                using (var gate = StructureGate(command.WorkingDirectory))
                {
                    ThrowIfPartialStructureActive(command.WorkingDirectory);
                    return await ExecutePartialConflictGuardCoreAsync(command, request, cancellationToken).ConfigureAwait(false);
                }
            }
            return await ExecutePartialConflictGuardCoreAsync(command, request, cancellationToken).ConfigureAwait(false);
        }
        private async Task<PlasticCommandResult> ExecutePartialConflictGuardCoreAsync(PlasticProcessCommand command, PlasticCommandRequest request, CancellationToken cancellationToken)
        {
            if (request.Command == PlasticCommand.Add || request.Command == PlasticCommand.Checkout || request.Command == PlasticCommand.Checkin || request.Command == PlasticCommand.Update || request.Command == PlasticCommand.Undo)
                ThrowIfPartialStructureActive(command.WorkingDirectory);
            if (request.Command != PlasticCommand.Checkin && request.Command != PlasticCommand.Undo)
                return await ExecuteWithMergeGuardAsync(command, request, cancellationToken).ConfigureAwait(false);
            var workspace = await GetWorkspaceAsync(command.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            if (!workspace.IsPartial) return await ExecuteWithMergeGuardAsync(command, request, cancellationToken).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadPartialState(workspace.RootPath, false);
                if (request.Command == PlasticCommand.Checkin)
                {
                    if (state != null)
                    {
                        ValidatePartialConfiguration(state, workspace);
                        if (!state.Session.Ready || state.Session.Applying || state.Session.Conflicts.Any(item => !item.Resolved))
                            throw new ArgumentException("Resolve or cancel every prepared Partial conflict before checkin. Interrupted resolutions require explicit file undo; no checkin was performed.");
                        foreach (var conflict in state.Session.Conflicts)
                        {
                            string local = MergeLocalPath(workspace.RootPath, conflict.RepositoryPath);
                            var current = await ReadPartialConflictAsync(workspace, conflict.RepositoryPath, cancellationToken).ConfigureAwait(false);
                            if (!current.CanResolve || current.BaseChangeset != conflict.IncomingChangeset || current.IncomingChangeset != conflict.IncomingChangeset || current.ItemId != conflict.ItemId)
                                throw new ArgumentException("A reviewed Partial resolution changed or a newer revision arrived. Review it again before checkin.");
                        }
                    }
                    var incoming = await PreviewPartialConflictsAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false);
                    if (incoming.Any(item => PartialSelected(request, workspace.RootPath, item.RepositoryPath)))
                        throw new ArgumentException("Selected files have incoming conflicts. Prepare and resolve them before checkin; no official merge tool was launched.");
                }
                // No native standard merge session can coexist with a Partial resolution.
                var result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded || state == null) return result;
                var pending = await GetStatusAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false);
                foreach (var conflict in state.Session.Conflicts.ToList())
                {
                    string local = MergeLocalPath(workspace.RootPath, conflict.RepositoryPath);
                    if (PartialSelected(request, workspace.RootPath, conflict.RepositoryPath) && !pending.Any(item => SamePath(item.Path, local)))
                    {
                        state.Session.Conflicts.Remove(conflict);
                        if (state.ApplyingPath == conflict.RepositoryPath) { state.Session.Applying = false; state.Session.Ready = true; state.ApplyingPath = ""; }
                    }
                }
                if (state.Session.Conflicts.Count == 0) File.Delete(PartialIndex(workspace.RootPath)); else SavePartialState(state);
                return result;
            }
        }
        private static bool PartialSelected(PlasticCommandRequest request, string root, string repositoryPath)
        {
            string local = MergeLocalPath(root, repositoryPath);
            return request.Paths.Any(value =>
            {
                string path = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(request.WorkingDirectory, value));
                return SamePath(path, local) || ((request.Command == PlasticCommand.Checkin || request.Recursive) && IsWithin(local, path));
            });
        }
        private async Task<PlasticWorkspace> PartialWorkspaceAsync(string root, CancellationToken token)
        {
            var workspace = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
            if (!SamePath(Path.GetFullPath(root), workspace.RootPath)) throw new ArgumentException("Select the exact Partial workspace root for incoming conflict operations.");
            if (!workspace.IsPartial) throw new ArgumentException("This operation requires a Partial workspace.");
            if (HasSavedMergeSession(workspace.RootPath) || File.Exists(Path.Combine(workspace.RootPath, ".plastic", "plastic.mergeprogress"))) throw new ArgumentException("Complete the existing native merge before resolving Partial incoming changes.");
            return workspace;
        }
        private async Task<PlasticPartialConflict> ReadPartialConflictAsync(PlasticWorkspace workspace, string path, CancellationToken token)
        {
            string local = MergeLocalPath(workspace.RootPath, path);
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "fileinfo", local,
                "--fields=ServerPath,RevisionChangeset,RevisionHeadChangeset,RepSpec,IsUnderXlink,Type,Status", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var items = SafeXml.Load(result.Output).Descendants("FileInfo").ToList();
            if (items.Count != 1) throw new InvalidDataException("Expected one Partial file identity.");
            var info = items[0];
            long loaded, incoming;
            var conflict = new PlasticPartialConflict { RepositoryPath = path, BaseChangeset = -1, IncomingChangeset = -1, Reason = "Incoming deletion, move, Xlink or replaced path needs a separate structural decision.", CanResolve = false };
            if (!Int64.TryParse((string)info.Element("RevisionChangeset"), out loaded) || !Int64.TryParse((string)info.Element("RevisionHeadChangeset"), out incoming) || loaded < 0 || incoming < 0) return conflict;
            conflict.BaseChangeset = loaded; conflict.IncomingChangeset = incoming;
            if ((string)info.Element("ServerPath") != path || (string)info.Element("RepSpec") != workspace.Repository || (string)info.Element("IsUnderXlink") != "false" ||
                ((string)info.Element("Type") != "txt" && (string)info.Element("Type") != "bin") || !File.Exists(local)) return conflict;
            long baseItem = await PartialItemIdAsync(workspace, path, loaded, token).ConfigureAwait(false);
            long incomingItem = await PartialItemIdAsync(workspace, path, incoming, token).ConfigureAwait(false);
            conflict.ItemId = baseItem;
            if (baseItem != incomingItem || baseItem < 0) return conflict;
            conflict.CanResolve = true; conflict.Reason = ""; return conflict;
        }
        private async Task<long> PartialItemIdAsync(PlasticWorkspace workspace, string path, long changeset, CancellationToken token)
        {
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", path, "--tree=cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository, "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var entries = SafeXml.Load(result.Output).Descendants("LsItem").Where(entry => (string)entry.Element("CurrentPath") == path).ToList();
            if (entries.Count != 1 || !String.IsNullOrEmpty((string)entries[0].Element("SymlinkTarget"))) return -1;
            return MergeNumber((string)entries[0].Element("ItemId"));
        }
        private async Task RequirePartialContentChangeAsync(PlasticWorkspace workspace, string path, CancellationToken token)
        {
            var pending = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
            if (!pending.Any(item => SamePath(item.Path, path) && !item.IsDirectory && (item.StatusCode == "CH" || item.StatusCode == "CO")) ||
                pending.Any(item => (SamePath(item.Path, path) || IsWithin(path, item.Path)) && (item.StatusCode != "CH" && item.StatusCode != "CO")))
                throw new ArgumentException("Only a loaded file with a local content change can be resolved. Finish structural changes first.");
        }
        private async Task ValidatePartialBeforeApplyAsync(PartialState state, PlasticPartialConflict conflict, PlasticWorkspace workspace, CancellationToken token)
        {
            ValidatePartialConfiguration(state, workspace);
            string local = MergeLocalPath(workspace.RootPath, conflict.RepositoryPath);
            using (var input = File.OpenRead(local))
            {
                ToolFileInformation information;
                if (!GetFileInformationByHandle(input.SafeFileHandle, out information) || information.Links != 1) throw new ArgumentException("Partial resolution requires a file without hard-link aliases; no native undo was started.");
            }
            await RequirePartialContentChangeAsync(workspace, local, token).ConfigureAwait(false);
            var current = await ReadPartialConflictAsync(workspace, conflict.RepositoryPath, token).ConfigureAwait(false);
            if (!current.CanResolve || current.BaseChangeset != conflict.BaseChangeset || current.IncomingChangeset != conflict.IncomingChangeset || current.ItemId != conflict.ItemId || MergeHash(local) != state.LocalHashes[conflict.RepositoryPath])
                throw new ArgumentException("The local or incoming contributor changed after preparation. No resolution was applied.");
        }
        private static string PartialConfiguration(string root)
        {
            return String.Join("|", new[] { "plastic.selector", "plastic.workspace", "plastic.fullycheckeddirectories" }.Select(name =>
            { string path = Path.Combine(root, ".plastic", name); return name + ":" + (File.Exists(path) ? MergeHash(path) : "missing"); }));
        }
        private static void ValidatePartialConfiguration(PartialState state, PlasticWorkspace workspace)
        { if (state.Repository != workspace.Repository || state.Configuration != PartialConfiguration(workspace.RootPath)) throw new ArgumentException("The Partial workspace identity, selector or load configuration changed. Keep session backups and undo the affected file before restarting."); }
        private string PartialIndex(string root) { return Path.Combine(root, ".plastic", "tortoisescm-partial.session"); }
        public bool HasSavedPartialConflictSession(string root) { return File.Exists(PartialIndex(Path.GetFullPath(root))); }
        private void WritePartialIndex(string root, string id)
        { string index = PartialIndex(root); RejectReparsePath(index); File.WriteAllText(index, Path.Combine(MergeSessionDirectory(id), "partial.xml")); }
        private PlasticMergeConflictFiles PartialFiles(PartialState state, string path)
        {
            string directory = Path.Combine(MergeSessionDirectory(state.Session.SessionId), state.InputDirectories[path]);
            return new PlasticMergeConflictFiles { SessionId = state.Session.SessionId, RepositoryPath = path,
                BasePath = Path.Combine(directory, "base.bin"), LocalPath = Path.Combine(directory, "local.bin"), RemotePath = Path.Combine(directory, "incoming.bin"), ResultPath = Path.Combine(directory, "result" + Path.GetExtension(path)) };
        }
        private void ValidatePartialInputs(PartialState state, PlasticPartialConflict conflict)
        {
            var files = PartialFiles(state, conflict.RepositoryPath);
            if (MergeHash(files.LocalPath) != state.LocalHashes[conflict.RepositoryPath]) throw new InvalidDataException("The original local backup was changed.");
            // The manifest records all immutable contributor hashes, verified by LoadPartialState.
        }
        private void SavePartialState(PartialState state)
        {
            string directory = MergeSessionDirectory(state.Session.SessionId); CreatePrivateMergeDirectory(directory); state.Session.RecoveryDirectory = directory;
            var document = new XElement("PartialConflictSession", new XAttribute("id", state.Session.SessionId), new XAttribute("root", state.Session.WorkspaceRoot),
                new XAttribute("repository", state.Repository), new XAttribute("configuration", state.Configuration), new XAttribute("ready", state.Session.Ready), new XAttribute("applying", state.Session.Applying), new XAttribute("applyingPath", state.ApplyingPath ?? ""));
            foreach (var conflict in state.Session.Conflicts)
            {
                var files = PartialFiles(state, conflict.RepositoryPath); string resultHash;
                state.ResultHashes.TryGetValue(conflict.RepositoryPath, out resultHash);
                document.Add(new XElement("Conflict", new XAttribute("path", conflict.RepositoryPath), new XAttribute("directory", state.InputDirectories[conflict.RepositoryPath]), new XAttribute("base", conflict.BaseChangeset), new XAttribute("incoming", conflict.IncomingChangeset),
                    new XAttribute("item", conflict.ItemId), new XAttribute("resolved", conflict.Resolved), new XAttribute("localHash", state.LocalHashes[conflict.RepositoryPath]),
                    new XAttribute("baseHash", MergeHash(files.BasePath)), new XAttribute("incomingHash", MergeHash(files.RemotePath)), new XAttribute("resultHash", resultHash ?? "")));
            }
            string target = Path.Combine(directory, "partial.xml"), temporary = target + ".new"; RejectReparsePath(target); RejectReparsePath(temporary);
            new XDocument(document).Save(temporary); if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        }
        private PartialState LoadPartialState(string root, bool required)
        {
            string index = PartialIndex(root); RejectReparsePath(index);
            if (!File.Exists(index)) { if (required) throw new ArgumentException("Prepare a Partial conflict before applying a result."); return null; }
            string target = File.ReadAllText(index); RejectReparsePath(target);
            var element = SafeXml.Load(File.ReadAllText(target)).Root;
            if (element == null || element.Name != "PartialConflictSession") throw new InvalidDataException("Invalid Partial conflict session.");
            string id = (string)element.Attribute("id");
            if (!SamePath(target, Path.Combine(MergeSessionDirectory(id), "partial.xml"))) throw new InvalidDataException("The Partial session belongs to different client settings. Use the settings that prepared it or explicitly undo the affected files.");
            if (!SamePath((string)element.Attribute("root"), root)) throw new InvalidDataException("Partial session workspace mismatch.");
            var state = new PartialState { Repository = (string)element.Attribute("repository"), Configuration = (string)element.Attribute("configuration"), ApplyingPath = (string)element.Attribute("applyingPath"),
                Session = new PlasticPartialConflictSession { SessionId = id, WorkspaceRoot = root, RecoveryDirectory = MergeSessionDirectory(id), Ready = (bool)element.Attribute("ready"), Applying = (bool)element.Attribute("applying") } };
            foreach (var entry in element.Elements("Conflict"))
            {
                string path = (string)entry.Attribute("path"); ValidateRepositoryFilePath(path);
                string directory = (string)entry.Attribute("directory"); Guid directoryId;
                if (!Guid.TryParseExact(directory, "N", out directoryId)) throw new InvalidDataException("Invalid Partial contributor directory.");
                state.InputDirectories.Add(path, directory);
                var conflict = new PlasticPartialConflict { RepositoryPath = path, BaseChangeset = MergeNumber((string)entry.Attribute("base")), IncomingChangeset = MergeNumber((string)entry.Attribute("incoming")), ItemId = MergeNumber((string)entry.Attribute("item")), Resolved = (bool)entry.Attribute("resolved"), CanResolve = true, Reason = "" };
                state.Session.Conflicts.Add(conflict); state.LocalHashes.Add(path, (string)entry.Attribute("localHash")); state.ResultHashes.Add(path, (string)entry.Attribute("resultHash"));
                var files = PartialFiles(state, path);
                if (MergeHash(files.BasePath) != (string)entry.Attribute("baseHash") || MergeHash(files.RemotePath) != (string)entry.Attribute("incomingHash") || MergeHash(files.LocalPath) != state.LocalHashes[path]) throw new InvalidDataException("An immutable Partial contributor backup was changed.");
            }
            return state;
        }
    }
}
