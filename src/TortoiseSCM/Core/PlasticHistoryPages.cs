// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed class PlasticHistoryPage
    {
        public string Repository { get; set; }
        public string Scope { get; set; }
        public IList<PlasticHistoryItem> Items { get; set; }
        public int ScannedChangesets { get; set; }
        public bool HasMore { get; set; }
        public long? NextBeforeChangeset { get; set; }
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
        public async Task<PlasticHistoryPage> GetHistoryPageAsync(string path, long? beforeChangeset, int scanLimit, CancellationToken cancellationToken)
        {
            if (scanLimit < 1 || scanLimit > 100) throw new ArgumentOutOfRangeException("scanLimit", "Scan between 1 and 100 changesets per page.");
            if (beforeChangeset.HasValue && beforeChangeset.Value < 0) throw new ArgumentOutOfRangeException("beforeChangeset");
            var validated = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            string root = validated.WorkingDirectory;
            string absolute = validated.Arguments[1];
            var workspace = DiscoverWorkspace(root);
            if (String.IsNullOrWhiteSpace(workspace.Repository)) throw new InvalidDataException("Workspace selector does not identify a repository.");
            string scope = SamePath(absolute, root) ? "/" : "/" + absolute.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
            var page = new PlasticHistoryPage { Repository = workspace.Repository, Scope = scope, Items = new List<PlasticHistoryItem>() };
            if (beforeChangeset == 0) return page;
            string query = (beforeChangeset.HasValue ? "where changesetid < " + beforeChangeset.Value.ToString(CultureInfo.InvariantCulture) + " " : "") +
                "order by changesetid desc limit " + (scanLimit + 1).ToString(CultureInfo.InvariantCulture);
            var response = await ExecuteAsync(RevisionCommand(root, new[] { "find", "changeset", query, "--xml", "--encoding=utf-8", "--nototal" }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(response);
            var candidates = ParseChangesets(response.Output, root);
            if (candidates.Count > scanLimit + 1 || candidates.Select(item => item.Changeset).Distinct().Count() != candidates.Count ||
                (beforeChangeset.HasValue && candidates.Any(item => item.Changeset >= beforeChangeset.Value)))
                throw new InvalidDataException("The server returned an invalid history page. No incomplete result was accepted.");
            foreach (var item in candidates.Take(scanLimit))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool matches = scope == "/";
                if (!matches)
                {
                    var files = await HistoryFilesCachedAsync(root, workspace.Repository, item.Changeset, cancellationToken).ConfigureAwait(false);
                    matches = files.Any(file => HistoryPathMatches(file, scope));
                }
                page.ScannedChangesets++;
                if (matches) { item.Path = absolute; item.Repository = workspace.Repository; page.Items.Add(item); }
            }
            ValidateHistoryRepository(root, workspace.Repository);
            page.HasMore = candidates.Count > scanLimit;
            if (page.HasMore) page.NextBeforeChangeset = candidates[scanLimit - 1].Changeset;
            return page;
        }

        private async Task<IList<PlasticChangesetFile>> HistoryFilesCachedAsync(string root, string repository, long changeset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string key = repository + "\n" + changeset.ToString(CultureInfo.InvariantCulture);
            IList<PlasticChangesetFile> files;
            lock (historyCacheGate) if (historyFilesCache.TryGetValue(key, out files)) return files;
            files = await ChangesetFilesAsync(root, changeset, cancellationToken).ConfigureAwait(false);
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
