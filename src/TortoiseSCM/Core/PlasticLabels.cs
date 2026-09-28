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
    public sealed class PlasticLabel
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public long Changeset { get; set; }
        public string Owner { get; set; }
        public string Date { get; set; }
        public string Comment { get; set; }
        public string Repository { get; set; }
        public string Branch { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public Task<IList<PlasticLabel>> GetLabelsAsync(string path, CancellationToken token)
        { return GetLabelsAsync(path, null, token); }

        public async Task<IList<PlasticLabel>> GetLabelsAsync(string path, string expectedRepository, CancellationToken token)
        {
            var context = CaptureLabelContext(path, expectedRepository, token);
            await PrepareLabelContextAsync(path, context, token).ConfigureAwait(false);
            return await ReadLabelsAsync(context, token).ConfigureAwait(false);
        }

        public Task<PlasticLabel> ResolveLabelAsync(string path, string name, CancellationToken token)
        { return ResolveLabelAsync(path, name, null, token); }

        public async Task<PlasticLabel> ResolveLabelAsync(string path, string name, string expectedRepository, CancellationToken token)
        {
            ValidateReadableLabelName(name);
            var labels = await GetLabelsAsync(path, expectedRepository, token).ConfigureAwait(false);
            var label = labels.SingleOrDefault(item => item.Name == name);
            if (label == null) throw new ArgumentException("The label no longer exists in this repository. Refresh the label list.");
            return label;
        }

        public async Task<PlasticChangesetDetails> GetLabelChangesetAsync(string path, long changeset, string expectedRepository, CancellationToken token)
        {
            ValidateChangeset(changeset);
            var context = CaptureLabelContext(path, expectedRepository, token);
            await PrepareLabelContextAsync(path, context, token).ConfigureAwait(false);
            string source = "cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + context.Repository;
            var logged = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "log", source, "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(logged); ValidateHistoricalContext(context);
            var document = SafeXml.Load(logged.Output);
            long number;
            if (document.Root == null || document.Root.Name != "LogList" || document.Root.Elements().Count() != 1 ||
                document.Root.Elements("Changeset").Count() != 1 ||
                !Int64.TryParse(LabelField(document.Root.Element("Changeset"), "ChangesetId"), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number != changeset)
                throw new InvalidDataException("The label target does not identify the requested changeset in the captured repository.");
            var row = document.Root.Element("Changeset");
            var details = new PlasticChangesetDetails { Changeset = new PlasticHistoryItem {
                Changeset = changeset, RevisionSpec = source, Repository = context.Repository, Path = context.RootPath,
                Owner = LabelField(row, "Owner"), Comment = LabelField(row, "Comment"), Branch = LabelField(row, "Branch"), CreationDate = LabelField(row, "Date") },
                Files = new List<PlasticChangesetFile>() };
            if (changeset != 0)
            {
                var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "diff", source, "--repositorypaths", "--encoding=utf-8",
                    "--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}" }), token).ConfigureAwait(false);
                RequireSuccess(result); ValidateHistoricalContext(context);
                details.Files = ParseChangesetFiles(result.Output);
                foreach (var file in details.Files)
                {
                    ValidateRepositoryDirectoryPath(file.Path);
                    if (!String.IsNullOrEmpty(file.OldPath)) ValidateRepositoryDirectoryPath(file.OldPath);
                }
            }
            token.ThrowIfCancellationRequested(); ValidateHistoricalContext(context);
            return details;
        }

        // cm label create is an upsert. Publishing through rename prevents a concurrent
        // creator of the requested name from having its label silently moved to our cs.
        // A failed publish leaves the explicitly reported temporary label for review.
        public async Task<PlasticCommandResult> CreateLabelAsync(string path, string name, long changeset, string comment,
            string expectedRepository, CancellationToken token)
        {
            ValidateLabelName(name); ValidateChangeset(changeset);
            if (String.IsNullOrWhiteSpace(comment) || comment.Any(c => Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                throw new ArgumentException("Provide a nonempty label comment without control characters. This prevents an external comment editor from opening.");
            var context = CaptureLabelContext(path, expectedRepository, token);
            await PrepareLabelContextAsync(path, context, token).ConfigureAwait(false);
            using (var gate = StructureGate(context.RootPath))
            {
                var labels = await ReadLabelsAsync(context, token).ConfigureAwait(false);
                if (labels.Any(item => String.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("That label already exists. Choose another name; existing labels are never reapplied.");
                string source = "cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + context.Repository;
                var logged = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "log", source, "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
                RequireSuccess(logged);
                var document = SafeXml.Load(logged.Output);
                long number;
                if (document.Root == null || document.Root.Name != "LogList" || document.Root.Elements().Count() != 1 ||
                    document.Root.Elements("Changeset").Count() != 1 ||
                    !Int64.TryParse(LabelField(document.Root.Element("Changeset"), "ChangesetId"), NumberStyles.None, CultureInfo.InvariantCulture, out number) || number != changeset)
                    throw new InvalidDataException("The source does not identify the requested changeset in the captured repository.");
                ValidateHistoricalContext(context);
                token.ThrowIfCancellationRequested();
                string temporary = (name.StartsWith("tortoisescm-autotest-", StringComparison.Ordinal) ?
                    "tortoisescm-autotest-label-pending-" : "tortoisescm-label-pending-") + Guid.NewGuid().ToString("N");
                string advisory = " Refresh labels before retrying. The server may contain the requested label '" + name +
                    "' or temporary label '" + temporary + "' in " + context.Repository + ". No automatic label deletion or workspace switch was performed.";
                try
                {
                    var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "label", "create", "lb:" + temporary + "@" + context.Repository,
                        source, "-c=" + comment }), token).ConfigureAwait(false);
                    if (!result.Succeeded) { result.Error += advisory; return result; }
                    var staged = (await ReadLabelsAsync(context, token).ConfigureAwait(false)).SingleOrDefault(item => item.Name == temporary);
                    if (staged == null || staged.Changeset != changeset || staged.Comment.Replace("\r\n", "\n").Replace("\r", "\n") != comment.Replace("\r\n", "\n").Replace("\r", "\n"))
                        throw new InvalidOperationException("The temporary label's identity and target could not be verified.");
                    ValidateHistoricalContext(context);
                    token.ThrowIfCancellationRequested();
                    result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "label", "rename", "lb:" + temporary + "@" + context.Repository, name }), token).ConfigureAwait(false);
                    if (!result.Succeeded) { result.Error += advisory; return result; }
                    var after = await ReadLabelsAsync(context, token).ConfigureAwait(false);
                    var published = after.SingleOrDefault(item => item.Name == name);
                    if (published == null || published.Id != staged.Id || published.Changeset != changeset || after.Any(item => item.Name == temporary))
                        throw new InvalidOperationException("The published label's identity and target could not be verified.");
                    result.Output = "Created label " + name + " (id:" + published.Id.ToString(CultureInfo.InvariantCulture) + ") at " + source + ".";
                    return result;
                }
                catch (OperationCanceledException error) { throw new OperationCanceledException("Label creation or verification was cancelled." + advisory, error, token); }
                catch (Exception error) { throw new InvalidOperationException("Label creation did not finish reliably: " + error.Message + advisory, error); }
            }
        }

        // Native label deletion accepts names only. Revalidate the reviewed immutable ID
        // and target immediately before invoking it; cross-client server races cannot be
        // made atomic with this version of cm and must not be described as a CAS delete.
        public async Task<PlasticCommandResult> DeleteLabelAsync(string path, string name, long expectedId, long expectedChangeset,
            string expectedRepository, CancellationToken token)
        {
            ValidateLabelName(name); ValidateChangeset(expectedChangeset);
            if (expectedId <= 0) throw new ArgumentOutOfRangeException("expectedId", "Specify the positive ID of the reviewed label.");
            if (String.IsNullOrWhiteSpace(expectedRepository)) throw new ArgumentException("Specify the repository of the reviewed label.");
            var context = CaptureLabelContext(path, expectedRepository, token);
            await PrepareLabelContextAsync(path, context, token).ConfigureAwait(false);
            using (var gate = StructureGate(context.RootPath))
            {
                var selected = (await ReadLabelsAsync(context, token).ConfigureAwait(false)).SingleOrDefault(item => item.Name == name);
                if (selected == null || selected.Id != expectedId || selected.Changeset != expectedChangeset)
                    throw new InvalidOperationException("The label was removed, replaced or moved since it was reviewed. Refresh and review it again before deletion.");
                ValidateHistoricalContext(context);
                token.ThrowIfCancellationRequested();
                const string advisory = " The server label may already have been deleted. Refresh labels before retrying; no workspace files were changed.";
                try
                {
                    var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "label", "delete", "lb:" + name + "@" + context.Repository }), token).ConfigureAwait(false);
                    if (!result.Succeeded) { result.Error += advisory; return result; }
                    if ((await ReadLabelsAsync(context, token).ConfigureAwait(false)).Any(item => item.Id == expectedId || item.Name == name))
                        throw new InvalidOperationException("The deleted label's absence could not be verified.");
                    result.Output = "Deleted label " + name + " (id:" + expectedId.ToString(CultureInfo.InvariantCulture) + ") from " + context.Repository + ".";
                    return result;
                }
                catch (OperationCanceledException error) { throw new OperationCanceledException("Label deletion or verification was cancelled." + advisory, error, token); }
                catch (Exception error) { throw new InvalidOperationException("Label deletion did not finish reliably: " + error.Message + advisory, error); }
            }
        }

        private PlasticWorkspace CaptureLabelContext(string path, string expectedRepository, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var context = DiscoverWorkspace(path);
            if (context == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            ValidateLabelRepository(context.Repository);
            if (expectedRepository != null && context.Repository != expectedRepository)
                throw new InvalidOperationException("The workspace repository changed. Refresh the label list before continuing.");
            return context;
        }

        private async Task PrepareLabelContextAsync(string path, PlasticWorkspace context, CancellationToken token)
        {
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            if (!SamePath(command.WorkingDirectory, context.RootPath)) throw new InvalidOperationException("The selected workspace changed during label preparation.");
            ValidateHistoricalContext(context);
        }

        private async Task<IList<PlasticLabel>> ReadLabelsAsync(PlasticWorkspace context, CancellationToken token)
        {
            ValidateHistoricalContext(context);
            var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "find", "label", "on repository '" + context.Repository + "'",
                "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var labels = ParseLabels(result.Output, context.Repository);
            ValidateHistoricalContext(context);
            return labels;
        }

        public static IList<PlasticLabel> ParseLabels(string xml, string repository)
        {
            ValidateLabelRepository(repository);
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "PLASTICQUERY" || document.Root.Elements().Any(item => item.Name != "MARKER"))
                throw new InvalidDataException("Invalid label query XML.");
            var result = new List<PlasticLabel>();
            var ids = new HashSet<long>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in document.Root.Elements("MARKER"))
            {
                long id, changeset;
                string name = LabelField(row, "NAME");
                try { ValidateReadableLabelName(name); } catch (ArgumentException error) { throw new InvalidDataException("The label name cannot be displayed safely.", error); }
                if (!Int64.TryParse(LabelField(row, "ID"), NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0 ||
                    !Int64.TryParse(LabelField(row, "CHANGESET"), NumberStyles.None, CultureInfo.InvariantCulture, out changeset) || changeset < 0 ||
                    !ids.Add(id) || !names.Add(name)) throw new InvalidDataException("Invalid or duplicate label identity.");
                string repName = LabelField(row, "REPNAME"), server = LabelField(row, "REPSERVER");
                if (repName + "@" + server != repository || LabelField(row, "REPOSITORY") != repName)
                    throw new InvalidDataException("The label query returned a different repository.");
                string date = LabelField(row, "DATE");
                DateTimeOffset parsed;
                if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed)) throw new InvalidDataException("Invalid label creation date.");
                result.Add(new PlasticLabel { Id = id, Name = name, Changeset = changeset, Date = date, Repository = repository,
                    Owner = LabelField(row, "OWNER"), Comment = LabelField(row, "COMMENT"), Branch = LabelField(row, "BRANCH") });
            }
            return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string LabelField(XElement row, string name)
        {
            var fields = row.Elements(name).ToList();
            if (fields.Count != 1 || fields[0].HasElements) throw new InvalidDataException("Invalid or missing label field: " + name + ".");
            return fields[0].Value;
        }

        private static void ValidateLabelRepository(string repository)
        {
            ValidateBranchRepository(repository);
            if (repository.IndexOf('\'') >= 0) throw new InvalidDataException("Label queries do not support apostrophes in repository specifications.");
        }

        // Read names are display data and exact client-side keys, never command specs.
        // Do not make a repository unreadable because another client created a name
        // outside the conservative subset supported by our create/delete actions.
        private static void ValidateReadableLabelName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.Any(Char.IsControl))
                throw new ArgumentException("Specify a nonempty label name without control characters.");
        }

        public static void ValidateLabelName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name != name.Trim() || name.StartsWith("-", StringComparison.Ordinal) || name.Any(Char.IsControl) ||
                name.IndexOfAny(new[] { '@', '#', ':', '"', '\\', '/' }) >= 0)
                throw new ArgumentException("Specify a label name without a repository suffix, leading dash, outer whitespace, control characters or @ # : quotation marks and path separators.");
        }
    }
}
