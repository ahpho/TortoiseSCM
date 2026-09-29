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
            string output = await HistoricalReadAsync(context.RootPath, context.Repository,
                new[] { "find", "changeset", query, "--xml", "--encoding=utf-8", "--nototal" }, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); ValidateHistoricalContext(context);
            var nodes = ParseGraphNodes(output, context.Repository);
            if (nodes.Count != 1 || nodes[0].Changeset != changeset)
                throw new InvalidDataException("服务器未返回所选提交的唯一身份。");
            if (!nodes[0].ParentChangeset.HasValue)
                throw new InvalidOperationException("此提交没有父版本，无法比较文件更改。");
            // A one-spec cm diff is exactly parent -> changeset. Reuse the data
            // already read for details/bold, but retain comparison path normalization
            // (including files changed under a moved directory).
            string diff = await HistoryDiffAsync(context.RootPath, context.Repository, changeset, token).ConfigureAwait(false);
            ParseChangesetFiles(diff); // Validate the complete record before removing revision IDs.
            string comparisonOutput = String.Join("\n", diff.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => String.Join("|", line.Split('|').Take(5))));
            var files = ParseChangesetComparisonFiles(comparisonOutput);
            var comparison = new PlasticChangesetComparison { RootPath = context.RootPath, Repository = context.Repository,
                FromChangeset = nodes[0].ParentChangeset.Value, ToChangeset = changeset, Files = files };
            RememberParentComparison(context.Repository, comparison.FromChangeset, changeset, comparisonOutput);
            token.ThrowIfCancellationRequested(); ValidateHistoricalContext(context);
            if (comparison.Repository != context.Repository || !SamePath(comparison.RootPath, context.RootPath))
                throw new InvalidOperationException("工作区或仓库已改变。");
            return comparison;
        }
    }
}
