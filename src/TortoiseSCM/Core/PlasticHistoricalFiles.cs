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
    public sealed class PlasticHistoricalFile
    {
        public string RepositoryPath { get; set; }
        public long Changeset { get; set; }
        public string RevisionSpec { get; set; }
        public byte[] Content { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public Task<PlasticHistoricalFile> GetHistoricalFileAsync(string workspacePath, string repositoryPath, long changeset, CancellationToken cancellationToken)
        { return GetHistoricalFileAsync(workspacePath, repositoryPath, changeset, null, cancellationToken); }

        public async Task<PlasticHistoricalFile> GetHistoricalFileAsync(string workspacePath, string repositoryPath, long changeset, string expectedRepository, CancellationToken cancellationToken)
        {
            var context = await HistoricalContextAsync(workspacePath, repositoryPath, changeset, cancellationToken).ConfigureAwait(false);
            ValidateExpectedHistoricalRepository(context, expectedRepository);
            string temporary = NewHistoricalTemporaryDirectory();
            string target = Path.Combine(temporary, "revision" + Path.GetExtension(repositoryPath));
            try
            {
                await DownloadHistoricalFileAsync(context, repositoryPath, changeset, target, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateHistoricalContext(context);
                return new PlasticHistoricalFile { RepositoryPath = repositoryPath, Changeset = changeset,
                    RevisionSpec = HistoricalSpec(context.Repository, repositoryPath, changeset), Content = File.ReadAllBytes(target) };
            }
            finally { RemoveHistoricalTemporaryDirectory(temporary, target); }
        }

        public Task<PlasticCommandResult> ExportRevisionAsync(string workspacePath, string repositoryPath, long changeset, string outputPath, bool overwrite, CancellationToken cancellationToken)
        { return ExportRevisionAsync(workspacePath, repositoryPath, changeset, outputPath, overwrite, null, cancellationToken); }

        public async Task<PlasticCommandResult> ExportRevisionAsync(string workspacePath, string repositoryPath, long changeset, string outputPath, bool overwrite, string expectedRepository, CancellationToken cancellationToken)
        {
            string output = ValidateHistoricalOutput(outputPath, overwrite);
            using (var gate = OpenPartialDirectoryOutputGate(output))
                return await ExportRevisionLockedAsync(workspacePath, repositoryPath, changeset, output, overwrite, expectedRepository, cancellationToken).ConfigureAwait(false);
        }

        private FileStream OpenPartialDirectoryOutputGate(string output)
        {
            output = ValidateHistoricalOutput(output, true);
            var workspace = DiscoverWorkspace(output);
            if (workspace == null) return null;
            var gate = StructureGate(workspace.RootPath);
            try { ThrowIfPartialDirectoryActive(workspace.RootPath); return gate; }
            catch { gate.Dispose(); throw; }
        }

        private async Task<PlasticCommandResult> ExportRevisionLockedAsync(string workspacePath, string repositoryPath, long changeset, string outputPath, bool overwrite, string expectedRepository, CancellationToken cancellationToken)
        {
            string output = ValidateHistoricalOutput(outputPath, overwrite);
            var context = await HistoricalContextAsync(workspacePath, repositoryPath, changeset, cancellationToken).ConfigureAwait(false);
            ValidateExpectedHistoricalRepository(context, expectedRepository);
            // Download outside the destination first: server failure never touches existing bytes.
            string temporary = NewHistoricalTemporaryDirectory();
            string downloaded = Path.Combine(temporary, "revision");
            string staged = Path.Combine(Path.GetDirectoryName(output), ".tortoisescm-export-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                await DownloadHistoricalFileAsync(context, repositoryPath, changeset, downloaded, cancellationToken).ConfigureAwait(false);
                ValidateHistoricalOutput(output, overwrite);
                using (var source = new FileStream(downloaded, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await source.CopyToAsync(destination, 81920, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateHistoricalOutput(output, overwrite);
                ValidateHistoricalContext(context);
                // Move(CreateNew) is atomic and refuses races. Replace replaces the directory
                // entry instead of changing other hard links to the previous destination.
                if (overwrite && File.Exists(output)) File.Replace(staged, output, null);
                else File.Move(staged, output);
                return new PlasticCommandResult { ExitCode = 0, Output = "Exported " + HistoricalSpec(context.Repository, repositoryPath, changeset) + Environment.NewLine + output };
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
                RemoveHistoricalTemporaryDirectory(temporary, downloaded);
            }
        }

        public Task<PlasticDiffResult> GetRevisionDiffAsync(string workspacePath, string repositoryPath, long fromChangeset, long toChangeset, CancellationToken cancellationToken)
        { return GetRevisionDiffAsync(workspacePath, repositoryPath, repositoryPath, fromChangeset, toChangeset, cancellationToken); }

        public async Task<PlasticDiffResult> GetRevisionDiffAsync(string workspacePath, string fromRepositoryPath, string toRepositoryPath, long fromChangeset, long toChangeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(fromChangeset); ValidateChangeset(toChangeset);
            ValidateRepositoryFilePath(fromRepositoryPath); ValidateRepositoryFilePath(toRepositoryPath);
            var context = await HistoricalContextAsync(workspacePath, fromRepositoryPath, fromChangeset, cancellationToken).ConfigureAwait(false);
            string temporary = NewHistoricalTemporaryDirectory();
            string beforePath = Path.Combine(temporary, "from"), afterPath = Path.Combine(temporary, "to");
            try
            {
                await DownloadHistoricalFileAsync(context, fromRepositoryPath, fromChangeset, beforePath, cancellationToken).ConfigureAwait(false);
                await DownloadHistoricalFileAsync(context, toRepositoryPath, toChangeset, afterPath, cancellationToken).ConfigureAwait(false);
                ValidateHistoricalContext(context);
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var diff = CompareContent(toRepositoryPath, HistoricalSpec(context.Repository, fromRepositoryPath, fromChangeset), File.ReadAllBytes(beforePath), File.ReadAllBytes(afterPath), false);
                    if (!diff.IsBinary && diff.HasChanges)
                        diff.DiffText = diff.DiffText.Replace("--- " + toRepositoryPath + " (base)\n+++ " + toRepositoryPath + " (working)\n",
                            "--- " + fromRepositoryPath + " (cs:" + fromChangeset.ToString(CultureInfo.InvariantCulture) + ")\n+++ " + toRepositoryPath + " (cs:" + toChangeset.ToString(CultureInfo.InvariantCulture) + ")\n");
                    ValidateHistoricalContext(context);
                    return diff;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { RemoveHistoricalTemporaryDirectory(temporary, beforePath, afterPath); }
        }

        public Task<PlasticCommandResult> OpenRevisionDiffToolAsync(string workspacePath, string repositoryPath, long fromChangeset, long toChangeset, CancellationToken cancellationToken)
        { return OpenRevisionDiffToolAsync(workspacePath, repositoryPath, repositoryPath, fromChangeset, toChangeset, cancellationToken); }

        public Task<PlasticCommandResult> OpenRevisionDiffToolAsync(string workspacePath, string fromRepositoryPath, string toRepositoryPath, long fromChangeset, long toChangeset, CancellationToken cancellationToken)
        { return OpenRevisionDiffToolAsync(workspacePath, fromRepositoryPath, toRepositoryPath, fromChangeset, toChangeset, null, cancellationToken); }

        public async Task<PlasticCommandResult> OpenRevisionDiffToolAsync(string workspacePath, string fromRepositoryPath, string toRepositoryPath,
            long fromChangeset, long toChangeset, string expectedRepository, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string beyondCompare = config.UseBeyondCompare ? BeyondCompareTool.ResolveExecutable(config.BeyondComparePath) : null;
            var host = !config.UseBeyondCompare && config.UseBuiltInDiff ? RequireToolHost() : null;
            ValidateChangeset(fromChangeset); ValidateChangeset(toChangeset);
            ValidateRepositoryFilePath(fromRepositoryPath); ValidateRepositoryFilePath(toRepositoryPath);
            var context = await HistoricalContextAsync(workspacePath, fromRepositoryPath, fromChangeset, cancellationToken).ConfigureAwait(false);
            ValidateExpectedHistoricalRepository(context, expectedRepository);
            // Validate both endpoints even for the native viewer, which otherwise owns errors
            // in its GUI. Missing historical paths remain explicit errors, never empty bytes.
            await ValidateHistoricalFileAsync(context, fromRepositoryPath, fromChangeset, cancellationToken).ConfigureAwait(false);
            await ValidateHistoricalFileAsync(context, toRepositoryPath, toChangeset, cancellationToken).ConfigureAwait(false);
            ValidateHistoricalContext(context);
            if (host == null && beyondCompare == null && String.IsNullOrWhiteSpace(config.DiffToolPath))
                return await ExecuteAsync(new PlasticProcessCommand { FileName = config.CmPath, WorkingDirectory = context.RootPath, Interactive = true,
                    Arguments = new List<string> { "diff", HistoricalSpec(context.Repository, fromRepositoryPath, fromChangeset),
                        HistoricalSpec(context.Repository, toRepositoryPath, toChangeset) } }, cancellationToken).ConfigureAwait(false);
            if (host == null && beyondCompare == null) PlasticToolArguments.ValidateConfiguration(config.DiffToolPath, config.DiffToolArguments, false);
            string temporary = NewHistoricalTemporaryDirectory();
            string before = Path.Combine(temporary, "from-" + fromChangeset.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(fromRepositoryPath));
            string after = Path.Combine(temporary, "to-" + toChangeset.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(toRepositoryPath));
            bool preserve = false;
            try
            {
                await DownloadHistoricalFileAsync(context, fromRepositoryPath, fromChangeset, before, cancellationToken).ConfigureAwait(false);
                await DownloadHistoricalFileAsync(context, toRepositoryPath, toChangeset, after, cancellationToken).ConfigureAwait(false);
                ValidateHistoricalContext(context);
                File.SetAttributes(before, File.GetAttributes(before) | FileAttributes.ReadOnly);
                File.SetAttributes(after, File.GetAttributes(after) | FileAttributes.ReadOnly);
                cancellationToken.ThrowIfCancellationRequested();
                if (host != null) return await host.ShowDiffAsync(before, after, cancellationToken).ConfigureAwait(false);
                if (beyondCompare != null)
                    return await BeyondCompareProcess.RunAsync(beyondCompare,
                        HistoricalBeyondCompareArguments(before, after,
                            HistoricalSpec(context.Repository, fromRepositoryPath, fromChangeset),
                            HistoricalSpec(context.Repository, toRepositoryPath, toChangeset)), temporary, cancellationToken).ConfigureAwait(false);
                return await ExecuteAsync(new PlasticProcessCommand { FileName = config.DiffToolPath, WorkingDirectory = temporary,
                    Arguments = PlasticToolArguments.Expand(config.DiffToolArguments,
                        new Dictionary<string, string> { { "base", before }, { "local", after } }, false) }, cancellationToken).ConfigureAwait(false);
            }
            catch (BeyondCompareWaitException) { preserve = true; throw; }
            finally { if (!preserve) RemoveHistoricalTemporaryDirectory(temporary, before, after); }
        }

        // A missing endpoint is only represented by empty bytes after the server has
        // re-proved the selected added/deleted row in this exact pair of complete trees.
        // Ordinary historical comparisons continue to reject missing paths.
        public async Task<PlasticCommandResult> OpenChangesetFileDiffToolAsync(string workspacePath,
            PlasticChangesetComparison comparison, PlasticChangesetFile file, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (comparison == null || file == null) throw new ArgumentNullException(comparison == null ? "comparison" : "file");
            string repository = comparison.Repository, root = comparison.RootPath;
            long fromChangeset = comparison.FromChangeset, toChangeset = comparison.ToChangeset;
            string path = file.Path, oldPath = file.OldPath ?? "", status = file.Status, itemType = file.ItemType;
            if (!new[] { "A", "C", "D", "M" }.Contains(status) || !new[] { "F", "B" }.Contains(itemType))
                throw new ArgumentException("请选择普通文件或二进制文件的变更；目录、符号链接和跨仓库链接不能作为文件比较。");
            string sourcePath = String.IsNullOrEmpty(oldPath) ? path : oldPath;
            ValidateRepositoryFilePath(sourcePath); ValidateRepositoryFilePath(path);
            ValidateChangeset(fromChangeset); ValidateChangeset(toChangeset);
            string executable = BeyondCompareTool.ResolveExecutable(config.BeyondComparePath);
            var context = await HistoricalContextAsync(workspacePath, sourcePath, fromChangeset, cancellationToken).ConfigureAwait(false);
            if (context.Repository != repository || String.IsNullOrWhiteSpace(root) || !SamePath(context.RootPath, root))
                throw new InvalidOperationException("工作区或仓库与已查看的变更集比较不一致，请刷新后重试。");
            var current = await GetChangesetComparisonAsync(context.RootPath, fromChangeset, toChangeset, cancellationToken).ConfigureAwait(false);
            ValidateHistoricalContext(context);
            if (current.Repository != repository || !SamePath(current.RootPath, root) || current.Files.Count(item =>
                item.Status == status && item.Path == path && (item.OldPath ?? "") == oldPath && item.ItemType == itemType) != 1)
                throw new InvalidOperationException("所选文件与服务器的变更集比较不一致，请刷新后重试。");

            string temporary = NewHistoricalTemporaryDirectory();
            string before = Path.Combine(temporary, "from-" + fromChangeset.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(sourcePath));
            string after = Path.Combine(temporary, "to-" + toChangeset.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(path));
            bool preserve = false;
            try
            {
                if (status == "A") File.WriteAllBytes(before, new byte[0]);
                else await DownloadHistoricalFileAsync(context, sourcePath, fromChangeset, before, cancellationToken).ConfigureAwait(false);
                if (status == "D") File.WriteAllBytes(after, new byte[0]);
                else await DownloadHistoricalFileAsync(context, path, toChangeset, after, cancellationToken).ConfigureAwait(false);
                ValidateHistoricalContext(context);
                File.SetAttributes(before, File.GetAttributes(before) | FileAttributes.ReadOnly);
                File.SetAttributes(after, File.GetAttributes(after) | FileAttributes.ReadOnly);
                string beforeTitle = HistoricalSpec(repository, sourcePath, fromChangeset) + (status == "A" ? " (不存在 / empty)" : "");
                string afterTitle = HistoricalSpec(repository, path, toChangeset) + (status == "D" ? " (不存在 / empty)" : "");
                cancellationToken.ThrowIfCancellationRequested();
                return await BeyondCompareProcess.RunAsync(executable, HistoricalBeyondCompareArguments(before, after, beforeTitle, afterTitle),
                    temporary, cancellationToken).ConfigureAwait(false);
            }
            catch (BeyondCompareWaitException) { preserve = true; throw; }
            finally { if (!preserve) RemoveHistoricalTemporaryDirectory(temporary, before, after); }
        }

        private static IList<string> HistoricalBeyondCompareArguments(string before, string after, string beforeTitle, string afterTitle)
        {
            // Titles are arguments, never text interpolated into the command template.
            return new List<string> { "/solo", "/readonly", before, after, "/lefttitle=" + beforeTitle, "/righttitle=" + afterTitle };
        }

        private async Task<PlasticWorkspace> HistoricalContextAsync(string workspacePath, string repositoryPath, long changeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(changeset); ValidateRepositoryFilePath(repositoryPath);
            cancellationToken.ThrowIfCancellationRequested();
            var context = DiscoverWorkspace(workspacePath);
            if (context == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            ValidateBranchRepository(context.Repository);
            var command = await BuildReadCommandAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            if (!SamePath(command.WorkingDirectory, context.RootPath))
                throw new InvalidOperationException("The selected workspace changed during historical file preparation. Refresh before continuing.");
            ValidateHistoricalContext(context);
            return context;
        }

        private void ValidateHistoricalContext(PlasticWorkspace expected)
        {
            var current = DiscoverWorkspace(expected.RootPath);
            // A converted Gluon workspace can retain "Standard" in plastic.workspace.
            // GetWorkspaceAsync obtains authoritative mode from cm status, so that
            // value must not be compared with DiscoverWorkspace's legacy mode hint.
            // Pinned historical reads require the same workspace and selector;
            // mutation callers retain their own authoritative mode/load-rule guards.
            if (current == null || !SamePath(current.RootPath, expected.RootPath) || current.Name != expected.Name || current.Repository != expected.Repository ||
                NormalizeMergeSelector(current.Selector) != NormalizeMergeSelector(expected.Selector))
                throw new InvalidOperationException("The workspace repository or selector changed during the historical file operation. Refresh before continuing.");
        }

        private static void ValidateExpectedHistoricalRepository(PlasticWorkspace context, string expectedRepository)
        {
            if (expectedRepository != null && context.Repository != expectedRepository)
                throw new InvalidOperationException("The workspace repository no longer matches the reviewed historical snapshot. Refresh before continuing.");
        }

        private async Task ValidateHistoricalFileAsync(PlasticWorkspace context, string repositoryPath, long changeset, CancellationToken cancellationToken)
        {
            ValidateHistoricalContext(context);
            var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "ls", repositoryPath,
                "--tree=cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + context.Repository, "--xml", "--encoding=utf-8" }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            ValidateHistoricalContext(context);
            var document = SafeXml.Load(result.Output);
            if (document.Root == null || document.Root.Name != "LsResults") throw new InvalidDataException("Unexpected historical file listing.");
            var items = document.Descendants("LsItem").Where(item => String.Equals((string)item.Element("CurrentPath"), repositoryPath, StringComparison.Ordinal)).ToList();
            if (items.Count != 1) throw new ArgumentException("The file does not exist at cs:" + changeset.ToString(CultureInfo.InvariantCulture) + ": " + repositoryPath);
            var file = items[0];
            string repository = (string)file.Element("Repository");
            if (!String.IsNullOrEmpty(repository) && repository != context.Repository && repository != "rep:" + context.Repository)
                throw new ArgumentException("Historical file belongs to another repository; cross-repository links cannot be compared or exported.");
            string type = (string)file.Element("Type") ?? "";
            if ((string)file.Element("Name") == "." || type.Equals("dir", StringComparison.OrdinalIgnoreCase) || type.Equals("directory", StringComparison.OrdinalIgnoreCase) || type == "目录")
                throw new ArgumentException("Select one historical file; directories cannot be exported or compared as files.");
            if (!String.IsNullOrEmpty((string)file.Element("SymlinkTarget"))) throw new ArgumentException("Historical symbolic links are not supported.");
        }

        private async Task DownloadHistoricalFileAsync(PlasticWorkspace context, string repositoryPath, long changeset, string target, CancellationToken cancellationToken)
        {
            await ValidateHistoricalFileAsync(context, repositoryPath, changeset, cancellationToken).ConfigureAwait(false);
            var result = await ExecuteAsync(RevisionCommand(context.RootPath,
                new[] { "cat", HistoricalSpec(context.Repository, repositoryPath, changeset), "--file=" + target }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            ValidateHistoricalContext(context);
            if (!File.Exists(target)) throw new IOException("Plastic did not produce the requested historical file.");
        }

        private static string HistoricalSpec(string repository, string path, long changeset)
        { return "serverpath:" + path + "#cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + repository; }

        private static void ValidateRepositoryFilePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path.Length < 2 || path[0] != '/' || path.IndexOfAny(new[] { '\\', ':', '*', '?', '"', '<', '>', '|', '#', '@' }) >= 0 || path.Any(Char.IsControl))
                throw new ArgumentException("Use an explicit repository file path such as /folder/file.txt; revision-spec delimiters and wildcards are not allowed.");
            foreach (string part in path.Substring(1).Split('/'))
                if (String.IsNullOrEmpty(part) || part == "." || part == ".." || part.Equals(".plastic", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".") || part.EndsWith(" "))
                    throw new ArgumentException("Repository paths cannot contain traversal, empty components, metadata or ambiguous Windows names.");
        }

        private static string ValidateHistoricalOutput(string path, bool overwrite)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3 || path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
                throw new ArgumentException("Export output must be an absolute file path.");
            if (path.Substring(Path.GetPathRoot(path).Length).IndexOf(':') >= 0)
                throw new ArgumentException("Export output cannot be an alternate data stream.");
            foreach (string component in path.Substring(Path.GetPathRoot(path).Length).Split('\\', '/'))
            {
                if (component == "." || component == "..") continue;
                string device = component.Split('.')[0];
                if (component.EndsWith(".") || component.EndsWith(" ") ||
                    new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
                        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(device, StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException("Export output cannot contain ambiguous Windows names or reserved devices.");
            }
            string output = Path.GetFullPath(path);
            if (output.Substring(Path.GetPathRoot(output).Length).IndexOf(':') >= 0 || output.Split('\\', '/').Any(p => p.Equals(".plastic", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Export output cannot be workspace metadata or an alternate data stream.");
            RejectReparsePath(output);
            if (Directory.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output))) throw new ArgumentException("Choose an output file in an existing directory.");
            if (!overwrite && File.Exists(output)) throw new ArgumentException("Export destination exists; choose another file or explicitly allow overwrite.");
            return output;
        }

        private static string NewHistoricalTemporaryDirectory()
        {
            string path = Path.Combine(Path.GetTempPath(), "TortoiseSCM-history-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); return path;
        }

        private static void RemoveHistoricalTemporaryDirectory(string directory, params string[] files)
        {
            foreach (string file in files)
                if (File.Exists(file)) { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
            Directory.Delete(directory);
        }
    }
}
