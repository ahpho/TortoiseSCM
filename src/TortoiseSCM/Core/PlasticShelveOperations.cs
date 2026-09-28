// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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

    public sealed class PlasticShelveComparisonFile
    {
        public string Status { get; set; }
        public string Path { get; set; }
        public string OldPath { get; set; }
        public string ItemType { get; set; }
        public PlasticDiffResult Diff { get; set; }
    }

    public sealed class PlasticShelveComparison
    {
        public string Repository { get; set; }
        public string RootPath { get; set; }
        public long ShelveId { get; set; }
        public long ParentChangeset { get; set; }
        public IList<PlasticShelveComparisonFile> Files { get; set; }
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

        public async Task<PlasticShelveComparison> GetShelveComparisonAsync(string root, long shelveId, CancellationToken cancellationToken)
        {
            ValidateShelveId(shelveId);
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            var shelves = await GetShelvesAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
            var shelve = shelves.SingleOrDefault(item => item.ShelveId == shelveId);
            if (shelve == null)
                throw new ArgumentException("The selected shelveset no longer exists in this repository. Refresh the shelves list.");
            ValidateShelveContext(context);
            var changes = await GetShelveChangesAsync(context.RootPath, shelveId, cancellationToken).ConfigureAwait(false);
            var result = new List<PlasticShelveComparisonFile>();
            foreach (var change in changes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (change.ItemType == "D")
                {
                    result.Add(new PlasticShelveComparisonFile { Status = change.Status, Path = change.Path,
                        OldPath = change.OldPath, ItemType = change.ItemType });
                    continue;
                }
                if (change.ItemType != "F" && change.ItemType != "B")
                    throw new ArgumentException("Shelveset comparison supports files only; directory, xlink and symlink changes require the Plastic client: " + change.Path);
                string sourcePath = String.IsNullOrEmpty(change.OldPath) ? change.Path : change.OldPath;
                byte[] before = new byte[0], after;
                string revision = HistoricalSpec(context.Repository, sourcePath, shelve.ParentChangeset);
                string temporary = NewHistoricalTemporaryDirectory();
                string beforePath = Path.Combine(temporary, "before"), afterPath = Path.Combine(temporary, "after");
                try
                {
                    if (change.Status != "A")
                    {
                        if (shelve.ParentChangeset <= 0)
                            throw new InvalidDataException("The shelveset has no valid parent changeset for a non-added file: " + sourcePath);
                        await DownloadHistoricalFileAsync(context, sourcePath, shelve.ParentChangeset, beforePath, cancellationToken).ConfigureAwait(false);
                        before = File.ReadAllBytes(beforePath);
                    }
                    if (change.Status != "D")
                    {
                        await DownloadShelveFileAsync(context, change.Path, shelveId, afterPath, cancellationToken).ConfigureAwait(false);
                        after = File.ReadAllBytes(afterPath);
                    }
                    else after = new byte[0];
                    result.Add(new PlasticShelveComparisonFile { Status = change.Status, Path = change.Path,
                        OldPath = change.OldPath, ItemType = change.ItemType,
                        Diff = CompareContent(change.Path, revision, before, after, change.ItemType == "B") });
                }
                finally { RemoveHistoricalTemporaryDirectory(temporary, beforePath, afterPath); }
            }
            ValidateShelveContext(context);
            return new PlasticShelveComparison { Repository = context.Repository, RootPath = context.RootPath,
                ShelveId = shelveId, ParentChangeset = shelve.ParentChangeset, Files = result };
        }

        /// <summary>Open one shelveset file in the configured comparison profile.</summary>
        public Task<PlasticCommandResult> OpenShelveDiffToolAsync(string root, long shelveId,
            string repositoryPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = DiscoverWorkspace(root);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            return OpenShelveDiffToolAsync(root, shelveId, repositoryPath, workspace.Repository, workspace.Selector, cancellationToken);
        }

        public async Task<PlasticCommandResult> OpenShelveDiffToolAsync(string root, long shelveId,
            string repositoryPath, string expectedRepository, string expectedSelector, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateShelveId(shelveId); ValidateRepositoryFilePath(repositoryPath);
            ValidateBranchRepository(expectedRepository);
            if (expectedSelector == null) throw new ArgumentNullException("expectedSelector");
            var expected = new PlasticWorkspace { RootPath = Path.GetFullPath(root), Repository = expectedRepository, Selector = expectedSelector };
            ValidateShelveContext(expected);
            string beyondCompare = config.UseBeyondCompare ? ComparisonTool.ResolveExecutable(config) : null;
            if (String.IsNullOrEmpty(beyondCompare))
                throw new InvalidOperationException("暂存集文件比较需要已配置的比较工具，请检查工具设置。");
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            ValidateShelveContext(expected);
            var shelves = await GetShelvesAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
            var shelve = shelves.SingleOrDefault(item => item.ShelveId == shelveId);
            if (shelve == null) throw new ArgumentException("所选暂存集已不存在，请刷新列表。");
            var changes = await GetShelveChangesAsync(context.RootPath, shelveId, cancellationToken).ConfigureAwait(false);
            var change = ShelveDiffSelection(changes, repositoryPath);
            string signature = ShelveDiffSignature(changes);
            string sourcePath = String.IsNullOrEmpty(change.OldPath) ? change.Path : change.OldPath;
            string temporary = NewHistoricalTemporaryDirectory();
            string before = Path.Combine(temporary, "parent" + Path.GetExtension(sourcePath));
            string after = Path.Combine(temporary, "shelve" + Path.GetExtension(change.Path));
            bool preserve = false;
            try
            {
                // Empty sides are intentional only after the native status proves add/delete.
                if (change.Status == "A") File.WriteAllBytes(before, new byte[0]);
                else
                {
                    if (shelve.ParentChangeset <= 0) throw new InvalidDataException("暂存集没有可用于比较的父变更集。");
                    await DownloadHistoricalFileAsync(context, sourcePath, shelve.ParentChangeset, before, cancellationToken).ConfigureAwait(false);
                }
                if (change.Status == "D") File.WriteAllBytes(after, new byte[0]);
                else await DownloadShelveFileAsync(context, change.Path, shelveId, after, cancellationToken).ConfigureAwait(false);
                ValidateHistoricalContext(context);
                var currentChanges = await GetShelveChangesAsync(context.RootPath, shelveId, cancellationToken).ConfigureAwait(false);
                var currentShelves = await GetShelvesAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
                var currentShelve = currentShelves.SingleOrDefault(item => item.ShelveId == shelveId);
                if (currentShelve == null || currentShelve.ObjectId != shelve.ObjectId || currentShelve.ParentChangeset != shelve.ParentChangeset ||
                    ShelveDiffSignature(currentChanges) != signature)
                    throw new InvalidOperationException("暂存集或文件明细在比较准备期间已改变，请刷新后重试。");
                ValidateHistoricalContext(context); ValidateShelveContext(expected); cancellationToken.ThrowIfCancellationRequested();
                File.SetAttributes(before, File.GetAttributes(before) | FileAttributes.ReadOnly);
                File.SetAttributes(after, File.GetAttributes(after) | FileAttributes.ReadOnly);
                var args = PlasticToolArguments.Expand(BeyondCompareTool.DiffArguments,
                    new Dictionary<string, string> { { "base", before }, { "local", after } }, false);
                for (int i = 0; i < args.Count; i++)
                {
                    if (args[i].StartsWith("/lefttitle=", StringComparison.Ordinal))
                        args[i] = "/lefttitle=Parent cs:" + shelve.ParentChangeset.ToString(CultureInfo.InvariantCulture) + " " + sourcePath + (change.Status == "A" ? " (empty: added)" : "");
                    if (args[i].StartsWith("/righttitle=", StringComparison.Ordinal))
                        args[i] = "/righttitle=Shelve sh:" + shelveId.ToString(CultureInfo.InvariantCulture) + " " + change.Path + (change.Status == "D" ? " (empty: deleted)" : "");
                }
                return await ComparisonTool.RunDiffAsync(beyondCompare, args, temporary, cancellationToken).ConfigureAwait(false);
            }
            catch (BeyondCompareWaitException) { preserve = true; throw; }
            finally { if (!preserve) RemoveHistoricalTemporaryDirectory(temporary, before, after); }
        }

        private static PlasticChangesetFile ShelveDiffSelection(IList<PlasticChangesetFile> changes, string path)
        {
            var matches = changes.Where(item => String.Equals(item.Path, path, StringComparison.Ordinal)).ToList();
            if (matches.Count == 0) throw new ArgumentException("所选文件不在该暂存集中，请刷新文件明细。");
            if (matches.Any(item => item.ItemType != "F" && item.ItemType != "B"))
                throw new ArgumentException("暂存集比较只支持普通文件；目录和链接请使用 Plastic 客户端。");
            // Native diff can emit both M and C for one renamed, edited file.
            // Both rows must agree on the exact original path and file type.
            if (matches.Count != 1 && !(matches.Count == 2 && matches.Count(item => item.Status == "M") == 1 &&
                matches.Count(item => item.Status == "C") == 1 && matches[0].OldPath == matches[1].OldPath &&
                matches[0].ItemType == matches[1].ItemType))
                throw new InvalidDataException("暂存集文件存在不明确的重复记录，请刷新后重试。");
            return matches[0];
        }

        private static string ShelveDiffSignature(IList<PlasticChangesetFile> changes)
        {
            return String.Join("\n", changes.Select(item => String.Join("|", new[] { item.Status, item.Path, item.OldPath, item.ItemType }))
                .OrderBy(item => item, StringComparer.Ordinal));
        }

        public async Task<PlasticCommandResult> ExportShelveAsync(string root, long shelveId, string outputDirectory,
            bool overwrite, CancellationToken cancellationToken)
        {
            ValidateShelveId(shelveId);
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            var shelves = await GetShelvesAsync(context.RootPath, cancellationToken).ConfigureAwait(false);
            var shelve = shelves.SingleOrDefault(item => item.ShelveId == shelveId);
            if (shelve == null)
                throw new ArgumentException("The selected shelveset no longer exists in this repository. Refresh the shelves list.");
            string output = ValidateShelveOutputDirectory(outputDirectory);
            RejectShelveOutputWorkspace(output);
            var changes = await GetShelveChangesAsync(context.RootPath, shelveId, cancellationToken).ConfigureAwait(false);
            var files = changes.Where(item => item.Status != "D" && (item.ItemType == "F" || item.ItemType == "B")).ToList();
            if (changes.Any(item => item.ItemType != "F" && item.ItemType != "B" && item.Status != "D"))
                throw new ArgumentException("Shelveset export supports file changes only; directory, xlink and symlink changes require the Plastic client.");
            if (files.Any(item => item.Path.Equals("/shelveset.manifest", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("A shelveset file conflicts with the reserved export manifest name.");
            var targets = files.Select(item => Path.GetFullPath(Path.Combine(output, item.Path.Substring(1).Replace('/', Path.DirectorySeparatorChar)))).ToList();
            targets.Add(Path.Combine(output, "shelveset.manifest"));
            ValidateShelveExportTargets(output, targets, overwrite);
            // Stage beside the destination so the final directory move remains atomic
            // and works when the system temporary directory is on another volume.
            string temporary = Path.Combine(Path.GetDirectoryName(output), ".tortoisescm-shelve-export-" + Guid.NewGuid().ToString("N"));
            string staged = Path.Combine(temporary, "export");
            bool destinationTouched = false;
            try
            {
                Directory.CreateDirectory(temporary);
                Directory.CreateDirectory(staged);
                foreach (var change in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string relative = change.Path.Substring(1).Replace('/', Path.DirectorySeparatorChar);
                    string stagedPath = Path.GetFullPath(Path.Combine(staged, relative));
                    if (!IsWithin(stagedPath, staged)) throw new InvalidDataException("The shelveset contained an unsafe repository path.");
                    RejectReparsePath(stagedPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(stagedPath));
                    await DownloadShelveFileAsync(context, change.Path, shelveId, stagedPath, cancellationToken).ConfigureAwait(false);
                }
                string manifest = Path.Combine(staged, "shelveset.manifest");
                using (var writer = new StreamWriter(manifest, false, new UTF8Encoding(false)))
                {
                    writer.WriteLine("# TortoiseSCM shelveset {0}@{1} parent cs:{2}", shelveId.ToString(CultureInfo.InvariantCulture),
                        context.Repository, shelve.ParentChangeset.ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("status\ttype\tpath\toldpath");
                    foreach (var change in changes)
                        writer.WriteLine(String.Join("\t", new[] { change.Status, change.ItemType, change.Path, change.OldPath ?? "" }));
                }
                ValidateShelveContext(context);
                ValidateShelveOutputDirectory(output);
                RejectShelveOutputWorkspace(output);
                ValidateShelveExportTargets(output, targets, overwrite);
                cancellationToken.ThrowIfCancellationRequested();
                if (!Directory.Exists(output))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output));
                    Directory.Move(staged, output);
                    destinationTouched = true;
                    staged = null;
                }
                else
                {
                    foreach (string source in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        string relative = source.Substring(staged.TrimEnd('\\', '/') .Length).TrimStart('\\', '/');
                        string target = Path.Combine(output, relative);
                        ValidateShelveOutputDirectory(output);
                        RejectShelveOutputWorkspace(output);
                        ValidateShelveExportTargets(output, new[] { target }, overwrite);
                        RejectReparsePath(source);
                        destinationTouched = true;
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        MoveExportFile(source, target, overwrite);
                    }
                }
                return new PlasticCommandResult { ExitCode = 0, Output = "Exported shelveset sh:" + shelveId.ToString(CultureInfo.InvariantCulture) +
                    " to " + output + Environment.NewLine + "Files: " + files.Count.ToString(CultureInfo.InvariantCulture) };
            }
            catch (OperationCanceledException error)
            {
                if (!destinationTouched) throw;
                throw new OperationCanceledException("Shelveset export was cancelled. Some destination files may already have been written; inspect the output before retrying.", error, cancellationToken);
            }
            catch (ArgumentException error)
            {
                if (!destinationTouched) throw;
                return ShelveExportFailure(error, true);
            }
            catch (InvalidDataException error)
            {
                if (!destinationTouched) throw;
                return ShelveExportFailure(error, true);
            }
            catch (Exception error)
            {
                return ShelveExportFailure(error, destinationTouched);
            }
            finally
            {
                if (staged != null && Directory.Exists(staged)) Directory.Delete(staged, true);
                if (Directory.Exists(temporary)) Directory.Delete(temporary);
            }
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
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = DiscoverWorkspace(root);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            return await ApplyShelveAsync(root, shelveId, workspace.Repository, workspace.Selector, cancellationToken).ConfigureAwait(false);
        }

        public async Task<PlasticCommandResult> ApplyShelveAsync(string root, long shelveId, string expectedRepository,
            string expectedSelector, CancellationToken cancellationToken)
        {
            ValidateShelveId(shelveId);
            ValidateBranchRepository(expectedRepository);
            if (expectedSelector == null) throw new ArgumentNullException("expectedSelector");
            var captured = new PlasticWorkspace { RootPath = Path.GetFullPath(root), Repository = expectedRepository, Selector = expectedSelector };
            ValidateShelveContext(captured);
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            if (!SamePath(context.RootPath, captured.RootPath)) throw new ArgumentException("Apply shelveset requires the explicit workspace root.");
            ValidateShelveContext(captured);
            var workspace = await GetWorkspaceAsync(captured.RootPath, cancellationToken).ConfigureAwait(false);
            if (workspace.IsPartial)
                throw new ArgumentException("Applying a shelveset is supported only in a Standard workspace. Partial/Gluon workspaces must use the official Plastic client.");
            ValidateShelveContext(captured);

            using (var structureGate = StructureGate(captured.RootPath))
            using (var mergeGate = OpenMergeGate(captured.RootPath))
            {
                var currentWorkspace = await GetWorkspaceAsync(captured.RootPath, cancellationToken).ConfigureAwait(false);
                if (currentWorkspace.IsPartial)
                    throw new ArgumentException("The workspace changed to Partial/Gluon mode during shelveset preparation; no shelveset was applied.");
                ValidateShelveContext(captured);
                RejectShelveSessions(captured.RootPath);
                // A normal status omits ignored entries. Include all local entries
                // so an ignored/private file at a shelveset path cannot be silently
                // overwritten by the native apply operation.
                var statusResult = await ExecuteAsync(RevisionCommand(captured.RootPath, new[] {
                    "status", captured.RootPath, "--all", "--ignored", "--xml", "--encoding=utf-8", "--fullpaths"
                }), cancellationToken).ConfigureAwait(false);
                RequireSuccess(statusResult);
                var pending = ParseStatus(statusResult.Output, captured.RootPath);
                if (pending.Count != 0)
                    throw new ArgumentException("The workspace has pending changes. Applying a shelveset is refused to prevent overwriting local content; review or save those changes first.");
                var shelves = await GetShelvesAsync(captured.RootPath, cancellationToken).ConfigureAwait(false);
                if (!shelves.Any(item => item.ShelveId == shelveId))
                    throw new ArgumentException("The selected shelveset no longer exists in this repository. Refresh the shelves list.");
                var details = await GetShelveChangesAsync(captured.RootPath, shelveId, cancellationToken).ConfigureAwait(false);
                if (details.Count == 0)
                    throw new InvalidDataException("The selected shelveset has no changed files and cannot be applied safely.");
                ValidateShelveContext(captured);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteAsync(RevisionCommand(captured.RootPath, new[] {
                    "shelveset", "apply", ShelveSpec(shelveId, captured.Repository), "--encoding=utf-8"
                }), cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                    result.Error += " The shelveset may not have been applied; inspect workspace status before retrying.";
                else
                {
                    try { ValidateShelveContext(captured); }
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
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = DiscoverWorkspace(root);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            return await DeleteShelveAsync(root, shelveId, workspace.Repository, workspace.Selector, cancellationToken).ConfigureAwait(false);
        }

        public async Task<PlasticCommandResult> DeleteShelveAsync(string root, long shelveId, string expectedRepository,
            string expectedSelector, CancellationToken cancellationToken)
        {
            ValidateShelveId(shelveId);
            ValidateBranchRepository(expectedRepository);
            if (expectedSelector == null) throw new ArgumentNullException("expectedSelector");
            var captured = new PlasticWorkspace { RootPath = Path.GetFullPath(root), Repository = expectedRepository, Selector = expectedSelector };
            ValidateShelveContext(captured);
            var context = await ValidateShelveRootAsync(root, cancellationToken).ConfigureAwait(false);
            if (!SamePath(context.RootPath, captured.RootPath)) throw new ArgumentException("Delete shelveset requires the explicit workspace root.");
            ValidateShelveContext(captured);
            using (var mergeGate = OpenMergeGate(captured.RootPath))
            {
                var shelves = await GetShelvesAsync(captured.RootPath, cancellationToken).ConfigureAwait(false);
                if (!shelves.Any(item => item.ShelveId == shelveId))
                    throw new ArgumentException("The selected shelveset no longer exists in this repository. Refresh the shelves list.");
                ValidateShelveContext(captured);
                cancellationToken.ThrowIfCancellationRequested();
                var result = await ExecuteAsync(RevisionCommand(captured.RootPath, new[] {
                    "shelveset", "delete", ShelveSpec(shelveId, captured.Repository)
                }), cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                    result.Error += " The shelveset may still exist; refresh the shelves list before retrying.";
                else
                {
                    try
                    {
                        ValidateShelveContext(captured);
                        var remaining = await GetShelvesAsync(captured.RootPath, cancellationToken).ConfigureAwait(false);
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
            cancellationToken.ThrowIfCancellationRequested();
            var captured = DiscoverWorkspace(root);
            if (captured == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            var command = await BuildReadCommandAsync(root, cancellationToken).ConfigureAwait(false);
            if (!SamePath(command.Arguments[1], command.WorkingDirectory))
                throw new ArgumentException("Shelveset operations require the explicit workspace root.");
            if (!SamePath(command.WorkingDirectory, captured.RootPath))
                throw new InvalidOperationException("The selected workspace changed during shelveset preparation. Refresh before continuing.");
            ValidateBranchRepository(captured.Repository);
            ValidateShelveContext(captured);
            return captured;
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

        private async Task DownloadShelveFileAsync(PlasticWorkspace context, string repositoryPath, long shelveId,
            string target, CancellationToken cancellationToken)
        {
            ValidateRepositoryFilePath(repositoryPath);
            RejectReparsePath(target);
            var result = await ExecuteAsync(RevisionCommand(context.RootPath,
                new[] { "cat", ShelveFileSpec(context.Repository, repositoryPath, shelveId), "--file=" + target }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            if (!File.Exists(target)) throw new IOException("Plastic did not produce the requested shelveset file.");
        }

        private static string ShelveFileSpec(string repository, string path, long shelveId)
        { return "serverpath:" + path + "#" + ShelveSpec(shelveId, repository); }

        private static string ValidateShelveOutputDirectory(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3 ||
                path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new ArgumentException("Shelveset export output must be an absolute directory path.");
            if (path.Substring(Path.GetPathRoot(path).Length).IndexOf(':') >= 0)
                throw new ArgumentException("Shelveset export output cannot be an alternate data stream.");
            string output = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (output.Length == Path.GetPathRoot(output).TrimEnd('\\', '/').Length)
                throw new ArgumentException("Shelveset export cannot target a drive root.");
            foreach (string component in output.Substring(Path.GetPathRoot(output).Length).Split('\\', '/'))
            {
                if (String.IsNullOrEmpty(component) || component == "." || component == "..") continue;
                string device = component.Split('.')[0];
                if (component.EndsWith(".") || component.EndsWith(" ") ||
                    new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(device, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Shelveset export output cannot contain ambiguous Windows names or reserved devices.");
            }
            if (output.Split('\\', '/').Any(part => part.Equals(".plastic", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Shelveset export output cannot be workspace metadata.");
            RejectReparsePath(output);
            if (File.Exists(output)) throw new ArgumentException("Shelveset export output must be a directory.");
            if (!Directory.Exists(output) && !Directory.Exists(Path.GetDirectoryName(output)))
                throw new ArgumentException("Choose an output directory with an existing parent.");
            return output;
        }

        private static void MoveExportFile(string source, string target, bool overwrite)
        {
            RejectReparsePath(target);
            if (!overwrite) { File.Move(source, target); return; }
            if (File.Exists(target)) File.Replace(source, target, null);
            else File.Move(source, target);
        }

        private void RejectShelveOutputWorkspace(string output)
        {
            if (DiscoverWorkspace(output) != null)
                throw new ArgumentException("Shelveset export must use a directory outside every Plastic workspace.");
        }

        private void ValidateShelveExportTargets(string output, IEnumerable<string> targets, bool overwrite)
        {
            foreach (string target in targets)
            {
                if (!IsWithin(target, output) || target.Split('\\', '/').Any(part => part.Equals(".plastic", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The shelveset contained an unsafe output path.");
                RejectReparsePath(target);
                if (DiscoverWorkspace(target) != null)
                    throw new ArgumentException("Shelveset export cannot write into a Plastic workspace or nested workspace: " + target);
                if (Directory.Exists(target)) throw new ArgumentException("The shelveset export target is a directory: " + target);
                if (!overwrite && File.Exists(target)) throw new ArgumentException("Export destination exists; choose another directory or explicitly allow overwrite: " + target);
            }
        }

        private static PlasticCommandResult ShelveExportFailure(Exception error, bool destinationTouched)
        {
            return new PlasticCommandResult { ExitCode = 1, Error = "Shelveset export did not finish reliably: " + error.Message +
                (destinationTouched ? " Some destination files may already have been written; inspect the output before retrying." : " No destination file was written.") };
        }

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
