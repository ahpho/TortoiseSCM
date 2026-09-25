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
            "          changeset, rollback, switch, export, diff-history, remove, move, ignore, settings, merge\r\n" +
            "          merge-preview, merge-start, merge-status, merge-prepare, merge-resolve, merge-conflict-tool\r\n" +
            "          locks, unlock, cache-refresh, history-page\r\n" +
            "Options: --json --yes --recursive --comment <text> --commentsfile <UTF-8-file>\r\n" +
            "         --timeout <seconds> --cm <absolute-exe-path> --help\r\n" +
            "History: --changeset <number> (required for changeset, rollback, switch)\r\n" +
            "Paged history: history-page --path <scope> [--before <exclusive-changeset>] [--limit <1..100>]\r\n" +
            "  Default scan limit is 50; path pages may be empty with older history still available.\r\n" +
            "rollback restores selected content as pending changes; switch replaces the whole workspace revision.\r\n" +
            "Export: export --path <workspace> --item </repository/file> --changeset N --output <file> --yes [--overwrite]\r\n" +
            "Compare: diff-history --path <workspace> --item </repository/file> --from N --to N [--external]\r\n" +
            "Files: remove|ignore --path <file> --yes; move --path <source> --destination <absolute-path> --yes\r\n" +
            "Tools: diff --external; merge --base <file> --local <file> --remote <file> --output <file> --yes\r\n" +
            "Workspace merge: merge-preview|merge-start --path <root> --changeset <source> [--yes]\r\n" +
            "  merge-status --path <root>; merge-prepare|merge-conflict-tool --path <root> --changeset N --item </file> --yes\r\n" +
            "  merge-resolve --path <root> --changeset N --item </file> --result <absolute-file> --yes\r\n" +
            "  merge-directory-resolve --path <root> --changeset N --conflict N --resolution src|dst|rename [--rename name] --yes\r\n" +
            "  merge-continue --path <root> --changeset N --yes; merge-directory-cancel --path <root> --yes\r\n" +
            "Partial: partial-conflicts|partial-conflict-status --path <root>\r\n" +
            "  partial-conflict-prepare|partial-conflict-tool --path <root> --item </file> --yes\r\n" +
            "  partial-conflict-resolve --path <root> --item </file> --result <absolute-file> --yes\r\n" +
            "  partial-conflict-cancel --path <root> --yes (unapplied preparations only)\r\n" +
            "Partial structure: partial-structure-preview|partial-structure-status --path <root>\r\n" +
            "  partial-structure-prepare --path <root> --item </file> --yes\r\n" +
            "  partial-structure-resolve --path <root> --resolution keep-local|take-incoming|rename [--rename name] --yes\r\n" +
            "  partial-structure-cancel --path <root> --yes (unapplied only)\r\n" +
            "  partial-structure-recover --path <root> --yes (restore pinned incoming; retain backups)\r\n" +
            "Partial directories: partial-directory-preview|partial-directory-status --path <root>\r\n" +
            "  partial-directory-prepare --path <root> --item </directory> --yes\r\n" +
            "  partial-directory-resolve --path <root> --resolution take-incoming|keep-local --yes\r\n" +
            "  Deleted directory keep-local: re-add the backed-up local tree as new items; checkin separately.\r\n" +
            "  partial-directory-cancel|partial-directory-recover --path <root> --yes\r\n" +
            "Merge results remain pending; checkin is always a separate command.\r\n" +
            "Locks: locks --path <root>; unlock --path <root> --lock-id <guid> --yes (current user's lock only)\r\n" +
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
            if (options.Command == "cache-refresh")
            {
                int count = OverlayCacheHost.RefreshAsync(config, options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                response.data = new { workspace = workspaceData, entries = count, cacheFile = new OverlayCacheStore(OverlayCacheStore.DefaultDirectory).SnapshotPath };
                response.output = "Refreshed " + count + " cached overlay paths.";
                return;
            }
            if (options.Command == "locks")
            {
                var locks = client.GetLocksAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                response.data = new { workspace = workspaceData, locks = locks.Select(item => new {
                    lockId = item.LockId.ToString(), repository = item.Repository, itemId = item.ItemId, date = item.Date,
                    destinationBranch = item.DestinationBranch, destinationRevision = item.DestinationRevision,
                    holderBranch = item.HolderBranch, holderRevision = item.HolderRevision, status = item.Status,
                    owner = item.Owner, workspace = item.Workspace, path = item.Path, canUnlock = item.CanUnlock }).ToArray() };
                response.output = String.Join(Environment.NewLine, locks.Select(item => item.LockId + "\t" + item.Owner + "\t" + item.Path));
                return;
            }
            if (options.Command == "unlock")
            {
                SetResult(response, client.UnlockOwnAsync(options.Paths[0], options.LockId.Value, CancellationToken.None).GetAwaiter().GetResult());
                response.data = new { workspace = workspaceData, lockId = options.LockId.Value.ToString() };
                return;
            }
            if (options.Command.StartsWith("partial-directory-", StringComparison.Ordinal))
            {
                if (options.Command == "partial-directory-preview")
                {
                    var conflicts = client.PreviewPartialDirectoriesAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    response.data = new { workspace = workspaceData, conflicts = conflicts.Select(PartialDirectoryData).ToArray() };
                    response.output = String.Join(Environment.NewLine, conflicts.Select(item => item.RepositoryPath + "\t" + item.Kind + "\t" + item.IncomingPath + "\t" + item.Items.Count + " affected items\t" + item.Reason + Environment.NewLine +
                        String.Join(Environment.NewLine, item.ResolutionOptions.Select(option => "  " + option + ": " + PartialDirectoryResolutionDescription(item, option))) + Environment.NewLine +
                        String.Join(Environment.NewLine, item.Items.Select(child => "  " + (child.IsDirectory ? "D" : "F") + (child.HasLocalChanges ? " modified " : " ") + child.RepositoryPath + " -> " + (String.IsNullOrEmpty(child.IncomingPath) ? "(removed)" : child.IncomingPath))))); return;
                }
                if (options.Command == "partial-directory-status" || options.Command == "partial-directory-prepare")
                {
                    var session = options.Command == "partial-directory-status" ? client.GetPartialDirectorySessionAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult() :
                        client.PreparePartialDirectoryAsync(options.Paths[0], options.Item, CancellationToken.None).GetAwaiter().GetResult();
                    response.data = new { workspace = workspaceData, sessionId = session == null ? null : session.SessionId,
                        recoveryDirectory = session == null ? null : session.RecoveryDirectory, ready = session == null || session.Ready, applying = session != null && session.Applying,
                        conflict = session == null ? null : PartialDirectoryData(session.Conflict), resolution = session == null ? null : session.Resolution };
                    response.output = session == null ? "No active Partial directory session." : "Partial directory session: " + session.SessionId + "\r\nRecovery backups: " + session.RecoveryDirectory; return;
                }
                if (options.Command == "partial-directory-cancel")
                {
                    client.CancelPartialDirectoryAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    response.output = "Unapplied directory preparation cancelled; working tree unchanged, backups retained."; return;
                }
                SetResult(response, options.Command == "partial-directory-recover" ? client.RecoverPartialDirectoryAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult() :
                    client.ResolvePartialDirectoryAsync(options.Paths[0], options.Resolution, CancellationToken.None).GetAwaiter().GetResult()); return;
            }
            if (options.Command.StartsWith("partial-structure-", StringComparison.Ordinal))
            {
                if (options.Command == "partial-structure-preview")
                {
                    var conflicts = client.PreviewPartialStructureAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    response.data = new { workspace = workspaceData, conflicts = conflicts.Select(PartialStructureData).ToArray() };
                    response.output = String.Join(Environment.NewLine, conflicts.Select(item => item.RepositoryPath + "\t" + item.Kind + "\t" + item.IncomingPath + "\t" + item.Reason)); return;
                }
                if (options.Command == "partial-structure-status" || options.Command == "partial-structure-prepare")
                {
                    var session = options.Command == "partial-structure-status" ? client.GetPartialStructureSessionAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult() :
                        client.PreparePartialStructureAsync(options.Paths[0], options.Item, CancellationToken.None).GetAwaiter().GetResult();
                    response.data = new { workspace = workspaceData, sessionId = session == null ? null : session.SessionId,
                        recoveryDirectory = session == null ? null : session.RecoveryDirectory, ready = session == null || session.Ready, applying = session != null && session.Applying,
                        conflict = session == null ? null : PartialStructureData(session.Conflict), resolution = session == null ? null : session.Resolution, renamePath = session == null ? null : session.RenamePath };
                    response.output = session == null ? "No active Partial structure session." : "Partial structure session: " + session.SessionId + "\r\nRecovery backups: " + session.RecoveryDirectory; return;
                }
                if (options.Command == "partial-structure-cancel")
                {
                    client.CancelPartialStructureAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    response.output = "Unapplied structure preparation cancelled; working files unchanged, backups retained."; return;
                }
                SetResult(response, options.Command == "partial-structure-recover" ? client.RecoverPartialStructureAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult() :
                    client.ResolvePartialStructureAsync(options.Paths[0], options.Resolution, options.Rename, CancellationToken.None).GetAwaiter().GetResult()); return;
            }
            if (options.Command.StartsWith("partial-conflict", StringComparison.Ordinal))
            {
                if (options.Command == "partial-conflicts")
                {
                    var conflicts = client.PreviewPartialConflictsAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    response.data = new { workspace = workspaceData, conflicts = conflicts.Select(PartialConflictData).ToArray() };
                    response.output = String.Join(Environment.NewLine, conflicts.Select(item => item.RepositoryPath + "\tcs:" + item.BaseChangeset + " -> cs:" + item.IncomingChangeset + "\t" + item.Reason)); return;
                }
                if (options.Command == "partial-conflict-status")
                {
                    var session = client.GetPartialConflictSessionAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    response.data = new { workspace = workspaceData, sessionId = session == null ? null : session.SessionId,
                        recoveryDirectory = session == null ? null : session.RecoveryDirectory,
                        applying = session != null && session.Applying, ready = session == null || session.Ready,
                        conflicts = session == null ? new object[0] : session.Conflicts.Select(PartialConflictData).ToArray() };
                    response.output = session == null ? "No active Partial conflict session." : "Partial conflict session: " + session.SessionId; return;
                }
                if (options.Command == "partial-conflict-cancel")
                {
                    client.CancelPartialConflictPreparationAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    response.output = "Unapplied Partial preparation cancelled. Workspace files were not changed."; return;
                }
                if (options.Command == "partial-conflict-resolve")
                {
                    SetResult(response, client.ResolvePartialConflictAsync(options.Paths[0], options.Item, options.Result, CancellationToken.None).GetAwaiter().GetResult()); return;
                }
                var files = client.PreparePartialConflictAsync(options.Paths[0], options.Item, CancellationToken.None).GetAwaiter().GetResult();
                if (options.Command == "partial-conflict-tool")
                    SetResult(response, client.RunMergeToolAsync(files.BasePath, files.LocalPath, files.RemotePath, files.ResultPath, CancellationToken.None).GetAwaiter().GetResult());
                else response.output = "Review the result, then apply it with partial-conflict-resolve. No workspace files changed.";
                response.data = new { workspace = workspaceData, sessionId = files.SessionId, item = files.RepositoryPath,
                    basePath = files.BasePath, localPath = files.LocalPath, remotePath = files.RemotePath, resultPath = files.ResultPath }; return;
            }
            if (options.Command.StartsWith("merge-", StringComparison.Ordinal))
            {
                if (options.Command == "merge-directory-cancel")
                {
                    client.CancelDirectoryMergeAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    response.output = "Directory planning session cancelled. Workspace files were not changed."; return;
                }
                if (options.Command == "merge-directory-resolve" || options.Command == "merge-continue")
                {
                    var session = options.Command == "merge-continue" ? client.ContinueMergeAsync(options.Paths[0], options.Changeset.Value, CancellationToken.None).GetAwaiter().GetResult() :
                        client.ResolveDirectoryConflictAsync(options.Paths[0], options.Changeset.Value, options.Conflict.Value, options.Resolution, options.Rename, CancellationToken.None).GetAwaiter().GetResult();
                    response.data = new { workspace = workspaceData, sessionId = session.SessionId, awaitingDirectoryResolution = session.AwaitingDirectoryResolution, plan = MergePlanData(session.Plan) };
                    response.output = MergePlanText(session.Plan); return;
                }
                if (options.Command == "merge-preview")
                {
                    var plan = client.PreviewMergeAsync(options.Paths[0], options.Changeset.Value, CancellationToken.None).GetAwaiter().GetResult();
                    response.data = new { workspace = workspaceData, plan = MergePlanData(plan) };
                    response.output = MergePlanText(plan); return;
                }
                if (options.Command == "merge-start" || options.Command == "merge-status")
                {
                    var session = options.Command == "merge-start" ? client.BeginMergeAsync(options.Paths[0], options.Changeset.Value, CancellationToken.None).GetAwaiter().GetResult() :
                        client.GetMergeSessionAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                    if (session == null)
                    {
                        response.data = new { workspace = workspaceData, sessionId = (string)null, isRollback = false, plan = (object)null };
                        response.output = "No active TortoiseSCM merge session."; return;
                    }
                    response.data = new { workspace = workspaceData, sessionId = session.SessionId, isRollback = session.IsRollback, awaitingDirectoryResolution = session.AwaitingDirectoryResolution, plan = MergePlanData(session.Plan) };
                    response.output = MergePlanText(session.Plan); return;
                }
                if (options.Command == "merge-resolve")
                {
                    SetResult(response, client.ApplyMergeFileResolutionAsync(options.Paths[0], options.Changeset.Value, options.Item, options.Result,
                        CancellationToken.None).GetAwaiter().GetResult());
                    response.data = new { workspace = workspaceData, sourceChangeset = options.Changeset.Value, item = options.Item, resultPath = options.Result };
                    return;
                }
                var files = client.PrepareMergeConflictAsync(options.Paths[0], options.Changeset.Value, options.Item, CancellationToken.None).GetAwaiter().GetResult();
                if (options.Command == "merge-conflict-tool")
                    SetResult(response, client.RunMergeToolAsync(files.BasePath, files.LocalPath, files.RemotePath, files.ResultPath, CancellationToken.None).GetAwaiter().GetResult());
                else response.output = "Conflict inputs prepared. Write the resolved content to " + files.ResultPath + " and use merge-resolve to apply it.";
                response.data = new { workspace = workspaceData, sessionId = files.SessionId, item = files.RepositoryPath,
                    basePath = files.BasePath, localPath = files.LocalPath, remotePath = files.RemotePath, resultPath = files.ResultPath };
                return;
            }
            if (options.Command == "remove" || options.Command == "move" || options.Command == "ignore")
            {
                var result = options.Command == "remove" ? client.RemoveAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult() :
                    options.Command == "move" ? client.MoveAsync(options.Paths[0], options.Destination, CancellationToken.None).GetAwaiter().GetResult() :
                    client.IgnoreAsync(options.Paths[0], CancellationToken.None).GetAwaiter().GetResult();
                SetResult(response, result);
                response.data = new { workspace = workspaceData, path = options.Paths[0], destination = options.Destination };
                return;
            }
            if (options.Command == "export")
            {
                SetResult(response, client.ExportRevisionAsync(options.Paths[0], options.Item, options.Changeset.Value,
                    options.Output, options.Overwrite, CancellationToken.None).GetAwaiter().GetResult());
                response.data = new { workspace = workspaceData, item = options.Item, changeset = options.Changeset.Value, outputPath = options.Output };
                return;
            }
            if (options.Command == "diff-history")
            {
                if (options.External)
                {
                    SetResult(response, client.OpenRevisionDiffToolAsync(options.Paths[0], options.Item, options.From.Value, options.To.Value, CancellationToken.None).GetAwaiter().GetResult());
                    response.data = new { workspace = workspaceData, item = options.Item, from = options.From.Value, to = options.To.Value, external = true };
                }
                else
                {
                    var diff = client.GetRevisionDiffAsync(options.Paths[0], options.Item, options.From.Value, options.To.Value, CancellationToken.None).GetAwaiter().GetResult();
                    response.output = diff.DiffText;
                    response.data = new { workspace = workspaceData, item = options.Item, from = options.From.Value, to = options.To.Value,
                        path = diff.Path, baseRevision = diff.BaseRevision, diffText = diff.DiffText, isBinary = diff.IsBinary, hasChanges = diff.HasChanges };
                }
                return;
            }
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
            if (options.Command == "history-page")
            {
                var page = client.GetHistoryPageAsync(options.Paths[0], options.Before, options.Limit ?? 50, CancellationToken.None).GetAwaiter().GetResult();
                response.data = new { workspace = workspaceData, repository = page.Repository, scope = page.Scope,
                    scannedChangesets = page.ScannedChangesets, hasMore = page.HasMore, nextBeforeChangeset = page.NextBeforeChangeset,
                    entries = page.Items.Select(entry => new { path = entry.Path, revisionSpec = entry.RevisionSpec, changeset = entry.Changeset,
                        creationDate = entry.CreationDate, owner = entry.Owner, branch = entry.Branch, comment = entry.Comment, repository = entry.Repository }).ToArray() };
                response.output = String.Join(Environment.NewLine, page.Items.Select(entry =>
                    "Changeset " + entry.Changeset.ToString(CultureInfo.InvariantCulture) + " | " + entry.Owner + " | " + entry.Branch + "\r\n" + entry.Comment));
                response.output += Environment.NewLine + "Scanned " + page.ScannedChangesets + " changesets; " + page.Items.Count + " matched. " +
                    (page.HasMore ? "Older history remains; continue with --before " + page.NextBeforeChangeset.Value.ToString(CultureInfo.InvariantCulture) + "." : "No older history remains.");
                if (page.Scope != "/") response.output += Environment.NewLine + "Path publication history includes rollback commits; it does not follow renamed items through their older paths.";
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

        private static object MergePlanData(PlasticMergePlan plan)
        {
            return new { workspaceRoot = plan.WorkspaceRoot, repository = plan.Repository, sourceChangeset = plan.SourceChangeset,
                destinationChangeset = plan.DestinationChangeset, baseChangeset = plan.BaseChangeset, alreadyConnected = plan.AlreadyConnected,
                fileConflicts = plan.FileConflicts.Select(conflict => new { item = conflict.RepositoryPath, baseChangeset = conflict.BaseChangeset,
                    sourceChangeset = conflict.SourceChangeset, destinationChangeset = conflict.DestinationChangeset, itemId = conflict.ItemId, resolved = conflict.Resolved }).ToArray(),
                operations = plan.Operations.Select(operation => new { kind = operation.Kind, path = operation.Path, destinationPath = operation.DestinationPath }).ToArray(),
                directoryConflicts = plan.DirectoryConflicts.Select(conflict => new { index = conflict.Index, kind = conflict.Kind, description = conflict.Description,
                    sourcePath = conflict.SourcePath, destinationPath = conflict.DestinationPath, resolved = conflict.Resolved, resolution = conflict.Resolution,
                    rename = conflict.Rename, isDirectory = conflict.IsDirectory, sourceOperation = conflict.SourceOperation, destinationOperation = conflict.DestinationOperation,
                    sourceOriginalPath = conflict.SourceOriginalPath, destinationOriginalPath = conflict.DestinationOriginalPath, resolutionOptions = conflict.ResolutionOptions }).ToArray() };
        }

        private static object PartialConflictData(PlasticPartialConflict item)
        {
            return new { item = item.RepositoryPath, baseChangeset = item.BaseChangeset, incomingChangeset = item.IncomingChangeset,
                itemId = item.ItemId, canResolve = item.CanResolve, reason = item.Reason, resolved = item.Resolved };
        }

        private static object PartialDirectoryData(PlasticPartialDirectoryConflict item)
        {
            if (item == null) return null;
            return new { repositoryPath = item.RepositoryPath, incomingPath = item.IncomingPath, kind = item.Kind, reason = item.Reason,
                itemId = item.ItemId, incomingChangeset = item.IncomingChangeset, resolutionOptions = item.ResolutionOptions,
                resolutionDetails = item.ResolutionOptions.Select(option => new { resolution = option, description = PartialDirectoryResolutionDescription(item, option),
                    readdsAsNewItems = item.Kind == "incoming-directory-delete" && option == "keep-local" }).ToArray(),
                items = item.Items.Select(child => new { repositoryPath = child.RepositoryPath, incomingPath = child.IncomingPath,
                    isDirectory = child.IsDirectory, itemId = child.ItemId, baseChangeset = child.BaseChangeset,
                    incomingRevisionChangeset = child.IncomingRevisionChangeset, hasLocalChanges = child.HasLocalChanges }).ToArray() };
        }
        private static string PartialDirectoryResolutionDescription(PlasticPartialDirectoryConflict item, string option)
        {
            if (item.Kind == "incoming-directory-delete") return option == "keep-local" ?
                "Re-add all backed-up local files and empty directories at their original paths as new items. Old item identities and history are not restored. Review and check in separately." :
                "Accept the server deletion; remove the local tree and retain its recovery backups.";
            return option == "keep-local" ? "Follow the server directory move and keep locally edited file contents; clean files use incoming contents. Check in separately." :
                "Accept the server directory position and contents; retain local recovery backups.";
        }
        private static object PartialStructureData(PlasticPartialStructureConflict item)
        {
            if (item == null) return null;
            return new { item = item.RepositoryPath, originalPath = item.OriginalPath, incomingPath = item.IncomingPath, kind = item.Kind, reason = item.Reason,
                baseChangeset = item.BaseChangeset, incomingChangeset = item.IncomingChangeset, itemId = item.ItemId,
                incomingItemId = item.IncomingItemId, incomingRevisionChangeset = item.IncomingRevisionChangeset, resolutionOptions = item.ResolutionOptions };
        }

        private static string MergePlanText(PlasticMergePlan plan)
        {
            return "Merge cs:" + plan.SourceChangeset.ToString(CultureInfo.InvariantCulture) + " into cs:" + plan.DestinationChangeset.ToString(CultureInfo.InvariantCulture) +
                "\r\nContent conflicts: " + plan.FileConflicts.Count.ToString(CultureInfo.InvariantCulture) +
                "\r\nDirectory conflicts: " + plan.DirectoryConflicts.Count.ToString(CultureInfo.InvariantCulture) +
                "\r\nOperations: " + plan.Operations.Count.ToString(CultureInfo.InvariantCulture);
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
        internal string Base, Local, Remote, Output, Item, Destination, Result;
        internal bool Help, Recursive, External, Overwrite;
        internal int? Timeout, Limit, Conflict;
        internal string Resolution, Rename;
        internal long? Changeset, From, To, Before;
        internal Guid? LockId;
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
                    case "--item": case "--from": case "--to": case "--destination": case "--result": case "--lock-id": case "--before": case "--limit":
                    case "--conflict": case "--resolution": case "--rename": ++i; break;
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
                    case "--overwrite": options.Overwrite = true; break;
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
                    case "--item": options.Item = Value(args, ref i); break;
                    case "--destination": options.Destination = AbsolutePath(Value(args, ref i)); break;
                    case "--result": options.Result = AbsolutePath(Value(args, ref i)); break;
                    case "--conflict":
                        int conflict;
                        if (!Int32.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out conflict) || conflict < 1) throw new ArgumentException("--conflict must be a positive index.");
                        options.Conflict = conflict; break;
                    case "--resolution": options.Resolution = Value(args, ref i); break;
                    case "--rename": options.Rename = Value(args, ref i); break;
                    case "--lock-id":
                        Guid lockId;
                        if (!Guid.TryParse(Value(args, ref i), out lockId) || lockId == Guid.Empty) throw new ArgumentException("--lock-id must be a nonempty GUID.");
                        options.LockId = lockId; break;
                    case "--from": options.From = Revision(Value(args, ref i)); break;
                    case "--to": options.To = Revision(Value(args, ref i)); break;
                    case "--before": options.Before = Revision(Value(args, ref i)); break;
                    case "--limit":
                        int limit;
                        if (!Int32.TryParse(Value(args, ref i), NumberStyles.None, CultureInfo.InvariantCulture, out limit) || limit < 1 || limit > 100)
                            throw new ArgumentException("--limit must be an integer from 1 to 100.");
                        options.Limit = limit; break;
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
            if (!new[] { "status", "workspace", "add", "checkout", "checkin", "undo", "update", "history", "diff", "changeset", "rollback", "switch", "settings", "merge", "export", "diff-history", "remove", "move", "ignore",
                "merge-preview", "merge-start", "merge-status", "merge-prepare", "merge-resolve", "merge-conflict-tool", "merge-directory-resolve", "merge-continue", "merge-directory-cancel", "locks", "unlock", "cache-refresh", "history-page",
                "partial-conflicts", "partial-conflict-status", "partial-conflict-prepare", "partial-conflict-tool", "partial-conflict-resolve", "partial-conflict-cancel",
                "partial-structure-preview", "partial-structure-status", "partial-structure-prepare", "partial-structure-resolve", "partial-structure-cancel", "partial-structure-recover",
                "partial-directory-preview", "partial-directory-status", "partial-directory-prepare", "partial-directory-resolve", "partial-directory-cancel", "partial-directory-recover" }.Contains(options.Command))
                throw new ArgumentException("Unsupported CLI command: " + options.Command);
            if (options.Command != "settings" && options.Command != "merge" && options.Paths.Count == 0) throw new ArgumentException("At least one explicit --path is required.");
            if ((options.Command == "settings" || options.Command == "merge") && options.Paths.Count != 0) throw new ArgumentException("This command does not accept --path.");
            bool write = new[] { "add", "checkout", "checkin", "undo", "update", "rollback", "switch", "merge", "export", "remove", "move", "ignore", "merge-start", "merge-prepare", "merge-resolve", "merge-conflict-tool", "unlock" }.Contains(options.Command) || options.ChangesSettings;
            if (write && !yes) throw new ArgumentException("Write commands require explicit --yes confirmation.");
            bool partialWorkflow = options.Command.StartsWith("partial-conflict", StringComparison.Ordinal);
            bool structureWorkflow = options.Command.StartsWith("partial-structure-", StringComparison.Ordinal);
            bool structureResolution = options.Command == "partial-structure-resolve";
            bool partialDirectoryWorkflow = options.Command.StartsWith("partial-directory-", StringComparison.Ordinal);
            bool partialDirectoryResolution = options.Command == "partial-directory-resolve";
            if (partialDirectoryWorkflow && options.Paths.Count != 1) throw new ArgumentException("Partial directory operations require exactly one workspace root.");
            if (partialDirectoryWorkflow && options.Command != "partial-directory-preview" && options.Command != "partial-directory-status" && !yes) throw new ArgumentException("Partial directory writes require --yes.");
            if (structureWorkflow && options.Paths.Count != 1) throw new ArgumentException("Partial structure operations require exactly one workspace root.");
            if (structureWorkflow && options.Command != "partial-structure-preview" && options.Command != "partial-structure-status" && !yes) throw new ArgumentException("Partial structure writes require --yes.");
            bool partialFile = new[] { "partial-conflict-prepare", "partial-conflict-tool", "partial-conflict-resolve" }.Contains(options.Command);
            if (partialWorkflow && options.Paths.Count != 1) throw new ArgumentException("Partial conflict operations require exactly one workspace root.");
            if ((partialFile || options.Command == "partial-conflict-cancel") && !yes) throw new ArgumentException("Partial conflict writes require --yes.");
            bool directoryResolution = options.Command == "merge-directory-resolve";
            if ((directoryResolution || options.Command == "merge-continue" || options.Command == "merge-directory-cancel") && !yes) throw new ArgumentException("Merge writes require --yes.");
            if (directoryResolution != options.Conflict.HasValue) throw new ArgumentException("--conflict is required only for merge-directory-resolve.");
            if ((directoryResolution || structureResolution || partialDirectoryResolution) != (options.Resolution != null)) throw new ArgumentException("--resolution is required only for structural resolution commands.");
            if (partialDirectoryResolution && !new[] { "keep-local", "take-incoming" }.Contains(options.Resolution)) throw new ArgumentException("--resolution must be keep-local or take-incoming.");
            if (directoryResolution && !new[] { "src", "dst", "rename" }.Contains(options.Resolution)) throw new ArgumentException("--resolution must be src, dst or rename.");
            if (structureResolution && !new[] { "keep-local", "take-incoming", "rename" }.Contains(options.Resolution)) throw new ArgumentException("--resolution must be keep-local, take-incoming or rename.");
            if (((directoryResolution || structureResolution) && options.Resolution == "rename") != (options.Rename != null)) throw new ArgumentException("--rename is required only for rename resolution.");
            if (options.Command == "cache-refresh" && (options.Paths.Count != 1 || !yes)) throw new ArgumentException("cache-refresh requires one workspace root and --yes to write the local cache.");
            if (options.Command == "history-page" && options.Paths.Count != 1) throw new ArgumentException("history-page requires exactly one file, directory or workspace scope.");
            if (options.Command != "history-page" && (options.Before.HasValue || options.Limit.HasValue)) throw new ArgumentException("--before and --limit are supported only for history-page.");
            if (options.ChangesSettings && options.Command != "settings") throw new ArgumentException("Tool configuration options require --command settings.");
            if (new[] { "remove", "move", "ignore" }.Contains(options.Command) && options.Paths.Count != 1) throw new ArgumentException("File operations require exactly one explicit --path.");
            if ((options.Command == "move") != (options.Destination != null)) throw new ArgumentException("--destination is required only for move.");
            if ((options.Command == "unlock") != options.LockId.HasValue) throw new ArgumentException("--lock-id is required only for unlock.");
            if ((options.Command == "locks" || options.Command == "unlock") && options.Paths.Count != 1) throw new ArgumentException("Lock operations require exactly one workspace root.");
            bool mergeWorkflow = options.Command.StartsWith("merge-", StringComparison.Ordinal);
            bool conflictFile = partialFile || options.Command == "partial-structure-prepare" || options.Command == "partial-directory-prepare" || options.Command == "merge-prepare" || options.Command == "merge-resolve" || options.Command == "merge-conflict-tool";
            bool needsChangeset = new[] { "changeset", "rollback", "switch", "export", "merge-preview", "merge-start", "merge-prepare", "merge-resolve", "merge-conflict-tool", "merge-directory-resolve", "merge-continue" }.Contains(options.Command);
            if (needsChangeset != options.Changeset.HasValue) throw new ArgumentException("This command " + (needsChangeset ? "requires" : "does not accept") + " --changeset.");
            if (needsChangeset && options.Paths.Count != 1) throw new ArgumentException("Select exactly one file or directory scope for this command.");
            if (mergeWorkflow && options.Paths.Count != 1) throw new ArgumentException("Workspace merge operations require exactly one explicit workspace root.");
            if (conflictFile && String.IsNullOrWhiteSpace(options.Item)) throw new ArgumentException("This merge operation requires a repository --item path.");
            if ((options.Command == "merge-resolve" || options.Command == "partial-conflict-resolve") != (options.Result != null)) throw new ArgumentException("--result is required only for conflict resolution commands.");
            if (options.External && ((options.Command != "diff" && options.Command != "diff-history") || options.Paths.Count != 1)) throw new ArgumentException("--external requires diff or diff-history with exactly one scope.");
            bool historyFile = options.Command == "export" || options.Command == "diff-history";
            if (historyFile && (options.Paths.Count != 1 || String.IsNullOrWhiteSpace(options.Item))) throw new ArgumentException("Historical file operations require one --path and a repository --item path.");
            if (options.Item != null && !historyFile && !conflictFile) throw new ArgumentException("--item is valid only for historical file or merge conflict operations.");
            if (options.Command == "diff-history" && (!options.From.HasValue || !options.To.HasValue)) throw new ArgumentException("diff-history requires --from and --to changeset numbers.");
            if (options.Command != "diff-history" && (options.From.HasValue || options.To.HasValue)) throw new ArgumentException("--from and --to are valid only for diff-history.");
            if (options.Overwrite && options.Command != "export") throw new ArgumentException("--overwrite is valid only for export.");
            if (options.Command == "export" && options.Output == null) throw new ArgumentException("export requires --output.");
            if (options.Output != null && options.Command != "export" && options.Command != "merge") throw new ArgumentException("--output is valid only for export and merge.");
            bool mergePaths = options.Base != null || options.Local != null || options.Remote != null;
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

        private static long Revision(string value)
        {
            long revision;
            if (!Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out revision)) throw new ArgumentException("Changeset numbers must be nonnegative integers.");
            return revision;
        }

        private static string AbsolutePath(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || !Path.IsPathRooted(value) || Path.GetPathRoot(value).Length < 3)
                throw new ArgumentException("Paths must be absolute: " + value);
            return Path.GetFullPath(value);
        }
    }
}
