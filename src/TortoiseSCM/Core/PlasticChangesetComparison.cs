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
    public sealed class PlasticChangesetComparison
    {
        public string Repository { get; set; }
        public string RootPath { get; set; }
        public long FromChangeset { get; set; }
        public long ToChangeset { get; set; }
        public IList<PlasticChangesetFile> Files { get; set; }
    }

    public sealed partial class PlasticClient
    {
        // Compare the two complete trees, not the union of intervening commits.
        // Structural directory changes retain cm's directory rows; unchanged children
        // are not invented as individual changes. No workspace update is performed.
        public async Task<PlasticChangesetComparison> GetChangesetComparisonAsync(string workspacePath, long fromChangeset, long toChangeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(fromChangeset); ValidateChangeset(toChangeset);
            var command = await BuildReadCommandAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            var context = DiscoverWorkspace(command.WorkingDirectory);
            if (String.IsNullOrWhiteSpace(context.Repository)) throw new InvalidDataException("Workspace selector does not identify a repository.");
            string repository = context.Repository;
            string output = await HistoricalReadAsync(context.RootPath, repository, new[] { "diff",
                "cs:" + fromChangeset.ToString(CultureInfo.InvariantCulture) + "@" + repository,
                "cs:" + toChangeset.ToString(CultureInfo.InvariantCulture) + "@" + repository,
                "--repositorypaths", "--encoding=utf-8", "--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}" }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var files = ParseChangesetComparisonFiles(output);
            ValidateHistoryRepository(context.RootPath, repository);
            return new PlasticChangesetComparison { Repository = repository, RootPath = context.RootPath,
                FromChangeset = fromChangeset, ToChangeset = toChangeset, Files = files };
        }

        internal static IList<PlasticChangesetFile> ParseChangesetComparisonFiles(string output)
        {
            var result = new List<PlasticChangesetFile>();
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split('|');
                if (fields.Length != 5 || !new[] { "A", "C", "D", "M" }.Contains(fields[0]) ||
                    !new[] { "D", "F", "B", "S", "X" }.Contains(fields[2]))
                    throw new InvalidDataException("Unexpected changeset comparison output. No incomplete result was accepted.");
                string path = ComparisonPath(fields[1], false, fields[0] == "C" && fields[2] == "D");
                string source = ComparisonPath(fields[3], true);
                string destination = ComparisonPath(fields[4], true);
                if (fields[0] == "M")
                {
                    if (String.IsNullOrEmpty(source) || String.IsNullOrEmpty(destination))
                        throw new InvalidDataException("A moved item did not identify both historical paths.");
                    path = destination;
                }
                else if (source.Length != 0 || destination.Length != 0)
                    throw new InvalidDataException("Unexpected move paths on a non-moved comparison row.");
                // Multiple statuses for one item (for example moved and changed) are
                // meaningful and must not be collapsed by path.
                result.Add(new PlasticChangesetFile { Status = fields[0], Path = path, OldPath = source, ItemType = fields[2] });
            }
            foreach (var changed in result.Where(item => item.Status == "C"))
            {
                var moves = result.Where(item => item.Status == "M" &&
                    (item.Path == changed.Path || (item.ItemType == "D" && changed.Path.StartsWith(item.Path + "/", StringComparison.Ordinal))))
                    .OrderByDescending(item => item.Path.Length).ToList();
                if (moves.Count > 0)
                {
                    var moved = moves[0];
                    if (moves.Any(item => item.Path == moved.Path && item.OldPath != moved.OldPath))
                        throw new InvalidDataException("Ambiguous historical source path for a moved item.");
                    changed.OldPath = moved.OldPath + changed.Path.Substring(moved.Path.Length);
                }
            }
            return result;
        }

        private static string ComparisonPath(string value, bool allowEmpty, bool allowRoot = false)
        {
            if (value.StartsWith("\"", StringComparison.Ordinal) && value.EndsWith("\"", StringComparison.Ordinal) && value.Length >= 2)
                value = value.Substring(1, value.Length - 2);
            if (allowEmpty && value.Length == 0) return value;
            value = value.Replace('\\', '/');
            if (allowRoot && value == "/") return value;
            try { ValidateRepositoryFilePath(value); }
            catch (ArgumentException error) { throw new InvalidDataException("The changeset comparison contained an unsafe or ambiguous repository path.", error); }
            return value;
        }
    }
}
