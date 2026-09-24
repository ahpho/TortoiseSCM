// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TortoiseSCM
{
    internal static class CliRunner
    {
        internal const string Help = "TortoiseSCM --cli --command <command> --path <absolute-path> [--path ...]\r\n" +
            "Commands: status, workspace, add, checkout, checkin, undo, update, history, diff,\r\n" +
            "          changeset, rollback, switch, settings, merge\r\n" +
            "Options: --json --yes --recursive --comment <text> --commentsfile <UTF-8-file>\r\n" +
            "         --timeout <seconds> --cm <absolute-exe-path> --help\r\n" +
            "History: --changeset <number> (required for changeset, rollback, switch)\r\n" +
            "rollback restores selected content as pending changes; switch replaces the whole workspace revision.\r\n" +
            "Tools: diff --external; merge --base <file> --local <file> --remote <file> --output <file> --yes\r\n" +
            "Settings: --diff-tool <exe> --diff-args <template> --merge-tool <exe> --merge-args <template>\r\n" +
            "          --settings-file <file> (optional isolated configuration); no tool options reads settings.\r\n" +
            "Write commands require --yes. Checkin requires a nonempty comment.\r\n" +
            "Exit codes: 0 success; 1 SCM/runtime error; 2 invalid arguments; 124 timeout.\r\n" +
            "--json writes exactly one UTF-8 JSON object to stdout, including errors.\r\n" +
            "Timeout applies to each cm process. Paths must belong to one workspace.";

        internal static int Run(string[] args)
        {
            bool json = CliOptions.RequestsJson(args);
            var response = new CliResponse();
            try
            {
                var options = CliOptions.Parse(args);
                response.command = options.Command;
                if (options.Help) response.output = Help;
                else Execute(options, response);
            }
            catch (PlasticCommandException error) { SetResult(response, error.Result); }
            catch (ArgumentException error) { response.exitCode = 2; response.error = error.Message; }
            catch (Exception error) { response.exitCode = 1; response.error = error.Message; }
            response.success = response.exitCode == 0;
            try
            {
                string text = json ? new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(response) :
                    (response.success ? response.output : response.error + (String.IsNullOrEmpty(response.output) ? "" : "\r\n" + response.output));
                WriteOutput(text, !json && !response.success);
            }
            // A closed pipe or invalid output handle must not escape to WinExe's
            // unhandled-exception dialog. There is no usable channel to report it on.
            catch (Exception) { return 1; }
            return response.exitCode;
        }

        private static void Execute(CliOptions options, CliResponse response)
        {
            var config = options.SettingsFile == null ? PlasticClientConfig.Load() : PlasticClientConfig.Load(options.SettingsFile);
            if (options.Cm != null) config.CmPath = options.Cm;
            if (options.Timeout.HasValue) config.Timeout = TimeSpan.FromSeconds(options.Timeout.Value);
            if (options.Command == "settings")
            {
                if (options.DiffTool != null) config.DiffToolPath = options.DiffTool;
                if (options.DiffArgs != null) config.DiffToolArguments = options.DiffArgs;
                if (options.MergeTool != null) config.MergeToolPath = options.MergeTool;
                if (options.MergeArgs != null) config.MergeToolArguments = options.MergeArgs;
                if (options.ChangesSettings) config.Save();
                response.data = new { settings = new { settingsFile = config.SettingsPath, cm = config.CmPath,
                    timeout = config.Timeout.TotalSeconds, diffTool = config.DiffToolPath, diffArgs = config.DiffToolArguments,
                    mergeTool = config.MergeToolPath, mergeArgs = config.MergeToolArguments } };
                response.output = "Diff tool: " + config.DiffToolPath + "\r\nDiff arguments: " + config.DiffToolArguments +
                    "\r\nMerge tool: " + config.MergeToolPath + "\r\nMerge arguments: " + config.MergeToolArguments;
                return;
            }
            var client = new PlasticClient(config);
            if (options.Command == "merge")
            {
                SetResult(response, client.RunMergeToolAsync(options.Base, options.Local, options.Remote, options.Output, CancellationToken.None).GetAwaiter().GetResult());
                response.data = new { outputPath = options.Output };
                return;
            }
            var workspace = client.DiscoverWorkspace(options.Paths[0]);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            // Validate every scope up front, before any command has a chance to run.
            foreach (string path in options.Paths)
                client.Build(new PlasticCommandRequest { Command = PlasticCommand.Status,
                    WorkingDirectory = workspace.RootPath, Paths = new List<string> { path } });
            workspace = client.GetWorkspaceAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
            var workspaceData = new { rootPath = workspace.RootPath, name = workspace.Name,
                repository = workspace.Repository, selector = workspace.Selector, isPartial = workspace.IsPartial };
            if (options.Command == "workspace")
            {
                response.data = new { workspace = workspaceData };
                response.output = workspace.RootPath;
                return;
            }
            if (options.Command == "status")
            {
                var items = new List<PlasticStatusItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var output = new StringBuilder();
                foreach (string path in options.Paths)
                {
                    var result = client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Status,
                        WorkingDirectory = workspace.RootPath, Paths = new List<string> { path } }, CancellationToken.None).GetAwaiter().GetResult();
                    if (!result.Succeeded) { SetResult(response, result); return; }
                    foreach (var item in PlasticClient.ParseStatus(result.Output, workspace.RootPath))
                    {
                        string scope = path.TrimEnd('\\', '/');
                        if (!item.Path.Equals(scope, StringComparison.OrdinalIgnoreCase) &&
                            !item.Path.StartsWith(scope + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(item.Path + "\0" + item.StatusCode + "\0" + item.OldPath)) continue;
                        items.Add(item); output.AppendLine(item.StatusCode + "\t" + item.Path);
                    }
                }
                response.data = new { workspace = workspaceData, entries = items.Select(item => new {
                    path = item.Path, oldPath = item.OldPath, status = item.StatusCode,
                    description = item.StatusDescription, isDirectory = item.IsDirectory }).ToArray() };
                response.output = output.ToString();
                return;
            }
            if (options.Command == "history")
            {
                var entries = new List<PlasticHistoryItem>();
                foreach (string path in options.Paths)
                    entries.AddRange(client.GetHistoryAsync(path, CancellationToken.None).GetAwaiter().GetResult());
                response.data = new { workspace = workspaceData, entries = entries.Select(entry => new {
                    path = entry.Path, revisionSpec = entry.RevisionSpec, changeset = entry.Changeset,
                    creationDate = entry.CreationDate, owner = entry.Owner, branch = entry.Branch,
                    comment = entry.Comment, repository = entry.Repository }).ToArray() };
                response.output = String.Join(Environment.NewLine, entries.Select(entry =>
                    "Changeset " + entry.Changeset.ToString(CultureInfo.InvariantCulture) + " | " + entry.Owner + " | " + entry.Branch + "\r\n" + entry.Comment));
                return;
            }
            if (options.Command == "diff")
            {
                if (options.External)
                {
                    SetResult(response, client.OpenDiffToolAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult());
                    response.data = new { workspace = workspaceData, external = true, path = options.Paths[0] };
                    return;
                }
                var diffs = new List<PlasticDiffResult>();
                foreach (string path in options.Paths)
                    diffs.Add(client.GetDiffTextAsync(path, CancellationToken.None).GetAwaiter().GetResult());
                response.data = new { workspace = workspaceData, diffs = diffs.Select(diff => new {
                    path = diff.Path, baseRevision = diff.BaseRevision, diffText = diff.DiffText,
                    isBinary = diff.IsBinary, hasChanges = diff.HasChanges }).ToArray() };
                response.output = String.Join(Environment.NewLine, diffs.Select(diff => diff.DiffText));
                return;
            }
            if (options.Command == "changeset")
            {
                var details = client.GetChangesetAsync(options.Paths[0], options.Changeset.Value, CancellationToken.None).GetAwaiter().GetResult();
                var entry = details.Changeset;
                response.data = new { workspace = workspaceData, changeset = new {
                    path = entry.Path, revisionSpec = entry.RevisionSpec, changeset = entry.Changeset,
                    creationDate = entry.CreationDate, owner = entry.Owner, branch = entry.Branch,
                    comment = entry.Comment, repository = entry.Repository }, files = details.Files.Select(file => new {
                        status = file.Status, path = file.Path, oldPath = file.OldPath, itemType = file.ItemType }).ToArray() };
                response.output = "Changeset " + entry.Changeset.ToString(CultureInfo.InvariantCulture) + "\r\n" + entry.Comment + "\r\n" +
                    String.Join(Environment.NewLine, details.Files.Select(file => file.Status + "\t" + file.Path));
                return;
            }
            if (options.Command == "rollback" || options.Command == "switch")
            {
                var result = options.Command == "rollback" ?
                    client.RollbackAsync(options.Paths[0], options.Changeset.Value, CancellationToken.None).GetAwaiter().GetResult() :
                    client.SwitchAsync(options.Paths[0], options.Changeset.Value, CancellationToken.None).GetAwaiter().GetResult();
                SetResult(response, result);
                response.data = new { workspace = workspaceData, changeset = options.Changeset.Value,
                    path = options.Paths[0], operation = options.Command == "rollback" ? "restore-pending" : "switch-workspace" };
                return;
            }
            var command = (PlasticCommand)Enum.Parse(typeof(PlasticCommand), options.Command, true);
            var request = new PlasticCommandRequest { Command = command, Paths = options.Paths,
                WorkingDirectory = workspace.RootPath, Comment = options.Comment, Recursive = options.Recursive };
            SetResult(response, client.RunAsync(request, CancellationToken.None).GetAwaiter().GetResult());
            response.data = new { workspace = workspaceData };
        }

        private static void SetResult(CliResponse response, PlasticCommandResult result)
        {
            response.exitCode = result.TimedOut ? 124 : (result.Succeeded ? 0 : 1);
            response.output = result.Output;
            response.error = result.Error;
            if (!result.Succeeded && String.IsNullOrWhiteSpace(response.error))
                response.error = "Plastic SCM exited with code " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + ".";
        }

        // WinExe retains inherited redirected handles. Attach only when neither output
        // handle exists, so attaching can never replace a caller's stdout/stderr pipes.
        private static void WriteOutput(string text, bool error)
        {
            if (Invalid(GetStdHandle(-11)) && Invalid(GetStdHandle(-12))) AttachConsole(unchecked((uint)-1));
            IntPtr handle = GetStdHandle(error ? -12 : -11);
            uint mode;
            if (!Invalid(handle) && GetConsoleMode(handle, out mode))
            {
                string line = text + Environment.NewLine;
                for (int offset = 0; offset < line.Length; )
                {
                    // Console writes have a bounded buffer unlike redirected streams.
                    string chunk = line.Substring(offset, Math.Min(8192, line.Length - offset));
                    uint written;
                    if (!WriteConsole(handle, chunk, (uint)chunk.Length, out written, IntPtr.Zero) || written == 0)
                        throw new IOException("Unable to write console output.");
                    offset += (int)written;
                }
            }
            else
            {
                using (var writer = new StreamWriter(error ? Console.OpenStandardError() : Console.OpenStandardOutput(), new UTF8Encoding(false)))
                { writer.WriteLine(text); }
            }
        }

        private static bool Invalid(IntPtr handle) { return handle == IntPtr.Zero || handle == new IntPtr(-1); }
        [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int standardHandle);
        [DllImport("kernel32.dll")] private static extern bool AttachConsole(uint processId);
        [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "WriteConsoleW")]
        private static extern bool WriteConsole(IntPtr handle, string buffer, uint count, out uint written, IntPtr reserved);
    }

    internal sealed class CliResponse
    {
        public int schemaVersion = 1;
        public string command = "";
        public bool success;
        public int exitCode;
        public string output = "";
        public string error = "";
        public object data;
    }

    internal sealed class CliOptions
    {
        internal string Command = "status", Comment, Cm, DiffTool, DiffArgs, MergeTool, MergeArgs, SettingsFile;
        internal string Base, Local, Remote, Output;
        internal bool Help, Recursive, External;
        internal int? Timeout;
        internal long? Changeset;
        internal bool ChangesSettings { get { return DiffTool != null || DiffArgs != null || MergeTool != null || MergeArgs != null; } }
        internal readonly List<string> Paths = new List<string>();

        internal static bool RequestsJson(string[] args)
        {
            // An option-looking comment is still a value, not an output-mode switch.
            for (int i = 0; i < args.Length; ++i)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--json": return true;
                    case "--command": case "--path": case "--comment": case "--commentsfile": case "--cm": case "--timeout": ++i; break;
                    case "--changeset": case "--diff-tool": case "--diff-args": case "--merge-tool": case "--merge-args":
                    case "--settings-file": case "--base": case "--local": case "--remote": case "--output": ++i; break;
                }
            }
            return false;
        }

        internal static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string commentsFile = null;
            bool yes = false;
            for (int i = 0; i < args.Length; ++i)
            {
                string option = args[i].ToLowerInvariant();
                if (option != "--path" && !seen.Add(option)) throw new ArgumentException("Duplicate option: " + option);
                switch (option)
                {
                    case "--cli": case "--json": break;
                    case "--help": options.Help = true; break;
                    case "--yes": yes = true; break;
                    case "--recursive": options.Recursive = true; break;
                    case "--external": options.External = true; break;
                    case "--command": options.Command = Value(args, ref i).ToLowerInvariant(); break;
                    case "--path":
                        string path = AbsolutePath(Value(args, ref i));
                        if (!options.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)) options.Paths.Add(path);
                        break;
                    case "--comment": options.Comment = Value(args, ref i); break;
                    case "--commentsfile": commentsFile = AbsolutePath(Value(args, ref i)); break;
                    case "--cm": options.Cm = AbsolutePath(Value(args, ref i)); break;
                    case "--settings-file": options.SettingsFile = AbsolutePath(Value(args, ref i)); break;
                    case "--diff-tool": options.DiffTool = Value(args, ref i); if (options.DiffTool.Length > 0) options.DiffTool = AbsolutePath(options.DiffTool); break;
                    case "--diff-args": options.DiffArgs = Value(args, ref i); break;
                    case "--merge-tool": options.MergeTool = Value(args, ref i); if (options.MergeTool.Length > 0) options.MergeTool = AbsolutePath(options.MergeTool); break;
                    case "--merge-args": options.MergeArgs = Value(args, ref i); break;
                    case "--base": options.Base = AbsolutePath(Value(args, ref i)); break;
                    case "--local": options.Local = AbsolutePath(Value(args, ref i)); break;
                    case "--remote": options.Remote = AbsolutePath(Value(args, ref i)); break;
                    case "--output": options.Output = AbsolutePath(Value(args, ref i)); break;
                    case "--changeset":
                        long changeset;
                        if (!Int64.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out changeset))
                            throw new ArgumentException("--changeset must be a nonnegative integer.");
                        options.Changeset = changeset;
                        break;
                    case "--timeout":
                        int seconds;
                        if (!Int32.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out seconds) || seconds < 1 || seconds > 86400)
                            throw new ArgumentException("--timeout must be an integer from 1 to 86400 seconds.");
                        options.Timeout = seconds;
                        break;
                    default: throw new ArgumentException("Unknown CLI option: " + option);
                }
            }
            if (options.Help) return options;
            if (!new[] { "status", "workspace", "add", "checkout", "checkin", "undo", "update", "history", "diff", "changeset", "rollback", "switch", "settings", "merge" }.Contains(options.Command))
                throw new ArgumentException("Unsupported CLI command: " + options.Command);
            if (options.Command != "settings" && options.Command != "merge" && options.Paths.Count == 0) throw new ArgumentException("At least one explicit --path is required.");
            if ((options.Command == "settings" || options.Command == "merge") && options.Paths.Count != 0) throw new ArgumentException("This command does not accept --path.");
            bool write = new[] { "add", "checkout", "checkin", "undo", "update", "rollback", "switch", "merge" }.Contains(options.Command) || options.ChangesSettings;
            if (write && !yes) throw new ArgumentException("Write commands require explicit --yes confirmation.");
            if (options.ChangesSettings && options.Command != "settings") throw new ArgumentException("Tool configuration options require --command settings.");
            bool needsChangeset = new[] { "changeset", "rollback", "switch" }.Contains(options.Command);
            if (needsChangeset != options.Changeset.HasValue) throw new ArgumentException("--changeset is required only for changeset, rollback and switch commands.");
            if (needsChangeset && options.Paths.Count != 1) throw new ArgumentException("Select exactly one file or directory scope for this command.");
            if (options.External && (options.Command != "diff" || options.Paths.Count != 1)) throw new ArgumentException("--external requires diff with exactly one file path.");
            bool mergePaths = options.Base != null || options.Local != null || options.Remote != null || options.Output != null;
            if (mergePaths && options.Command != "merge") throw new ArgumentException("Merge paths are valid only for --command merge.");
            if (options.Command == "merge" && (options.Base == null || options.Local == null || options.Remote == null || options.Output == null))
                throw new ArgumentException("Merge requires --base, --local, --remote and --output.");
            if (options.Comment != null && commentsFile != null) throw new ArgumentException("Use either --comment or --commentsfile.");
            if ((options.Comment != null || commentsFile != null) && options.Command != "checkin")
                throw new ArgumentException("Comments are valid only for checkin.");
            if (options.Recursive && options.Command != "add" && options.Command != "checkout" && options.Command != "undo")
                throw new ArgumentException("--recursive is supported for add, checkout and undo only.");
            if (commentsFile != null)
            {
                if (!File.Exists(commentsFile)) throw new ArgumentException("The comments file does not exist.");
                if (new FileInfo(commentsFile).Length > 65536) throw new ArgumentException("The comments file exceeds 64 KiB.");
                try { options.Comment = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(commentsFile)).TrimStart('\uFEFF'); }
                catch (DecoderFallbackException) { throw new ArgumentException("The comments file must be UTF-8."); }
            }
            if (options.Command == "checkin" && String.IsNullOrWhiteSpace(options.Comment)) throw new ArgumentException("Checkin requires a nonempty comment.");
            return options;
        }

        private static string Value(string[] args, ref int index)
        {
            if (++index >= args.Length) throw new ArgumentException("Missing option value.");
            return args[index];
        }

        private static string AbsolutePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || Path.GetPathRoot(value).Length < 3)
                throw new ArgumentException("Paths must be absolute: " + value);
            return Path.GetFullPath(value);
        }
    }
}
