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
        public async Task<PlasticHistoricalFile> GetHistoricalFileAsync(string workspacePath, string repositoryPath, long changeset, CancellationToken cancellationToken)
        {
            var context = await HistoricalContextAsync(workspacePath, repositoryPath, changeset, cancellationToken).ConfigureAwait(false);
            string temporary = NewHistoricalTemporaryDirectory();
            string target = Path.Combine(temporary, "revision" + Path.GetExtension(repositoryPath));
            try
            {
                await DownloadHistoricalFileAsync(context, repositoryPath, changeset, target, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return new PlasticHistoricalFile { RepositoryPath = repositoryPath, Changeset = changeset,
                    RevisionSpec = HistoricalSpec(context.Repository, repositoryPath, changeset), Content = File.ReadAllBytes(target) };
            }
            finally { RemoveHistoricalTemporaryDirectory(temporary, target); }
        }

        public async Task<PlasticCommandResult> ExportRevisionAsync(string workspacePath, string repositoryPath, long changeset, string outputPath, bool overwrite, CancellationToken cancellationToken)
        {
            string output = ValidateHistoricalOutput(outputPath, overwrite);
            using (var gate = OpenPartialDirectoryOutputGate(output))
                return await ExportRevisionLockedAsync(workspacePath, repositoryPath, changeset, output, overwrite, cancellationToken).ConfigureAwait(false);
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

        private async Task<PlasticCommandResult> ExportRevisionLockedAsync(string workspacePath, string repositoryPath, long changeset, string outputPath, bool overwrite, CancellationToken cancellationToken)
        {
            string output = ValidateHistoricalOutput(outputPath, overwrite);
            var context = await HistoricalContextAsync(workspacePath, repositoryPath, changeset, cancellationToken).ConfigureAwait(false);
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

        public async Task<PlasticDiffResult> GetRevisionDiffAsync(string workspacePath, string repositoryPath, long fromChangeset, long toChangeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(fromChangeset); ValidateChangeset(toChangeset);
            var before = await GetHistoricalFileAsync(workspacePath, repositoryPath, fromChangeset, cancellationToken).ConfigureAwait(false);
            var after = await GetHistoricalFileAsync(workspacePath, repositoryPath, toChangeset, cancellationToken).ConfigureAwait(false);
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diff = CompareContent(repositoryPath, before.RevisionSpec, before.Content, after.Content, false);
                if (!diff.IsBinary && diff.HasChanges)
                    diff.DiffText = diff.DiffText.Replace("--- " + repositoryPath + " (base)\n+++ " + repositoryPath + " (working)\n",
                        "--- " + repositoryPath + " (cs:" + fromChangeset.ToString(CultureInfo.InvariantCulture) + ")\n+++ " + repositoryPath + " (cs:" + toChangeset.ToString(CultureInfo.InvariantCulture) + ")\n");
                return diff;
            }, cancellationToken).ConfigureAwait(false);
        }

        public async Task<PlasticCommandResult> OpenRevisionDiffToolAsync(string workspacePath, string repositoryPath, long fromChangeset, long toChangeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(fromChangeset); ValidateChangeset(toChangeset);
            var context = await HistoricalContextAsync(workspacePath, repositoryPath, fromChangeset, cancellationToken).ConfigureAwait(false);
            // Validate both endpoints even for the native viewer, which otherwise owns errors
            // in its GUI. Missing historical paths remain explicit errors, never empty bytes.
            await ValidateHistoricalFileAsync(context, repositoryPath, fromChangeset, cancellationToken).ConfigureAwait(false);
            await ValidateHistoricalFileAsync(context, repositoryPath, toChangeset, cancellationToken).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(config.DiffToolPath))
                return await ExecuteAsync(new PlasticProcessCommand { FileName = config.CmPath, WorkingDirectory = context.RootPath, Interactive = true,
                    Arguments = new List<string> { "diff", HistoricalSpec(context.Repository, repositoryPath, fromChangeset),
                        HistoricalSpec(context.Repository, repositoryPath, toChangeset) } }, cancellationToken).ConfigureAwait(false);
            PlasticToolArguments.ValidateConfiguration(config.DiffToolPath, config.DiffToolArguments, false);
            string temporary = NewHistoricalTemporaryDirectory();
            string before = Path.Combine(temporary, "from-" + fromChangeset.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(repositoryPath));
            string after = Path.Combine(temporary, "to-" + toChangeset.ToString(CultureInfo.InvariantCulture) + Path.GetExtension(repositoryPath));
            try
            {
                await DownloadHistoricalFileAsync(context, repositoryPath, fromChangeset, before, cancellationToken).ConfigureAwait(false);
                await DownloadHistoricalFileAsync(context, repositoryPath, toChangeset, after, cancellationToken).ConfigureAwait(false);
                File.SetAttributes(before, File.GetAttributes(before) | FileAttributes.ReadOnly);
                File.SetAttributes(after, File.GetAttributes(after) | FileAttributes.ReadOnly);
                return await ExecuteAsync(new PlasticProcessCommand { FileName = config.DiffToolPath, WorkingDirectory = temporary,
                    Arguments = PlasticToolArguments.Expand(config.DiffToolArguments,
                        new Dictionary<string, string> { { "base", before }, { "local", after } }, false) }, cancellationToken).ConfigureAwait(false);
            }
            finally { RemoveHistoricalTemporaryDirectory(temporary, before, after); }
        }

        private async Task<PlasticWorkspace> HistoricalContextAsync(string workspacePath, string repositoryPath, long changeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(changeset); ValidateRepositoryFilePath(repositoryPath);
            var command = await BuildReadCommandAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            var context = DiscoverWorkspace(command.WorkingDirectory);
            if (String.IsNullOrWhiteSpace(context.Repository)) throw new InvalidDataException("Workspace selector does not identify a repository.");
            return context;
        }

        private async Task ValidateHistoricalFileAsync(PlasticWorkspace context, string repositoryPath, long changeset, CancellationToken cancellationToken)
        {
            var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "ls", repositoryPath,
                "--tree=cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + context.Repository, "--xml", "--encoding=utf-8" }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            var document = SafeXml.Load(result.Output);
            if (document.Root == null || document.Root.Name != "LsResults") throw new InvalidDataException("Unexpected historical file listing.");
            var items = document.Descendants("LsItem").Where(item => String.Equals((string)item.Element("CurrentPath"), repositoryPath, StringComparison.Ordinal)).ToList();
            if (items.Count != 1) throw new ArgumentException("The file does not exist at cs:" + changeset.ToString(CultureInfo.InvariantCulture) + ": " + repositoryPath);
            var file = items[0];
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
