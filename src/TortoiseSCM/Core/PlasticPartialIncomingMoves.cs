// GPL-2.0-or-later. Rebase one edited file onto an incoming move without updating a directory.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        // Native configure may reorder these lines. Preserve every line and its
        // multiplicity; only ordering differs from the older session fingerprint.
        private static string IncomingMoveConfiguration(string root)
        {
            var parts = new List<string> { "incoming-move-v1" };
            foreach (string name in new[] { "plastic.selector", "plastic.workspace", "plastic.fullycheckeddirectories" })
            {
                string path = Path.Combine(root, ".plastic", name); RejectReparsePath(path);
                if (!File.Exists(path)) { parts.Add(name + ":missing"); continue; }
                if (name != "plastic.fullycheckeddirectories") parts.Add(name + ":" + MergeHash(path));
                else
                {
                    string normalized = String.Join("\n", File.ReadAllLines(path).OrderBy(line => line, StringComparer.Ordinal));
                    using (var hash = SHA256.Create()) parts.Add(name + ":" + Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(normalized))));
                }
            }
            return String.Join("|", parts);
        }

        private async Task ValidateIncomingMoveScopeAsync(PlasticWorkspace workspace, PlasticPartialStructureConflict conflict, CancellationToken token, StructureTree knownTree = null)
        {
            ValidateIncomingMovePaths(conflict);
            string root = workspace.RootPath, original = MergeLocalPath(root, conflict.OriginalPath), destination = MergeLocalPath(root, conflict.IncomingPath);
            RejectReparsePath(original); RejectReparsePath(destination);
            if (!File.Exists(original) || Directory.Exists(original)) throw new ArgumentException("An incoming move currently supports an existing locally edited file only.");
            StructureRequireSingleLink(original);
            if (File.Exists(destination) || Directory.Exists(destination)) throw new ArgumentException("The incoming move destination is already occupied locally.");
            var changes = await GetStatusAsync(root, token).ConfigureAwait(false);
            var selected = changes.Where(item => SamePath(item.Path, original)).ToList();
            if (selected.Count == 0 || selected.Any(item => item.IsDirectory || !new[] { "CH", "CO" }.Contains(item.StatusCode) || !String.IsNullOrEmpty(item.OldPath)))
                throw new ArgumentException("Incoming moves can currently resolve local content edits or checkouts only.");
            if (changes.Any(item => SamePath(item.Path, destination) || (!String.IsNullOrEmpty(item.OldPath) && (SamePath(item.OldPath, original) || SamePath(item.OldPath, destination)))))
                throw new ArgumentException("The incoming move destination is involved in another pending operation.");
            var tree = knownTree ?? await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            if (tree.Changeset != conflict.IncomingChangeset) throw new ArgumentException("The incoming branch changed. Refresh the structural preview.");
            ValidateIncomingMoveTree(workspace, conflict, tree);
            await ValidatePartialLoadedDirectoriesAsync(workspace, tree, token).ConfigureAwait(false);
            await ValidateStructureParentsAsync(workspace, new[] { conflict.OriginalPath, conflict.IncomingPath }, tree, token).ConfigureAwait(false);
            await RequireIncomingMoveIdentityAsync(workspace, original, conflict.ItemId, token).ConfigureAwait(false);
        }

        private static void ValidateIncomingMovePaths(PlasticPartialStructureConflict conflict)
        {
            ValidateRepositoryFilePath(conflict.OriginalPath); ValidateRepositoryFilePath(conflict.IncomingPath);
            if (conflict.Kind != "incoming-move" || conflict.ItemId < 0 || conflict.ItemId != conflict.IncomingItemId ||
                !String.Equals(conflict.RepositoryPath, conflict.OriginalPath, StringComparison.Ordinal) ||
                String.Equals(conflict.OriginalPath, conflict.IncomingPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Combined local moves, identity replacements and case-only incoming renames require a separate decision.");
        }

        private static void ValidateIncomingMoveTree(PlasticWorkspace workspace, PlasticPartialStructureConflict conflict, StructureTree tree)
        {
            var incoming = tree.Items.SingleOrDefault(item => String.Equals((string)item.Element("CurrentPath"), conflict.IncomingPath, StringComparison.OrdinalIgnoreCase));
            if (incoming == null || !StructureRegularFile(incoming, workspace.Repository) || (long?)incoming.Element("ItemId") != conflict.IncomingItemId ||
                !String.Equals((string)incoming.Element("CurrentPath"), conflict.IncomingPath, StringComparison.Ordinal) ||
                tree.Items.Any(item => String.Equals((string)item.Element("CurrentPath"), conflict.OriginalPath, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The incoming move changed identity or location, or the original path was reused. No parent directory was updated.");
        }

        private async Task<XElement> RequireIncomingMoveIdentityAsync(PlasticWorkspace workspace, string path, long identity, CancellationToken token)
        {
            RejectReparsePath(path);
            if (Directory.Exists(path)) throw new ArgumentException("An incoming move file became a directory.");
            if (File.Exists(path)) StructureRequireSingleLink(path);
            var nested = DiscoverWorkspace(path);
            if (nested == null || !SamePath(nested.RootPath, workspace.RootPath)) throw new ArgumentException("An incoming move path belongs to a nested workspace.");
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "fileinfo", path, "--fields=RevisionChangeset,Status,Type,IsUnderXlink", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result);
            var info = SafeXml.Load(result.Output).Descendants("FileInfo").Single();
            if (!new[] { "controlled", "checked-out" }.Contains((string)info.Element("Status")) ||
                !new[] { "txt", "bin" }.Contains((string)info.Element("Type")) || (string)info.Element("IsUnderXlink") != "false" ||
                await StructureLocalItemIdAsync(workspace.RootPath, path, token).ConfigureAwait(false) != identity)
                throw new ArgumentException("The incoming move path is no longer the expected controlled file. Recovery did not overwrite another item.");
            return info;
        }

        private async Task StructureTakeIncomingMoveAsync(StructureState state, PlasticWorkspace workspace, bool recovery, CancellationToken token, Dictionary<string, string> recoveryHashes)
        {
            var conflict = state.Session.Conflict; ValidateIncomingMovePaths(conflict);
            string root = workspace.RootPath, original = MergeLocalPath(root, conflict.OriginalPath), destination = MergeLocalPath(root, conflict.IncomingPath);
            var scope = new[] { conflict.OriginalPath, conflict.IncomingPath };
            var tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            if (!recovery && tree.Changeset != conflict.IncomingChangeset) throw new ArgumentException("The incoming branch changed before applying the move.");
            ValidateIncomingMoveTree(workspace, conflict, tree);
            await ValidateStructureParentsAsync(workspace, scope, tree, token).ConfigureAwait(false);
            await ValidatePartialLoadedDirectoriesAsync(workspace, tree, token).ConfigureAwait(false);
            foreach (string path in scope) { string local = MergeLocalPath(root, path); RejectReparsePath(local); if (Directory.Exists(local)) throw new ArgumentException("A conflict file path became a directory."); }
            var pending = await GetStatusAsync(root, token).ConfigureAwait(false);
            var selected = pending.Where(item => scope.Any(path => SamePath(item.Path, MergeLocalPath(root, path)) || (!String.IsNullOrEmpty(item.OldPath) && SamePath(item.OldPath, MergeLocalPath(root, path))))).ToList();
            if (selected.Any(item => item.IsDirectory || !new[] { "CH", "CO", "LD", "PR", "IG" }.Contains(item.StatusCode) || !String.IsNullOrEmpty(item.OldPath)))
                throw new ArgumentException("Another structural operation affects the incoming move. Preserve it before recovery.");
            // Validate both paths before undoing either one. A successful exact
            // unload/load sequence never has both identities loaded at once.
            var controlled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in scope)
            {
                string local = MergeLocalPath(root, path);
                var info = await StructureFileInfoAsync(root, local, token).ConfigureAwait(false);
                if ((string)info.Element("Status") == "private")
                {
                    if (!File.Exists(local)) continue;
                    if (!recovery || recoveryHashes == null || !recoveryHashes.ContainsKey(path) || StructureFileHash(local) != recoveryHashes[path])
                        throw new IOException("A private incoming move path has not been backed up for this recovery.");
                    StructureRequireSingleLink(local);
                }
                else
                {
                    await RequireIncomingMoveIdentityAsync(workspace, local, conflict.ItemId, token).ConfigureAwait(false);
                    controlled.Add(path);
                }
            }
            if (controlled.Count > 1) throw new ArgumentException("Both move paths carry the same controlled identity. Preserve the workspace for manual review.");
            foreach (string path in scope)
            {
                string local = MergeLocalPath(root, path);
                if (controlled.Contains(path))
                {
                    if (selected.Any(item => SamePath(item.Path, local)))
                    {
                        await RequireIncomingMoveIdentityAsync(workspace, local, conflict.ItemId, token).ConfigureAwait(false);
                        string expected = recovery ? recoveryHashes == null || !recoveryHashes.ContainsKey(path) ? null : recoveryHashes[path] : path == conflict.OriginalPath ? state.LocalHash : null;
                        if (expected == null || StructureFileHash(local) != expected) throw new IOException("The local move contributor changed immediately before native undo.");
                        RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "undo", local }), token).ConfigureAwait(false));
                    }
                }
                else if (File.Exists(local))
                {
                    if (recoveryHashes == null || !recoveryHashes.ContainsKey(path) || StructureFileHash(local) != recoveryHashes[path]) throw new IOException("The recovery-time private bytes changed before removal.");
                    StructureRequireSingleLink(local); File.Delete(local);
                }
            }
            if (controlled.Contains(conflict.OriginalPath))
            {
                await RequireIncomingMoveIdentityAsync(workspace, original, conflict.ItemId, token).ConfigureAwait(false);
                if (StructureFileHash(original) != state.BaseHash) throw new IOException("The original file changed before exact unload.");
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "configure", "-" + conflict.OriginalPath }), token).ConfigureAwait(false));
                if (File.Exists(original) || Directory.Exists(original)) throw new IOException("Exact unload left the original move path occupied.");
            }
            if (!File.Exists(destination))
            {
                tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
                if (!recovery && tree.Changeset != conflict.IncomingChangeset) throw new IOException("The branch advanced after unload. The original bytes remain backed up; use explicit recovery.");
                ValidateIncomingMoveTree(workspace, conflict, tree);
                await ValidateStructureParentsAsync(workspace, scope, tree, token).ConfigureAwait(false);
                await ValidatePartialLoadedDirectoriesAsync(workspace, tree, token).ConfigureAwait(false);
                if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("The incoming destination became occupied before loading.");
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "configure", "+" + conflict.IncomingPath }), token).ConfigureAwait(false));
            }
            var loaded = await RequireIncomingMoveIdentityAsync(workspace, destination, conflict.IncomingItemId, token).ConfigureAwait(false);
            if (!File.Exists(destination)) throw new IOException("The incoming move did not load a regular file.");
            // Configure has no revision flag. Pin only after confirming that it
            // loaded the same file identity, never a replacement directory.
            long loadedRevision = (long?)loaded.Element("RevisionChangeset") ?? -1;
            if (loadedRevision == conflict.IncomingRevisionChangeset && StructureFileHash(destination) != state.IncomingHash)
                throw new IOException("The pinned incoming file was edited after loading. Its new bytes were not overwritten; use explicit recovery.");
            if (loadedRevision != conflict.IncomingRevisionChangeset)
            {
                if (loadedRevision < 0 || (string)loaded.Element("Status") != "controlled") throw new IOException("The incoming file has no clean loaded revision to pin.");
                string actualBackup = Path.Combine(state.Session.RecoveryDirectory, "loaded-" + Guid.NewGuid().ToString("N") + ".bin");
                await DownloadHistoricalFileAsync(workspace, conflict.IncomingPath, loadedRevision, actualBackup, token).ConfigureAwait(false);
                string actualHash = StructureFileHash(actualBackup); File.SetAttributes(actualBackup, FileAttributes.ReadOnly);
                if (StructureFileHash(destination) != actualHash) throw new IOException("The newly loaded revision was edited locally; no pinned update was performed.");
                tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
                ValidateIncomingMoveTree(workspace, conflict, tree);
                await ValidatePartialLoadedDirectoriesAsync(workspace, tree, token).ConfigureAwait(false);
                await ValidatePartialLoadedDirectoriesAtAsync(workspace, conflict.IncomingChangeset, token).ConfigureAwait(false);
                await ValidateStructureParentsAsync(workspace, scope, tree, token).ConfigureAwait(false);
                loaded = await RequireIncomingMoveIdentityAsync(workspace, destination, conflict.IncomingItemId, token).ConfigureAwait(false);
                if ((string)loaded.Element("Status") != "controlled" || (long?)loaded.Element("RevisionChangeset") != loadedRevision || StructureFileHash(destination) != actualHash)
                    throw new IOException("The incoming file changed immediately before its pinned update.");
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "update", destination, "--changeset=" + conflict.IncomingChangeset.ToString(CultureInfo.InvariantCulture), "--dontmerge", "--report" }), token).ConfigureAwait(false));
            }
            var final = await RequireIncomingMoveIdentityAsync(workspace, destination, conflict.IncomingItemId, token).ConfigureAwait(false);
            if ((string)final.Element("Status") != "controlled" || (long?)final.Element("RevisionChangeset") != conflict.IncomingRevisionChangeset ||
                StructureFileHash(destination) != state.IncomingHash || File.Exists(original) || Directory.Exists(original)) throw new IOException("The incoming move did not reach the pinned identity, revision and content.");
            var finalPending = await GetStatusAsync(root, token).ConfigureAwait(false);
            if (finalPending.Any(item => scope.Any(path => SamePath(item.Path, MergeLocalPath(root, path)) || (!String.IsNullOrEmpty(item.OldPath) && SamePath(item.OldPath, MergeLocalPath(root, path))))))
                throw new IOException("The incoming move still has a pending structural operation.");
            ValidateStructureConfiguration(state, workspace);
        }
    }
}
