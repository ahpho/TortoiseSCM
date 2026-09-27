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
    /// <summary>Options for the read-only annotate/blame operation.</summary>
    public sealed class PlasticBlameOptions
    {
        /// <summary>
        /// Native comparison mode: none, eol, whitespaces or eol&amp;whitespaces.
        /// A null or empty value uses Plastic's default (none).
        /// </summary>
        public string Ignore { get; set; }
    }

    /// <summary>A single source line and the revision which last changed it.</summary>
    public sealed class PlasticBlameLine
    {
        public int Line { get; set; }
        public string Owner { get; set; }
        public long Changeset { get; set; }
        public string Date { get; set; }
        public string Branch { get; set; }
        public string Content { get; set; }
        public string Revision { get; set; }
        public string Comment { get; set; }
        public bool IsMergeRevision { get; set; }
        public string Repository { get; set; }
    }

    public sealed partial class PlasticClient
    {
        /// <summary>Annotates one existing controlled file without changing the workspace.</summary>
        public Task<IList<PlasticBlameLine>> GetBlameAsync(string path, CancellationToken cancellationToken)
        { return GetBlameAsync(path, null, cancellationToken); }

        public async Task<IList<PlasticBlameLine>> GetBlameAsync(string path, PlasticBlameOptions options,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options = options ?? new PlasticBlameOptions();
            ValidateBlameIgnore(options.Ignore);

            PlasticProcessCommand validated = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            string absolute = validated.Arguments[1];
            if (Directory.Exists(absolute))
                throw new ArgumentException("Annotate requires one explicit file; directories are not supported.", "path");
            if (!File.Exists(absolute))
                throw new ArgumentException("Annotate requires an existing controlled file: " + absolute, "path");

            PlasticWorkspace workspace = DiscoverWorkspace(validated.WorkingDirectory);
            if (workspace == null || String.IsNullOrWhiteSpace(workspace.Repository))
                throw new InvalidDataException("Workspace selector does not identify a repository.");

            var arguments = new List<string> {
                "annotate", absolute,
                "--format={line}\t{owner}\t{changeset}\t{date}\t{branch}\t{rev}\t{ismergerev}\t{rep}\t{comment}\t{content}",
                "--encoding=utf-8", "--dateformat=o"
            };
            if (!String.IsNullOrWhiteSpace(options.Ignore)) arguments.Add("--ignore=" + options.Ignore);
            PlasticCommandResult result = await ExecuteAsync(RevisionCommand(workspace.RootPath, arguments), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            cancellationToken.ThrowIfCancellationRequested();
            PlasticWorkspace current = DiscoverWorkspace(workspace.RootPath);
            if (current == null || !String.Equals(current.Repository, workspace.Repository, StringComparison.Ordinal))
                throw new InvalidOperationException("The workspace repository changed during annotate loading. Refresh the blame view.");
            return ParseBlame(result.Output, workspace.Repository);
        }

        /// <summary>
        /// Parses the tabular native output. The first nine fields are parsed from
        /// the left and content is the final field, so tabs and pipe characters in
        /// source text remain part of the content. Native output has no escaping for
        /// multiline comments; such output is rejected instead of being misattributed.
        /// </summary>
        internal static IList<PlasticBlameLine> ParseBlame(string output, string repository)
        {
            if (output == null) throw new ArgumentNullException("output");
            if (String.IsNullOrWhiteSpace(repository)) throw new ArgumentException("A repository is required.", "repository");
            var result = new List<PlasticBlameLine>();
            using (var reader = new StringReader(output))
            {
                string raw;
                int expectedLine = 1;
                bool first = true;
                while ((raw = reader.ReadLine()) != null)
                {
                    if (first && raw.Length > 0 && raw[0] == '\uFEFF') raw = raw.Substring(1);
                    first = false;
                    // An empty file has no rows; an empty source line still has all
                    // metadata fields and ends with a tab, so it is never skipped.
                    if (raw.Length == 0) throw new InvalidDataException("Unexpected empty Plastic annotate row.");
                    string[] fields = raw.Split(new[] { '\t' }, 10, StringSplitOptions.None);
                    if (fields.Length != 10) throw new InvalidDataException("Malformed Plastic annotate row: expected ten tab-separated fields.");

                    int line;
                    long changeset;
                    if (!Int32.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out line) || line != expectedLine)
                        throw new InvalidDataException("Plastic annotate returned a non-sequential line number.");
                    if (!Int64.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out changeset) || changeset < 0)
                        throw new InvalidDataException("Plastic annotate returned an invalid changeset.");
                    RequireField(fields[1], "owner"); RequireField(fields[3], "date"); RequireField(fields[4], "branch");
                    RequireField(fields[5], "revision"); RequireField(fields[7], "repository");
                    if (!String.Equals(NormalizeRepository(fields[7]), NormalizeRepository(repository), StringComparison.Ordinal))
                        throw new InvalidDataException("Plastic annotate returned a foreign repository.");
                    bool merge = ParseMergeFlag(fields[6]);
                    result.Add(new PlasticBlameLine { Line = line, Owner = fields[1], Changeset = changeset,
                        Date = fields[3], Branch = fields[4], Revision = fields[5], IsMergeRevision = merge,
                        Repository = repository, Comment = fields[8], Content = fields[9] });
                    expectedLine++;
                }
            }
            return result;
        }

        private static void ValidateBlameIgnore(string ignore)
        {
            if (String.IsNullOrWhiteSpace(ignore)) return;
            if (!new[] { "none", "eol", "whitespaces", "eol&whitespaces" }.Contains(ignore, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("--ignore must be none, eol, whitespaces or eol&whitespaces.");
        }

        private static void RequireField(string value, string field)
        {
            if (String.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
                throw new InvalidDataException("Plastic annotate returned an invalid " + field + " field.");
        }

        private static bool ParseMergeFlag(string value)
        {
            if (new[] { "true", "yes", "y", "1", "是", "si", "sí" }.Contains(value, StringComparer.OrdinalIgnoreCase)) return true;
            if (new[] { "false", "no", "n", "0", "否" }.Contains(value, StringComparer.OrdinalIgnoreCase)) return false;
            throw new InvalidDataException("Plastic annotate returned an invalid merge flag.");
        }

        private static string NormalizeRepository(string value)
        { return (value ?? "").StartsWith("rep:", StringComparison.OrdinalIgnoreCase) ? value.Substring(4) : value; }
    }
}
