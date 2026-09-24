// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
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
    public sealed class PlasticChangesetFile
    {
        public string Status { get; set; }
        public string Path { get; set; }
        public string OldPath { get; set; }
        public string ItemType { get; set; }
    }

    public sealed class PlasticChangesetDetails
    {
        public PlasticHistoryItem Changeset { get; set; }
        public IList<PlasticChangesetFile> Files { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public async Task<PlasticChangesetDetails> GetChangesetAsync(string path, long changeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(changeset);
            var validated = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            var metadata = await FindChangesetsAsync(validated.WorkingDirectory, changeset, cancellationToken).ConfigureAwait(false);
            if (metadata.Count != 1) throw new ArgumentException("The requested changeset does not exist in this repository.");
            return new PlasticChangesetDetails { Changeset = metadata[0],
                Files = await ChangesetFilesAsync(validated.WorkingDirectory, changeset, cancellationToken).ConfigureAwait(false) };
        }

        private async Task<IList<PlasticHistoryItem>> GetDirectoryHistoryAsync(string path, string root, CancellationToken cancellationToken)
        {
            var changesets = await FindChangesetsAsync(root, null, cancellationToken).ConfigureAwait(false);
            if (SamePath(path, root)) return changesets;
            string scope = "/" + path.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
            var result = new List<PlasticHistoryItem>();
            // Directory revisions alone omit content-only edits to descendants. Inspect each
            // changeset's paths, including deleted/moved items absent from today's workspace.
            foreach (var item in changesets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var files = await ChangesetFilesAsync(root, item.Changeset, cancellationToken).ConfigureAwait(false);
                if (files.Any(f => InRepositoryScope(f.Path, scope) || InRepositoryScope(f.OldPath, scope)))
                { item.Path = path; result.Add(item); }
            }
            return result;
        }

        private async Task<IList<PlasticHistoryItem>> FindChangesetsAsync(string root, long? changeset, CancellationToken cancellationToken)
        {
            var args = new List<string> { "find", "changeset" };
            if (changeset.HasValue) args.Add("where changesetid = " + changeset.Value.ToString(CultureInfo.InvariantCulture));
            args.Add("--xml"); args.Add("--encoding=utf-8"); args.Add("--nototal");
            var result = await ExecuteAsync(RevisionCommand(root, args), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            return ParseChangesets(result.Output, root);
        }

        internal static IList<PlasticHistoryItem> ParseChangesets(string xml, string root)
        {
            XDocument document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "PLASTICQUERY") throw new InvalidDataException("Unexpected changeset query XML.");
            var result = new List<PlasticHistoryItem>();
            foreach (var element in document.Root.Elements("CHANGESET"))
            {
                long number;
                if (!Int64.TryParse((string)element.Element("CHANGESETID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) || number < 0)
                    throw new InvalidDataException("Invalid changeset number in query response.");
                result.Add(new PlasticHistoryItem { Path = root, RevisionSpec = "cs:" + number.ToString(CultureInfo.InvariantCulture), Changeset = number,
                    CreationDate = (string)element.Element("DATE") ?? "", Owner = (string)element.Element("OWNER") ?? "",
                    Branch = (string)element.Element("BRANCH") ?? "", Comment = (string)element.Element("COMMENT") ?? "",
                    Repository = (string)element.Element("REPOSITORY") ?? "" });
            }
            return result.OrderByDescending(x => x.Changeset).ToList();
        }

        private async Task<IList<PlasticChangesetFile>> ChangesetFilesAsync(string root, long changeset, CancellationToken cancellationToken)
        {
            if (changeset == 0) return new List<PlasticChangesetFile>();
            var args = new List<string> { "diff", "cs:" + changeset.ToString(CultureInfo.InvariantCulture), "--repositorypaths",
                "--encoding=utf-8", "--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}" };
            var result = await ExecuteAsync(RevisionCommand(root, args), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            return ParseChangesetFiles(result.Output);
        }

        internal static IList<PlasticChangesetFile> ParseChangesetFiles(string output)
        {
            var result = new List<PlasticChangesetFile>();
            foreach (string line in output.Split(new [] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split('|');
                if (fields.Length != 5 || !new [] { "A", "C", "D", "M" }.Contains(fields[0]))
                    throw new InvalidDataException("Unexpected changeset file output: " + line);
                string path = fields[1].Trim('"'), oldPath = fields[3].Trim('"'), destination = fields[4].Trim('"');
                if (fields[0] == "M" && !String.IsNullOrEmpty(destination)) path = destination;
                result.Add(new PlasticChangesetFile { Status = fields[0], Path = path.Replace('\\', '/'),
                    OldPath = oldPath.Replace('\\', '/'), ItemType = fields[2] });
            }
            return result;
        }

        // Revert restores tracked content as pending changes. It never rewrites server history.
        public async Task<PlasticCommandResult> RollbackAsync(string path, long changeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(changeset);
            var validated = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            string absolute = validated.Arguments[1], root = validated.WorkingDirectory;
            if (SamePath(absolute, root)) return await RollbackWorkspaceAsync(root, changeset, cancellationToken).ConfigureAwait(false);
            await ValidateCleanRevisionOperationAsync(root, absolute, cancellationToken).ConfigureAwait(false);
            await GetChangesetAsync(root, changeset, cancellationToken).ConfigureAwait(false);
            var workspace = await GetWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            if (workspace.IsPartial && !File.Exists(absolute) && !Directory.Exists(absolute))
                throw new ArgumentException("部分工作区只能回滚已加载的文件或目录；请先加载目标项，或在完整工作区恢复删除的目录结构。");
            if (workspace.IsPartial && Directory.Exists(absolute))
            {
                string repositoryPath = "/" + absolute.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
                var before = await ListRevisionIdentitiesAsync(root, absolute, null, cancellationToken).ConfigureAwait(false);
                var target = await ListRevisionIdentitiesAsync(root, repositoryPath, changeset, cancellationToken).ConfigureAwait(false);
                if (!before.SetEquals(target)) throw new ArgumentException("部分工作区目录回滚不支持增删、移动或未加载项；请在完整工作区恢复目录结构。未执行回滚。");
            }
            var result = await ExecuteAsync(RevisionCommand(root, new [] { "revert", absolute + "#cs:" + changeset.ToString(CultureInfo.InvariantCulture) }), cancellationToken).ConfigureAwait(false);
            if (result.Succeeded) result.Output += Environment.NewLine + "Historical content restored as pending changes; check in to publish.";
            return result;
        }

        private async Task<HashSet<string>> ListRevisionIdentitiesAsync(string root, string path, long? changeset, CancellationToken cancellationToken)
        {
            var args = new List<string> { "ls", path, "--recursive", "--xml", "--encoding=utf-8" };
            if (changeset.HasValue) args.Add("--tree=cs:" + changeset.Value.ToString(CultureInfo.InvariantCulture));
            var result = await ExecuteAsync(RevisionCommand(root, args), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            var document = SafeXml.Load(result.Output);
            if (document.Root == null || document.Root.Name != "LsResults") throw new InvalidDataException("Unexpected tree listing response.");
            var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in document.Descendants("LsItem"))
            {
                string itemPath = (string)item.Element("WkPath"), itemId = (string)item.Element("ItemId");
                if (String.IsNullOrEmpty(itemPath) || String.IsNullOrEmpty(itemId)) throw new InvalidDataException("Incomplete tree identity.");
                identities.Add(itemPath.Replace('\\', '/').TrimStart('/') + "|" + itemId);
            }
            return identities;
        }

        // Standard uses a changeset selector. Partial preserves its loaded scope and working
        // branch, loading that scope at the requested changeset using the native partial update.
        public async Task<PlasticCommandResult> SwitchAsync(string root, long changeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(changeset);
            var validated = await BuildReadCommandAsync(root, cancellationToken).ConfigureAwait(false);
            if (!SamePath(validated.Arguments[1], validated.WorkingDirectory)) throw new ArgumentException("切换历史快照必须显式选择工作区根目录。");
            root = validated.WorkingDirectory;
            var workspace = await GetWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            await ValidateCleanRevisionOperationAsync(root, root, cancellationToken).ConfigureAwait(false);
            await GetChangesetAsync(root, changeset, cancellationToken).ConfigureAwait(false);
            IList<string> args = workspace.IsPartial
                ? new List<string> { "partial", "update", root, "--changeset=" + changeset.ToString(CultureInfo.InvariantCulture), "--dontmerge", "--report" }
                : new List<string> { "switch", "cs:" + changeset.ToString(CultureInfo.InvariantCulture), "--workspace=" + root };
            var result = await ExecuteAsync(RevisionCommand(root, args), cancellationToken).ConfigureAwait(false);
            if (result.Succeeded && workspace.IsPartial) result.Output += Environment.NewLine + "Loaded partial workspace scope at the requested changeset; working branch and load configuration retained.";
            return result;
        }

        private async Task ValidateCleanRevisionOperationAsync(string root, string scope, CancellationToken cancellationToken)
        {
            await Task.Run(() => { RejectReparsePath(scope); if (Directory.Exists(scope)) RejectUnsafeDescendants(scope, root, cancellationToken); }, cancellationToken).ConfigureAwait(false);
            var pending = await GetStatusAsync(root, cancellationToken).ConfigureAwait(false);
            string normalizedScope = scope.TrimEnd('\\', '/');
            bool inside = pending.Any(item => IsWithinScope(item.Path, normalizedScope) || IsWithinScope(item.OldPath, normalizedScope));
            if (inside) throw new ArgumentException("选定范围存在待定更改或私有文件；为避免覆盖，请先签入、撤销或移出这些文件。未执行回滚或切换。");
        }

        internal static bool IsWithinScope(string path, string scope)
        {
            if (String.IsNullOrEmpty(path)) return false;
            return SamePath(path, scope) || path.StartsWith(scope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private PlasticProcessCommand RevisionCommand(string root, IEnumerable<string> args)
        { return new PlasticProcessCommand { FileName = config.CmPath, WorkingDirectory = root, Arguments = args.ToList() }; }
        private static void ValidateChangeset(long changeset)
        { if (changeset < 0) throw new ArgumentOutOfRangeException("changeset", "Changeset must be nonnegative."); }
        private static bool SamePath(string left, string right)
        { return left.TrimEnd('\\', '/').Equals(right.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase); }
        private static bool InRepositoryScope(string path, string scope)
        { return path.Equals(scope, StringComparison.OrdinalIgnoreCase) || path.StartsWith(scope + "/", StringComparison.OrdinalIgnoreCase); }
    }
}
