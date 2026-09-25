// GPL-2.0-or-later. Explicit, backed-up directory decisions in Partial workspaces.
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
    public sealed class PlasticPartialDirectoryItem
    {
        public string RepositoryPath { get; set; }
        public string IncomingPath { get; set; }
        public bool IsDirectory { get; set; }
        public long ItemId { get; set; }
        public long BaseChangeset { get; set; }
        public long IncomingRevisionChangeset { get; set; }
        public bool HasLocalChanges { get; set; }
    }
    public sealed class PlasticPartialDirectoryConflict
    {
        public string RepositoryPath { get; set; }
        public string IncomingPath { get; set; }
        public string Kind { get; set; }
        public long ItemId { get; set; }
        public long IncomingChangeset { get; set; }
        public string Reason { get; set; }
        public IList<string> ResolutionOptions { get; set; }
        public IList<PlasticPartialDirectoryItem> Items { get; set; }
        public PlasticPartialDirectoryConflict() { ResolutionOptions = new List<string>(); Items = new List<PlasticPartialDirectoryItem>(); Reason = ""; IncomingPath = ""; }
    }
    public sealed class PlasticPartialDirectorySession
    {
        public string SessionId { get; set; }
        public string WorkspaceRoot { get; set; }
        public PlasticPartialDirectoryConflict Conflict { get; set; }
        public string RecoveryDirectory { get; set; }
        public bool Ready { get; set; }
        public bool Applying { get; set; }
        public string Resolution { get; set; }
    }
    public sealed partial class PlasticClient
    {
        private sealed class DirectoryDecisionState
        {
            internal PlasticPartialDirectorySession Session;
            internal string Repository, Configuration, Pending;
            internal bool FullWorkspace;
            internal string FullUpdateHash, FullOutsideIdentityKey;
            internal string LoadNamespace = "", PendingLoadNamespace = "", OutsideSnapshot = "";
            internal bool OutsideSnapshotRecorded;
            internal bool Readding;
            internal readonly HashSet<string> AddIntents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, long> AddedIdentities = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            internal string[] LoadRules, ScopedLoadRules;
            internal readonly Dictionary<string, string> LocalHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> BaseHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> IncomingHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        private static string DirectoryDecisionIndex(string root) { return Path.Combine(root, ".plastic", "tortoisescm-partial-directory.session"); }
        public bool HasSavedPartialDirectorySession(string root) { return File.Exists(DirectoryDecisionIndex(Path.GetFullPath(root))); }
        public void ThrowIfPartialDirectoryActive(string root)
        { if (HasSavedPartialDirectorySession(root)) throw new ArgumentException("A Partial directory decision is active. Finish it, cancel an unapplied preparation, or explicitly recover its reviewed subtree first."); }
        private static bool DirectoryDecisionWithin(string path, string directory)
        { return String.Equals(path, directory, StringComparison.OrdinalIgnoreCase) || path.StartsWith(directory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase); }
        private static bool DirectoryDecisionType(XElement item)
        { return new[] { "dir", "directory", "目录" }.Contains((string)item.Element("Type"), StringComparer.OrdinalIgnoreCase); }
        private static long DirectoryDecisionNumber(XElement item, string name)
        { long result; return Int64.TryParse((string)item.Element(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out result) ? result : -1; }
        private async Task<List<XElement>> DirectoryDecisionLocalTreeAsync(PlasticWorkspace workspace, CancellationToken token)
        {
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", workspace.RootPath, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result);
            return SafeXml.Load(result.Output).Descendants("LsItem").ToList();
        }
        private async Task<StructureTree> DirectoryDecisionTreeAtAsync(PlasticWorkspace workspace, long changeset, CancellationToken token)
        {
            if (changeset < 0) throw new ArgumentException("A directory's loaded revision cannot be verified.");
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", "/", "--tree=cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(result);
            return new StructureTree { Changeset = changeset, Items = SafeXml.Load(result.Output).Descendants("LsItem").ToList() };
        }
        public async Task<IList<PlasticPartialDirectoryConflict>> PreviewPartialDirectoriesAsync(string root, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            var loaded = await DirectoryDecisionLocalTreeAsync(workspace, token).ConfigureAwait(false);
            var tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            var candidates = new List<PlasticPartialDirectoryConflict>();
            foreach (var directory in loaded.Where(DirectoryDecisionType).Where(item => DirectoryDecisionNumber(item, "ItemId") > 0))
            {
                string path = StructureRepositoryPath(workspace.RootPath, (string)directory.Element("CurrentPath")); if (path == "/") continue;
                long id = DirectoryDecisionNumber(directory, "ItemId");
                var incoming = tree.Items.SingleOrDefault(item => DirectoryDecisionNumber(item, "ItemId") == id);
                if (incoming != null && String.Equals((string)incoming.Element("CurrentPath"), path, StringComparison.Ordinal)) continue;
                candidates.Add(new PlasticPartialDirectoryConflict { RepositoryPath = path, IncomingPath = incoming == null ? "" : (string)incoming.Element("CurrentPath"), ItemId = id, IncomingChangeset = tree.Changeset,
                    Kind = incoming == null ? "incoming-directory-delete" : "incoming-directory-move" });
            }
            var result = candidates.Where(item => !candidates.Any(parent => parent != item && DirectoryDecisionWithin(item.RepositoryPath, parent.RepositoryPath))).OrderBy(item => item.RepositoryPath, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var conflict in result)
            {
                foreach (var item in loaded.Where(item => DirectoryDecisionWithin(StructureRepositoryPath(workspace.RootPath, (string)item.Element("CurrentPath")), conflict.RepositoryPath)))
                {
                    string path = StructureRepositoryPath(workspace.RootPath, (string)item.Element("CurrentPath"));
                    conflict.Items.Add(new PlasticPartialDirectoryItem { RepositoryPath = path, IncomingPath = conflict.Kind == "incoming-directory-move" ? conflict.IncomingPath + path.Substring(conflict.RepositoryPath.Length) : "",
                        ItemId = DirectoryDecisionNumber(item, "ItemId"), BaseChangeset = DirectoryDecisionNumber(item, "Changeset"), IsDirectory = DirectoryDecisionType(item), IncomingRevisionChangeset = -1 });
                }
                try
                {
                    await ValidateDirectoryDecisionPreviewAsync(workspace, conflict, loaded, tree, token).ConfigureAwait(false);
                    conflict.ResolutionOptions.Add("take-incoming");
                    conflict.ResolutionOptions.Add("keep-local");
                }
                catch (ArgumentException error) { conflict.Reason = error.Message; }
            }
            return result;
        }
        private async Task ValidateDirectoryDecisionPreviewAsync(PlasticWorkspace workspace, PlasticPartialDirectoryConflict conflict, List<XElement> loaded, StructureTree tree, CancellationToken token)
        {
            ValidateRepositoryFilePath(conflict.RepositoryPath);
            if (conflict.Kind == "incoming-directory-move")
            {
                ValidateRepositoryFilePath(conflict.IncomingPath);
                if (DirectoryDecisionWithin(conflict.IncomingPath, conflict.RepositoryPath) || DirectoryDecisionWithin(conflict.RepositoryPath, conflict.IncomingPath)) throw new ArgumentException("Case-only or overlapping directory moves require a separate decision.");
                string destination = MergeLocalPath(workspace.RootPath, conflict.IncomingPath); RejectReparsePath(destination);
                if (File.Exists(destination) || Directory.Exists(destination)) throw new ArgumentException("The incoming directory destination is already occupied locally.");
            }
            if (conflict.Items.Count == 0 || conflict.Items.Any(item => item.ItemId <= 0)) throw new ArgumentException("The selected subtree contains private, ignored or newly added items.");
            var pending = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
            foreach (var item in conflict.Items)
            {
                string local = MergeLocalPath(workspace.RootPath, item.RepositoryPath); RejectReparsePath(local);
                var nested = DiscoverWorkspace(local); if (nested == null || !SamePath(nested.RootPath, workspace.RootPath)) throw new ArgumentException("Nested workspaces are not supported in directory decisions.");
                var entry = loaded.Single(value => SamePath((string)value.Element("CurrentPath"), local));
                if ((string)entry.Element("Repository") != "rep:" + workspace.Repository || !String.IsNullOrEmpty((string)entry.Element("SymlinkTarget")) || (!item.IsDirectory && !StructureRegularFile(entry, workspace.Repository))) throw new ArgumentException("Directory decisions do not support Xlinks, symlinks or unknown item types.");
                if (item.IsDirectory) { if (!Directory.Exists(local)) throw new ArgumentException("Every selected directory must already be loaded."); }
                else { if (!File.Exists(local)) throw new ArgumentException("Every selected file must already be loaded."); StructureRequireSingleLink(local); }
                var infoResult = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "fileinfo", local, "--fields=RevisionChangeset,Type,Status,IsUnderXlink", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(infoResult);
                var info = SafeXml.Load(infoResult.Output).Descendants("FileInfo").Single();
                if ((string)info.Element("IsUnderXlink") != "false" || !new[] { "controlled", "checked-out" }.Contains((string)info.Element("Status"))) throw new ArgumentException("Every selected item must be controlled outside Xlinks.");
                if (!item.IsDirectory || item.BaseChangeset < 0) item.BaseChangeset = DirectoryDecisionNumber(info, "RevisionChangeset");
                if (item.BaseChangeset < 0) throw new ArgumentException("A selected item's loaded revision cannot be verified.");
                item.HasLocalChanges = pending.Any(change => SamePath(change.Path, local));
            }
            foreach (var change in pending)
            {
                string path = StructureRepositoryPath(workspace.RootPath, change.Path);
                if (DirectoryDecisionWithin(path, conflict.RepositoryPath))
                {
                    if (change.IsDirectory || !new[] { "CH", "CO" }.Contains(change.StatusCode) || !String.IsNullOrEmpty(change.OldPath)) throw new ArgumentException("Only local file content changes or checkouts are supported inside the directory; finish local structural changes first.");
                }
                else if (DirectoryDecisionWithin(conflict.RepositoryPath, path) || (!String.IsNullOrEmpty(conflict.IncomingPath) && (DirectoryDecisionWithin(path, conflict.IncomingPath) || DirectoryDecisionWithin(conflict.IncomingPath, path)))) throw new ArgumentException("The source or destination parent is involved in another pending operation.");
                if (!String.IsNullOrEmpty(change.OldPath) && DirectoryDecisionWithin(StructureRepositoryPath(workspace.RootPath, change.OldPath), conflict.RepositoryPath)) throw new ArgumentException("A selected item has been moved outside the directory.");
            }
            DirectoryDecisionRequirePhysicalTree(workspace.RootPath, conflict.RepositoryPath, conflict.Items.Select(item => item.RepositoryPath), conflict.Items.Where(item => item.IsDirectory).Select(item => item.RepositoryPath));
            DirectoryDecisionScopedLoadRules(workspace.RootPath, conflict);
            var originalRoot = conflict.Items.Single(item => item.RepositoryPath == conflict.RepositoryPath);
            var originalTree = await DirectoryDecisionTreeAtAsync(workspace, originalRoot.BaseChangeset, token).ConfigureAwait(false);
            // Pure parent moves can retain the directory's older revision. Locate that
            // historical subtree by identity, then compare its relative hierarchy.
            var historicalRoot = originalTree.Items.SingleOrDefault(item => DirectoryDecisionNumber(item, "ItemId") == originalRoot.ItemId);
            if (historicalRoot == null || !DirectoryDecisionType(historicalRoot)) throw new ArgumentException("The selected directory's historical identity cannot be verified.");
            string historicalPath = (string)historicalRoot.Element("CurrentPath");
            ValidateRepositoryFilePath(historicalPath);
            var originalItems = originalTree.Items.Where(item => DirectoryDecisionWithin((string)item.Element("CurrentPath"), historicalPath)).ToList();
            if (originalItems.Count != conflict.Items.Count || conflict.Items.Any(item => !originalItems.Any(entry => (string)entry.Element("CurrentPath") == historicalPath + item.RepositoryPath.Substring(conflict.RepositoryPath.Length) && DirectoryDecisionNumber(entry, "ItemId") == item.ItemId && DirectoryDecisionType(entry) == item.IsDirectory))) throw new ArgumentException("The selected directory is not a complete, unchanged loaded hierarchy of its recorded revision.");
            ValidateDirectoryDecisionIncoming(workspace, conflict, tree);
            await DirectoryDecisionValidateOutsideAsync(workspace, conflict, loaded, tree, token).ConfigureAwait(false);
        }
        private static void ValidateDirectoryDecisionIncoming(PlasticWorkspace workspace, PlasticPartialDirectoryConflict conflict, StructureTree tree)
        {
            if (tree.Items.Any(item => DirectoryDecisionWithin((string)item.Element("CurrentPath"), conflict.RepositoryPath))) throw new ArgumentException("The original directory path is occupied by replacement incoming items.");
            if (conflict.Kind == "incoming-directory-delete")
            {
                if (tree.Items.Any(entry => conflict.Items.Any(item => DirectoryDecisionNumber(entry, "ItemId") == item.ItemId))) throw new ArgumentException("Some deleted-directory descendants were moved elsewhere; resolve that combined structure separately.");
                return;
            }
            var incoming = tree.Items.Where(item => DirectoryDecisionWithin((string)item.Element("CurrentPath"), conflict.IncomingPath)).ToList();
            if (incoming.Count != conflict.Items.Count) throw new ArgumentException("The incoming directory move also adds or removes descendants. This first directory resolver requires the same hierarchy.");
            foreach (var item in conflict.Items)
            {
                var entry = incoming.SingleOrDefault(value => String.Equals((string)value.Element("CurrentPath"), item.IncomingPath, StringComparison.Ordinal));
                if (entry == null || DirectoryDecisionNumber(entry, "ItemId") != item.ItemId || DirectoryDecisionType(entry) != item.IsDirectory ||
                    (string)entry.Element("Repository") != "rep:" + workspace.Repository || !String.IsNullOrEmpty((string)entry.Element("SymlinkTarget")) || (!item.IsDirectory && !StructureRegularFile(entry, workspace.Repository))) throw new ArgumentException("The moved directory contains renamed, replaced or linked descendants.");
                long revision = DirectoryDecisionNumber(entry, "Changeset");
                if (item.IncomingRevisionChangeset >= 0 && item.IncomingRevisionChangeset != revision) throw new ArgumentException("The incoming directory contents advanced after preparation; preserve the saved backups and refresh the decision.");
                item.IncomingRevisionChangeset = revision;
            }
        }
        private async Task DirectoryDecisionValidateOutsideAsync(PlasticWorkspace workspace, PlasticPartialDirectoryConflict conflict, List<XElement> loaded, StructureTree tree, CancellationToken token)
        {
            foreach (var directory in loaded.Where(DirectoryDecisionType).Where(item => DirectoryDecisionNumber(item, "ItemId") > 0))
            {
                string path = StructureRepositoryPath(workspace.RootPath, (string)directory.Element("CurrentPath"));
                if (DirectoryDecisionWithin(path, conflict.RepositoryPath) || (!String.IsNullOrEmpty(conflict.IncomingPath) && DirectoryDecisionWithin(path, conflict.IncomingPath))) continue;
                var incoming = tree.Items.SingleOrDefault(item => String.Equals((string)item.Element("CurrentPath"), path, StringComparison.OrdinalIgnoreCase));
                if (incoming == null || !DirectoryDecisionType(incoming) || DirectoryDecisionNumber(incoming, "ItemId") != DirectoryDecisionNumber(directory, "ItemId") || (string)directory.Element("Repository") != "rep:" + workspace.Repository || !String.IsNullOrEmpty((string)directory.Element("SymlinkTarget")) || (string)incoming.Element("Repository") != "rep:" + workspace.Repository || !String.IsNullOrEmpty((string)incoming.Element("SymlinkTarget"))) throw new ArgumentException("Another loaded directory '" + path + "' also has incoming structure changes. Resolve one isolated directory change at a time.");
            }
            await ValidateStructureParentsAsync(workspace, new[] { conflict.RepositoryPath, conflict.IncomingPath }.Where(path => !String.IsNullOrEmpty(path)), tree, token).ConfigureAwait(false);
        }
        private static string[] DirectoryDecisionLoadRules(string root)
        { string path = Path.Combine(root, ".plastic", "plastic.fullycheckeddirectories"); RejectReparsePath(path); return File.Exists(path) ? File.ReadAllLines(path).OrderBy(line => line, StringComparer.Ordinal).ToArray() : new string[0]; }
        private static string[] DirectoryDecisionScopedLoadRules(string root, PlasticPartialDirectoryConflict conflict)
        {
            if (File.Exists(Path.Combine(root, ".plastic", "plastic.fullupdate")))
            {
                if (DirectoryDecisionLoadRules(root).Length != 0) throw new ArgumentException("Full-workspace loading has unexpected explicit directory rules; restore a consistent load configuration first.");
                return new string[0];
            }
            var rules = DirectoryDecisionLoadRules(root); var scoped = new List<string>();
            foreach (var directory in conflict.Items.Where(item => item.IsDirectory))
            {
                string suffix = ":" + directory.ItemId.ToString(CultureInfo.InvariantCulture); var matches = rules.Where(line => line.EndsWith(suffix, StringComparison.Ordinal)).ToList();
                if (matches.Count != 1) throw new ArgumentException("The complete directory subtree must already be loaded, including every descendant directory. Partial load exclusions are not expanded automatically.");
                scoped.Add(matches[0]);
            }
            return scoped.OrderBy(line => line, StringComparer.Ordinal).ToArray();
        }
        private static void DirectoryDecisionRequirePhysicalTree(string root, string directory, IEnumerable<string> paths, IEnumerable<string> directories)
        {
            var allowed = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase); var folders = new HashSet<string>(directories, StringComparer.OrdinalIgnoreCase);
            string localRoot = MergeLocalPath(root, directory); RejectReparsePath(localRoot);
            if (!Directory.Exists(localRoot)) throw new ArgumentException("The selected directory is not loaded.");
            var stack = new Stack<string>(); stack.Push(localRoot); var observed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (stack.Count > 0)
            {
                string current = stack.Pop(); RejectReparsePath(current); string repositoryPath = StructureRepositoryPath(root, current); observed.Add(repositoryPath);
                if (!allowed.Contains(repositoryPath) || !folders.Contains(repositoryPath) || (current != localRoot && Directory.Exists(Path.Combine(current, ".plastic")))) throw new ArgumentException("The selected subtree contains an unreviewed directory or nested workspace.");
                foreach (string child in Directory.EnumerateFileSystemEntries(current))
                {
                    RejectReparsePath(child); string path = StructureRepositoryPath(root, child);
                    if (!allowed.Contains(path)) throw new ArgumentException("The selected subtree contains an unreviewed private or ignored item: " + path);
                    if (Directory.Exists(child)) stack.Push(child);
                    else { if (folders.Contains(path)) throw new ArgumentException("A selected directory became a file."); StructureRequireSingleLink(child); observed.Add(path); }
                }
            }
            if (!observed.SetEquals(allowed)) throw new ArgumentException("A selected file or directory is missing from the loaded subtree.");
        }
        private static string DirectoryDecisionConfiguration(string root)
        {
            return String.Join("|", new[] { "plastic.selector", "plastic.workspace" }.Select(name => { string path = Path.Combine(root, ".plastic", name); RejectReparsePath(path); return name + ":" + (File.Exists(path) ? MergeHash(path) : "missing"); }));
        }
        private static string DirectoryDecisionOutsideIdentities(PlasticWorkspace workspace, PlasticPartialDirectoryConflict conflict, IEnumerable<XElement> items, bool local)
        {
            return String.Join("\n", items.Where(item => DirectoryDecisionNumber(item, "ItemId") > 0).Select(item => new { Item = item, Path = local ? StructureRepositoryPath(workspace.RootPath, (string)item.Element("CurrentPath")) : (string)item.Element("CurrentPath") })
                .Where(entry => entry.Path != "/" && !DirectoryDecisionWithin(entry.Path, conflict.RepositoryPath) && (String.IsNullOrEmpty(conflict.IncomingPath) || !DirectoryDecisionWithin(entry.Path, conflict.IncomingPath)))
                .Select(entry => String.Join("|", entry.Path, DirectoryDecisionNumber(entry.Item, "ItemId"), DirectoryDecisionType(entry.Item))).OrderBy(value => value, StringComparer.Ordinal));
        }
        private static string DirectoryDecisionBackup(DirectoryDecisionState state, string kind, string path)
        { return Path.Combine(state.Session.RecoveryDirectory, kind + "-" + MergeKey(path) + ".bin"); }
        private static IEnumerable<string> DirectoryDecisionPaths(PlasticPartialDirectoryConflict conflict)
        { return conflict.Items.SelectMany(item => new[] { item.RepositoryPath, item.IncomingPath }).Where(path => !String.IsNullOrEmpty(path)).Distinct(StringComparer.OrdinalIgnoreCase); }
        private async Task<string> DirectoryDecisionPendingAsync(PlasticWorkspace workspace, PlasticPartialDirectoryConflict conflict, CancellationToken token)
        {
            var changes = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
            return String.Join("\n", changes.Where(item => DirectoryDecisionPaths(conflict).Any(path => SamePath(item.Path, MergeLocalPath(workspace.RootPath, path)) || (!String.IsNullOrEmpty(item.OldPath) && SamePath(item.OldPath, MergeLocalPath(workspace.RootPath, path)))))
                .Select(item => item.StatusCode + "|" + item.Path.ToUpperInvariant() + "|" + item.OldPath).OrderBy(value => value, StringComparer.Ordinal));
        }
        public async Task<PlasticPartialDirectorySession> PreparePartialDirectoryAsync(string root, string repositoryPath, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false); root = workspace.RootPath;
            ValidateMergeStorageLocation(root);
            using (var structureGate = StructureGate(root))
            using (var gate = OpenMergeGate(root))
            {
                if (HasSavedPartialDirectorySession(root) || HasSavedPartialStructureSession(root) || HasSavedPartialConflictSession(root) || HasSavedMergeSession(root)) throw new ArgumentException("Finish or cancel the existing conflict session before preparing a directory decision.");
                var conflict = (await PreviewPartialDirectoriesAsync(root, token).ConfigureAwait(false)).SingleOrDefault(item => String.Equals(item.RepositoryPath, repositoryPath, StringComparison.Ordinal));
                if (conflict == null || conflict.ResolutionOptions.Count == 0) throw new ArgumentException(conflict == null ? "The selected incoming directory change no longer exists." : conflict.Reason);
                var state = new DirectoryDecisionState { Repository = workspace.Repository, Configuration = DirectoryDecisionConfiguration(root), LoadRules = DirectoryDecisionLoadRules(root), ScopedLoadRules = DirectoryDecisionScopedLoadRules(root, conflict),
                    Session = new PlasticPartialDirectorySession { SessionId = Guid.NewGuid().ToString("N"), WorkspaceRoot = root, Conflict = conflict, Ready = true, Resolution = "" } };
                string fullUpdate = Path.Combine(root, ".plastic", "plastic.fullupdate"); RejectReparsePath(fullUpdate);
                state.FullWorkspace = File.Exists(fullUpdate); state.FullUpdateHash = state.FullWorkspace ? MergeHash(fullUpdate) : "missing"; state.FullOutsideIdentityKey = "";
                if (state.FullWorkspace)
                {
                    var loaded = await DirectoryDecisionLocalTreeAsync(workspace, token).ConfigureAwait(false);
                    var incoming = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
                    state.FullOutsideIdentityKey = DirectoryDecisionOutsideIdentities(workspace, conflict, incoming.Items, false);
                    if (DirectoryDecisionOutsideIdentities(workspace, conflict, loaded, true) != state.FullOutsideIdentityKey) throw new ArgumentException("Full-workspace loading also has incoming structure changes outside the selected directory. Resolve that outside structure separately first.");
                    if ((await GetStatusAsync(root, token).ConfigureAwait(false)).Any(item => item.IsDirectory && item.StatusCode != "PR" && item.StatusCode != "IG")) throw new ArgumentException("Full-workspace directory decisions require other local directory structure changes to be completed first.");
                    state.LoadRules = loaded.Where(DirectoryDecisionType).Where(item => DirectoryDecisionNumber(item, "ItemId") > 0 && StructureRepositoryPath(root, (string)item.Element("CurrentPath")) != "/")
                        .Select(item => DirectoryDecisionNumber(item, "ItemId").ToString(CultureInfo.InvariantCulture)).OrderBy(line => line, StringComparer.Ordinal).ToArray();
                    state.ScopedLoadRules = conflict.Items.Where(item => item.IsDirectory).Select(item => item.ItemId.ToString(CultureInfo.InvariantCulture)).OrderBy(line => line, StringComparer.Ordinal).ToArray();
                }
                state.Session.RecoveryDirectory = MergeSessionDirectory(state.Session.SessionId); CreatePrivateMergeDirectory(state.Session.RecoveryDirectory);
                state.Pending = await DirectoryDecisionPendingAsync(workspace, conflict, token).ConfigureAwait(false);
                foreach (var item in conflict.Items.Where(item => !item.IsDirectory))
                {
                    string local = MergeLocalPath(root, item.RepositoryPath), backup = DirectoryDecisionBackup(state, "local", item.RepositoryPath);
                    StructureRequireSingleLink(local); string hash = StructureFileHash(local); File.Copy(local, backup, false);
                    if (StructureFileHash(local) != hash || MergeHash(backup) != hash) throw new IOException("A directory contributor changed while its backup was copied.");
                    state.LocalHashes[item.RepositoryPath] = hash;
                    await DownloadPartialIdentityFileAsync(workspace, item.ItemId, item.BaseChangeset, DirectoryDecisionBackup(state, "base", item.RepositoryPath), token).ConfigureAwait(false);
                    state.BaseHashes[item.RepositoryPath] = MergeHash(DirectoryDecisionBackup(state, "base", item.RepositoryPath));
                    if (!item.HasLocalChanges && hash != state.BaseHashes[item.RepositoryPath]) throw new IOException("A file changed without a matching reviewed pending-content entry. Refresh the directory preview.");
                    if (conflict.Kind == "incoming-directory-move")
                    {
                        await DownloadHistoricalFileAsync(workspace, item.IncomingPath, conflict.IncomingChangeset, DirectoryDecisionBackup(state, "incoming", item.RepositoryPath), token).ConfigureAwait(false);
                        state.IncomingHashes[item.RepositoryPath] = MergeHash(DirectoryDecisionBackup(state, "incoming", item.RepositoryPath));
                    }
                    foreach (string kind in new[] { "local", "base", "incoming" }) { string file = DirectoryDecisionBackup(state, kind, item.RepositoryPath); if (File.Exists(file)) File.SetAttributes(file, FileAttributes.ReadOnly); }
                }
                ValidateDirectoryDecisionConfiguration(state, workspace, false);
                await RequireDirectoryDecisionOriginalAsync(state, workspace, token).ConfigureAwait(false);
                SaveDirectoryDecisionState(state);
                string index = DirectoryDecisionIndex(root); RejectReparsePath(index); File.WriteAllText(index, Path.Combine(state.Session.RecoveryDirectory, "directory.xml"));
                return state.Session;
            }
        }
        public async Task<PlasticPartialDirectorySession> GetPartialDirectorySessionAsync(string root, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var structureGate = StructureGate(workspace.RootPath)) using (var gate = OpenMergeGate(workspace.RootPath))
            { var state = LoadDirectoryDecisionState(workspace.RootPath, false); return state == null ? null : state.Session; }
        }
        public async Task CancelPartialDirectoryAsync(string root, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var structureGate = StructureGate(workspace.RootPath)) using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadDirectoryDecisionState(workspace.RootPath, true);
                if (!state.Session.Ready || state.Session.Applying) throw new ArgumentException("An interrupted directory decision requires explicit recovery to the reviewed incoming subtree.");
                File.Delete(DirectoryDecisionIndex(workspace.RootPath));
            }
        }
        public async Task<PlasticCommandResult> ResolvePartialDirectoryAsync(string root, string resolution, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false); root = workspace.RootPath;
            using (var structureGate = StructureGate(root)) using (var gate = OpenMergeGate(root))
            {
                var state = LoadDirectoryDecisionState(root, true);
                if (!state.Session.Ready || state.Session.Applying) throw new ArgumentException("Recover the interrupted directory decision before another resolution.");
                if (!state.Session.Conflict.ResolutionOptions.Contains(resolution)) throw new ArgumentException("Choose one of the offered directory resolutions.");
                ValidateDirectoryDecisionConfiguration(state, workspace, false);
                await RequireDirectoryDecisionOriginalAsync(state, workspace, token).ConfigureAwait(false);
                state.Session.Resolution = resolution; state.Session.Ready = false; state.Session.Applying = true; SaveDirectoryDecisionState(state);
                try
                {
                    await ApplyDirectoryDecisionIncomingAsync(state, workspace, false, token).ConfigureAwait(false);
                    if (resolution == "keep-local" && state.Session.Conflict.Kind == "incoming-directory-delete")
                        await RestoreDeletedDirectoryAsync(state, workspace, token).ConfigureAwait(false);
                    else if (resolution == "keep-local")
                    {
                        foreach (var item in state.Session.Conflict.Items.Where(item => !item.IsDirectory && item.HasLocalChanges))
                        {
                            string local = MergeLocalPath(root, item.IncomingPath); StructureRequireSingleLink(local);
                            var identity = await RequireIncomingMoveIdentityAsync(workspace, local, item.ItemId, token).ConfigureAwait(false);
                            if ((long?)identity.Element("RevisionChangeset") != item.IncomingRevisionChangeset) throw new IOException("The incoming file changed identity or revision before local content restoration.");
                            if (StructureFileHash(local) != state.IncomingHashes[item.RepositoryPath]) throw new IOException("The incoming file changed before its local content was restored.");
                            string temporary = local + ".tscm-" + Guid.NewGuid().ToString("N");
                            try { File.Copy(DirectoryDecisionBackup(state, "local", item.RepositoryPath), temporary, false); File.SetAttributes(temporary, FileAttributes.Normal); if (StructureFileHash(local) != state.IncomingHashes[item.RepositoryPath]) throw new IOException("The incoming file changed during restoration."); File.Replace(temporary, local, null); }
                            finally { if (File.Exists(temporary)) File.Delete(temporary); }
                        }
                    }
                    await VerifyDirectoryDecisionFinalAsync(state, workspace, resolution == "keep-local", token).ConfigureAwait(false);
                    File.Delete(DirectoryDecisionIndex(root));
                    return new PlasticCommandResult { Output = (resolution == "keep-local" && state.Session.Conflict.Kind == "incoming-directory-delete" ?
                        "Restored the original local directory contents as NEW pending additions. The deleted identities and history were not restored. " : "Applied the reviewed directory decision. ") +
                        "Backups remain at " + state.Session.RecoveryDirectory + ". Pending changes require a separate checkin." };
                }
                catch (Exception error) { throw new InvalidOperationException("The directory decision did not finish. Other writes remain blocked. Recover explicitly to the reviewed incoming subtree; backups remain at " + state.Session.RecoveryDirectory + ". " + error.Message, error); }
            }
        }
        public async Task<PlasticCommandResult> RecoverPartialDirectoryAsync(string root, CancellationToken token)
        {
            var workspace = await PartialWorkspaceAsync(root, token).ConfigureAwait(false); root = workspace.RootPath;
            using (var structureGate = StructureGate(root)) using (var gate = OpenMergeGate(root))
            {
                var state = LoadDirectoryDecisionState(root, true);
                if (state.Session.Ready && !state.Session.Applying) throw new ArgumentException("This directory preparation has not been applied; cancel it instead.");
                ValidateDirectoryDecisionConfiguration(state, workspace, true);
                if (state.Readding) await RecoverReaddedDirectoryAsync(state, workspace, token).ConfigureAwait(false);
                else await ApplyDirectoryDecisionIncomingAsync(state, workspace, true, token).ConfigureAwait(false);
                await VerifyDirectoryDecisionFinalAsync(state, workspace, false, token).ConfigureAwait(false);
                File.Delete(DirectoryDecisionIndex(root));
                return new PlasticCommandResult { Output = "Recovered the selected directory to its reviewed incoming state. Original and recovery-time file bytes remain at " + state.Session.RecoveryDirectory + ". No checkin was performed." };
            }
        }
        private async Task RequireDirectoryDecisionOriginalAsync(DirectoryDecisionState state, PlasticWorkspace workspace, CancellationToken token)
        {
            var conflict = state.Session.Conflict;
            var fresh = (await PreviewPartialDirectoriesAsync(workspace.RootPath, token).ConfigureAwait(false)).SingleOrDefault(item => item.RepositoryPath == conflict.RepositoryPath);
            if (fresh == null || fresh.ResolutionOptions.Count == 0 || DirectoryDecisionKey(fresh) != DirectoryDecisionKey(conflict)) throw new ArgumentException(fresh != null && !String.IsNullOrEmpty(fresh.Reason) ? fresh.Reason : "The local or incoming directory hierarchy changed after preparation. Cancel and prepare again.");
            foreach (var item in conflict.Items.Where(item => !item.IsDirectory)) if (StructureFileHash(MergeLocalPath(workspace.RootPath, item.RepositoryPath)) != state.LocalHashes[item.RepositoryPath]) throw new ArgumentException("A reviewed file changed after preparation. Cancel and prepare again.");
            if (await DirectoryDecisionPendingAsync(workspace, conflict, token).ConfigureAwait(false) != state.Pending) throw new ArgumentException("The directory's pending changes changed after preparation.");
        }
        private static string DirectoryDecisionKey(PlasticPartialDirectoryConflict conflict)
        {
            return String.Join("|", conflict.RepositoryPath, conflict.IncomingPath, conflict.Kind, conflict.ItemId, conflict.IncomingChangeset) + "\n" + String.Join("\n", conflict.Items.OrderBy(item => item.RepositoryPath, StringComparer.Ordinal).Select(item => String.Join("|", item.RepositoryPath, item.IncomingPath, item.ItemId, item.IsDirectory, item.BaseChangeset, item.IncomingRevisionChangeset, item.HasLocalChanges)));
        }
        private static void ValidateDirectoryDecisionConfiguration(DirectoryDecisionState state, PlasticWorkspace workspace, bool intermediate)
        {
            if (state.Repository != workspace.Repository || state.Configuration != DirectoryDecisionConfiguration(workspace.RootPath)) throw new ArgumentException("The workspace identity or selector changed. Restore that configuration before directory recovery.");
            var actual = DirectoryDecisionLoadRules(workspace.RootPath);
            string fullUpdate = Path.Combine(workspace.RootPath, ".plastic", "plastic.fullupdate"); RejectReparsePath(fullUpdate);
            bool full = File.Exists(fullUpdate);
            if (state.FullWorkspace)
            {
                if (full)
                { if (MergeHash(fullUpdate) != state.FullUpdateHash || actual.Length != 0) throw new ArgumentException("The full-workspace loading marker or explicit rules changed unexpectedly."); return; }
                if (!intermediate) throw new ArgumentException("The full-workspace loading configuration changed after preparation.");
                var identities = new List<string>(); string observedNamespace = "";
                foreach (string line in actual)
                {
                    int separator = line.LastIndexOf(':'); Guid namespaceGuid; long identity;
                    if (separator <= 0 || !Guid.TryParse(line.Substring(0, separator), out namespaceGuid) || !Int64.TryParse(line.Substring(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out identity) || identity <= 0 || identity.ToString(CultureInfo.InvariantCulture) != line.Substring(separator + 1)) throw new ArgumentException("A full-workspace intermediate loading rule has an unknown format.");
                    string prefix = line.Substring(0, separator);
                    if ((!String.IsNullOrEmpty(observedNamespace) && observedNamespace != prefix) || (!String.IsNullOrEmpty(state.LoadNamespace) && state.LoadNamespace != prefix)) throw new ArgumentException("The directory loading namespace changed during the session.");
                    observedNamespace = prefix; identities.Add(identity.ToString(CultureInfo.InvariantCulture));
                }
                var scopedIds = new HashSet<string>(state.ScopedLoadRules, StringComparer.Ordinal);
                if (identities.Distinct().Count() != identities.Count || !identities.Where(id => !scopedIds.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(state.LoadRules.Where(id => !scopedIds.Contains(id)))) throw new ArgumentException("The full-workspace intermediate rules changed an unrelated directory's loading membership.");
                state.PendingLoadNamespace = observedNamespace;
                return;
            }
            else if (full) throw new ArgumentException("The workspace changed from explicit Partial loading to full-workspace loading.");
            if (!intermediate)
            { if (!actual.SequenceEqual(state.LoadRules)) throw new ArgumentException("The Partial directory load rules changed after preparation."); return; }
            var allowed = new HashSet<string>(state.ScopedLoadRules, StringComparer.Ordinal);
            if (!actual.Where(line => !allowed.Contains(line)).SequenceEqual(state.LoadRules.Where(line => !allowed.Contains(line))) || actual.Where(allowed.Contains).Distinct().Count() != actual.Count(allowed.Contains)) throw new ArgumentException("Unrelated Partial load rules changed. Restore them before directory recovery.");
        }
        private static Dictionary<string, string> DirectoryDecisionScopeSnapshot(DirectoryDecisionState state)
        {
            string root = state.Session.WorkspaceRoot; var conflict = state.Session.Conflict;
            var paths = new HashSet<string>(DirectoryDecisionPaths(conflict), StringComparer.OrdinalIgnoreCase);
            var directories = new HashSet<string>(conflict.Items.Where(item => item.IsDirectory).SelectMany(item => new[] { item.RepositoryPath, item.IncomingPath }).Where(path => !String.IsNullOrEmpty(path)), StringComparer.OrdinalIgnoreCase);
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in paths)
            {
                string local = MergeLocalPath(root, path); RejectReparsePath(local);
                if (Directory.Exists(local))
                {
                    if (!directories.Contains(path) || Directory.Exists(Path.Combine(local, ".plastic"))) throw new ArgumentException("A reviewed file became a directory or nested workspace.");
                    foreach (string child in Directory.EnumerateFileSystemEntries(local))
                    { RejectReparsePath(child); if (!paths.Contains(StructureRepositoryPath(root, child))) throw new ArgumentException("An unreviewed file or directory appeared in the selected subtree. Preserve it separately before recovery: " + child); }
                    result[path] = "directory";
                }
                else if (File.Exists(local))
                { if (directories.Contains(path)) throw new ArgumentException("A reviewed directory became a file."); StructureRequireSingleLink(local); result[path] = MergeHash(local); }
                else result[path] = "missing";
            }
            return result;
        }
        private static void DirectoryDecisionRequireSnapshot(DirectoryDecisionState state, Dictionary<string, string> expected)
        {
            var current = DirectoryDecisionScopeSnapshot(state);
            if (current.Count != expected.Count || expected.Any(item => !current.ContainsKey(item.Key) || current[item.Key] != item.Value)) throw new IOException("The selected subtree changed after its last verified snapshot. No further native directory operation was started.");
        }
        private static string DirectoryDecisionOutsideSnapshot(DirectoryDecisionState state)
        {
            var result = new List<string>(); string root = state.Session.WorkspaceRoot; var conflict = state.Session.Conflict; var stack = new Stack<string>(); stack.Push(root);
            while (stack.Count > 0)
            {
                string directory = stack.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    if (SamePath(entry, Path.Combine(root, ".plastic"))) continue;
                    string path = StructureRepositoryPath(root, entry);
                    if (DirectoryDecisionWithin(path, conflict.RepositoryPath) || (!String.IsNullOrEmpty(conflict.IncomingPath) && DirectoryDecisionWithin(path, conflict.IncomingPath))) continue;
                    RejectReparsePath(entry);
                    if (Directory.Exists(entry)) { result.Add("D|" + path); stack.Push(entry); }
                    else result.Add("F|" + path + "|" + MergeHash(entry));
                }
            }
            return String.Join("\n", result.OrderBy(value => value, StringComparer.Ordinal));
        }
        private static void DirectoryDecisionRequireOutside(DirectoryDecisionState state, string expected)
        { if (DirectoryDecisionOutsideSnapshot(state) != expected) throw new IOException("An unrelated workspace path changed during the directory operation. No further mutation was started."); }
        private async Task DirectoryDecisionPinNamespaceAsync(DirectoryDecisionState state, PlasticWorkspace workspace, string outside, bool recovery, CancellationToken token)
        {
            if (!state.FullWorkspace || !String.IsNullOrEmpty(state.LoadNamespace) || String.IsNullOrEmpty(state.PendingLoadNamespace)) return;
            string observedNamespace = state.PendingLoadNamespace;
            if (recovery && (!state.OutsideSnapshotRecorded || outside != state.OutsideSnapshot)) throw new ArgumentException("Unrelated paths or bytes changed before the native loading namespace was recorded. Preserve those changes before recovering this interrupted directory decision.");
            var tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            ValidateDirectoryDecisionIncoming(workspace, state.Session.Conflict, tree);
            if (DirectoryDecisionOutsideIdentities(workspace, state.Session.Conflict, tree.Items, false) != state.FullOutsideIdentityKey) throw new ArgumentException("Unrelated incoming structure changed before the native loading namespace was verified.");
            var loaded = await DirectoryDecisionLocalTreeAsync(workspace, token).ConfigureAwait(false);
            if (DirectoryDecisionOutsideIdentities(workspace, state.Session.Conflict, loaded, true) != state.FullOutsideIdentityKey) throw new ArgumentException("Unrelated loaded identities changed before the native loading namespace was verified.");
            await DirectoryDecisionValidateOutsideAsync(workspace, state.Session.Conflict, loaded, tree, token).ConfigureAwait(false);
            DirectoryDecisionRequireOutside(state, outside);
            ValidateDirectoryDecisionConfiguration(state, workspace, true);
            if (state.PendingLoadNamespace != observedNamespace || File.Exists(Path.Combine(workspace.RootPath, ".plastic", "plastic.fullupdate"))) throw new ArgumentException("The native loading namespace or mode changed while its membership was being verified.");
            state.LoadNamespace = observedNamespace;
            SaveDirectoryDecisionState(state);
        }
        private static void DirectoryDecisionValidateLoadedScope(PlasticWorkspace workspace, PlasticPartialDirectoryConflict conflict, IEnumerable<XElement> loaded)
        {
            foreach (var entry in loaded)
            {
                string path = StructureRepositoryPath(workspace.RootPath, (string)entry.Element("CurrentPath"));
                if (!DirectoryDecisionWithin(path, conflict.RepositoryPath) && (String.IsNullOrEmpty(conflict.IncomingPath) || !DirectoryDecisionWithin(path, conflict.IncomingPath))) continue;
                var item = conflict.Items.SingleOrDefault(value => String.Equals(path, value.RepositoryPath, StringComparison.OrdinalIgnoreCase) || (!String.IsNullOrEmpty(value.IncomingPath) && String.Equals(path, value.IncomingPath, StringComparison.OrdinalIgnoreCase)));
                if (item == null || DirectoryDecisionNumber(entry, "ItemId") != item.ItemId || DirectoryDecisionType(entry) != item.IsDirectory || (string)entry.Element("Repository") != "rep:" + workspace.Repository || !String.IsNullOrEmpty((string)entry.Element("SymlinkTarget"))) throw new ArgumentException("A selected controlled identity changed immediately before native directory configuration.");
            }
        }
        private async Task ApplyDirectoryDecisionIncomingAsync(DirectoryDecisionState state, PlasticWorkspace workspace, bool recovery, CancellationToken token)
        {
            string root = workspace.RootPath; var conflict = state.Session.Conflict;
            ValidateDirectoryDecisionConfiguration(state, workspace, recovery);
            var tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            ValidateDirectoryDecisionIncoming(workspace, conflict, tree);
            if (state.FullWorkspace && DirectoryDecisionOutsideIdentities(workspace, conflict, tree.Items, false) != state.FullOutsideIdentityKey) throw new ArgumentException("Unrelated incoming structure changed after the full-workspace directory preparation. Recovery will not widen the loading scope.");
            if (!recovery && tree.Changeset != conflict.IncomingChangeset) throw new ArgumentException("The incoming branch advanced after preparation.");
            var loaded = await DirectoryDecisionLocalTreeAsync(workspace, token).ConfigureAwait(false);
            await DirectoryDecisionValidateOutsideAsync(workspace, conflict, loaded, tree, token).ConfigureAwait(false);
            var scope = DirectoryDecisionScopeSnapshot(state); string outside = DirectoryDecisionOutsideSnapshot(state);
            var pending = await GetStatusAsync(root, token).ConfigureAwait(false);
            var selected = pending.Where(item => DirectoryDecisionPaths(conflict).Any(path => SamePath(item.Path, MergeLocalPath(root, path)) || (!String.IsNullOrEmpty(item.OldPath) && SamePath(item.OldPath, MergeLocalPath(root, path))))).ToList();
            if (selected.Any(item => !String.IsNullOrEmpty(item.OldPath) || (item.IsDirectory
                ? !recovery || !new[] { "PR", "IG" }.Contains(item.StatusCode) || !conflict.Items.Any(known => known.IsDirectory && (SamePath(item.Path, MergeLocalPath(root, known.RepositoryPath)) || (!String.IsNullOrEmpty(known.IncomingPath) && SamePath(item.Path, MergeLocalPath(root, known.IncomingPath)))))
                : !new[] { "CH", "CO", "LD", "PR", "IG" }.Contains(item.StatusCode)))) throw new ArgumentException("A new local structural operation affects the reviewed directory. Preserve and finish it before recovery.");
            var controlled = new Dictionary<string, PlasticPartialDirectoryItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in loaded)
            {
                string path = StructureRepositoryPath(root, (string)entry.Element("CurrentPath"));
                if (!DirectoryDecisionWithin(path, conflict.RepositoryPath) && (String.IsNullOrEmpty(conflict.IncomingPath) || !DirectoryDecisionWithin(path, conflict.IncomingPath))) continue;
                var item = conflict.Items.SingleOrDefault(value => String.Equals(path, value.RepositoryPath, StringComparison.OrdinalIgnoreCase) || (!String.IsNullOrEmpty(value.IncomingPath) && String.Equals(path, value.IncomingPath, StringComparison.OrdinalIgnoreCase)));
                if (item == null) throw new ArgumentException("An unreviewed native item appeared in the selected subtree.");
                long id = DirectoryDecisionNumber(entry, "ItemId");
                if (id > 0)
                {
                    if (id != item.ItemId || DirectoryDecisionType(entry) != item.IsDirectory || (string)entry.Element("Repository") != "rep:" + workspace.Repository || !String.IsNullOrEmpty((string)entry.Element("SymlinkTarget"))) throw new ArgumentException("A reviewed path now belongs to another controlled identity or link.");
                    controlled.Add(path, item);
                }
                else if (!recovery || !String.IsNullOrWhiteSpace((string)entry.Element("ItemId"))) throw new ArgumentException("Only backed-up private recovery bytes are accepted at reviewed paths.");
            }
            if (controlled.ContainsKey(conflict.RepositoryPath) && !String.IsNullOrEmpty(conflict.IncomingPath) && controlled.ContainsKey(conflict.IncomingPath)) throw new ArgumentException("Both source and destination directory identities are loaded; preserve the workspace for separate review.");
            if (recovery)
            {
                string observed = Path.Combine(state.Session.RecoveryDirectory, "recovery-" + Guid.NewGuid().ToString("N")); CreatePrivateMergeDirectory(observed);
                foreach (var entry in scope.Where(item => item.Value != "directory" && item.Value != "missing"))
                { string backup = Path.Combine(observed, MergeKey(entry.Key) + ".bin"); File.Copy(MergeLocalPath(root, entry.Key), backup, false); if (MergeHash(backup) != entry.Value) throw new IOException("A recovery-time file changed while its backup was copied."); File.SetAttributes(backup, FileAttributes.ReadOnly); }
            }
            else foreach (var item in conflict.Items.Where(item => !item.IsDirectory)) if (scope[item.RepositoryPath] != state.LocalHashes[item.RepositoryPath]) throw new IOException("An original directory file changed immediately before application.");
            DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
            if (!recovery) { state.OutsideSnapshot = outside; state.OutsideSnapshotRecorded = true; SaveDirectoryDecisionState(state); }
            await DirectoryDecisionPinNamespaceAsync(state, workspace, outside, recovery, token).ConfigureAwait(false);
            foreach (var entry in controlled.Where(pair => !pair.Value.IsDirectory).ToList())
            {
                string path = entry.Key, local = MergeLocalPath(root, path); var item = entry.Value;
                var info = await RequireIncomingMoveIdentityAsync(workspace, local, item.ItemId, token).ConfigureAwait(false);
                long expectedRevision = path == item.RepositoryPath ? item.BaseChangeset : item.IncomingRevisionChangeset;
                if ((long?)info.Element("RevisionChangeset") != expectedRevision) throw new ArgumentException("A controlled recovery file changed revision after preparation.");
                string expectedCleanHash = path == item.RepositoryPath ? state.BaseHashes[item.RepositoryPath] : state.IncomingHashes[item.RepositoryPath];
                if (selected.Any(change => SamePath(change.Path, local)))
                {
                    DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                    RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "undo", local }), token).ConfigureAwait(false));
                    if (StructureFileHash(local) != expectedCleanHash) throw new IOException("Native undo did not restore the reviewed file revision.");
                    scope[path] = expectedCleanHash;
                    DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                }
                else if (scope[path] != expectedCleanHash) throw new IOException("A controlled file has unreviewed bytes without a matching pending change.");
            }
            // Recovery may encounter fresh private bytes at a previously unloaded
            // reviewed filename. Remove only those exact, durably copied files.
            foreach (var entry in scope.Where(item => item.Value != "directory" && item.Value != "missing" && !controlled.ContainsKey(item.Key)).ToList())
            {
                if (!recovery) throw new ArgumentException("A reviewed file became private before application.");
                DirectoryDecisionRequireSnapshot(state, scope); string local = MergeLocalPath(root, entry.Key); StructureRequireSingleLink(local); File.Delete(local); scope[entry.Key] = "missing";
            }
            foreach (string path in scope.Where(item => item.Value == "directory" && !controlled.ContainsKey(item.Key)).Select(item => item.Key).OrderByDescending(path => path.Length).ToList())
            {
                if (!recovery) throw new ArgumentException("A reviewed directory became private before application.");
                DirectoryDecisionRequireSnapshot(state, scope); string local = MergeLocalPath(root, path);
                if (Directory.EnumerateFileSystemEntries(local).Any()) throw new IOException("A private recovery directory still contains unreviewed data.");
                Directory.Delete(local, false); scope[path] = "missing";
            }
            foreach (string directory in new[] { conflict.RepositoryPath, conflict.IncomingPath }.Where(path => !String.IsNullOrEmpty(path)))
            {
                string local = MergeLocalPath(root, directory);
                if (controlled.ContainsKey(directory))
                {
                    DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                    var current = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false); ValidateDirectoryDecisionIncoming(workspace, conflict, current);
                    if (state.FullWorkspace && DirectoryDecisionOutsideIdentities(workspace, conflict, current.Items, false) != state.FullOutsideIdentityKey) throw new ArgumentException("Unrelated incoming structure changed before native directory unload.");
                    var currentLoaded = await DirectoryDecisionLocalTreeAsync(workspace, token).ConfigureAwait(false);
                    // Known private recovery paths have been backed up and removed
                    // above; every remaining entry must still be its reviewed ID.
                    DirectoryDecisionValidateLoadedScope(workspace, conflict, currentLoaded);
                    await DirectoryDecisionValidateOutsideAsync(workspace, conflict, currentLoaded, current, token).ConfigureAwait(false);
                    var beforeUnload = await GetStatusAsync(root, token).ConfigureAwait(false);
                    if (beforeUnload.Any(change => DirectoryDecisionPaths(conflict).Any(path => SamePath(change.Path, MergeLocalPath(root, path)) || (!String.IsNullOrEmpty(change.OldPath) && SamePath(change.OldPath, MergeLocalPath(root, path)))))) throw new IOException("A new pending change appeared after the directory contributors were restored. No directory unload was started.");
                    DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                    RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "configure", "-" + directory }), token).ConfigureAwait(false));
                    foreach (string path in scope.Keys.Where(path => DirectoryDecisionWithin(path, directory)).ToList()) scope[path] = Directory.Exists(MergeLocalPath(root, path)) ? "directory" : StructureFileHash(MergeLocalPath(root, path));
                    if (scope.Any(pair => DirectoryDecisionWithin(pair.Key, directory) && pair.Value != "missing" && pair.Value != "directory")) throw new IOException("Native directory unload left an unexpected file in the selected subtree.");
                }
                DirectoryDecisionRemoveEmptyDirectories(state, directory);
                foreach (string path in scope.Keys.Where(path => DirectoryDecisionWithin(path, directory)).ToList()) scope[path] = "missing";
                DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside); ValidateDirectoryDecisionConfiguration(state, workspace, true);
                await DirectoryDecisionPinNamespaceAsync(state, workspace, outside, false, token).ConfigureAwait(false);
            }
            if (conflict.Kind == "incoming-directory-move")
            {
                tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false); ValidateDirectoryDecisionIncoming(workspace, conflict, tree);
                if (state.FullWorkspace && DirectoryDecisionOutsideIdentities(workspace, conflict, tree.Items, false) != state.FullOutsideIdentityKey) throw new ArgumentException("Unrelated incoming structure changed before native directory load.");
                await DirectoryDecisionValidateOutsideAsync(workspace, conflict, await DirectoryDecisionLocalTreeAsync(workspace, token).ConfigureAwait(false), tree, token).ConfigureAwait(false);
                DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "configure", "+" + conflict.IncomingPath }), token).ConfigureAwait(false));
            }
            DirectoryDecisionRequireOutside(state, outside);
            await VerifyDirectoryDecisionFinalAsync(state, workspace, false, token).ConfigureAwait(false);
        }
        private static void DirectoryDecisionRemoveEmptyDirectories(DirectoryDecisionState state, string selected)
        {
            foreach (string path in state.Session.Conflict.Items.Where(item => item.IsDirectory).SelectMany(item => new[] { item.RepositoryPath, item.IncomingPath }).Where(path => !String.IsNullOrEmpty(path) && DirectoryDecisionWithin(path, selected)).OrderByDescending(path => path.Length))
            {
                string local = MergeLocalPath(state.Session.WorkspaceRoot, path); RejectReparsePath(local);
                if (!Directory.Exists(local)) continue;
                if (Directory.EnumerateFileSystemEntries(local).Any()) throw new IOException("A reviewed directory gained new contents during unload; it was not removed.");
                Directory.Delete(local, false);
            }
        }
        private async Task ValidateReaddedDirectoryAsync(DirectoryDecisionState state, PlasticWorkspace workspace, bool complete, CancellationToken token)
        {
            var conflict = state.Session.Conflict;
            if (!state.Readding || conflict.Kind != "incoming-directory-delete") throw new InvalidDataException("The directory is not in its saved restoration phase.");
            ValidateDirectoryDecisionConfiguration(state, workspace, true);
            var expectedRules = state.FullWorkspace ? new string[0] : state.LoadRules.Except(state.ScopedLoadRules).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (!DirectoryDecisionLoadRules(workspace.RootPath).SequenceEqual(expectedRules) || (state.FullWorkspace && !File.Exists(Path.Combine(workspace.RootPath, ".plastic", "plastic.fullupdate")))) throw new ArgumentException("Restoring the deleted directory changed its reviewed loading state.");
            var tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            ValidateDirectoryDecisionIncoming(workspace, conflict, tree);
            if (state.FullWorkspace && DirectoryDecisionOutsideIdentities(workspace, conflict, tree.Items, false) != state.FullOutsideIdentityKey) throw new ArgumentException("Unrelated incoming structure changed during directory restoration.");
            var loaded = await DirectoryDecisionLocalTreeAsync(workspace, token).ConfigureAwait(false);
            await DirectoryDecisionValidateOutsideAsync(workspace, conflict, loaded, tree, token).ConfigureAwait(false);
            DirectoryDecisionScopeSnapshot(state); // Includes unknown descendants, links and changed item types.
            var pending = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
            var selected = pending.Where(change => DirectoryDecisionWithin(StructureRepositoryPath(workspace.RootPath, change.Path), conflict.RepositoryPath) ||
                (!String.IsNullOrEmpty(change.OldPath) && DirectoryDecisionWithin(StructureRepositoryPath(workspace.RootPath, change.OldPath), conflict.RepositoryPath))).ToList();
            foreach (var change in selected)
            {
                string path = StructureRepositoryPath(workspace.RootPath, change.Path);
                var item = conflict.Items.SingleOrDefault(value => value.RepositoryPath == path);
                if (item == null || item.IsDirectory != change.IsDirectory || !String.IsNullOrEmpty(change.OldPath) ||
                    (change.StatusCode != "AD" && change.StatusCode != "PR" && change.StatusCode != "IG") ||
                    (change.StatusCode == "AD" && !state.AddIntents.Contains(path))) throw new ArgumentException("An unreviewed structural change appeared in the restored directory.");
            }
            bool identitiesChanged = false;
            foreach (var entry in loaded.Where(value => DirectoryDecisionWithin(StructureRepositoryPath(workspace.RootPath, (string)value.Element("CurrentPath")), conflict.RepositoryPath)))
            {
                string path = StructureRepositoryPath(workspace.RootPath, (string)entry.Element("CurrentPath"));
                var item = conflict.Items.SingleOrDefault(value => value.RepositoryPath == path);
                if (item == null || !String.IsNullOrEmpty((string)entry.Element("SymlinkTarget")) || DirectoryDecisionNumber(entry, "ItemId") > 0) throw new ArgumentException("A restored path became linked or belongs to a published controlled identity.");
                var infoResult = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "fileinfo", MergeLocalPath(workspace.RootPath, path), "--fields=Status,Type,IsUnderXlink,RepSpec", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(infoResult);
                var info = SafeXml.Load(infoResult.Output).Descendants("FileInfo").Single();
                bool added = (string)info.Element("Status") == "added";
                if ((string)info.Element("IsUnderXlink") != "false" || (added ? !state.AddIntents.Contains(path) || !selected.Any(change => change.StatusCode == "AD" && SamePath(change.Path, MergeLocalPath(workspace.RootPath, path))) : (string)info.Element("Status") != "private")) throw new ArgumentException("A restored path no longer has its reviewed added/private identity.");
                if (added)
                {
                    long addedId = DirectoryDecisionNumber(entry, "ItemId"), recordedId;
                    if (addedId >= 0 || DirectoryDecisionType(entry) != item.IsDirectory || (string)info.Element("RepSpec") != workspace.Repository || (string)entry.Element("Repository") != "rep:" + workspace.Repository ||
                        (!item.IsDirectory && !StructureRegularFile(entry, workspace.Repository))) throw new ArgumentException("An added directory descendant changed type or repository.");
                    if (state.AddedIdentities.TryGetValue(path, out recordedId)) { if (recordedId != addedId) throw new ArgumentException("A restored addition was replaced by another pending identity."); }
                    else { state.AddedIdentities.Add(path, addedId); identitiesChanged = true; }
                }
            }
            if (identitiesChanged) SaveDirectoryDecisionState(state);
            foreach (var change in selected.Where(change => change.StatusCode == "AD"))
            {
                string path = StructureRepositoryPath(workspace.RootPath, change.Path);
                var entry = loaded.SingleOrDefault(value => SamePath((string)value.Element("CurrentPath"), change.Path));
                long recordedId;
                if (entry == null || !state.AddedIdentities.TryGetValue(path, out recordedId) || DirectoryDecisionNumber(entry, "ItemId") != recordedId) throw new ArgumentException("A restored pending addition no longer has its recorded native identity.");
            }
            if (complete)
            {
                DirectoryDecisionRequirePhysicalTree(workspace.RootPath, conflict.RepositoryPath, conflict.Items.Select(item => item.RepositoryPath), conflict.Items.Where(item => item.IsDirectory).Select(item => item.RepositoryPath));
                foreach (var item in conflict.Items)
                {
                    if (!selected.Any(change => change.StatusCode == "AD" && SamePath(change.Path, MergeLocalPath(workspace.RootPath, item.RepositoryPath)))) throw new IOException("Every restored file and directory must remain a new pending addition.");
                    if (!item.IsDirectory && StructureFileHash(MergeLocalPath(workspace.RootPath, item.RepositoryPath)) != state.LocalHashes[item.RepositoryPath]) throw new IOException("A restored file differs from its reviewed original local bytes.");
                }
            }
        }
        private async Task RestoreDeletedDirectoryAsync(DirectoryDecisionState state, PlasticWorkspace workspace, CancellationToken token)
        {
            string root = workspace.RootPath;
            state.Readding = true; SaveDirectoryDecisionState(state);
            var scope = DirectoryDecisionScopeSnapshot(state); string outside = DirectoryDecisionOutsideSnapshot(state);
            if (scope.Any(pair => pair.Value != "missing")) throw new IOException("The deleted directory must be absent before its original contents are restored.");
            foreach (var item in state.Session.Conflict.Items.OrderBy(item => item.IsDirectory ? 0 : 1).ThenBy(item => item.RepositoryPath.Length).ThenBy(item => item.RepositoryPath, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                string local = MergeLocalPath(root, item.RepositoryPath);
                if (item.IsDirectory) { Directory.CreateDirectory(local); scope[item.RepositoryPath] = "directory"; }
                else { File.Copy(DirectoryDecisionBackup(state, "local", item.RepositoryPath), local, false); File.SetAttributes(local, FileAttributes.Normal); scope[item.RepositoryPath] = state.LocalHashes[item.RepositoryPath]; }
                DirectoryDecisionRequireSnapshot(state, scope);
            }
            foreach (var item in state.Session.Conflict.Items.OrderBy(item => item.IsDirectory ? 0 : 1).ThenBy(item => item.RepositoryPath.Length).ThenBy(item => item.RepositoryPath, StringComparer.Ordinal))
            {
                await ValidateReaddedDirectoryAsync(state, workspace, false, token).ConfigureAwait(false);
                DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                state.AddIntents.Add(item.RepositoryPath); SaveDirectoryDecisionState(state);
                RequireSuccess(await ExecuteAsync(RevisionCommand(root, new[] { "partial", "add", MergeLocalPath(root, item.RepositoryPath) }), token).ConfigureAwait(false));
                DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
            }
            await ValidateReaddedDirectoryAsync(state, workspace, true, token).ConfigureAwait(false);
            DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
        }
        private async Task RecoverReaddedDirectoryAsync(DirectoryDecisionState state, PlasticWorkspace workspace, CancellationToken token)
        {
            await ValidateReaddedDirectoryAsync(state, workspace, false, token).ConfigureAwait(false);
            var scope = DirectoryDecisionScopeSnapshot(state); string outside = DirectoryDecisionOutsideSnapshot(state);
            string observed = Path.Combine(state.Session.RecoveryDirectory, "recovery-" + Guid.NewGuid().ToString("N")); CreatePrivateMergeDirectory(observed);
            foreach (var entry in scope.Where(pair => pair.Value != "missing" && pair.Value != "directory"))
            {
                string backup = Path.Combine(observed, MergeKey(entry.Key) + ".bin"); File.Copy(MergeLocalPath(workspace.RootPath, entry.Key), backup, false);
                if (MergeHash(backup) != entry.Value) throw new IOException("A restored file changed while its recovery backup was copied.");
                File.SetAttributes(backup, FileAttributes.ReadOnly);
            }
            foreach (var item in state.Session.Conflict.Items.OrderBy(item => item.IsDirectory ? 1 : 0).ThenByDescending(item => item.RepositoryPath.Length))
            {
                await ValidateReaddedDirectoryAsync(state, workspace, false, token).ConfigureAwait(false);
                DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                string local = MergeLocalPath(workspace.RootPath, item.RepositoryPath);
                var pending = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
                if (pending.Any(change => change.StatusCode == "AD" && SamePath(change.Path, local)))
                {
                    DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                    RequireSuccess(await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "partial", "undo", local, "--added" }), token).ConfigureAwait(false));
                    DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                }
            }
            await ValidateReaddedDirectoryAsync(state, workspace, false, token).ConfigureAwait(false);
            if ((await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false)).Any(change => change.StatusCode == "AD" && DirectoryDecisionWithin(StructureRepositoryPath(workspace.RootPath, change.Path), state.Session.Conflict.RepositoryPath))) throw new IOException("Native undo left a pending addition in the restored directory; its bytes were not removed.");
            foreach (var entry in scope.Where(pair => pair.Value != "missing" && pair.Value != "directory").ToList())
            {
                DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                string local = MergeLocalPath(workspace.RootPath, entry.Key); StructureRequireSingleLink(local); File.Delete(local); scope[entry.Key] = "missing";
            }
            foreach (string path in scope.Where(pair => pair.Value == "directory").Select(pair => pair.Key).OrderByDescending(path => path.Length).ToList())
            {
                DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
                string local = MergeLocalPath(workspace.RootPath, path);
                if (Directory.EnumerateFileSystemEntries(local).Any()) throw new IOException("A restored directory gained unreviewed contents during recovery.");
                Directory.Delete(local, false); scope[path] = "missing";
            }
            DirectoryDecisionRequireSnapshot(state, scope); DirectoryDecisionRequireOutside(state, outside);
        }
        private async Task VerifyDirectoryDecisionFinalAsync(DirectoryDecisionState state, PlasticWorkspace workspace, bool keepLocal, CancellationToken token)
        {
            var conflict = state.Session.Conflict; string root = workspace.RootPath;
            if (keepLocal && conflict.Kind == "incoming-directory-delete") { await ValidateReaddedDirectoryAsync(state, workspace, true, token).ConfigureAwait(false); return; }
            ValidateDirectoryDecisionConfiguration(state, workspace, true);
            string original = MergeLocalPath(root, conflict.RepositoryPath);
            if (File.Exists(original) || Directory.Exists(original)) throw new IOException("The original directory path remains occupied after the incoming decision.");
            var rules = DirectoryDecisionLoadRules(root); var expectedRules = state.FullWorkspace ? new string[0] : conflict.Kind == "incoming-directory-move" ? state.LoadRules : state.LoadRules.Except(state.ScopedLoadRules).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            if (state.FullWorkspace && !File.Exists(Path.Combine(root, ".plastic", "plastic.fullupdate"))) throw new IOException("The directory decision has not restored full-workspace loading.");
            if (!rules.SequenceEqual(expectedRules)) throw new IOException("The directory decision did not preserve unrelated loading rules or reach its reviewed subtree loading state.");
            var tree = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false); ValidateDirectoryDecisionIncoming(workspace, conflict, tree);
            if (state.FullWorkspace && DirectoryDecisionOutsideIdentities(workspace, conflict, tree.Items, false) != state.FullOutsideIdentityKey) throw new IOException("Unrelated incoming structure changed during the full-workspace directory operation.");
            var loaded = await DirectoryDecisionLocalTreeAsync(workspace, token).ConfigureAwait(false);
            await DirectoryDecisionValidateOutsideAsync(workspace, conflict, loaded, tree, token).ConfigureAwait(false);
            if (conflict.Kind == "incoming-directory-move")
            {
                DirectoryDecisionRequirePhysicalTree(root, conflict.IncomingPath, conflict.Items.Select(item => item.IncomingPath), conflict.Items.Where(item => item.IsDirectory).Select(item => item.IncomingPath));
                foreach (var item in conflict.Items)
                {
                    string local = MergeLocalPath(root, item.IncomingPath); var actual = loaded.SingleOrDefault(entry => SamePath((string)entry.Element("CurrentPath"), local));
                    if (actual == null || DirectoryDecisionNumber(actual, "ItemId") != item.ItemId || DirectoryDecisionType(actual) != item.IsDirectory) throw new IOException("The loaded incoming directory does not match its reviewed item identities.");
                    if (!item.IsDirectory)
                    {
                        var info = await RequireIncomingMoveIdentityAsync(workspace, local, item.ItemId, token).ConfigureAwait(false);
                        string expected = keepLocal && item.HasLocalChanges ? state.LocalHashes[item.RepositoryPath] : state.IncomingHashes[item.RepositoryPath];
                        if ((long?)info.Element("RevisionChangeset") != item.IncomingRevisionChangeset || StructureFileHash(local) != expected) throw new IOException("An incoming directory file failed revision or content verification.");
                    }
                }
            }
            var pending = await GetStatusAsync(root, token).ConfigureAwait(false);
            var selected = pending.Where(change => DirectoryDecisionPaths(conflict).Any(path => SamePath(change.Path, MergeLocalPath(root, path)) || (!String.IsNullOrEmpty(change.OldPath) && SamePath(change.OldPath, MergeLocalPath(root, path))))).ToList();
            if (selected.Any(change => !keepLocal || change.IsDirectory || !new[] { "CH", "CO" }.Contains(change.StatusCode) || !conflict.Items.Any(item => item.HasLocalChanges && SamePath(change.Path, MergeLocalPath(root, item.IncomingPath))))) throw new IOException("The resulting directory has unexpected pending structural changes.");
        }
        private void SaveDirectoryDecisionState(DirectoryDecisionState state)
        {
            var session = state.Session; var conflict = session.Conflict;
            var xml = new XElement("PartialDirectory", new XAttribute("id", session.SessionId), new XAttribute("root", session.WorkspaceRoot), new XAttribute("repository", state.Repository),
                new XAttribute("configuration", state.Configuration), new XAttribute("fullWorkspace", state.FullWorkspace), new XAttribute("fullUpdateHash", state.FullUpdateHash), new XAttribute("fullOutsideIdentityKey", state.FullOutsideIdentityKey), new XAttribute("loadNamespace", state.LoadNamespace), new XAttribute("outsideRecorded", state.OutsideSnapshotRecorded), new XAttribute("outsideSnapshot", state.OutsideSnapshot), new XAttribute("pending", state.Pending), new XAttribute("ready", session.Ready), new XAttribute("applying", session.Applying), new XAttribute("resolution", session.Resolution),
                new XAttribute("readding", state.Readding), new XElement("AddIntents", state.AddIntents.Select(path => new XElement("Path", path))),
                new XElement("AddedIdentities", state.AddedIdentities.Select(pair => new XElement("Item", new XAttribute("path", pair.Key), new XAttribute("id", pair.Value)))),
                new XElement("LoadRules", state.LoadRules.Select(line => new XElement("Rule", line))), new XElement("ScopedLoadRules", state.ScopedLoadRules.Select(line => new XElement("Rule", line))),
                new XElement("Conflict", new XAttribute("path", conflict.RepositoryPath), new XAttribute("incoming", conflict.IncomingPath), new XAttribute("kind", conflict.Kind), new XAttribute("item", conflict.ItemId), new XAttribute("head", conflict.IncomingChangeset),
                    conflict.ResolutionOptions.Select(value => new XElement("Option", value)), conflict.Items.Select(item => new XElement("Item", new XAttribute("path", item.RepositoryPath), new XAttribute("incoming", item.IncomingPath),
                        new XAttribute("directory", item.IsDirectory), new XAttribute("item", item.ItemId), new XAttribute("base", item.BaseChangeset), new XAttribute("revision", item.IncomingRevisionChangeset), new XAttribute("changed", item.HasLocalChanges),
                        item.IsDirectory ? null : new XAttribute("localHash", state.LocalHashes[item.RepositoryPath]), item.IsDirectory ? null : new XAttribute("baseHash", state.BaseHashes[item.RepositoryPath]),
                        item.IsDirectory || !state.IncomingHashes.ContainsKey(item.RepositoryPath) ? null : new XAttribute("incomingHash", state.IncomingHashes[item.RepositoryPath])))));
            string target = Path.Combine(session.RecoveryDirectory, "directory.xml"), temporary = target + ".new"; RejectReparsePath(target); RejectReparsePath(temporary);
            new XDocument(xml).Save(temporary); if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
        }
        private DirectoryDecisionState LoadDirectoryDecisionState(string root, bool required)
        {
            string index = DirectoryDecisionIndex(root); RejectReparsePath(index);
            if (!File.Exists(index)) { if (required) throw new ArgumentException("Prepare a directory decision first."); return null; }
            string target = File.ReadAllText(index); RejectReparsePath(target); var xml = SafeXml.Load(File.ReadAllText(target)).Root;
            if (xml == null || xml.Name != "PartialDirectory") throw new InvalidDataException("Invalid Partial directory session.");
            string id = (string)xml.Attribute("id"), directory = MergeSessionDirectory(id);
            if (!SamePath(target, Path.Combine(directory, "directory.xml")) || !SamePath((string)xml.Attribute("root"), root)) throw new InvalidDataException("Use the same client settings that created this directory session.");
            var element = xml.Element("Conflict");
            var conflict = new PlasticPartialDirectoryConflict { RepositoryPath = (string)element.Attribute("path"), IncomingPath = (string)element.Attribute("incoming"), Kind = (string)element.Attribute("kind"), ItemId = (long)element.Attribute("item"), IncomingChangeset = (long)element.Attribute("head"), ResolutionOptions = element.Elements("Option").Select(value => value.Value).ToList() };
            if (conflict.Kind != "incoming-directory-delete" && conflict.Kind != "incoming-directory-move") throw new InvalidDataException("Unknown directory decision kind.");
            ValidateRepositoryFilePath(conflict.RepositoryPath);
            if (conflict.Kind == "incoming-directory-move") { ValidateRepositoryFilePath(conflict.IncomingPath); if (DirectoryDecisionWithin(conflict.IncomingPath, conflict.RepositoryPath) || DirectoryDecisionWithin(conflict.RepositoryPath, conflict.IncomingPath)) throw new InvalidDataException("Overlapping directory session paths."); }
            else if (!String.IsNullOrEmpty(conflict.IncomingPath)) throw new InvalidDataException("A deleted directory session has an unexpected destination.");
            if (conflict.ResolutionOptions.Any(value => value != "take-incoming" && value != "keep-local")) throw new InvalidDataException("Invalid saved directory resolution options.");
            var state = new DirectoryDecisionState { Repository = (string)xml.Attribute("repository"), Configuration = (string)xml.Attribute("configuration"), FullWorkspace = (bool)xml.Attribute("fullWorkspace"), FullUpdateHash = (string)xml.Attribute("fullUpdateHash"), FullOutsideIdentityKey = (string)xml.Attribute("fullOutsideIdentityKey"), LoadNamespace = (string)xml.Attribute("loadNamespace") ?? "", OutsideSnapshotRecorded = (bool?)xml.Attribute("outsideRecorded") ?? false, OutsideSnapshot = (string)xml.Attribute("outsideSnapshot") ?? "", Pending = (string)xml.Attribute("pending"), LoadRules = xml.Element("LoadRules").Elements("Rule").Select(value => value.Value).ToArray(), ScopedLoadRules = xml.Element("ScopedLoadRules").Elements("Rule").Select(value => value.Value).ToArray(),
                Session = new PlasticPartialDirectorySession { SessionId = id, WorkspaceRoot = root, RecoveryDirectory = directory, Conflict = conflict, Ready = (bool)xml.Attribute("ready"), Applying = (bool)xml.Attribute("applying"), Resolution = (string)xml.Attribute("resolution") } };
            foreach (var entry in element.Elements("Item"))
            {
                var item = new PlasticPartialDirectoryItem { RepositoryPath = (string)entry.Attribute("path"), IncomingPath = (string)entry.Attribute("incoming"), IsDirectory = (bool)entry.Attribute("directory"), ItemId = (long)entry.Attribute("item"), BaseChangeset = (long)entry.Attribute("base"), IncomingRevisionChangeset = (long)entry.Attribute("revision"), HasLocalChanges = (bool)entry.Attribute("changed") };
                ValidateRepositoryFilePath(item.RepositoryPath);
                if (!DirectoryDecisionWithin(item.RepositoryPath, conflict.RepositoryPath) || item.ItemId <= 0 || item.BaseChangeset < 0 || conflict.Items.Any(existing => String.Equals(existing.RepositoryPath, item.RepositoryPath, StringComparison.OrdinalIgnoreCase) || existing.ItemId == item.ItemId)) throw new InvalidDataException("Invalid or duplicate directory session item.");
                string expectedIncoming = conflict.Kind == "incoming-directory-move" ? conflict.IncomingPath + item.RepositoryPath.Substring(conflict.RepositoryPath.Length) : "";
                if (item.IncomingPath != expectedIncoming) throw new InvalidDataException("A directory session descendant escaped the reviewed hierarchy.");
                conflict.Items.Add(item);
                if (!item.IsDirectory)
                {
                    state.LocalHashes.Add(item.RepositoryPath, (string)entry.Attribute("localHash")); state.BaseHashes.Add(item.RepositoryPath, (string)entry.Attribute("baseHash"));
                    if (conflict.Kind == "incoming-directory-move") state.IncomingHashes.Add(item.RepositoryPath, (string)entry.Attribute("incomingHash"));
                    if (StructureFileHash(DirectoryDecisionBackup(state, "local", item.RepositoryPath)) != state.LocalHashes[item.RepositoryPath] || StructureFileHash(DirectoryDecisionBackup(state, "base", item.RepositoryPath)) != state.BaseHashes[item.RepositoryPath] ||
                        (conflict.Kind == "incoming-directory-move" && StructureFileHash(DirectoryDecisionBackup(state, "incoming", item.RepositoryPath)) != state.IncomingHashes[item.RepositoryPath])) throw new InvalidDataException("A directory contributor backup was changed or removed.");
                }
            }
            state.Readding = (bool?)xml.Attribute("readding") ?? false;
            if (xml.Element("AddIntents") != null) foreach (var path in xml.Element("AddIntents").Elements("Path"))
            {
                if (!conflict.Items.Any(item => item.RepositoryPath == path.Value) || !state.AddIntents.Add(path.Value)) throw new InvalidDataException("Invalid restored-directory add intent.");
            }
            if (xml.Element("AddedIdentities") != null) foreach (var entry in xml.Element("AddedIdentities").Elements("Item"))
            {
                string path = (string)entry.Attribute("path"); long addedId = (long)entry.Attribute("id");
                if (!state.AddIntents.Contains(path) || addedId >= 0 || state.AddedIdentities.ContainsKey(path)) throw new InvalidDataException("Invalid saved restored-directory identity.");
                state.AddedIdentities.Add(path, addedId);
            }
            if ((state.Readding || state.AddIntents.Count != 0) && (conflict.Kind != "incoming-directory-delete" || state.Session.Resolution != "keep-local" || !state.Session.Applying || state.Session.Ready)) throw new InvalidDataException("Invalid restored-directory phase.");
            if (!conflict.Items.Any(item => item.RepositoryPath == conflict.RepositoryPath && item.IsDirectory && item.ItemId == conflict.ItemId) || !state.ScopedLoadRules.All(state.LoadRules.Contains)) throw new InvalidDataException("Incomplete directory session hierarchy or loading rules.");
            return state;
        }
    }
}
