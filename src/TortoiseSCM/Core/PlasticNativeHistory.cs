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
    public sealed partial class PlasticClient
    {
        // Native revision history is only an early preview. It may omit publishing
        // an old revision (rollback), deleted paths, and ancestor move/delete events.
        // Callers MUST subsequently scan publication history from the head, not from
        // the preview's oldest row. HasMore never becomes false here.
        public async Task<PlasticHistoryPage> GetNativeHistoryPreviewAsync(string path, string branch, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (branch != null) ValidateBranchName(branch);
            var validated = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            string root = validated.WorkingDirectory, absolute = validated.Arguments[1];
            var workspace = DiscoverWorkspace(root);
            if (String.IsNullOrWhiteSpace(workspace.Repository)) throw new InvalidDataException("Workspace selector does not identify a repository.");
            string scope = SamePath(absolute, root) ? "/" : "/" + absolute.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
            var page = new PlasticHistoryPage { Repository = workspace.Repository, Scope = scope, Branch = branch,
                Items = new List<PlasticHistoryItem>(), HasMore = true };
            // Root publication pages already require only one native query. Missing
            // paths are handled by the complete publication scan, without fileinfo.
            if (scope == "/" || (!File.Exists(absolute) && !Directory.Exists(absolute))) return page;
            using (var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                budget.CancelAfter(TimeSpan.FromSeconds(4));
                try
                {
                    if (branch != null && !(await GetBranchesAsync(root, budget.Token).ConfigureAwait(false)).Any(item => item.Name == branch))
                        throw new ArgumentException("The selected history branch no longer exists. Refresh the branch list.");
                    ValidateHistoryRepository(root, workspace.Repository);
                    var response = await ExecuteAsync(RevisionCommand(root, new[] { "history", absolute, "--xml", "--encoding=utf-8", "--limit=10" }), budget.Token).ConfigureAwait(false);
                    RequireSuccess(response);
                    var candidates = ParseNativeHistoryPreview(response.Output, absolute, workspace.Repository);
                    foreach (var item in candidates)
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        if (branch != null && !String.Equals(item.Branch, branch, StringComparison.Ordinal)) continue;
                        // An item can have revisions created under a previous path.
                        // Only actual publication paths can safely enter this view.
                        var files = await HistoryFilesCachedAsync(root, workspace.Repository, item.Changeset, budget.Token).ConfigureAwait(false);
                        if (files.Any(file => HistoryPathMatches(file, scope))) page.Items.Add(item);
                    }
                }
                catch (OperationCanceledException) { cancellationToken.ThrowIfCancellationRequested(); }
                // Unsupported older clients, unavailable native history, or malformed
                // preview XML must not prevent the authoritative full scan. Preserve
                // only rows already validated against successful complete diffs.
                catch (PlasticCommandException) { }
                catch (InvalidDataException) { }
                catch (XmlException) { }
            }
            cancellationToken.ThrowIfCancellationRequested();
            ValidateHistoryRepository(root, workspace.Repository);
            return page;
        }

        internal static IList<PlasticHistoryItem> ParseNativeHistoryPreview(string xml, string absolute, string repository)
        {
            // Preview is expendable. Bound synchronous XML work even if an older
            // client ignores --limit or a changeset contains a very large comment.
            if (xml == null || xml.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("Native history preview exceeded its response budget.");
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "RevisionHistoriesResult" ||
                document.Root.Elements().Count() != 1 || document.Root.Elements("RevisionHistories").Count() != 1)
                throw new InvalidDataException("Unexpected native history preview XML.");
            var histories = document.Root.Element("RevisionHistories");
            if (histories.Elements().Any(item => item.Name != "RevisionHistory") || histories.Elements().Count() > 1)
                throw new InvalidDataException("Native history preview must describe only the requested item.");
            var result = new List<PlasticHistoryItem>();
            foreach (var history in histories.Elements("RevisionHistory"))
            {
                string itemName = NativeHistoryField(history, "ItemName", false);
                if (!String.Equals(itemName, absolute, StringComparison.OrdinalIgnoreCase) || history.Elements("Revisions").Count() != 1)
                    throw new InvalidDataException("Native history preview returned a different item.");
                var revisions = history.Element("Revisions");
                if (revisions.Elements().Any(item => item.Name != "Revision")) throw new InvalidDataException("Invalid native revision list.");
                foreach (var revision in revisions.Elements("Revision"))
                {
                    long number;
                    if (!Int64.TryParse(NativeHistoryField(revision, "ChangesetNumber", false), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                        throw new InvalidDataException("Invalid native history changeset number.");
                    string name = NativeHistoryField(revision, "Repository", false), server = NativeHistoryField(revision, "Server", false);
                    if (!String.Equals(name + "@" + server, repository, StringComparison.Ordinal) || revision.Elements("RepositorySpec").Count() != 1 ||
                        NativeHistoryField(revision.Element("RepositorySpec"), "Name", false) != name ||
                        NativeHistoryField(revision.Element("RepositorySpec"), "Server", false) != server)
                        throw new InvalidDataException("Native history preview returned a different repository.");
                    string revisionSpec = NativeHistoryField(revision, "RevisionSpec", false);
                    if (!revisionSpec.EndsWith("#cs:" + number.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
                        throw new InvalidDataException("Native history revision disagrees with its changeset.");
                    string branch = NativeHistoryField(revision, "Branch", false);
                    if (!branch.StartsWith("/", StringComparison.Ordinal) || branch.IndexOf('@') >= 0 || branch.Any(Char.IsControl))
                        throw new InvalidDataException("Invalid native history branch.");
                    DateTimeOffset date;
                    string creationDate = NativeHistoryField(revision, "CreationDate", false);
                    if (!DateTimeOffset.TryParse(creationDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                        throw new InvalidDataException("Invalid native history creation date.");
                    result.Add(new PlasticHistoryItem { Path = absolute, Repository = repository, Changeset = number,
                        RevisionSpec = "cs:" + number.ToString(CultureInfo.InvariantCulture), Branch = branch, CreationDate = creationDate,
                        Owner = NativeHistoryField(revision, "Owner", false), Comment = NativeHistoryField(revision, "Comment", true) });
                }
            }
            if (result.Select(item => item.Changeset).Distinct().Count() != result.Count)
                throw new InvalidDataException("Duplicate native history changesets.");
            // Older clients may ignore --limit; still keep the verification workload
            // bounded and prefer the newest candidates regardless of native order.
            return result.OrderByDescending(item => item.Changeset).Take(10).ToList();
        }

        private static string NativeHistoryField(XElement parent, string field, bool allowEmpty)
        {
            if (parent.Elements(field).Count() != 1 || parent.Element(field).HasElements)
                throw new InvalidDataException("Missing or ambiguous native history field: " + field);
            string value = parent.Element(field).Value;
            if (!allowEmpty && String.IsNullOrWhiteSpace(value)) throw new InvalidDataException("Empty native history field: " + field);
            return value;
        }
    }
}
