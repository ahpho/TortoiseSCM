// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        // Changeset numbers are repository-wide: the previous number need not be
        // this commit's parent, particularly on branches and after merges.
        public async Task<PlasticChangesetComparison> GetChangesetParentComparisonAsync(string path,
            long changeset, string expectedRepository, CancellationToken token)
        {
            ValidateChangeset(changeset); token.ThrowIfCancellationRequested();
            var context = DiscoverWorkspace(path);
            if (context == null) throw new InvalidOperationException("工作区不存在。");
            ValidateBranchRepository(context.Repository);
            ValidateExpectedHistoricalRepository(context, expectedRepository);
            if (context.Repository.Contains("'")) throw new InvalidDataException("仓库名称无法用于历史查询。");
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            ValidateHistoricalContext(context);
            if (!SamePath(command.WorkingDirectory, context.RootPath)) throw new InvalidOperationException("工作区已改变。");
            string query = "where changesetid = " + changeset.ToString(CultureInfo.InvariantCulture) +
                " on repository '" + context.Repository + "'";
            var result = await ExecuteAsync(RevisionCommand(context.RootPath,
                new[] { "find", "changeset", query, "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(result); token.ThrowIfCancellationRequested(); ValidateHistoricalContext(context);
            var nodes = ParseGraphNodes(result.Output, context.Repository);
            if (nodes.Count != 1 || nodes[0].Changeset != changeset)
                throw new InvalidDataException("服务器未返回所选提交的唯一身份。");
            if (!nodes[0].ParentChangeset.HasValue)
                throw new InvalidOperationException("此提交没有父版本，无法比较文件更改。");
            var comparison = await GetChangesetComparisonAsync(context.RootPath, nodes[0].ParentChangeset.Value,
                changeset, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); ValidateHistoricalContext(context);
            if (comparison.Repository != context.Repository || !SamePath(comparison.RootPath, context.RootPath))
                throw new InvalidOperationException("工作区或仓库已改变。");
            return comparison;
        }
    }
}
