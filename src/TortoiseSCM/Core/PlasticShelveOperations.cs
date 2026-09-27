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
    public sealed class PlasticShelve
    {
        public long ObjectId { get; set; }
        public long ShelveId { get; set; }
        public long ParentChangeset { get; set; }
        public string Owner { get; set; }
        public string Date { get; set; }
        public string Comment { get; set; }
        public string Repository { get; set; }
    }

    public sealed partial class PlasticClient
    {
        private sealed class ShelveSelection
        {
            public IList<string> Paths { get; set; }
            public string Signature { get; set; }
        }

        public async Task<IList<PlasticShelve>> GetShelvesAsync(string root, CancellationToken cancellationToken)
        {
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            var result = await ExecuteAsync(RevisionCommand(context.RootPath,
                new[] { "find", "shelve", "--xml", "--encoding=utf-8", "--nototal" }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            var shelves = ParseShelves(result.Output, context.Repository);
            ValidateShelveContext(context);
            return shelves;
        }

        public async Task<IList<PlasticChangesetFile>> GetShelveChangesAsync(string root, long shelveId, CancellationToken cancellationToken)
        {
            ValidateShelveId(shelveId);
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            var shelves = await GetShelvesAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
            if (!shelves.Any(item => item.ShelveId == shelveId))
                throw new ArgumentException("The selected shelveset no longer exists in this repository. Refresh the shelves list.");
            ValidateShelveContext(context);
            var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] {
                "diff", ShelveSpec(shelveId, context.Repository), "--repositorypaths", "--encoding=utf-8",
                "--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}"
            }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            var changes = ParseChangesetComparisonFiles(result.Output);
            ValidateShelveContext(context);
            return changes;
        }

        public Task<PlasticCommandResult> CreateShelveAsync(string root, IList<string> paths, string comment, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = DiscoverWorkspace(root);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            return CreateShelveAsync(root, paths == null ? null : paths.ToArray(), comment,
                workspace.Repository, workspace.Selector, cancellationToken);
        }

        public async Task<PlasticCommandResult> CreateShelveAsync(string root, IList<string> paths, string comment,
            string expectedRepository, string expectedSelector, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            paths = paths == null ? null : paths.ToArray();
            ValidateShelveComment(comment);
            ValidateBranchRepository(expectedRepository);
            if (expectedSelector == null) throw new ArgumentNullException("expectedSelector");
            if (paths == null || paths.Count == 0) throw new ArgumentException("Select at least one pending file to shelve.", "paths");
            if (paths.Count > 1000) throw new ArgumentException("Select at most 1000 files for one shelveset.", "paths");

            var captured = new PlasticWorkspace { RootPath = Path.GetFullPath(root), Repository = expectedRepository, Selector = expectedSelector };
            ValidateShelveContext(captured);
            var command = await BuildReadCommandAsync(root, cancellationToken).ConfigureAwait(false);
            if (!SamePath(command.Arguments[1], command.WorkingDirectory))
                throw new ArgumentException("Create shelveset requires the explicit workspace root.");
            captured.RootPath = command.WorkingDirectory;
            ValidateShelveContext(captured);

            using (var structureGate = StructureGate(captured.RootPath))
            using (var mergeGate = OpenMergeGate(captured.RootPath))
            {
                RejectShelveSessions(captured.RootPath);
                var selected = await ValidateShelveSelectionAsync(captured, paths, cancellationToken).ConfigureAwait(false);
                var workspace = await GetWorkspaceAsync(captured.RootPath, cancellationToken).ConfigureAwait(false);
                ValidateShelveContext(captured);
                var before = await GetShelvesAsync(captured.RootPath, cancellationToken).ConfigureAwait(false);
                // Re-read immediately before the server mutation. A status/type/move
                // identity change is rejected instead of shelving a newly changed scope.
                var current = await ValidateShelveSelectionAsync(captured, selected.Paths, cancellationToken).ConfigureAwait(false);
                if (current.Signature != selected.Signature)
                    throw new InvalidOperationException("The selected pending changes changed during shelveset preparation. Refresh and review them again.");
                selected = current;
                ValidateShelveContext(captured);
                cancellationToken.ThrowIfCancellationRequested();

                var arguments = new List<string>();
                if (workspace.IsPartial) { arguments.Add("partial"); arguments.Add("shelveset"); arguments.Add("create"); }
                else { arguments.Add("shelveset"); arguments.Add("create"); }
                arguments.AddRange(selected.Paths);
                arguments.Add(workspace.IsPartial ? "--applychanged" : "--all");
                arguments.Add("-c=" + comment);
                const string advisory = " The shelveset may already exist. Refresh shelves before retrying; local pending changes were not undone.";
                try
                {
                    var result = await ExecuteAsync(RevisionCommand(captured.RootPath, arguments), cancellationToken).ConfigureAwait(false);
                    if (!result.Succeeded) result.Error += advisory;
                    else
                    {
                        try
                        {
                            ValidateShelveContext(captured);
                            var known = new HashSet<long>(before.Select(item => item.ObjectId));
                            var created = (await GetShelvesAsync(captured.RootPath, cancellationToken).ConfigureAwait(false))
                                .Where(item => !known.Contains(item.ObjectId)).ToList();
                            if (created.Count != 1 || NormalizeShelveComment(created[0].Comment) != NormalizeShelveComment(comment) ||
                                created[0].Repository != captured.Repository)
                                throw new InvalidOperationException("The new shelveset could not be identified unambiguously.");
                            var expectedPaths = new HashSet<string>(selected.Paths.Select(path => "/" + path.Substring(captured.RootPath.TrimEnd('\\', '/').Length)
                                .TrimStart('\\', '/').Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
                            var saved = await GetShelveChangesAsync(captured.RootPath, created[0].ShelveId, cancellationToken).ConfigureAwait(false);
                            if (saved.Count == 0 || saved.Any(change => change.ItemType == "D" || !expectedPaths.Contains(change.Path)) ||
                                expectedPaths.Any(path => !saved.Any(change => String.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase))))
                                throw new InvalidOperationException("The created shelveset did not contain exactly the selected file scope.");
                            ValidateShelveContext(captured);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            result.ExitCode = 1;
                            result.Error += "Shelveset creation verification failed: " + error.Message + advisory;
                        }
                    }
                    return result;
                }
                catch (OperationCanceledException error)
                {
                    throw new OperationCanceledException("Shelveset creation or verification was cancelled." + advisory, error, cancellationToken);
                }
                catch (Exception error)
                {
                    throw new InvalidOperationException("Shelveset creation did not finish reliably." + advisory, error);
                }
            }
        }

        public async Task<PlasticCommandResult> ApplyShelveAsync(string root, long shelveId, CancellationToken cancellationToken)
        {
            ValidateShelveId(shelveId);
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            var workspace = await GetWorkspaceAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
            if (workspace.IsPartial)
                throw new ArgumentException("Applying a shelveset is supported only in a Standard workspace. Partial/Gluon workspaces must use the official Plastic client.");
            ValidateShelveContext(context);

            using (var structureGate = StructureGate(context.RootPath))
            using (var mergeGate = OpenMergeGate(context.RootPath))
            {
                var currentWorkspace = await GetWorkspaceAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
                if (currentWorkspace.IsPartial)
                    throw new ArgumentException("The workspace changed to Partial/Gluon mode during shelveset preparation; no shelveset was applied.");
                ValidateShelveContext(context);
                RejectShelveSessions(context.RootPath);
                // A normal status omits ignored entries. Include all local entries
                // so an ignored/private file at a shelveset path cannot be silently
                // overwritten by the native apply operation.
                var statusResult = await ExecuteAsync(RevisionCommand(context.RootPath, new[] {
                    "status", context.RootPath, "--all", "--ignored", "--xml", "--encoding=utf-8", "--fullpaths"
                }), cancellationToken).ConfigureAwait(false);
                RequireSuccess(statusResult);
                var pending = ParseStatus(statusResult.Output, context.RootPath);
                if (pending.Count != 0)
                    throw new ArgumentException("The workspace has pending changes. Applying a shelveset is refused to prevent overwriting local content; review or save those changes first.");
                var shelves = await GetShelvesAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
                if (!shelves.Any(item => item.ShelveId == shelveId))
                    throw new ArgumentException("The selected shelveset no longer exists in this repository. Refresh the shelves list.");
                var details = await GetShelveChangesAsync(context.RootPath, shelveId, cancellationToken).ConfigureAwait(false);
                if (details.Count == 0)
                    throw new InvalidDataException("The selected shelveset has no changed files and cannot be applied safely.");
                ValidateShelveContext(context);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] {
                    "shelveset", "apply", ShelveSpec(shelveId, context.Repository), "--encoding=utf-8"
                }), cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                    result.Error += " The shelveset may not have been applied; inspect workspace status before retrying.";
                else
                {
                    try { ValidateShelveContext(context); }
                    catch (Exception error)
                    {
                        result.ExitCode = 1;
                        result.Error += " Shelveset apply verification failed: " + error.Message + " Inspect workspace status before retrying.";
                    }
                }
                return result;
            }
        }

        public async Task<PlasticCommandResult> DeleteShelveAsync(string root, long shelveId, CancellationToken cancellationToken)
        {
            ValidateShelveId(shelveId);
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            ValidateShelveContext(context);
            using (var mergeGate = OpenMergeGate(context.RootPath))
            {
                var shelves = await GetShelvesAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
                if (!shelves.Any(item => item.ShelveId == shelveId))
                    throw new ArgumentException("The selected shelveset no longer exists in this repository. Refresh the shelves list.");
                ValidateShelveContext(context);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] {
                    "shelveset", "delete", ShelveSpec(shelveId, context.Repository)
                }), cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                    result.Error += " The shelveset may still exist; refresh the shelves list before retrying.";
                else
                {
                    try
                    {
                        ValidateShelveContext(context);
                        var remaining = await GetShelvesAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
                        if (remaining.Any(item => item.ShelveId == shelveId))
                        {
                            result.ExitCode = 1;
                            result.Error = (result.Error ?? "") + " Shelveset deletion could not be verified; refresh the shelves list before retrying.";
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error)
                    {
                        result.ExitCode = 1;
                        result.Error += " Shelveset deletion verification failed: " + error.Message + " Refresh the shelves list before retrying.";
                    }
                }
                return result;
            }
        }

        internal static IList<PlasticShelve> ParseShelves(string xml, string repository)
        {
            ValidateBranchRepository(repository);
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "PLASTICQUERY" ||
                document.Root.Elements().Any(item => item.Name != "SHELVE"))
                throw new InvalidDataException("Unexpected Plastic shelveset XML.");
            var result = new List<PlasticShelve>();
            var objects = new HashSet<long>(); var shelves = new HashSet<long>();
            string repositoryName = repository.Substring(0, repository.IndexOf('@'));
            foreach (var item in document.Root.Elements("SHELVE"))
            {
                long objectId, shelveId, parent;
                string owner = (string)item.Element("OWNER"), date = (string)item.Element("DATE"),
                    comment = (string)item.Element("COMMENT") ?? "", name = (string)item.Element("REPNAME"),
                    server = (string)item.Element("REPSERVER"), displayRepository = (string)item.Element("REPOSITORY");
                if (!Int64.TryParse((string)item.Element("ID"), NumberStyles.None, CultureInfo.InvariantCulture, out objectId) || objectId < 0 || !objects.Add(objectId) ||
                    !Int64.TryParse((string)item.Element("SHELVEID"), NumberStyles.None, CultureInfo.InvariantCulture, out shelveId) || shelveId < 0 || !shelves.Add(shelveId) ||
                    !Int64.TryParse((string)item.Element("PARENT"), NumberStyles.None, CultureInfo.InvariantCulture, out parent) || parent < 0 ||
                    String.IsNullOrWhiteSpace(owner) || String.IsNullOrWhiteSpace(date) ||
                    comment.Any(value => Char.IsControl(value) && value != '\r' && value != '\n' && value != '\t') ||
                    name + "@" + server != repository || displayRepository != repositoryName)
                    throw new InvalidDataException("Invalid, duplicate or foreign shelveset returned by the server.");
                result.Add(new PlasticShelve { ObjectId = objectId, ShelveId = shelveId, ParentChangeset = parent,
                    Owner = owner, Date = date, Comment = comment, Repository = repository });
            }
            return result.OrderByDescending(item => item.ShelveId).ToList();
        }

        private async Task<PlasticWorkspace> ValidateShelveRootAsync(string root, CancellationToken cancellationToken)
        {
            var command = await BuildReadCommandAsync(root, cancellationToken).ConfigureAwait(false);
            if (!SamePath(command.Arguments[1], command.WorkingDirectory))
                throw new ArgumentException("Shelveset operations require the explicit workspace root.");
            var workspace = DiscoverWorkspace(command.WorkingDirectory);
            ValidateBranchRepository(workspace.Repository);
            return workspace;
        }

        private async Task<ShelveSelection> ValidateShelveSelectionAsync(PlasticWorkspace workspace, IList<string> paths, CancellationToken cancellationToken)
        {
            var selected = new List<string>();
            foreach (string value in paths)
            {
                var validated = Build(new PlasticCommandRequest { Command = PlasticCommand.History,
                    WorkingDirectory = workspace.RootPath, Paths = new[] { value } }, cancellationToken);
                string absolute = validated.Arguments[1];
                if (selected.Contains(absolute, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Select each shelveset file only once.", "paths");
                selected.Add(absolute);
            }
            foreach (string path in selected)
            {
                if (Directory.Exists(path)) throw new ArgumentException("Directory shelveset selection is recursive. Expand it and select explicit pending files instead.");
                if (path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("Invalid shelveset file path.");
            }
            var pending = await GetStatusAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false);
            var signature = new List<string>();
            foreach (string path in selected)
            {
                if (pending.Any(item => item.IsDirectory && item.StatusCode != "CH" && item.StatusCode != "CO" &&
                    (IsWithinScope(path, item.Path) || IsWithinScope(path, item.OldPath))))
                    throw new ArgumentException("The selected file depends on a pending added, moved or deleted directory. Shelve that structure in the official client for now: " + path);
                var matches = pending.Where(item => SamePath(item.Path, path)).ToList();
                if (matches.Count == 0) throw new ArgumentException("Every selected shelveset file must still have a pending controlled change: " + path);
                if (matches.Any(item => item.IsDirectory || item.StatusCode == "PR" || item.StatusCode == "IG" ||
                    !new[] { "CH", "CO" }.Contains(item.StatusCode, StringComparer.OrdinalIgnoreCase)))
                    throw new ArgumentException("This milestone shelves only explicitly selected content changes (CH/CO); private, ignored and structural changes are refused: " + path);
                signature.AddRange(matches.Select(item => item.StatusCode + "|" + item.Path.ToUpperInvariant() + "|" +
                    (item.OldPath ?? "").ToUpperInvariant() + "|" + item.IsDirectory).OrderBy(value => value, StringComparer.Ordinal));
            }
            ValidateShelveContext(workspace);
            return new ShelveSelection { Paths = selected, Signature = String.Join("\n", signature) };
        }

        private void ValidateShelveContext(PlasticWorkspace expected)
        {
            var current = DiscoverWorkspace(expected.RootPath);
            if (current == null || current.Repository != expected.Repository ||
                NormalizeMergeSelector(current.Selector) != NormalizeMergeSelector(expected.Selector))
                throw new InvalidOperationException("The workspace repository or selector changed during the shelveset operation. Refresh before continuing.");
        }

        private void RejectShelveSessions(string root)
        {
            if (HasSavedMergeSession(root) || HasSavedPartialConflictSession(root) || HasSavedPartialStructureSession(root) ||
                HasSavedPartialDirectorySession(root) || File.Exists(Path.Combine(root, ".plastic", "plastic.mergeprogress")))
                throw new ArgumentException("Finish or recover the existing merge/conflict session before creating a shelveset.");
        }

        private static string ShelveSpec(long shelveId, string repository)
        { return "sh:" + shelveId.ToString(CultureInfo.InvariantCulture) + "@" + repository; }

        private static void ValidateShelveId(long shelveId)
        { if (shelveId < 0) throw new ArgumentOutOfRangeException("shelveId", "Shelveset ID must be nonnegative."); }

        private static void ValidateShelveComment(string comment)
        {
            if (String.IsNullOrWhiteSpace(comment) || comment.Any(value => Char.IsControl(value) && value != '\r' && value != '\n' && value != '\t'))
                throw new ArgumentException("Provide a nonempty shelveset comment without control characters.", "comment");
        }

        private static string NormalizeShelveComment(string comment)
        { return (comment ?? "").Replace("\r\n", "\n").Replace("\r", "\n"); }
    }
}
