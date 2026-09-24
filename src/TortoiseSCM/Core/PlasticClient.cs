// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request == null) throw new ArgumentNullException("request");
            if (!Enum.IsDefined(typeof(PlasticCommand), request.Command)) throw new ArgumentException("Unsupported command.");
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
            bool partial = workspace.IsPartial;
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
                    args.Add("checkin"); args.AddRange(paths); args.Add("--all"); args.Add("-c=" + request.Comment);
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
            return result;
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
                    try { current = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false); }
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
                ApplyWorkspaceMode(planned, actualWorkspace.IsPartial);
            }
            PlasticCommandResult result = await ExecuteAsync(planned, cancellationToken).ConfigureAwait(false);
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
                process.Start();
                process.StandardInput.Close();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                Task<string> error = process.StandardError.ReadToEndAsync();
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
                        Output = output.Status == TaskStatus.RanToCompletion ? output.Result : "",
                        Error = "The Plastic SCM process did not close its output streams." };
                string stdout = await output.ConfigureAwait(false);
                string stderr = await error.ConfigureAwait(false);
                return new PlasticCommandResult { ExitCode = timedOut ? -1 : process.ExitCode, Output = stdout,
                    Error = timedOut ? "The Plastic SCM command timed out.\r\n" + stderr : stderr, TimedOut = timedOut };
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
