// GPL-2.0-or-later. Read-only directory scope review before a native Partial switch.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed class PlasticPartialBranchSwitchDirectory
    {
        public string Path { get; set; }
        public string TargetPath { get; set; }
        public long ItemId { get; set; }
        public string Change { get; set; }
        public string Reason { get; set; }
    }

    public sealed class PlasticPartialBranchSwitchPreview
    {
        public string Repository { get; set; }
        public string Branch { get; set; }
        public long HeadChangeset { get; set; }
        public bool CanSwitch { get; set; }
        public int LoadedDirectoryCount { get; set; }
        public int LoadingRuleCount { get; set; }
        public bool IsFullyLoaded { get; set; }
        public IList<PlasticPartialBranchSwitchDirectory> Directories { get; set; }
        internal object ReviewEvidence { get; set; }
        public PlasticPartialBranchSwitchPreview() { Directories = new List<PlasticPartialBranchSwitchDirectory>(); }
    }

    public sealed partial class PlasticClient
    {
        private sealed class PartialSwitchReview
        {
            internal PlasticWorkspace Workspace;
            internal PlasticBranch Branch;
            internal IDictionary<string, string> Loading;
            internal string Tree, Identity;
            internal bool Safe;
        }
        private sealed class PartialSwitchItem
        {
            internal string Path;
            internal long Id;
            internal bool Directory, Supported;
        }

        public async Task<PlasticPartialBranchSwitchPreview> PreviewPartialBranchSwitchAsync(string root, string branch, CancellationToken token)
        {
            ValidateBranchName(branch);
            var command = await BuildReadCommandAsync(root, token).ConfigureAwait(false);
            if (!SamePath(command.Arguments[1], command.WorkingDirectory)) throw new ArgumentException("Partial switch preview requires the explicit workspace root.");
            root = command.WorkingDirectory;
            using (var structureGate = StructureGate(root)) using (var mergeGate = OpenMergeGate(root))
            {
                var workspace = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
                RequirePartialSwitchMapping(workspace);
                var review = new PartialSwitchReview { Workspace = workspace, Loading = CapturePartialSwitchLoading(root),
                    Tree = PartialSwitchMetadataHash(root, "plastic.wktree", true), Identity = PartialSwitchMetadataHash(root, "plastic.workspace", true) };
                await ValidateBranchSwitchCleanAsync(root, token).ConfigureAwait(false);
                review.Branch = (await GetBranchesAsync(root, token).ConfigureAwait(false)).SingleOrDefault(item => item.Name == branch);
                RequirePartialSwitchIdentity(review.Branch);
                var result = await ReadPartialSwitchPreviewAsync(workspace, review.Branch, token).ConfigureAwait(false);
                RequirePartialSwitchTarget((await GetBranchesAsync(root, token).ConfigureAwait(false)).SingleOrDefault(item => item.Name == branch), review.Branch);
                await ValidateBranchSwitchCleanAsync(root, token).ConfigureAwait(false);
                RequirePartialSwitchWorkspace(await GetWorkspaceAsync(root, token).ConfigureAwait(false), workspace);
                ValidatePartialSwitchPreflight(workspace, review.Loading, review.Tree, review.Identity);
                token.ThrowIfCancellationRequested();
                review.Safe = result.CanSwitch;
                result.ReviewEvidence = review;
                return result;
            }
        }

        // The display model is mutable for UI binding, but execution uses private
        // evidence captured by the read operation, never caller-edited rows or flags.
        public async Task<PlasticCommandResult> SwitchBranchAsync(string root, PlasticPartialBranchSwitchPreview preview, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var review = preview == null ? null : preview.ReviewEvidence as PartialSwitchReview;
            if (review == null || !review.Safe) throw new ArgumentException("A successful Partial branch switch preview is required.");
            if (!SamePath(root, review.Workspace.RootPath)) throw new ArgumentException("The preview belongs to another workspace.");
            using (var structureGate = StructureGate(root)) using (var mergeGate = OpenMergeGate(root))
            {
                ValidatePartialSwitchPreflight(review.Workspace, review.Loading, review.Tree, review.Identity);
                var workspace = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
                RequirePartialSwitchWorkspace(workspace, review.Workspace);
                ValidatePartialSwitchPreflight(review.Workspace, review.Loading, review.Tree, review.Identity);
                return await SwitchPartialBranchAsync(workspace, review.Branch.Name, token, review).ConfigureAwait(false);
            }
        }

        private static void RequirePartialSwitchMapping(PlasticWorkspace workspace)
        {
            ValidateBranchRepository(workspace.Repository);
            if (!workspace.IsPartial || Regex.Matches(workspace.Selector, @"(?m)^\s*repository\s+").Count != 1 ||
                Regex.Matches(workspace.Selector, @"(?m)^\s*path\s+").Count != 1 || !Regex.IsMatch(workspace.Selector, "(?m)^\\s*path\\s+\"/\"\\s*$"))
                throw new ArgumentException("Partial switch preview requires a single repository mapped at the Partial workspace root.");
        }

        private static void RequirePartialSwitchIdentity(PlasticBranch expected)
        {
            Guid guid;
            if (expected == null || expected.BranchId <= 0 || !System.Guid.TryParse(expected.Guid, out guid) || guid == System.Guid.Empty || expected.HeadChangeset < 0)
                throw new ArgumentException("Partial branch switch requires the target's native ID, GUID and head. Refresh the branch list.");
        }

        private async Task<PlasticPartialBranchSwitchPreview> ReadPartialSwitchPreviewAsync(PlasticWorkspace workspace, PlasticBranch branch, CancellationToken token)
        {
            string root = workspace.RootPath;
            var localResult = await ExecuteAsync(RevisionCommand(root, new[] { "ls", root, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(localResult);
            var loaded = ParsePartialSwitchTree(localResult.Output, workspace, true);
            var remoteResult = await ExecuteAsync(RevisionCommand(root, new[] { "ls", "/", "--tree=cs:" + branch.HeadChangeset.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(remoteResult);
            var target = ParsePartialSwitchTree(remoteResult.Output, workspace, false);
            string[] rules = File.ReadAllLines(Path.Combine(root, ".plastic", "plastic.fullycheckeddirectories"));
            var scopes = new List<string>();
            var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ruleIds = new HashSet<long>();
            foreach (string rule in rules)
            {
                string[] parts = rule.Split(':'); Guid namespaceId; long id;
                if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out namespaceId) || namespaceId == Guid.Empty ||
                    !Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0 || !ruleIds.Add(id))
                    throw new InvalidDataException("Invalid or duplicated Partial directory loading rule.");
                namespaces.Add(namespaceId.ToString("D"));
                var directory = loaded.SingleOrDefault(item => item.Id == id);
                if (directory == null || !directory.Directory || !directory.Supported)
                    throw new InvalidDataException("A loading rule cannot be matched to a regular loaded directory in this repository.");
                scopes.Add(directory.Path);
            }
            if (namespaces.Count > 1) throw new InvalidDataException("Partial loading rules use multiple identity namespaces.");
            bool full = File.Exists(Path.Combine(root, ".plastic", "plastic.fullupdate"));
            if (full) scopes.Add("/");
            var result = new PlasticPartialBranchSwitchPreview { Repository = workspace.Repository, Branch = branch.Name, HeadChangeset = branch.HeadChangeset,
                LoadedDirectoryCount = loaded.Count(item => item.Directory), LoadingRuleCount = rules.Length, IsFullyLoaded = full };
            foreach (var directory in loaded.Where(item => item.Directory))
            {
                var incoming = target.SingleOrDefault(item => item.Id == directory.Id);
                var atPath = target.SingleOrDefault(item => String.Equals(item.Path, directory.Path, StringComparison.OrdinalIgnoreCase));
                string change = "Unchanged", reason = "Loaded directory identity and path are retained.";
                if (!directory.Supported || incoming != null && (!incoming.Supported || !incoming.Directory))
                { change = "Unsupported"; reason = "The loaded or target item is linked, cross-repository or an unsupported directory type."; }
                else if (atPath != null && atPath.Id != directory.Id)
                { change = "Replaced"; reason = "The loaded path is occupied by another item at the target revision."; }
                else if (incoming == null)
                { change = "Deleted"; reason = "The loaded directory is absent at the target revision."; }
                else if (incoming.Path != directory.Path)
                { change = "Moved"; reason = "The loaded directory moves to a different path at the target revision."; }
                result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = directory.Path, TargetPath = incoming == null ? "" : incoming.Path,
                    ItemId = directory.Id, Change = change, Reason = reason });
            }
            foreach (var incoming in target.Where(item => item.Directory || !item.Supported))
            {
                if (loaded.Any(item => item.Id == incoming.Id) || !scopes.Any(scope => DirectoryDecisionWithin(incoming.Path, scope))) continue;
                result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = incoming.Path, TargetPath = incoming.Path, ItemId = incoming.Id,
                    Change = incoming.Supported ? "Added" : "Unsupported", Reason = "A new directory or unsupported item enters a fully loaded scope and may change its loading configuration." });
            }
            foreach (var file in loaded.Where(item => !item.Directory))
            {
                var counterparts = target.Where(item => item.Id == file.Id || String.Equals(item.Path, file.Path, StringComparison.OrdinalIgnoreCase));
                if (counterparts.Any(item => !item.Supported || item.Directory))
                {
                    result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = file.Path, TargetPath = file.Path, ItemId = file.Id,
                        Change = "Unsupported", Reason = "A loaded file becomes linked or is replaced by a directory at the target revision." });
                    continue;
                }
                var incoming = target.SingleOrDefault(item => item.Id == file.Id);
                if (incoming == null) continue;
                // Even a single selectively loaded file can cause native switch to
                // materialize its new parent. Every target ancestor must therefore
                // already be loaded at the same path with the same directory ID.
                string parent = incoming.Path;
                while (parent != "/")
                {
                    parent = parent.Substring(0, parent.LastIndexOf('/')); if (parent.Length == 0) parent = "/";
                    var targetParent = target.Single(item => item.Path == parent);
                    if (loaded.Any(item => item.Path == parent && item.Directory && item.Supported && item.Id == targetParent.Id)) continue;
                    result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = file.Path, TargetPath = incoming.Path, ItemId = file.Id,
                        Change = "Unsupported", Reason = "A loaded file would enter a directory that is not loaded at the same path and identity: " + parent });
                    break;
                }
            }
            result.Directories = result.Directories.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
            result.CanSwitch = result.Directories.All(item => item.Change == "Unchanged") && loaded.All(item => item.Supported);
            if (loaded.Any(item => !item.Directory && !item.Supported))
                foreach (var item in loaded.Where(item => !item.Directory && !item.Supported))
                    result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = item.Path, TargetPath = "", ItemId = item.Id, Change = "Unsupported", Reason = "The loaded tree contains an unsupported or linked item." });
            return result;
        }

        private static IList<PartialSwitchItem> ParsePartialSwitchTree(string xml, PlasticWorkspace workspace, bool local)
        {
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "LsResults" || document.Root.Elements().Count() != 1 || document.Root.Element("LsItems") == null ||
                document.Root.Element("LsItems").Elements().Any(item => item.Name != "LsItem")) throw new InvalidDataException("Unexpected Partial directory listing.");
            var result = new List<PartialSwitchItem>();
            var ids = new HashSet<long>(); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in document.Root.Element("LsItems").Elements("LsItem"))
            {
                string raw = RepositoryListingField(element, "CurrentPath");
                if (local && (!Path.IsPathRooted(raw) || Path.GetFullPath(raw).TrimEnd('\\') != raw.TrimEnd('\\')))
                    throw new InvalidDataException("Loaded directory listing has a noncanonical path.");
                string path = local ? StructureRepositoryPath(workspace.RootPath, raw) : raw;
                ValidateRepositoryDirectoryPath(path);
                long id = RepositoryListingNumber(element, "ItemId");
                if (id <= 0 || !ids.Add(id) || !paths.Add(path)) throw new InvalidDataException("Duplicate or invalid Partial item identity or path.");
                string type = RepositoryListingField(element, "Type"), repository = RepositoryListingField(element, "Repository");
                bool directory = new[] { "dir", "directory", "目录" }.Contains(type, StringComparer.OrdinalIgnoreCase);
                bool file = new[] { "txt", "bin", "text", "binary", "file", "Text file", "Binary file", "文本文件", "二进制文件" }.Contains(type, StringComparer.OrdinalIgnoreCase);
                bool supported = (directory || file) && String.IsNullOrEmpty(RepositoryListingField(element, "SymlinkTarget")) && (repository == "rep:" + workspace.Repository || repository == workspace.Repository);
                if (local) RejectReparsePath(raw);
                result.Add(new PartialSwitchItem { Id = id, Path = path, Directory = directory, Supported = supported });
            }
            if (!result.Any(item => item.Path == "/" && item.Directory && item.Supported)) throw new InvalidDataException("The Partial directory listing has no regular repository root.");
            foreach (var item in result.Where(item => item.Path != "/"))
            {
                string parent = item.Path.Substring(0, item.Path.LastIndexOf('/')); if (parent.Length == 0) parent = "/";
                if (!result.Any(candidate => candidate.Path == parent && candidate.Directory)) throw new InvalidDataException("The Partial directory listing has an incomplete hierarchy.");
            }
            return result;
        }
    }
}
