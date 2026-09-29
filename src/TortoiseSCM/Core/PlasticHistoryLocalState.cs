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
            ValidateHistoryLocalContext(state);
            return state;
        }

        internal async Task<bool> IsHistoryNotLoadedAsync(PlasticHistoryLocalState state, PlasticHistoryItem entry, CancellationToken token)
        {
            ValidateHistoryLocalContext(state);
            if (entry.Repository != state.Workspace.Repository) throw new InvalidOperationException("历史记录的仓库已改变。");
            if (state.LoadedChangesets != null) return !state.LoadedChangesets.Contains(entry.Changeset);
            bool missing;
            if (state.Missing.TryGetValue(entry.Changeset, out missing)) return missing;
            if (entry.Changeset == 0) return !state.Items.ContainsKey("/");
            var result = await ExecuteAsync(RevisionCommand(state.Workspace.RootPath, new[] { "diff",
                "cs:" + entry.Changeset.ToString(CultureInfo.InvariantCulture) + "@" + state.Workspace.Repository,
                "--repositorypaths", "--encoding=utf-8", "--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}|{revid}" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            missing = false;
            foreach (string line in result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();
                var fields = line.Split('|'); long revision;
                if (fields.Length != 6 || !Int64.TryParse(fields[5], out revision) || revision < 0)
                    throw new InvalidDataException("无法判断历史项是否已拉取。");
                var change = ParseChangesetFiles(String.Join("|", fields.Take(5))).Single();
                if (state.Scope != "/" && !HistoryPathMatches(change, state.Scope)) continue;
                HistoryLoadedItem atPath;
                if (change.Status != "M" && change.Status != "D" && state.Items.TryGetValue(change.Path, out atPath) &&
                    (atPath.Revision == revision || (await HistoryAncestorsAsync(state, atPath.Changeset, token).ConfigureAwait(false)).Contains(entry.Changeset)))
                    continue;
                // Follow item identity when later moves/replacements changed its path.
                long itemId = await HistoryRevisionItemAsync(state, revision, token).ConfigureAwait(false);
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

        private async Task<long> HistoryRevisionItemAsync(PlasticHistoryLocalState state, long revision, CancellationToken token)
        {
            long id;
            if (state.RevisionItems.TryGetValue(revision, out id)) return id;
            ValidateBranchRepository(state.Workspace.Repository);
            if (state.Workspace.Repository.Contains("'")) throw new InvalidDataException("无法查询此仓库的修订标识。");
            var result = await ExecuteAsync(RevisionCommand(state.Workspace.RootPath, new[] { "find", "revision",
                "where id = " + revision.ToString(CultureInfo.InvariantCulture) + " on repository '" + state.Workspace.Repository + "'",
                "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var document = SafeXml.Load(result.Output);
            var rows = document.Root == null ? new XElement[0] : document.Root.Elements("REVISION").ToArray();
            long actual;
            if (document.Root == null || document.Root.Name != "PLASTICQUERY" || rows.Length != 1 ||
                !Int64.TryParse((string)rows[0].Element("ID"), out actual) || actual != revision ||
                !Int64.TryParse((string)rows[0].Element("ITEMID"), out id) || id <= 0 ||
                (string)rows[0].Element("REPNAME") + "@" + (string)rows[0].Element("REPSERVER") != state.Workspace.Repository)
                throw new InvalidDataException("历史修订的项标识无效。");
            state.RevisionItems.Add(revision, id);
            return id;
        }

        private async Task<bool> HistoryItemRemovedAtHeadAsync(PlasticHistoryLocalState state, long id, long changeset, CancellationToken token)
        {
            if (state.HeadItems == null)
            {
                var branch = (await GetBranchesAsync(state.Workspace.RootPath, token).ConfigureAwait(false)).SingleOrDefault(item => item.IsCurrent);
                if (branch == null) return false;
                var result = await ExecuteAsync(RevisionCommand(state.Workspace.RootPath, new[] { "ls", "/",
                    "--tree=cs:" + branch.HeadChangeset.ToString(CultureInfo.InvariantCulture) + "@" + state.Workspace.Repository,
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
                state.HeadAncestors = await HistoryAncestorsAsync(state, branch.HeadChangeset, token).ConfigureAwait(false);
                state.HeadItems = ids;
            }
            // Historical changes later deleted on the selected branch need no download.
            return state.HeadAncestors.Contains(changeset) && !state.HeadItems.Contains(id);
        }

        private async Task<HashSet<long>> HistoryAncestorsAsync(PlasticHistoryLocalState state, long changeset, CancellationToken token)
        {
            HashSet<long> ancestors;
            if (state.Ancestors.TryGetValue(changeset, out ancestors)) return ancestors;
            var result = await ExecuteAsync(RevisionCommand(state.Workspace.RootPath, new[] { "log",
                "cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + state.Workspace.Repository,
                "--ancestors", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var document = SafeXml.Load(result.Output);
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
