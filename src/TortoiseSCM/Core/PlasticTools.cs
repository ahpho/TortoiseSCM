// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    // Templates are split before expansion. A path containing quotes, spaces or shell
    // metacharacters remains one argument, then ExecuteAsync quotes it for CreateProcess.
    public static class PlasticToolArguments
    {
        public static void ValidateConfiguration(string executable, string template, bool merge)
        {
            if (String.IsNullOrWhiteSpace(executable)) return;
            if (!Path.IsPathRooted(executable) || Path.GetPathRoot(executable).Length < 3 || !File.Exists(executable) ||
                !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose an existing absolute .exe path for the external tool.");
            Validate(template, merge);
        }

        public static void Validate(string template, bool merge)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string argument in Split(template))
            {
                foreach (Match match in Regex.Matches(argument, "\\{([^{}]*)\\}")) names.Add(match.Groups[1].Value);
                if (Regex.Replace(argument, "\\{([^{}]*)\\}", "").IndexOfAny(new[] { '{', '}' }) >= 0)
                    throw new ArgumentException("Unmatched brace in tool arguments.");
            }
            string[] required = merge ? new[] { "base", "local", "remote", "merged" } : new[] { "base", "local" };
            if (names.Except(required).Any()) throw new ArgumentException("Unsupported tool placeholder. Use " + String.Join(", ", required) + ".");
            if (required.Except(names).Any()) throw new ArgumentException("Tool arguments must include " + String.Join(", ", required.Select(n => "{" + n + "}")) + ".");
        }

        public static IList<string> Expand(string template, IDictionary<string, string> paths, bool merge)
        {
            Validate(template, merge);
            return Split(template).Select(argument => Regex.Replace(argument, "\\{([^{}]*)\\}", match => paths[match.Groups[1].Value])).ToList();
        }

        private static IList<string> Split(string template)
        {
            if (String.IsNullOrWhiteSpace(template) || template.IndexOf('\0') >= 0)
                throw new ArgumentException("Tool arguments cannot be empty or contain NUL.");
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false, started = false;
            for (int i = 0; i < template.Length; i++)
            {
                char c = template[i];
                if (c == '\\')
                {
                    int count = 1;
                    while (i + 1 < template.Length && template[i + 1] == '\\') { count++; i++; }
                    if (i + 1 < template.Length && template[i + 1] == '"')
                    {
                        current.Append('\\', count / 2); i++;
                        if (count % 2 == 0) quoted = !quoted; else current.Append('"');
                    }
                    else current.Append('\\', count);
                    started = true; continue;
                }
                if (c == '"') { quoted = !quoted; started = true; continue; }
                if (Char.IsWhiteSpace(c) && !quoted)
                {
                    if (started) { result.Add(current.ToString()); current.Clear(); started = false; }
                    continue;
                }
                current.Append(c); started = true;
            }
            if (quoted) throw new ArgumentException("Unclosed double quote in tool arguments.");
            if (started) result.Add(current.ToString());
            return result;
        }
    }

    public sealed partial class PlasticClient
    {
        public async Task<PlasticCommandResult> OpenDiffToolAsync(string path, CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(config.DiffToolPath))
                return await RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Diff, Paths = new List<string> { path },
                    WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(path)) }, cancellationToken).ConfigureAwait(false);
            PlasticToolArguments.ValidateConfiguration(config.DiffToolPath, config.DiffToolArguments, false);
            var command = await BuildReadCommandAsync(path, cancellationToken).ConfigureAwait(false);
            string local = command.Arguments[1];
            if (!File.Exists(local)) throw new ArgumentException("Choose an existing controlled file for the external diff tool.");
            command.Arguments = new List<string> { "fileinfo", local, "--xml", "--encoding=utf-8" };
            var info = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            RequireSuccess(info);
            XElement file = SafeXml.Load(info.Output).Descendants("FileInfo").FirstOrDefault();
            long changeset;
            if (file == null || !Int64.TryParse((string)file.Element("RevisionChangeset"), out changeset) || changeset < 0)
                throw new InvalidOperationException("The file has no checked-in base revision to compare.");
            string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-tool-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporary);
            string basePath = Path.Combine(temporary, "base" + Path.GetExtension(local));
            try
            {
                command.Arguments = new List<string> { "cat", "rev:" + local + "#cs:" + changeset.ToString(CultureInfo.InvariantCulture), "--file=" + basePath };
                var download = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                RequireSuccess(download);
                File.SetAttributes(basePath, File.GetAttributes(basePath) | FileAttributes.ReadOnly);
                command.FileName = config.DiffToolPath;
                command.Arguments = PlasticToolArguments.Expand(config.DiffToolArguments,
                    new Dictionary<string, string> { { "base", basePath }, { "local", local } }, false);
                // Wait for the configured tool; the base file must exist for its lifetime.
                return await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(basePath)) { File.SetAttributes(basePath, FileAttributes.Normal); File.Delete(basePath); }
                Directory.Delete(temporary);
            }
        }

        public async Task<PlasticCommandResult> RunMergeToolAsync(string basePath, string localPath, string remotePath, string mergedPath, CancellationToken cancellationToken)
        {
            if (String.IsNullOrWhiteSpace(config.MergeToolPath)) throw new InvalidOperationException("Configure an external merge tool in TortoiseSCM settings first.");
            PlasticToolArguments.ValidateConfiguration(config.MergeToolPath, config.MergeToolArguments, true);
            string[] inputs = new[] { ToolInput(basePath), ToolInput(localPath), ToolInput(remotePath) };
            if (String.IsNullOrWhiteSpace(mergedPath) || !Path.IsPathRooted(mergedPath) || Path.GetPathRoot(mergedPath).Length < 3) throw new ArgumentException("The merge output must be an absolute file path.");
            string output = Path.GetFullPath(mergedPath);
            RejectReparsePath(output);
            if (Directory.Exists(output) || !Directory.Exists(Path.GetDirectoryName(output))) throw new ArgumentException("Choose an output file in an existing directory.");
            if (inputs.Any(input => input.Equals(output, StringComparison.OrdinalIgnoreCase) || SameExistingFile(input, output)))
                throw new ArgumentException("Merge output must be different from all three input files.");
            return await ExecuteAsync(new PlasticProcessCommand { FileName = config.MergeToolPath, WorkingDirectory = Path.GetDirectoryName(output),
                Arguments = PlasticToolArguments.Expand(config.MergeToolArguments, new Dictionary<string, string> {
                    { "base", inputs[0] }, { "local", inputs[1] }, { "remote", inputs[2] }, { "merged", output } }, true) }, cancellationToken).ConfigureAwait(false);
        }

        private static string ToolInput(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3 || !File.Exists(path)) throw new ArgumentException("All merge inputs must be existing absolute file paths.");
            string result = Path.GetFullPath(path);
            RejectReparsePath(result);
            return result;
        }

        // Detect hard-link aliases as well as spelling aliases before launching a writer.
        private static bool SameExistingFile(string first, string second)
        {
            if (!File.Exists(second)) return false;
            using (var a = File.Open(first, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var b = File.Open(second, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                ToolFileInformation x, y;
                if (!GetFileInformationByHandle(a.SafeFileHandle, out x) || !GetFileInformationByHandle(b.SafeFileHandle, out y))
                    throw new IOException("Unable to verify that merge output is distinct from its inputs.");
                return x.VolumeSerial == y.VolumeSerial && x.IndexHigh == y.IndexHigh && x.IndexLow == y.IndexLow;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ToolFileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ToolFileInformation information);
    }
}
