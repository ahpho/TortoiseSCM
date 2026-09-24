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
    public sealed class PlasticCommandException : Exception
    {
        public PlasticCommandResult Result { get; private set; }
        public PlasticCommandException(PlasticCommandResult result)
            : base("Plastic command failed (" + result.ExitCode + "): " +
                (String.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error)) { Result = result; }
    }

    public sealed class PlasticHistoryItem
    {
        public string Path { get; set; }
        public string RevisionSpec { get; set; }
        public long Changeset { get; set; }
        public string CreationDate { get; set; }
        public string Owner { get; set; }
        public string Branch { get; set; }
        public string Comment { get; set; }
        public string Repository { get; set; }
    }

    public sealed class PlasticDiffResult
    {
        public string Path { get; set; }
        public string BaseRevision { get; set; }
        public string DiffText { get; set; }
        public bool IsBinary { get; set; }
        public bool HasChanges { get; set; }
    }

    public sealed partial class PlasticClient
    {
        // plastic.workspace's third line can remain "Standard" after conversion to Gluon.
        // Query the lightweight status header instead of treating that local hint as authoritative.
        public async Task<PlasticWorkspace> GetWorkspaceAsync(string path, CancellationToken cancellationToken)
        {
            var workspace = await Task.Run(() => DiscoverWorkspace(path), cancellationToken).ConfigureAwait(false);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            var result = await ExecuteAsync(new PlasticProcessCommand { FileName = config.CmPath, WorkingDirectory = workspace.RootPath,
                Arguments = new List<string> { "status", workspace.RootPath, "--header", "--xml", "--encoding=utf-8" } }, cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            XDocument status = SafeXml.Load(result.Output);
            XElement value = status.Root == null ? null : status.Root.Element("WorkspaceStatus");
            value = value == null ? null : value.Element("Status");
            long changeset;
            if (value == null || !Int64.TryParse((string)value.Element("Changeset"), out changeset) || changeset < -1)
                throw new InvalidDataException("Workspace status does not identify a valid loaded changeset.");
            workspace.IsPartial = changeset == -1;
            return workspace;
        }

        private static void ApplyWorkspaceMode(PlasticProcessCommand command, bool partial)
        {
            var args = command.Arguments.ToList();
            if (args.Count > 0 && args[0] == "partial") args.RemoveAt(0);
            if (!partial && args.Count > 1 && args[0] == "update" &&
                !args[1].TrimEnd('\\', '/').Equals(command.WorkingDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("完整工作区必须更新根目录；请选择工作区根目录并确认整个工作区更新。未执行任何更新。");
            if (!partial && args.Count > 1 && args[0] == "update" && !args.Contains("--last")) args.Add("--last");
            if (partial) args.Insert(0, "partial");
            if (partial && args.Contains("--private")) throw new ArgumentException("Add private files before checking in a partial workspace.");
            if (args.Contains("update"))
            {
                args.Remove("--report");
                if (partial) args.Add("--report");
            }
            command.Arguments = args;
        }

        public async Task<IList<PlasticHistoryItem>> GetHistoryAsync(string path, CancellationToken cancellationToken)
        {
            var command = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            bool directory = Directory.Exists(command.Arguments[1]);
            if (!directory && !File.Exists(command.Arguments[1]))
            {
                var info = await ExecuteAsync(new PlasticProcessCommand { FileName = config.CmPath, WorkingDirectory = command.WorkingDirectory,
                    Arguments = new [] { "fileinfo", command.Arguments[1], "--xml", "--encoding=utf-8" } }, cancellationToken).ConfigureAwait(false);
                if (info.Succeeded)
                {
                    XElement item = SafeXml.Load(info.Output).Descendants("FileInfo").FirstOrDefault();
                    directory = item != null && String.Equals((string)item.Element("Type"), "dir", StringComparison.OrdinalIgnoreCase);
                }
            }
            if (directory)
                return await GetDirectoryHistoryAsync(command.Arguments[1], command.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            var result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            return ParseHistory(result.Output);
        }

        public static IList<PlasticHistoryItem> ParseHistory(string xml)
        {
            XDocument document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "RevisionHistoriesResult")
                throw new InvalidDataException("Unexpected Plastic history XML.");
            var items = new List<PlasticHistoryItem>();
            foreach (XElement entry in document.Descendants("RevisionHistory"))
                foreach (XElement revision in entry.Descendants("Revision"))
                {
                    long changeset;
                    if (!Int64.TryParse((string)revision.Element("ChangesetNumber"), NumberStyles.Integer, CultureInfo.InvariantCulture, out changeset))
                        throw new InvalidDataException("History entry has no valid changeset number.");
                    items.Add(new PlasticHistoryItem { Path = (string)entry.Element("ItemName") ?? "",
                        RevisionSpec = (string)revision.Element("RevisionSpec") ?? "", Changeset = changeset,
                        CreationDate = (string)revision.Element("CreationDate") ?? "", Owner = (string)revision.Element("Owner") ?? "",
                        Branch = (string)revision.Element("Branch") ?? "", Comment = (string)revision.Element("Comment") ?? "",
                        Repository = (string)revision.Element("Repository") ?? "" });
                }
            return items;
        }

        public async Task<PlasticDiffResult> GetDiffTextAsync(string path, CancellationToken cancellationToken)
        {
            var command = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            string absolute = command.Arguments[1];
            if (Directory.Exists(absolute)) throw new ArgumentException("Select one controlled file to compare.");
            command.Arguments = new List<string> { "fileinfo", absolute, "--xml", "--encoding=utf-8" };
            var info = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            RequireSuccess(info);
            XElement file = SafeXml.Load(info.Output).Descendants("FileInfo").FirstOrDefault();
            long changeset;
            if (file == null || !Int64.TryParse((string)file.Element("RevisionChangeset"), out changeset) || changeset < 0)
                throw new InvalidOperationException("The file has no checked-in base revision to compare.");
            string revision = "rev:" + absolute + "#cs:" + changeset.ToString(CultureInfo.InvariantCulture);
            // A unique, empty private directory keeps cm output away from workspace files.
            string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TortoiseSCM-diff-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string baseFile = System.IO.Path.Combine(temporary, "base");
            try
            {
                command.Arguments = new List<string> { "cat", revision, "--file=" + baseFile };
                var download = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                RequireSuccess(download);
                return await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectReparsePath(absolute);
                    // An absent controlled file is a local deletion, represented by empty target bytes.
                    byte[] before = File.ReadAllBytes(baseFile);
                    byte[] after = File.Exists(absolute) ? File.ReadAllBytes(absolute) : new byte[0];
                    return CompareContent(absolute, revision, before, after,
                        String.Equals((string)file.Element("Type"), "bin", StringComparison.OrdinalIgnoreCase));
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(baseFile)) File.Delete(baseFile);
                Directory.Delete(temporary);
            }
        }

        private Task<PlasticProcessCommand> BuildReadCommandAsync(string path, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                string absolute = System.IO.Path.GetFullPath(path);
                var workspace = DiscoverWorkspace(absolute);
                if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
                return Build(new PlasticCommandRequest { Command = PlasticCommand.History, WorkingDirectory = workspace.RootPath,
                    Paths = new List<string> { absolute } }, cancellationToken);
            }, cancellationToken);
        }

        private static void RequireSuccess(PlasticCommandResult result)
        {
            if (!result.Succeeded) throw new PlasticCommandException(result);
        }

        // A single replacement hunk is deliberate: linear memory/time, including large source files.
        // Common leading/trailing lines remain context; unchanged lines inside the hunk may be replaced.
        internal static PlasticDiffResult CompareContent(string path, string revision, byte[] before, byte[] after, bool binary)
        {
            string left, right;
            bool isText = TryDecodeText(before, out left) & TryDecodeText(after, out right);
            var result = new PlasticDiffResult { Path = path, BaseRevision = revision, HasChanges = !before.SequenceEqual(after),
                IsBinary = binary || !isText, DiffText = "" };
            if (!result.HasChanges) return result;
            if (result.IsBinary) { result.DiffText = "Binary files differ: " + path + "\n"; return result; }
            string[] oldLines = TextLines(left), newLines = TextLines(right);
            int prefix = 0, suffix = 0;
            while (prefix < oldLines.Length && prefix < newLines.Length && oldLines[prefix] == newLines[prefix]) prefix++;
            while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix &&
                oldLines[oldLines.Length - suffix - 1] == newLines[newLines.Length - suffix - 1]) suffix++;
            if (prefix == oldLines.Length && prefix == newLines.Length)
            { result.DiffText = "Text encoding or byte-order mark differs: " + path + "\n"; return result; }
            int start = Math.Max(0, prefix - 3), contextAfter = Math.Min(3, suffix);
            int oldEnd = oldLines.Length - suffix + contextAfter, newEnd = newLines.Length - suffix + contextAfter;
            var diff = new StringBuilder();
            diff.Append("--- ").Append(path).Append(" (base)\n+++ ").Append(path).Append(" (working)\n@@ -")
                .Append(oldEnd == start ? start : start + 1).Append(',').Append(oldEnd - start).Append(" +")
                .Append(newEnd == start ? start : start + 1).Append(',').Append(newEnd - start).Append(" @@\n");
            for (int i = start; i < prefix; i++) AppendDiffLine(diff, ' ', oldLines[i]);
            for (int i = prefix; i < oldLines.Length - suffix; i++) AppendDiffLine(diff, '-', oldLines[i]);
            for (int i = prefix; i < newLines.Length - suffix; i++) AppendDiffLine(diff, '+', newLines[i]);
            for (int i = oldLines.Length - suffix; i < oldEnd; i++) AppendDiffLine(diff, ' ', oldLines[i]);
            result.DiffText = diff.ToString(); return result;
        }

        private static string[] TextLines(string text)
        {
            var lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++) if (text[i] == '\n') { lines.Add(text.Substring(start, i - start + 1)); start = i + 1; }
            if (start < text.Length) lines.Add(text.Substring(start));
            return lines.ToArray();
        }

        private static void AppendDiffLine(StringBuilder diff, char kind, string line)
        {
            diff.Append(kind).Append(line);
            if (!line.EndsWith("\n", StringComparison.Ordinal)) diff.Append("\n\\ No newline at end of file\n");
        }

        private static bool TryDecodeText(byte[] bytes, out string text)
        {
            text = null;
            try
            {
                int skip = 0;
                Encoding encoding = new UTF8Encoding(false, true);
                if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) skip = 3;
                else if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
                else if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
                text = encoding.GetString(bytes, skip, bytes.Length - skip);
                return !text.Any(c => c == '\0' || (Char.IsControl(c) && c != '\r' && c != '\n' && c != '\t' && c != '\f'));
            }
            catch (DecoderFallbackException) { return false; }
        }
    }
}
