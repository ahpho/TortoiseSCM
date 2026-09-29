// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed class PlasticHistoryPage
    {
        public string Repository { get; set; }
        public string Scope { get; set; }
        public string Branch { get; set; }
        public IList<PlasticHistoryItem> Items { get; set; }
        public int ScannedChangesets { get; set; }
        public bool HasMore { get; set; }
        public long? NextBeforeChangeset { get; set; }
        public string FallbackReason { get; set; }
    }

    public sealed partial class PlasticClient
    {
        private readonly object historyCacheGate = new object();
        private readonly Dictionary<string, IList<PlasticChangesetFile>> historyFilesCache = new Dictionary<string, IList<PlasticChangesetFile>>(StringComparer.Ordinal);
        private readonly Queue<string> historyCacheOrder = new Queue<string>();
        private int historyCacheRows;

        // This is changeset publication history for a repository path, not revision
        // creation history: publishing an old revision through rollback still appears.
        // Each request scans at most scanLimit changesets, even when none match the path.
        // Cursor pagination is stable when newer commits arrive; refresh starts at head.
        public Task<PlasticHistoryPage> GetHistoryPageAsync(string path, long? beforeChangeset, int scanLimit, CancellationToken cancellationToken)
        { return GetHistoryPageAsync(path, null, beforeChangeset, scanLimit, cancellationToken); }

        public async Task<PlasticHistoryPage> GetHistoryPageAsync(string path, string branch, long? beforeChangeset, int scanLimit, CancellationToken cancellationToken)
        {
            if (scanLimit < 1 || scanLimit > 100) throw new ArgumentOutOfRangeException("scanLimit", "Scan between 1 and 100 changesets per page.");
            if (beforeChangeset.HasValue && beforeChangeset.Value < 0) throw new ArgumentOutOfRangeException("beforeChangeset");
            if (branch != null) ValidateBranchName(branch);
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = DiscoverWorkspace(path);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            ValidateBranchRepository(workspace.Repository);
            if (workspace.Repository.Contains("'")) throw new InvalidDataException("The repository cannot safely be represented in a history query.");
            var validated = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            string root = validated.WorkingDirectory;
            string absolute = validated.Arguments[1];
            if (!SamePath(root, workspace.RootPath)) throw new InvalidOperationException("The workspace root changed while loading history.");
            ValidateHistoryRepository(root, workspace.Repository);
            if (branch != null)
            {
                if (!(await GetBranchesAsync(root, cancellationToken).ConfigureAwait(false)).Any(item => item.Name == branch))
                    throw new ArgumentException("The selected history branch no longer exists. Refresh the branch list.");
                ValidateHistoryRepository(root, workspace.Repository);
            }
            string scope = SamePath(absolute, root) ? "/" : "/" + absolute.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
            var page = new PlasticHistoryPage { Repository = workspace.Repository, Scope = scope, Branch = branch, Items = new List<PlasticHistoryItem>() };
            if (beforeChangeset == 0) return page;
            string query = (beforeChangeset.HasValue ? "where changesetid < " + beforeChangeset.Value.ToString(CultureInfo.InvariantCulture) + " " : "") +
                "order by changesetid desc limit " + (scanLimit + 1).ToString(CultureInfo.InvariantCulture) + " on repository '" + workspace.Repository + "'";
            var response = await ExecuteAsync(RevisionCommand(root, new[] { "find", "changeset", query, "--xml", "--encoding=utf-8", "--nototal" }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(response);
            ValidateHistoryRepository(root, workspace.Repository);
            var candidates = ParseChangesets(response.Output, root);
            if (candidates.Count > scanLimit + 1 || candidates.Select(item => item.Changeset).Distinct().Count() != candidates.Count ||
                (beforeChangeset.HasValue && candidates.Any(item => item.Changeset >= beforeChangeset.Value)))
                throw new InvalidDataException("The server returned an invalid history page. No incomplete result was accepted.");
            var scanned = candidates.Take(scanLimit).ToList();
            Dictionary<long, IList<PlasticChangesetFile>> batch = null;
            if (scope != "/" && scanned.Any(item => item.Changeset > 0 && (branch == null || item.Branch == branch)))
            {
                // One interval includes all branches, including commits publishing an old
                // revision. Never use the item's RevNo as the publication changeset ID.
                var log = await ExecuteAsync(RevisionCommand(root, new[] { "log", "cs:" + scanned[0].Changeset.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository,
                    "--from=cs:" + Math.Max(0, scanned[scanned.Count - 1].Changeset - 1).ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository,
                    "--allbranches", "--xml", "--repositorypaths", "--encoding=utf-8" }), cancellationToken).ConfigureAwait(false);
                ValidateHistoryRepository(root, workspace.Repository);
                cancellationToken.ThrowIfCancellationRequested();
                if (log.Succeeded)
                {
                    try { batch = ParseHistoryLog(log.Output, scanned, cancellationToken); }
                    catch (InvalidDataException error) { page.FallbackReason = error.Message; }
                    catch (XmlException) { page.FallbackReason = "The batch history XML could not be read."; }
                }
                else page.FallbackReason = "The client could not read batch history; using individual changeset queries.";
            }
            foreach (var item in scanned)
            {
                cancellationToken.ThrowIfCancellationRequested();
                page.ScannedChangesets++;
                // Keep the global scan/cursor bounded even when this branch is sparse.
                // Branch names never enter a find expression, including quoted names.
                if (branch != null && !String.Equals(item.Branch, branch, StringComparison.Ordinal)) continue;
                bool matches = scope == "/";
                if (!matches)
                {
                    IList<PlasticChangesetFile> files;
                    if (item.Changeset == 0) files = new List<PlasticChangesetFile>();
                    else if (batch == null || !batch.TryGetValue(item.Changeset, out files))
                        files = await HistoryFilesCachedAsync(root, workspace.Repository, item.Changeset, cancellationToken).ConfigureAwait(false);
                    matches = files.Any(file => HistoryPathMatches(file, scope));
                    // log XML omits file/directory type. Only a possible ancestor needs
                    // the precise diff record; unrelated and descendant paths need none.
                    if (!matches && batch != null && files.Any(file => HistoryLogMayBeAncestor(file, scope)))
                    {
                        files = await HistoryFilesCachedAsync(root, workspace.Repository, item.Changeset, cancellationToken).ConfigureAwait(false);
                        matches = files.Any(file => HistoryPathMatches(file, scope));
                    }
                }
                if (matches) { item.Path = absolute; item.Repository = workspace.Repository; page.Items.Add(item); }
            }
            ValidateHistoryRepository(root, workspace.Repository);
            page.HasMore = candidates.Count > scanLimit;
            if (page.HasMore) page.NextBeforeChangeset = candidates[scanLimit - 1].Changeset;
            return page;
        }

        private static bool HistoryLogMayBeAncestor(PlasticChangesetFile file, string scope)
        {
            return (!String.IsNullOrEmpty(file.Path) && InRepositoryScope(scope, file.Path)) ||
                (!String.IsNullOrEmpty(file.OldPath) && InRepositoryScope(scope, file.OldPath));
        }

        internal static Dictionary<long, IList<PlasticChangesetFile>> ParseHistoryLog(string xml, IList<PlasticHistoryItem> candidates, CancellationToken cancellationToken)
        {
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "LogList" || document.Root.Elements().Any(item => item.Name != "Changeset"))
                throw new InvalidDataException("Unexpected batch history XML; using individual changeset queries.");
            var result = new Dictionary<long, IList<PlasticChangesetFile>>();
            var expected = candidates.Where(item => item.Changeset > 0).ToDictionary(item => item.Changeset);
            foreach (var changeset in document.Root.Elements("Changeset"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long id;
                if (!Int64.TryParse(HistoryLogValue(changeset, "ChangesetId"), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 0 || result.ContainsKey(id))
                    throw new InvalidDataException("Invalid or repeated batch changeset; using individual changeset queries.");
                PlasticHistoryItem candidate;
                if (!expected.TryGetValue(id, out candidate) || !String.Equals(HistoryLogValue(changeset, "Branch"), candidate.Branch, StringComparison.Ordinal))
                    throw new InvalidDataException("Batch history did not match the requested changesets and branches; using individual changeset queries.");
                var changes = changeset.Element("Changes");
                if (changes == null || changeset.Elements("Changes").Count() != 1 || changes.Elements().Any(item => item.Name != "Item"))
                    throw new InvalidDataException("Incomplete batch changes; using individual changeset queries.");
                var files = new List<PlasticChangesetFile>();
                foreach (var item in changes.Elements("Item"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string type = HistoryLogValue(item, "Type"), source = HistoryLogValue(item, "SrcCmPath"), destination = HistoryLogValue(item, "DstCmPath");
                    if (!new[] { "Added", "Changed", "Deleted", "Moved" }.Contains(type) || !ValidHistoryLogPath(source) || !ValidHistoryLogPath(destination))
                        throw new InvalidDataException("Incomplete batch change paths; using individual changeset queries.");
                    files.Add(new PlasticChangesetFile { Status = type.Substring(0, 1), Path = destination.Replace('\\', '/'), OldPath = source.Replace('\\', '/'), ItemType = "" });
                }
                result.Add(id, files);
            }
            if (expected.Keys.Any(id => !result.ContainsKey(id)))
                throw new InvalidDataException("Batch history omitted requested changesets; using individual changeset queries.");
            return result;
        }

        private static string HistoryLogValue(XElement element, string name)
        {
            var fields = element.Elements(name).ToList();
            if (fields.Count != 1 || fields[0].HasElements)
                throw new InvalidDataException("Incomplete or ambiguous batch fields; using individual changeset queries.");
            return fields[0].Value;
        }

        private static bool ValidHistoryLogPath(string path)
        {
            if (String.IsNullOrEmpty(path) || !path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return false;
            return !path.Split('/', '\\').Any(part => part == "." || part == "..");
        }

        private async Task<IList<PlasticChangesetFile>> HistoryFilesCachedAsync(string root, string repository, long changeset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = repository + "\n" + changeset.ToString(CultureInfo.InvariantCulture);
            IList<PlasticChangesetFile> files;
            lock (historyCacheGate) if (historyFilesCache.TryGetValue(key, out files)) return files;
            ValidateBranchRepository(repository);
            if (changeset == 0) files = new List<PlasticChangesetFile>();
            else
            {
                files = ParseChangesetFiles(await HistoryDiffAsync(root, repository, changeset, cancellationToken).ConfigureAwait(false));
            }
            ValidateHistoryRepository(root, repository);
            cancellationToken.ThrowIfCancellationRequested();
            // Cache only complete successful immutable diffs. Both row and entry counts
            // are bounded; large commits remain usable without permanently retaining them.
            if (files.Count <= 5000)
            {
                lock (historyCacheGate)
                {
                    if (historyFilesCache.ContainsKey(key)) return historyFilesCache[key];
                    while (historyFilesCache.Count >= 128 || historyCacheRows + files.Count > 20000)
                    {
                        string oldest = historyCacheOrder.Dequeue();
                        historyCacheRows -= historyFilesCache[oldest].Count;
                        historyFilesCache.Remove(oldest);
                    }
                    historyFilesCache.Add(key, files); historyCacheOrder.Enqueue(key); historyCacheRows += files.Count;
                }
            }
            return files;
        }

        private void ValidateHistoryRepository(string root, string repository)
        {
            var workspace = DiscoverWorkspace(root);
            if (workspace == null || workspace.Repository != repository)
                throw new InvalidOperationException("The workspace repository changed during history loading. Refresh the history view.");
        }

        internal static bool HistoryPathMatches(PlasticChangesetFile file, string scope)
        {
            if (InRepositoryScope(file.Path, scope) || InRepositoryScope(file.OldPath, scope)) return true;
            // Moving/deleting an ancestor directory affects the selected descendant,
            // even when cm diff emits only the directory's own path record.
            bool directory = String.Equals(file.ItemType, "D", StringComparison.OrdinalIgnoreCase) || String.Equals(file.ItemType, "dir", StringComparison.OrdinalIgnoreCase);
            return directory && ((!String.IsNullOrEmpty(file.Path) && InRepositoryScope(scope, file.Path)) ||
                (!String.IsNullOrEmpty(file.OldPath) && InRepositoryScope(scope, file.OldPath)));
        }
    }
}
