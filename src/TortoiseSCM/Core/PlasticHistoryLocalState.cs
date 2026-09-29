// TortoiseSCM - GPL-2.0-or-later.
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
    // A fresh snapshot for each refresh, never a persistent "last seen" watermark.
    internal sealed class PlasticHistoryLocalState
    {
        internal PlasticWorkspace Workspace;
        internal string Scope;
        internal HashSet<long> LoadedChangesets;
        internal readonly Dictionary<string, HistoryLoadedItem> Items = new Dictionary<string, HistoryLoadedItem>(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<long, HashSet<long>> Ancestors = new Dictionary<long, HashSet<long>>();
        internal readonly Dictionary<long, bool> Missing = new Dictionary<long, bool>();
        internal readonly Dictionary<long, HistoryLoadedItem> ItemsById = new Dictionary<long, HistoryLoadedItem>();
        internal readonly Dictionary<long, long> RevisionItems = new Dictionary<long, long>();
        internal HashSet<long> HeadItems;
        internal HashSet<long> HeadAncestors;
        internal long HeadChangeset;
    }

    internal sealed class HistoryLoadedItem
    {
        internal long Revision;
        internal long Changeset;
        internal string Path;
    }

    public sealed partial class PlasticClient
    {
        internal async Task<PlasticHistoryLocalState> GetHistoryLocalStateAsync(string path, CancellationToken token)
        {
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            var workspace = DiscoverWorkspace(command.WorkingDirectory);
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath,
                new[] { "status", workspace.RootPath, "--header", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var document = SafeXml.Load(result.Output);
            string configuredName = document.Root == null ? "" : (string)document.Root.Element("WkConfigName");
            var status = document.Root == null ? null : document.Root.Element("WorkspaceStatus");
            status = status == null ? null : status.Element("Status");
            long changeset;
            if (status == null || !Int64.TryParse((string)status.Element("Changeset"), out changeset) || changeset < -1)
                throw new InvalidDataException("无法读取本地已加载版本。");
            var repository = status.Element("RepSpec");
            if (repository == null || (string)repository.Element("Name") + "@" + (string)repository.Element("Server") != workspace.Repository)
                throw new InvalidDataException("本地版本的仓库与历史记录不一致。");
            string absolute = command.Arguments[1];
            var state = new PlasticHistoryLocalState { Workspace = workspace,
                Scope = SamePath(absolute, workspace.RootPath) ? "/" : "/" + absolute.Substring(workspace.RootPath.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/') };
            if (changeset >= 0)
                state.LoadedChangesets = await HistoryAncestorsAsync(state, changeset, token).ConfigureAwait(false);
            else
            {
                // Gluon has no workspace changeset. Keep each item's actual base;
                // a newer file must not make a different, stale file look updated.
                result = await ExecuteAsync(RevisionCommand(workspace.RootPath,
                    new[] { "ls", workspace.RootPath, "--recursive", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
                RequireSuccess(result);
                document = SafeXml.Load(result.Output);
                if (document.Root == null || document.Root.Name != "LsResults") throw new InvalidDataException("无法读取本地加载项。");
                foreach (var item in document.Descendants("LsItem"))
                {
                    token.ThrowIfCancellationRequested();
                    long revision, loaded, id;
                    if (!Int64.TryParse((string)item.Element("RevId"), out revision) || revision < 0) continue;
                    if (!Int64.TryParse((string)item.Element("Changeset"), out loaded) || loaded < 0)
                        throw new InvalidDataException("本地加载项没有有效版本。");
                    if (!Int64.TryParse((string)item.Element("ItemId"), out id) || id <= 0)
                        throw new InvalidDataException("本地加载项没有有效标识。");
                    string itemRepository = (string)item.Element("Repository");
                    if (itemRepository != workspace.Repository && itemRepository != "rep:" + workspace.Repository) continue;
                    if (!String.IsNullOrEmpty((string)item.Element("SymlinkTarget"))) continue;
                    string local = (string)item.Element("CurrentPath");
                    if (String.IsNullOrEmpty(local) || !Path.IsPathRooted(local) || !IsWithin(local, workspace.RootPath))
                        throw new InvalidDataException("本地加载项路径不属于当前工作区。");
                    string relative = SamePath(local, workspace.RootPath) ? "/" : "/" + local.Substring(workspace.RootPath.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
                    var loadedItem = new HistoryLoadedItem { Revision = revision, Changeset = loaded, Path = relative };
                    state.Items.Add(relative, loadedItem); state.ItemsById.Add(id, loadedItem);
                    state.RevisionItems[revision] = id;
                }
            }
            // Only commits reachable from the selected update target can be
            // pending downloads. Unmerged sibling branches remain in Show Log,
            // but neither need downloading nor expensive per-item diff queries.
            var configured = System.Text.RegularExpressions.Regex.Matches(workspace.Selector, @"(?m)^\s*(?:smartbranch|branch|br)\s+""([^""]+)""\s*$");
            string configuredBranch = configured.Count == 1 ? configured[0].Groups[1].Value : "";
            result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "find", "branch", "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var branch = ParseBranches(result.Output, workspace.Repository).SingleOrDefault(item =>
                item.Name == configuredBranch && configuredName == item.Name + "@" + workspace.Repository);
            if (branch != null)
            {
                state.HeadChangeset = branch.HeadChangeset;
                state.HeadAncestors = await HistoryAncestorsAsync(state, branch.HeadChangeset, token).ConfigureAwait(false);
            }
            else if (state.LoadedChangesets != null)
            {
                // A changeset/label-pinned workspace has no advancing branch target.
                state.HeadChangeset = changeset;
                state.HeadAncestors = state.LoadedChangesets;
            }
            else throw new InvalidDataException("无法确定当前工作区的更新目标。");
            ValidateHistoryLocalContext(state);
            return state;
        }

        internal async Task<bool> IsHistoryNotLoadedAsync(PlasticHistoryLocalState state, PlasticHistoryItem entry, CancellationToken token)
        {
            ValidateHistoryLocalContext(state);
            bool missing = await EvaluateHistoryRowAsync(state, entry, token).ConfigureAwait(false);
            ValidateHistoryLocalContext(state);
            return missing;
        }

        internal async Task<Dictionary<long, bool>> GetHistoryNotLoadedAsync(PlasticHistoryLocalState state, IList<PlasticHistoryItem> entries, CancellationToken token)
        {
            ValidateHistoryLocalContext(state);
            var rows = new Dictionary<long, bool>();
            foreach (var entry in entries)
                rows[entry.Changeset] = await EvaluateHistoryRowAsync(state, entry, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateHistoryLocalContext(state);
            return rows;
        }

        private async Task<bool> EvaluateHistoryRowAsync(PlasticHistoryLocalState state, PlasticHistoryItem entry, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Repository != state.Workspace.Repository) throw new InvalidOperationException("历史记录的仓库已改变。");
            if (!state.HeadAncestors.Contains(entry.Changeset)) return false;
            if (state.LoadedChangesets != null) return !state.LoadedChangesets.Contains(entry.Changeset);
            bool missing;
            if (state.Missing.TryGetValue(entry.Changeset, out missing)) return missing;
            if (entry.Changeset == 0) return !state.Items.ContainsKey("/");
            var changes = (await HistoryFilesCachedAsync(state.Workspace.RootPath, state.Workspace.Repository,
                entry.Changeset, token).ConfigureAwait(false)).Where(change => state.Scope == "/" || HistoryPathMatches(change, state.Scope)).ToList();
            if (changes.Any(change => !change.Revision.HasValue)) throw new InvalidDataException("历史明细缺少修订标识，无法判断本地版本。");
            // Resolve identities in bounded batches, instead of starting cm once for
            // every moved/deleted/unloaded file in a commit.
            await PrepareHistoryRevisionItemsAsync(state, changes, entry.Changeset, token).ConfigureAwait(false);
            missing = false;
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                long revision = change.Revision.Value;
                // A partial update may load the directory itself before all of its
                // children. Its revision alone cannot prove a move/delete is applied.
                string oldDirectory = change.Status == "M" ? change.OldPath : change.Path;
                if (change.ItemType == "D" && (change.Status == "M" || change.Status == "D"))
                    foreach (var child in state.Items.Values.Where(local => local.Path.StartsWith(oldDirectory.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase) &&
                        (state.Scope == "/" || InRepositoryScope(local.Path, state.Scope) ||
                            (change.Status == "M" && InRepositoryScope(change.Path + local.Path.Substring(oldDirectory.Length), state.Scope)))))
                        if (!(await HistoryAncestorsAsync(state, child.Changeset, token).ConfigureAwait(false)).Contains(entry.Changeset))
                        { missing = true; break; }
                if (missing) break;
                HistoryLoadedItem atPath;
                if (change.Status != "M" && change.Status != "D" && state.Items.TryGetValue(change.Path, out atPath) &&
                    (atPath.Revision == revision || (await HistoryAncestorsAsync(state, atPath.Changeset, token).ConfigureAwait(false)).Contains(entry.Changeset)))
                    continue;
                // Follow item identity when later moves/replacements changed its path.
                long itemId = state.RevisionItems[revision];
                HistoryLoadedItem identity;
                state.ItemsById.TryGetValue(itemId, out identity);
                if (change.Status == "D")
                {
                    // A deletion is already reflected when its old revision is absent.
                    if (identity != null && (identity.Revision == revision ||
                        !(await HistoryAncestorsAsync(state, identity.Changeset, token).ConfigureAwait(false)).Contains(entry.Changeset))) missing = true;
                }
                else if (identity == null)
                    missing = !await HistoryItemRemovedAtHeadAsync(state, itemId, entry.Changeset, token).ConfigureAwait(false);
                else if (change.Status == "M" && identity.Path != change.Path)
                    missing = !(await HistoryAncestorsAsync(state, identity.Changeset, token).ConfigureAwait(false)).Contains(entry.Changeset);
                else if (identity.Revision != revision &&
                    !(await HistoryAncestorsAsync(state, identity.Changeset, token).ConfigureAwait(false)).Contains(entry.Changeset)) missing = true;
                if (missing) break;
            }
            ValidateHistoryLocalContext(state);
            state.Missing.Add(entry.Changeset, missing);
            return missing;
        }

        private async Task PrepareHistoryRevisionItemsAsync(PlasticHistoryLocalState state, IList<PlasticChangesetFile> changes, long preparingChangeset, CancellationToken token)
        {
            var needed = new List<long>();
            foreach (var change in changes)
            {
                long revision = change.Revision.Value;
                if (state.RevisionItems.ContainsKey(revision)) continue;
                HistoryLoadedItem local;
                if (change.Status != "M" && change.Status != "D" && state.Items.TryGetValue(change.Path, out local) &&
                    (await HistoryAncestorsAsync(state, local.Changeset, token).ConfigureAwait(false)).Contains(
                        // The caller checks publication ancestry, which is not the
                        // revision's creation changeset (rollback may reuse revisions).
                        preparingChangeset)) continue;
                needed.Add(revision);
            }
            var unknown = needed.Distinct().ToArray();
            ValidateBranchRepository(state.Workspace.Repository);
            if (state.Workspace.Repository.Contains("'")) throw new InvalidDataException("无法查询此仓库的修订标识。");
            for (int offset = 0; offset < unknown.Length; offset += 128)
            {
                var batch = new HashSet<long>(unknown.Skip(offset).Take(128));
                string query = "where " + String.Join(" or ", batch.Select(id => "id = " + id.ToString(CultureInfo.InvariantCulture))) +
                    " on repository '" + state.Workspace.Repository + "'";
                string xml = await HistoricalReadAsync(state.Workspace.RootPath, state.Workspace.Repository,
                    new[] { "find", "revision", query, "--xml", "--encoding=utf-8", "--nototal" }, token).ConfigureAwait(false);
                var document = SafeXml.Load(xml);
                if (document.Root == null || document.Root.Name != "PLASTICQUERY") throw new InvalidDataException("历史修订标识响应无效。");
                var found = new Dictionary<long, long>();
                foreach (var row in document.Root.Elements("REVISION"))
                {
                    long revision, id;
                    if (!Int64.TryParse((string)row.Element("ID"), out revision) || !batch.Contains(revision) || found.ContainsKey(revision) ||
                        !Int64.TryParse((string)row.Element("ITEMID"), out id) || id <= 0 ||
                        (string)row.Element("REPNAME") + "@" + (string)row.Element("REPSERVER") != state.Workspace.Repository)
                        throw new InvalidDataException("历史修订的项标识无效。");
                    found.Add(revision, id);
                }
                if (found.Count != batch.Count) throw new InvalidDataException("历史修订标识响应不完整。");
                ValidateHistoryLocalContext(state);
                foreach (var pair in found) state.RevisionItems[pair.Key] = pair.Value;
            }
        }

        private async Task<bool> HistoryItemRemovedAtHeadAsync(PlasticHistoryLocalState state, long id, long changeset, CancellationToken token)
        {
            if (state.HeadItems == null)
            {
                var result = await ExecuteAsync(RevisionCommand(state.Workspace.RootPath, new[] { "ls", "/",
                    "--tree=cs:" + state.HeadChangeset.ToString(CultureInfo.InvariantCulture) + "@" + state.Workspace.Repository,
                    "--recursive", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
                RequireSuccess(result);
                var document = SafeXml.Load(result.Output);
                if (document.Root == null || document.Root.Name != "LsResults" || document.Root.Element("LsItems") == null)
                    throw new InvalidDataException("无法检查历史项的后续删除。");
                var ids = new HashSet<long>();
                foreach (var item in document.Descendants("LsItem"))
                {
                    long number;
                    if (!Int64.TryParse((string)item.Element("ItemId"), out number) || number <= 0)
                        throw new InvalidDataException("分支目录项标识无效。");
                    ids.Add(number);
                }
                state.HeadItems = ids;
            }
            // Historical changes later deleted on the selected branch need no download.
            return state.HeadAncestors.Contains(changeset) && !state.HeadItems.Contains(id);
        }

        private async Task<HashSet<long>> HistoryAncestorsAsync(PlasticHistoryLocalState state, long changeset, CancellationToken token)
        {
            HashSet<long> ancestors;
            if (state.Ancestors.TryGetValue(changeset, out ancestors)) return ancestors;
            string xml = await HistoricalReadAsync(state.Workspace.RootPath, state.Workspace.Repository, new[] { "log",
                "cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + state.Workspace.Repository,
                "--ancestors", "--xml", "--encoding=utf-8" }, token).ConfigureAwait(false);
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "LogList") throw new InvalidDataException("无法读取本地版本的祖先记录。");
            ancestors = new HashSet<long>();
            foreach (var item in document.Root.Elements("Changeset"))
            {
                long number;
                if (!Int64.TryParse((string)item.Element("ChangesetId"), out number) || number < 0 || !ancestors.Add(number))
                    throw new InvalidDataException("本地版本的祖先记录无效。");
            }
            if (!ancestors.Contains(changeset)) throw new InvalidDataException("本地版本的祖先记录不完整。");
            ValidateHistoryLocalContext(state);
            state.Ancestors.Add(changeset, ancestors);
            return ancestors;
        }

        private void ValidateHistoryLocalContext(PlasticHistoryLocalState state)
        {
            var current = DiscoverWorkspace(state.Workspace.RootPath);
            if (current == null || current.Repository != state.Workspace.Repository || current.Selector != state.Workspace.Selector)
                throw new InvalidOperationException("工作区已切换，请刷新本地拉取状态。");
        }
    }
}
