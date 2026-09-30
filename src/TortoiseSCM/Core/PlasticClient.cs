// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public sealed partial class PlasticClient
    {
        private readonly PlasticClientConfig config;
        public PlasticClient(PlasticClientConfig config) { if (config == null) throw new ArgumentNullException("config"); this.config = config; }

        public PlasticWorkspace DiscoverWorkspace(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A workspace path is required.");
            string directory = System.IO.Path.GetFullPath(path);
            string driveRoot = System.IO.Path.GetPathRoot(directory);
            if (directory.Length > driveRoot.Length) directory = directory.TrimEnd('\\', '/');
            if (directory.Split('\\', '/').Any(part => part.Equals(".plastic", StringComparison.OrdinalIgnoreCase))) return null;
            RejectReparsePath(directory);
            if (!Directory.Exists(directory)) directory = System.IO.Path.GetDirectoryName(directory);
            while (!String.IsNullOrEmpty(directory))
            {
                string metadata = System.IO.Path.Combine(directory, ".plastic", "plastic.workspace");
                if (File.Exists(metadata))
                {
                    string[] lines = File.ReadAllLines(metadata);
                    string selectorPath = System.IO.Path.Combine(directory, ".plastic", "plastic.selector");
                    string selector = File.Exists(selectorPath) ? File.ReadAllText(selectorPath) : "";
                    Match repository = Regex.Match(selector, "repository\\s+\"([^\"]+)\"");
                    return new PlasticWorkspace { RootPath = directory, Name = lines.Length > 0 ? lines[0] : "", Selector = selector,
                        Repository = repository.Success ? repository.Groups[1].Value : "", IsPartial = lines.Any(line => line.Trim().Equals("Partial", StringComparison.OrdinalIgnoreCase)) };
                }
                directory = System.IO.Path.GetDirectoryName(directory);
            }
            return null;
        }

        public PlasticProcessCommand Build(PlasticCommandRequest request)
        {
            return Build(request, CancellationToken.None);
        }

        private PlasticProcessCommand Build(PlasticCommandRequest request, CancellationToken cancellationToken)
        { return Build(request, cancellationToken, null); }

        // Some write paths have already obtained the authoritative workspace
        // mode from cm status. Keep that value when the local workspace file
        // still contains the stale "Standard" hint left by Gluon conversion.
        private PlasticProcessCommand Build(PlasticCommandRequest request, CancellationToken cancellationToken, bool? partialOverride)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null) throw new ArgumentNullException("request");
            if (!Enum.IsDefined(typeof(PlasticCommand), request.Command)) throw new ArgumentException("Unsupported command.");
            if (!Enum.IsDefined(typeof(PlasticCheckinInputMode), request.CheckinInputMode)) throw new ArgumentException("Unsupported checkin input method.");
            string working = String.IsNullOrWhiteSpace(request.WorkingDirectory) ? Environment.CurrentDirectory : request.WorkingDirectory;
            PlasticWorkspace workspace = DiscoverWorkspace(working);
            if (workspace == null && request.Paths != null && request.Paths.Count > 0) workspace = DiscoverWorkspace(request.Paths[0]);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            var paths = new List<string>();
            foreach (string value in request.Paths ?? new List<string>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(value) || value.IndexOfAny(new [] { '*', '?', '\r', '\n', '\0' }) >= 0) throw new ArgumentException("Select explicit file or directory paths; wildcards are not supported.");
                string absolute = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(value) ? value : System.IO.Path.Combine(working, value));
                RejectReparsePath(absolute);
                if (!IsWithin(absolute, workspace.RootPath)) throw new ArgumentException("All selected paths must belong to the same workspace.");
                string relative = absolute.Substring(workspace.RootPath.TrimEnd('\\', '/').Length).TrimStart('\\', '/');
                if (relative.Equals(".plastic", StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".plastic\\", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Plastic workspace metadata cannot be selected.");
                PlasticWorkspace nested = DiscoverWorkspace(absolute);
                if (nested != null && !nested.RootPath.Equals(workspace.RootPath, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Selections cannot span nested workspaces.");
                if (!paths.Contains(absolute, StringComparer.OrdinalIgnoreCase)) paths.Add(absolute);
            }
            if (paths.Count == 0)
            {
                if (request.Command == PlasticCommand.Status || request.Command == PlasticCommand.Gluon) paths.Add(workspace.RootPath);
                else throw new ArgumentException("Select at least one explicit path for this operation.");
            }
            var args = new List<string>();
            bool recursiveMutation = request.Command == PlasticCommand.Checkin || request.Command == PlasticCommand.Update ||
                (request.Recursive && (request.Command == PlasticCommand.Add || request.Command == PlasticCommand.Checkout || request.Command == PlasticCommand.Undo));
            if (recursiveMutation) foreach (string path in paths.Where(Directory.Exists)) RejectUnsafeDescendants(path, workspace.RootPath, cancellationToken);
            var result = new PlasticProcessCommand { FileName = config.CmPath, WorkingDirectory = workspace.RootPath, Arguments = args };
            bool partial = partialOverride ?? workspace.IsPartial;
            if (partial && (request.Command == PlasticCommand.Add || request.Command == PlasticCommand.Checkout || request.Command == PlasticCommand.Checkin || request.Command == PlasticCommand.Undo || request.Command == PlasticCommand.Update)) args.Add("partial");
            switch (request.Command)
            {
                case PlasticCommand.Status:
                    if (paths.Count != 1) throw new ArgumentException("Status takes one file or directory scope.");
                    args.Add("status"); args.AddRange(paths); args.Add("--xml"); args.Add("--encoding=utf-8"); args.Add("--fullpaths"); break;
                case PlasticCommand.Add:
                    args.Add("add"); args.AddRange(paths); if (request.Recursive) args.Add("--recursive"); break;
                case PlasticCommand.Checkout:
                    args.Add("checkout"); args.AddRange(paths); if (request.Recursive) args.Add("--recursive"); break;
                case PlasticCommand.Checkin:
                    if (String.IsNullOrWhiteSpace(request.Comment)) throw new ArgumentException("A checkin comment is required.");
                    // The process runs at the workspace root. Relative arguments
                    // keep exact selections while avoiding repeating a long root
                    // for every file on the Windows command line.
                    args.Add("checkin"); args.AddRange(paths.Select(path => WorkspaceRelativePath(path, workspace.RootPath)));
                    if (request.Recursive) args.Add("--all"); args.Add("-c=" + request.Comment);
                    if (request.IncludePrivate) { if (partial) throw new ArgumentException("Add private files before checking in a partial workspace."); args.Add("--private"); } break;
                case PlasticCommand.Undo:
                    args.Add("undo"); args.AddRange(paths); if (request.Recursive) args.Add("--recursive"); break;
                case PlasticCommand.Update:
                    if (!partial && paths.Count != 1) throw new ArgumentException("Standard workspace update takes one path.");
                    args.Add("update"); args.AddRange(paths); args.Add("--dontmerge"); if (partial) args.Add("--report"); break;
                case PlasticCommand.History:
                    args.Add("history"); args.AddRange(paths); args.Add("--xml"); args.Add("--encoding=utf-8"); break;
                case PlasticCommand.Diff:
                    if (paths.Count != 1 || Directory.Exists(paths[0])) throw new ArgumentException("Select one controlled file to compare.");
                    if (String.IsNullOrWhiteSpace(request.DiffSpec)) throw new ArgumentException("Diff requires the loaded revision; use RunAsync to resolve it.");
                    args.Add("diff"); args.Add(request.DiffSpec); args.Add(paths[0]); result.Interactive = true; break;
                case PlasticCommand.Gluon:
                    result.FileName = config.GluonPath; args.Clear(); args.Add("--wk=" + workspace.RootPath); result.Interactive = true; break;
            }
            if (request.Command == PlasticCommand.Checkin &&
                (request.CheckinInputMode == PlasticCheckinInputMode.StandardInput ||
                 (request.CheckinInputMode == PlasticCheckinInputMode.Automatic &&
                  (paths.Count >= 128 || String.Join(" ", args.Select(QuoteArgument)).Length > 20000))))
            {
                // Both standard and partial checkin accept one path per line
                // from stdin. This avoids the Windows command-line length limit
                // while preserving one native changeset for the whole selection.
                args.Clear();
                if (partial) args.Add("partial");
                args.Add("checkin"); if (request.Recursive) args.Add("--all"); args.Add("-"); args.Add("-c=" + request.Comment);
                if (request.IncludePrivate) args.Add("--private");
                string root = workspace.RootPath.TrimEnd('\\', '/');
                // Both are exact selections resolved at the workspace root.
                // The partial form follows the documented `dir /S /B` example
                // and sends absolute paths; standard checkin uses relative ones.
                IEnumerable<string> inputPaths = partial ? paths : paths.Select(path =>
                    path.Equals(root, StringComparison.OrdinalIgnoreCase) ? "." : path.Substring(root.Length).TrimStart('\\', '/'));
                result.StandardInput = String.Join(Environment.NewLine, inputPaths) +
                    Environment.NewLine + Environment.NewLine;
            }
            return result;
        }

        private static string WorkspaceRelativePath(string path, string workspaceRoot)
        {
            string root = workspaceRoot.TrimEnd('\\', '/');
            string relative = path.Equals(root, StringComparison.OrdinalIgnoreCase) ? "." : path.Substring(root.Length).TrimStart('\\', '/');
            // A legal Windows filename can start with a dash. Keep it a path,
            // rather than letting cm interpret it as an option/comment.
            return relative.StartsWith("-", StringComparison.Ordinal) ? ".\\" + relative : relative;
        }

        public async Task<PlasticCommandResult> RunAsync(PlasticCommandRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Command == PlasticCommand.Update && request.Paths != null && request.Paths.Count > 1)
            {
                // Prepare and validate the complete selection before starting any update.
                // Standard workspaces accept one scope per cm invocation; never widen it
                // to the common parent or workspace merely to support multi-selection.
                List<PlasticProcessCommand> commands = await Task.Run(delegate
                {
                    var prepared = new List<PlasticProcessCommand>();
                    foreach (string selected in request.Paths)
                    {
                        var part = new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = request.WorkingDirectory,
                            Paths = new List<string> { selected } };
                        PlasticProcessCommand command = Build(part, cancellationToken);
                        if (prepared.Count > 0 && !prepared[0].WorkingDirectory.Equals(command.WorkingDirectory, StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("All selected paths must belong to the same workspace.");
                        prepared.Add(command);
                    }
                    return prepared;
                }, cancellationToken).ConfigureAwait(false);
                var actualWorkspace = await GetWorkspaceAsync(commands[0].WorkingDirectory, cancellationToken).ConfigureAwait(false);
                foreach (var command in commands) ApplyWorkspaceMode(command, actualWorkspace.IsPartial);
                var combined = new PlasticCommandResult();
                var output = new StringBuilder(); var errors = new StringBuilder();
                foreach (PlasticProcessCommand command in commands)
                {
                    PlasticCommandResult current;
                    try
                    {
                        var historyUpdate = await PrepareHistoryRootUpdateAsync(command, cancellationToken).ConfigureAwait(false);
                        current = await ExecuteWithPartialConflictGuardAsync(command, request, cancellationToken).ConfigureAwait(false);
                        await CompleteHistoryRootUpdateAsync(historyUpdate, current, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        // A later launch or cancellation must not hide already-completed
                        // updates: return their output alongside the stopping reason.
                        current = new PlasticCommandResult { ExitCode = -1, Error = error.Message };
                    }
                    output.Append(current.Output); errors.Append(current.Error);
                    combined.ExitCode = current.ExitCode; combined.TimedOut = current.TimedOut;
                    if (!current.Succeeded) break;
                }
                combined.Output = output.ToString(); combined.Error = errors.ToString(); return combined;
            }
            if (request.Command == PlasticCommand.Diff && String.IsNullOrWhiteSpace(request.DiffSpec))
            {
                if (request.Paths == null || request.Paths.Count != 1) throw new ArgumentException("Select one controlled file to compare.");
                var probe = new PlasticCommandRequest { Command = PlasticCommand.History, WorkingDirectory = request.WorkingDirectory, Paths = request.Paths };
                var infoCommand = await Task.Run(() => Build(probe, cancellationToken), cancellationToken).ConfigureAwait(false);
                string path = infoCommand.Arguments[1];
                infoCommand.Arguments = new List<string> { "fileinfo", path, "--xml", "--encoding=utf-8" };
                var info = await ExecuteAsync(infoCommand, cancellationToken).ConfigureAwait(false);
                if (!info.Succeeded) return info;
                XElement file = SafeXml.Load(info.Output).Descendants("FileInfo").FirstOrDefault();
                long changeset;
                if (file == null || !Int64.TryParse((string)file.Element("RevisionChangeset"), out changeset) || changeset < 0)
                    throw new InvalidOperationException("The file has no checked-in base revision to compare.");
                request = new PlasticCommandRequest { Command = PlasticCommand.Diff, WorkingDirectory = infoCommand.WorkingDirectory,
                    Paths = new List<string> { path }, DiffSpec = "rev:" + path + "#cs:" + changeset.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            }
            PlasticProcessCommand planned = await Task.Run(() => Build(request, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (request.Command == PlasticCommand.Add || request.Command == PlasticCommand.Checkout || request.Command == PlasticCommand.Checkin ||
                request.Command == PlasticCommand.Undo || request.Command == PlasticCommand.Update)
            {
                var actualWorkspace = await GetWorkspaceAsync(planned.WorkingDirectory, cancellationToken).ConfigureAwait(false);
                // Gluon conversions can leave plastic.workspace marked
                // Standard even though cm status reports a partial workspace.
                // Rebuild after that authoritative query so large-checkin
                // stdin paths use the mode-specific format (absolute for
                // partial, workspace-relative for standard).
                planned = await Task.Run(() => Build(request, cancellationToken, actualWorkspace.IsPartial), cancellationToken).ConfigureAwait(false);
                ApplyWorkspaceMode(planned, actualWorkspace.IsPartial);
            }
            var rootHistoryUpdate = request.Command == PlasticCommand.Update ?
                await PrepareHistoryRootUpdateAsync(planned, cancellationToken).ConfigureAwait(false) : null;
            PlasticCommandResult result = await ExecuteWithPartialConflictGuardAsync(planned, request, cancellationToken).ConfigureAwait(false);
            await CompleteHistoryRootUpdateAsync(rootHistoryUpdate, result, cancellationToken).ConfigureAwait(false);
            if (request.Command == PlasticCommand.History && result.Succeeded)
            {
                var history = new StringBuilder();
                foreach (XElement entry in SafeXml.Load(result.Output).Descendants("RevisionHistory"))
                {
                    history.AppendLine((string)entry.Element("ItemName"));
                    foreach (XElement revision in entry.Descendants("Revision"))
                    {
                        history.AppendFormat("Changeset {0} | {1} | {2} | {3}\r\n", (string)revision.Element("ChangesetNumber"),
                            (string)revision.Element("CreationDate"), (string)revision.Element("Owner"), (string)revision.Element("Branch"));
                        history.AppendLine((string)revision.Element("Comment")); history.AppendLine();
                    }
                }
                result.Output = history.ToString();
            }
            return result;
        }

        public async Task<IList<PlasticStatusItem>> GetStatusAsync(string path, CancellationToken cancellationToken)
        {
            PlasticWorkspace workspace = DiscoverWorkspace(path);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            var result = await RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Status, WorkingDirectory = workspace.RootPath, Paths = new List<string> { path } }, cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            return ParseStatus(result.Output, workspace.RootPath);
        }

        public static IList<PlasticStatusItem> ParseStatus(string xml, string workspaceRoot)
        {
            XDocument document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "StatusOutput") throw new InvalidDataException("Unexpected Plastic status XML.");
            var items = new List<PlasticStatusItem>();
            foreach (XElement change in document.Root.Descendants("Change"))
            {
                string path = (string)change.Element("Path");
                string code = (string)change.Element("Type");
                if (String.IsNullOrEmpty(path) || String.IsNullOrEmpty(code)) throw new InvalidDataException("Incomplete Plastic status entry.");
                if (!System.IO.Path.IsPathRooted(path)) path = System.IO.Path.Combine(workspaceRoot, path);
                path = System.IO.Path.GetFullPath(path);
                if (!IsWithin(path, System.IO.Path.GetFullPath(workspaceRoot))) throw new InvalidDataException("Status path is outside the selected workspace.");
                if (path.Split('\\', '/').Any(part => part.Equals(".plastic", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Status contains workspace metadata.");
                RejectReparsePath(path);
                string oldPath = (string)change.Element("OldPath") ?? "";
                if (!String.IsNullOrEmpty(oldPath) && !System.IO.Path.IsPathRooted(oldPath)) oldPath = System.IO.Path.Combine(workspaceRoot, oldPath);
                if (!String.IsNullOrEmpty(oldPath))
                {
                    oldPath = System.IO.Path.GetFullPath(oldPath);
                    if (!IsWithin(oldPath, System.IO.Path.GetFullPath(workspaceRoot))) throw new InvalidDataException("Moved source is outside the selected workspace.");
                }
                string revisionType = (string)change.Element("RevisionType") ?? "";
                items.Add(new PlasticStatusItem { Path = path, OldPath = oldPath, StatusCode = code,
                    Status = code, StatusDescription = (string)change.Element("TypeVerbose") ?? code,
                    IsDirectory = revisionType.Equals("enDirectory", StringComparison.OrdinalIgnoreCase) || revisionType.Equals("Directory", StringComparison.OrdinalIgnoreCase) || Directory.Exists(path) });
            }
            return items;
        }

        public async Task<PlasticCommandResult> ExecuteAsync(PlasticProcessCommand command, CancellationToken cancellationToken)
        {
            if (config.Timeout <= TimeSpan.Zero) throw new InvalidOperationException("Process timeout must be positive.");
            cancellationToken.ThrowIfCancellationRequested();
            string temporaryConfigDirectory = null;
            try
            {
                command = PrepareCheckinTransport(command, out temporaryConfigDirectory);
                bool shellCheckin = IsNativeCheckin(command) && !String.IsNullOrEmpty(command.StandardInput);
                if (shellCheckin) command = PrepareCheckinStandardInput(command, ref temporaryConfigDirectory);
                PlasticCommandResult result = await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
                return shellCheckin ? ReadShellCheckinResult(result) : result;
            }
            finally
            {
                if (temporaryConfigDirectory != null)
                {
                    // The awaited process has exited (including timeout or
                    // cancellation). Never remove configuration while cm uses it.
                    try { Directory.Delete(temporaryConfigDirectory, true); }
                    catch (IOException error) { Trace.TraceWarning("Cannot remove temporary checkin configuration: " + error.Message); }
                    catch (UnauthorizedAccessException error) { Trace.TraceWarning("Cannot remove temporary checkin configuration: " + error.Message); }
                }
            }
        }

        private static PlasticProcessCommand PrepareCheckinTransport(PlasticProcessCommand command, out string temporaryDirectory)
        {
            temporaryDirectory = null;
            if (!IsNativeCheckin(command)) return command;
            string original = GetCmClientConfigPath(command);
            if (!File.Exists(original)) return command; // Let cm report its normal configuration error.
            XDocument document = SafeXml.Load(File.ReadAllText(original));
            if (document.Root == null || document.Root.Name.LocalName != "ClientConfigData")
                throw new InvalidDataException("Invalid Plastic SCM client configuration.");
            XElement compression = document.Root.Element("PlasticProtoEnableLz4");
            if (compression != null && compression.Value.Equals("yes", StringComparison.OrdinalIgnoreCase)) return command;
            if (compression == null) document.Root.Add(new XElement("PlasticProtoEnableLz4", "yes"));
            else compression.Value = "yes";
            // Old servers can hang discarding a large newer-version TryCheckIn
            // request before falling back to their supported method version.
            // Supported protocol compression keeps one native atomic checkin
            // while avoiding that transport failure. All user settings,
            // credentials and language are preserved in this per-process copy.
            EnsureCheckinTemporaryDirectory(ref temporaryDirectory);
            string copy = System.IO.Path.Combine(temporaryDirectory, "client.conf");
            using (var writer = new StreamWriter(copy, false, new UTF8Encoding(false))) document.Save(writer);
            var arguments = command.Arguments.Where(argument => !argument.StartsWith("--clientconf=", StringComparison.OrdinalIgnoreCase) &&
                !argument.StartsWith("-clientconf=", StringComparison.OrdinalIgnoreCase)).ToList();
            arguments.Add("--clientconf=" + copy);
            return new PlasticProcessCommand { FileName = command.FileName, WorkingDirectory = command.WorkingDirectory,
                Arguments = arguments, StandardInput = command.StandardInput, Interactive = command.Interactive };
        }

        private static bool IsNativeCheckin(PlasticProcessCommand command)
        {
            return !command.Interactive && System.IO.Path.GetFileName(command.FileName).Equals("cm.exe", StringComparison.OrdinalIgnoreCase) &&
                command.Arguments.Count > 0 && (command.Arguments[0] == "checkin" ||
                (command.Arguments.Count > 1 && command.Arguments[0] == "partial" && command.Arguments[1] == "checkin"));
        }

        private static void EnsureCheckinTemporaryDirectory(ref string directory)
        {
            if (directory != null) return;
            directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TortoiseSCM", "checkin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        private static PlasticProcessCommand PrepareCheckinStandardInput(PlasticProcessCommand command, ref string temporaryDirectory)
        {
            if (!command.Arguments.Contains("-")) throw new ArgumentException("Native checkin stdin requires an explicit path-list argument.");
            string[] paths = command.StandardInput.TrimEnd('\r', '\n').Split(new [] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            if (paths.Length == 0 || paths.Any(path => String.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0))
                throw new ArgumentException("Checkin stdin must contain only one explicit path per line.");
            string workspaceRoot = System.IO.Path.GetFullPath(command.WorkingDirectory);
            paths = paths.Select(path => System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path :
                System.IO.Path.Combine(workspaceRoot, path))).ToArray();
            if (paths.Any(path => !IsWithin(path, workspaceRoot) || !System.IO.Path.IsPathRooted(path)))
                throw new ArgumentException("Checkin stdin paths must belong to the command workspace.");
            var innerArguments = command.Arguments.ToList();
            string comment = innerArguments.FirstOrDefault(argument => argument.StartsWith("-c=", StringComparison.Ordinal));
            if (comment != null)
            {
                EnsureCheckinTemporaryDirectory(ref temporaryDirectory);
                string commentsFile = System.IO.Path.Combine(temporaryDirectory, "comments.txt");
                File.WriteAllText(commentsFile, comment.Substring(3), new UTF8Encoding(false, true));
                innerArguments.Remove(comment);
                innerArguments.Add("-commentsfile=" + commentsFile);
            }
            var shellArguments = new List<string> { "shell", "--encoding=utf-8", "--enablestderr" };
            foreach (string argument in innerArguments.Where(argument => argument.StartsWith("--clientconf=", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-clientconf=", StringComparison.OrdinalIgnoreCase)).ToArray())
            { innerArguments.Remove(argument); shellArguments.Add(argument); }
            // cm's ordinary Console.ReadLine reader corrupts CP936 characters
            // at some long-input buffer boundaries. Its supported shell UTF-8
            // reader preserves every path. Execute exactly one checkin inside
            // one cm process; comments live in a file so their newlines cannot
            // become commands. The path list ends before the controlled exit.
            // An early native validation failure can leave the path list unread.
            // Absolute drive/UNC paths cannot be cm shell command names, even
            // then. Never send workspace-relative names as shell input lines.
            string line = String.Join(" ", innerArguments.Select(QuoteCmShellArgument));
            string input = line + Environment.NewLine + String.Join(Environment.NewLine, paths) +
                Environment.NewLine + Environment.NewLine + "exit" + Environment.NewLine;
            return new PlasticProcessCommand { FileName = command.FileName, WorkingDirectory = command.WorkingDirectory,
                Arguments = shellArguments, StandardInput = input };
        }

        private static string QuoteCmShellArgument(string argument)
        {
            // cm shell has its own quote-toggle parser; it does not implement
            // CRT backslash escaping. Windows paths cannot contain a quote.
            if (argument.IndexOfAny(new [] { '"', '\r', '\n', '\0' }) >= 0)
                throw new ArgumentException("Unsupported character in an internal Plastic SCM shell argument.");
            return argument.IndexOf(' ') >= 0 ? "\"" + argument + "\"" : argument;
        }

        internal static PlasticCommandResult ReadShellCheckinResult(PlasticCommandResult result)
        {
            if (!result.Succeeded) return result;
            MatchCollection statuses = Regex.Matches(result.Output, @"(?m)^CommandResult (-?\d+)\r?$", RegexOptions.CultureInvariant);
            int exitCode;
            if (statuses.Count != 1 || !Int32.TryParse(statuses[0].Groups[1].Value, out exitCode))
            {
                result.ExitCode = -1;
                result.Error += (String.IsNullOrEmpty(result.Error) ? "" : Environment.NewLine) +
                    "Plastic SCM did not return one unambiguous checkin result. Refresh status and inspect history before retrying.";
                return result;
            }
            result.ExitCode = exitCode;
            result.Output = result.Output.Remove(statuses[0].Index, statuses[0].Length).TrimEnd('\r', '\n') + Environment.NewLine;
            return result;
        }

        private async Task<PlasticCommandResult> ExecuteCoreAsync(PlasticProcessCommand command, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo { FileName = command.FileName, Arguments = String.Join(" ", command.Arguments.Select(QuoteArgument)),
                WorkingDirectory = command.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
            if (start.Arguments.Length > 30000) throw new ArgumentException("Too many selected paths for a Windows command; select fewer files.");
            if (command.Interactive)
            {
                // Gluon and the diff viewer own their UI lifetime; do not keep the caller busy
                // or inherit redirected pipes which would outlive this request.
                start.RedirectStandardInput = false; start.RedirectStandardOutput = false; start.RedirectStandardError = false;
                start.StandardOutputEncoding = null; start.StandardErrorEncoding = null;
                using (Process launched = Process.Start(start))
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    if (launched.HasExited && launched.ExitCode != 0)
                        return new PlasticCommandResult { ExitCode = launched.ExitCode, Error = "The Plastic SCM viewer could not be started." };
                    return new PlasticCommandResult { ExitCode = 0, Output = "Started the Plastic SCM viewer." };
                }
            }
            using (var process = new Process { StartInfo = start })
            {
                // Encode before starting a mutation. A character unavailable in
                // cm's input code page must not silently become '?' in a path.
                byte[] inputBytes = String.IsNullOrEmpty(command.StandardInput) ? null :
                    GetStandardInputEncoding(command).GetBytes(command.StandardInput);
                process.Start();
                Task<byte[]> output = ReadAllBytesAsync(process.StandardOutput.BaseStream);
                // cm uses UTF-8 for machine-readable output but localized
                // diagnostics follow the Windows console code page on some
                // installations.  Read stderr as bytes and choose UTF-8 only
                // when it is a valid encoding; otherwise use the system code
                // page.  This prevents a failed bulk check-in from showing
                // mojibake while keeping UTF-8 diagnostics intact.
                Task<byte[]> error = ReadAllBytesAsync(process.StandardError.BaseStream);
                try
                {
                    if (inputBytes != null)
                        await process.StandardInput.BaseStream.WriteAsync(inputBytes, 0, inputBytes.Length).ConfigureAwait(false);
                }
                // Close the raw pipe, not the default StreamWriter: flushing a
                // never-used UTF-8 writer can append its BOM to the path stream.
                finally { process.StandardInput.BaseStream.Close(); }
                var timer = Stopwatch.StartNew();
                bool timedOut = false;
                while (!process.HasExited)
                {
                    if (cancellationToken.IsCancellationRequested || timer.Elapsed > config.Timeout)
                    {
                        timedOut = !cancellationToken.IsCancellationRequested;
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        process.WaitForExit(5000);
                        cancellationToken.ThrowIfCancellationRequested();
                        break;
                    }
                    await Task.Delay(75).ConfigureAwait(false);
                }
                // A child process can inherit pipe handles after its parent exits. Bound
                // the drain so a timeout cannot turn into an infinite ReadToEnd wait.
                Task drain = Task.WhenAll(output, error);
                if (await Task.WhenAny(drain, Task.Delay(5000)).ConfigureAwait(false) != drain)
                    return new PlasticCommandResult { ExitCode = -1, TimedOut = true,
                        Output = output.Status == TaskStatus.RanToCompletion ? DecodeDiagnostic(output.Result) : "",
                        Error = "The Plastic SCM process did not close its output streams." };
                string stdout = DecodeDiagnostic(await output.ConfigureAwait(false));
                string stderr = DecodeDiagnostic(await error.ConfigureAwait(false));
                return new PlasticCommandResult { ExitCode = timedOut ? -1 : process.ExitCode, Output = stdout,
                    Error = timedOut ? "The Plastic SCM command timed out.\r\n" + stderr : stderr, TimedOut = timedOut };
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();

        private static Encoding GetStandardInputEncoding(PlasticProcessCommand command)
        {
            if (!System.IO.Path.GetFileName(command.FileName).Equals("cm.exe", StringComparison.OrdinalIgnoreCase))
                return new UTF8Encoding(false, true);
            if (command.Arguments.Count > 0 && command.Arguments[0] == "shell" && command.Arguments.Contains("--encoding=utf-8"))
                return new UTF8Encoding(false, true);
            // cm 11 reads Console.In. It switches that console to UTF-8 only
            // for its Chinese/Japanese/Korean UI language; with English UI on
            // Chinese Windows it reads CP936 even though status XML is UTF-8.
            string language = null;
            // cm sets its console encoding before processing --clientconf.
            // Our transport copy preserves that startup language unchanged.
            string location = GetCmClientConfigPath(command, false);
            if (File.Exists(location))
            {
                XElement data = SafeXml.Load(File.ReadAllText(location)).Root;
                language = data == null ? null : (string)data.Element("Language");
            }
            return SelectCmStandardInputEncoding(language, (int)GetOEMCP());
        }

        private static string GetCmClientConfigPath(PlasticProcessCommand command)
        { return GetCmClientConfigPath(command, true); }

        private static string GetCmClientConfigPath(PlasticProcessCommand command, bool includeExplicitConfig)
        {
            string explicitConfig = !includeExplicitConfig ? null : command.Arguments.FirstOrDefault(argument => argument.StartsWith("--clientconf=", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("-clientconf=", StringComparison.OrdinalIgnoreCase));
            if (explicitConfig != null)
            {
                string path = explicitConfig.Substring(explicitConfig.IndexOf('=') + 1);
                return System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(command.WorkingDirectory, path));
            }
            string executable = command.FileName;
            if (!System.IO.Path.IsPathRooted(executable))
            {
                foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                {
                    if (String.IsNullOrWhiteSpace(directory)) continue;
                    string candidate;
                    try { candidate = System.IO.Path.Combine(directory.Trim('"'), executable); }
                    catch (ArgumentException) { continue; }
                    if (File.Exists(candidate)) { executable = candidate; break; }
                }
            }
            string installed = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(executable) ?? "", "client.conf");
            if (File.Exists(installed)) return installed;
            string userDirectory = Environment.GetEnvironmentVariable("PLASTIC_HOME");
            if (String.IsNullOrEmpty(userDirectory)) userDirectory = Environment.GetEnvironmentVariable("PLASTIC_HOME", EnvironmentVariableTarget.User);
            if (String.IsNullOrEmpty(userDirectory)) userDirectory = Environment.GetEnvironmentVariable("PLASTIC_HOME", EnvironmentVariableTarget.Machine);
            if (String.IsNullOrEmpty(userDirectory)) userDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "plastic4");
            return System.IO.Path.Combine(userDirectory, "client.conf");
        }

        internal static Encoding SelectCmStandardInputEncoding(string language, int consoleCodePage)
        {
            if (String.IsNullOrEmpty(language))
            {
                string culture = System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName;
                language = culture == "zh" ? "zh-Hans" : culture;
            }
            if (language == "zh-Hans" || language == "zh-Hant" || language == "ja" || language == "ko" ||
                (language == "zh" && System.Globalization.CultureInfo.CurrentCulture.TwoLetterISOLanguageName == "zh"))
                return new UTF8Encoding(false, true);
            return Encoding.GetEncoding(consoleCodePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
        {
            using (var memory = new MemoryStream())
            {
                await stream.CopyToAsync(memory).ConfigureAwait(false);
                return memory.ToArray();
            }
        }

        private static string DecodeDiagnostic(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return "";
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            byte[] payload = offset == 0 ? bytes : bytes.Skip(offset).ToArray();
            try { return new UTF8Encoding(false, true).GetString(payload); }
            catch (DecoderFallbackException)
            {
                // cm.exe writes localized diagnostics using the active Windows
                // console code page on some installations (CP936 on Chinese
                // Windows), while machine-readable output is UTF-8.
                try { return Encoding.Default.GetString(payload); }
                catch (Exception) { return Encoding.UTF8.GetString(payload); }
            }
        }

        // CommandLineToArgvW / CRT quoting: never invoke cmd.exe or interpolate a shell command.
        public static string QuoteArgument(string argument)
        {
            if (argument == null) throw new ArgumentNullException("argument");
            if (argument.IndexOf('\0') >= 0) throw new ArgumentException("NUL is not valid in an argument.");
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                if (character == '"') { result.Append('\\', slashes * 2 + 1); result.Append('"'); }
                else { result.Append('\\', slashes); result.Append(character); }
                slashes = 0;
            }
            result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
        }

        private static bool IsWithin(string path, string root)
        {
            root = root.TrimEnd('\\', '/');
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void RejectReparsePath(string path)
        {
            for (string current = path; !String.IsNullOrEmpty(current); current = System.IO.Path.GetDirectoryName(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("Symbolic links and junctions are not supported as operation paths: " + current);
            }
        }

        private static void RejectUnsafeDescendants(string directory, string workspaceRoot, CancellationToken cancellationToken)
        {
            var pending = new Stack<string>(); pending.Push(directory);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string current = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(current))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (System.IO.Path.GetFileName(entry).Equals(".plastic", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!current.TrimEnd('\\').Equals(workspaceRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("A selected directory contains a nested workspace: " + current);
                        continue;
                    }
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("A selected directory contains a symbolic link or junction: " + entry);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                }
            }
        }
    }
}
