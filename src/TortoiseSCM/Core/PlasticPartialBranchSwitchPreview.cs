// GPL-2.0-or-later. Read-only directory scope review before a native Partial switch.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

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
            internal PartialSwitchItem Parent;
        }
        private sealed class PartialSwitchTree
        {
            internal readonly List<PartialSwitchItem> Items = new List<PartialSwitchItem>();
            internal readonly Dictionary<long, PartialSwitchItem> ById = new Dictionary<long, PartialSwitchItem>();
            internal readonly Dictionary<string, PartialSwitchItem> ByPath = new Dictionary<string, PartialSwitchItem>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, PartialSwitchItem> ByExactPath = new Dictionary<string, PartialSwitchItem>(StringComparer.Ordinal);
        }
        private sealed class PartialSwitchTextReader : TextReader
        {
            private readonly TextReader input;
            private readonly CancellationToken token;
            internal PartialSwitchTextReader(TextReader input, CancellationToken token) { this.input = input; this.token = token; }
            public override int Peek() { token.ThrowIfCancellationRequested(); return input.Peek(); }
            public override int Read() { token.ThrowIfCancellationRequested(); return input.Read(); }
            public override int Read(char[] buffer, int index, int count)
            { token.ThrowIfCancellationRequested(); return input.Read(buffer, index, Math.Min(count, 4096)); }
        }
        private sealed class PartialSwitchPathComparer : IComparer<string>
        {
            private readonly CancellationToken token;
            internal PartialSwitchPathComparer(CancellationToken token) { this.token = token; }
            public int Compare(string left, string right)
            { token.ThrowIfCancellationRequested(); return StringComparer.OrdinalIgnoreCase.Compare(left, right); }
        }

        public async Task<PlasticPartialBranchSwitchPreview> PreviewPartialBranchSwitchAsync(string root, string branch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
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
            var loaded = ParsePartialSwitchTree(localResult.Output, workspace, true, token);
            var remoteResult = await ExecuteAsync(RevisionCommand(root, new[] { "ls", "/", "--tree=cs:" + branch.HeadChangeset.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(remoteResult);
            var target = ParsePartialSwitchTree(remoteResult.Output, workspace, false, token);
            var rules = new List<string>();
            using (var reader = new StreamReader(Path.Combine(root, ".plastic", "plastic.fullycheckeddirectories")))
            {
                string rule;
                while ((rule = reader.ReadLine()) != null) { token.ThrowIfCancellationRequested(); rules.Add(rule); }
            }
            token.ThrowIfCancellationRequested();
            return BuildPartialSwitchPreview(workspace, branch, loaded, target, rules.ToArray(), File.Exists(Path.Combine(root, ".plastic", "plastic.fullupdate")), token);
        }

        private static PlasticPartialBranchSwitchPreview BuildPartialSwitchPreview(PlasticWorkspace workspace, PlasticBranch branch,
            PartialSwitchTree loaded, PartialSwitchTree target, string[] rules, bool full, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var scopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ruleIds = new HashSet<long>();
            foreach (string rule in rules)
            {
                token.ThrowIfCancellationRequested();
                string[] parts = rule.Split(':'); Guid namespaceId; long id;
                if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "D", out namespaceId) || namespaceId == Guid.Empty ||
                    !Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0 || !ruleIds.Add(id))
                    throw new InvalidDataException("Invalid or duplicated Partial directory loading rule.");
                namespaces.Add(namespaceId.ToString("D"));
                PartialSwitchItem directory;
                if (!loaded.ById.TryGetValue(id, out directory) || !directory.Directory || !directory.Supported)
                    throw new InvalidDataException("A loading rule cannot be matched to a regular loaded directory in this repository.");
                scopes.Add(directory.Path);
            }
            if (namespaces.Count > 1) throw new InvalidDataException("Partial loading rules use multiple identity namespaces.");
            if (full) scopes.Add("/");
            // Parent links were validated using ordinal paths. Scope membership is
            // deliberately case-insensitive, while retained ancestor identity is
            // deliberately case-sensitive (a case-only move still blocks switch).
            var inScope = new Dictionary<PartialSwitchItem, bool>();
            var unsafeAncestor = new Dictionary<PartialSwitchItem, string>();
            var pending = new Stack<PartialSwitchItem>();
            foreach (var item in target.Items)
            {
                token.ThrowIfCancellationRequested();
                var current = item;
                while (current != null && !inScope.ContainsKey(current))
                { token.ThrowIfCancellationRequested(); pending.Push(current); current = current.Parent; }
                while (pending.Count != 0)
                {
                    token.ThrowIfCancellationRequested();
                    current = pending.Pop();
                    inScope.Add(current, scopes.Contains(current.Path) || current.Parent != null && inScope[current.Parent]);
                    PartialSwitchItem original;
                    bool retained = loaded.ByExactPath.TryGetValue(current.Path, out original) && original.Directory && original.Supported && original.Id == current.Id;
                    unsafeAncestor.Add(current, !retained ? current.Path : current.Parent == null ? null : unsafeAncestor[current.Parent]);
                }
            }
            var result = new PlasticPartialBranchSwitchPreview { Repository = workspace.Repository, Branch = branch.Name, HeadChangeset = branch.HeadChangeset,
                LoadingRuleCount = rules.Length, IsFullyLoaded = full };
            bool supportedLoadedTree = true;
            foreach (var directory in loaded.Items)
            {
                token.ThrowIfCancellationRequested();
                supportedLoadedTree &= directory.Supported;
                if (!directory.Directory) continue;
                result.LoadedDirectoryCount++;
                PartialSwitchItem incoming, atPath;
                target.ById.TryGetValue(directory.Id, out incoming);
                target.ByPath.TryGetValue(directory.Path, out atPath);
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
            foreach (var incoming in target.Items)
            {
                token.ThrowIfCancellationRequested();
                if ((!incoming.Directory && incoming.Supported) || loaded.ById.ContainsKey(incoming.Id) || !inScope[incoming]) continue;
                result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = incoming.Path, TargetPath = incoming.Path, ItemId = incoming.Id,
                    Change = incoming.Supported ? "Added" : "Unsupported", Reason = "A new directory or unsupported item enters a fully loaded scope and may change its loading configuration." });
            }
            foreach (var file in loaded.Items)
            {
                token.ThrowIfCancellationRequested();
                if (file.Directory) continue;
                PartialSwitchItem incoming, atPath;
                target.ById.TryGetValue(file.Id, out incoming);
                target.ByPath.TryGetValue(file.Path, out atPath);
                if (incoming != null && (!incoming.Supported || incoming.Directory) || atPath != null && (!atPath.Supported || atPath.Directory))
                {
                    result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = file.Path, TargetPath = file.Path, ItemId = file.Id,
                        Change = "Unsupported", Reason = "A loaded file becomes linked or is replaced by a directory at the target revision." });
                    continue;
                }
                if (incoming == null) continue;
                // Even a single selectively loaded file can cause native switch to
                // materialize its new parent. Every target ancestor must therefore
                // already be loaded at the same path with the same directory ID.
                string parent = incoming.Parent == null ? null : unsafeAncestor[incoming.Parent];
                if (parent != null)
                {
                    result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = file.Path, TargetPath = incoming.Path, ItemId = file.Id,
                        Change = "Unsupported", Reason = "A loaded file would enter a directory that is not loaded at the same path and identity: " + parent });
                }
            }
            token.ThrowIfCancellationRequested();
            try { result.Directories = result.Directories.OrderBy(item => item.Path, new PartialSwitchPathComparer(token)).ToList(); }
            catch (InvalidOperationException) { token.ThrowIfCancellationRequested(); throw; }
            result.CanSwitch = supportedLoadedTree;
            foreach (var row in result.Directories)
            { token.ThrowIfCancellationRequested(); result.CanSwitch &= row.Change == "Unchanged"; }
            foreach (var item in loaded.Items)
            {
                token.ThrowIfCancellationRequested();
                if (!item.Directory && !item.Supported)
                    result.Directories.Add(new PlasticPartialBranchSwitchDirectory { Path = item.Path, TargetPath = "", ItemId = item.Id, Change = "Unsupported", Reason = "The loaded tree contains an unsupported or linked item." });
            }
            token.ThrowIfCancellationRequested();
            return result;
        }

        private static PartialSwitchTree ParsePartialSwitchTree(string xml, PlasticWorkspace workspace, bool local, CancellationToken token)
        {
            using (var input = new StringReader(xml)) return ParsePartialSwitchTreeFromReader(input, workspace, local, token);
        }

        private static PartialSwitchTree ParsePartialSwitchTreeFromReader(TextReader input, PlasticWorkspace workspace, bool local, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            XDocument document;
            // Keep the shared SafeXml security settings, but bound each text read
            // so cancellation can interrupt XML construction before item matching.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using (var text = new PartialSwitchTextReader(input, token))
            using (var reader = XmlReader.Create(text, settings)) document = XDocument.Load(reader);
            token.ThrowIfCancellationRequested();
            if (document.Root == null || document.Root.Name != "LsResults" || document.Root.Elements().Take(2).Count() != 1 || document.Root.Element("LsItems") == null)
                throw new InvalidDataException("Unexpected Partial directory listing.");
            var result = new PartialSwitchTree();
            foreach (var element in document.Root.Element("LsItems").Elements())
            {
                token.ThrowIfCancellationRequested();
                if (element.Name != "LsItem") throw new InvalidDataException("Unexpected Partial directory listing.");
                string raw = RepositoryListingField(element, "CurrentPath");
                if (local && (!Path.IsPathRooted(raw) || Path.GetFullPath(raw).TrimEnd('\\') != raw.TrimEnd('\\')))
                    throw new InvalidDataException("Loaded directory listing has a noncanonical path.");
                string path = local ? StructureRepositoryPath(workspace.RootPath, raw) : raw;
                ValidateRepositoryDirectoryPath(path);
                long id = RepositoryListingNumber(element, "ItemId");
                if (id <= 0 || result.ById.ContainsKey(id) || result.ByPath.ContainsKey(path)) throw new InvalidDataException("Duplicate or invalid Partial item identity or path.");
                string type = RepositoryListingField(element, "Type"), repository = RepositoryListingField(element, "Repository");
                bool directory = new[] { "dir", "directory", "目录" }.Contains(type, StringComparer.OrdinalIgnoreCase);
                bool file = new[] { "txt", "bin", "text", "binary", "file", "Text file", "Binary file", "文本文件", "二进制文件" }.Contains(type, StringComparer.OrdinalIgnoreCase);
                bool supported = (directory || file) && String.IsNullOrEmpty(RepositoryListingField(element, "SymlinkTarget")) && (repository == "rep:" + workspace.Repository || repository == workspace.Repository);
                if (local) RejectReparsePath(raw);
                var item = new PartialSwitchItem { Id = id, Path = path, Directory = directory, Supported = supported };
                result.Items.Add(item); result.ById.Add(id, item); result.ByPath.Add(path, item); result.ByExactPath.Add(path, item);
            }
            PartialSwitchItem root;
            if (!result.ByExactPath.TryGetValue("/", out root) || !root.Directory || !root.Supported) throw new InvalidDataException("The Partial directory listing has no regular repository root.");
            foreach (var item in result.Items)
            {
                token.ThrowIfCancellationRequested();
                if (item.Path == "/") continue;
                string parent = item.Path.Substring(0, item.Path.LastIndexOf('/')); if (parent.Length == 0) parent = "/";
                PartialSwitchItem parentItem;
                if (!result.ByExactPath.TryGetValue(parent, out parentItem) || !parentItem.Directory) throw new InvalidDataException("The Partial directory listing has an incomplete hierarchy.");
                item.Parent = parentItem;
            }
            token.ThrowIfCancellationRequested();
            return result;
        }
    }
}
