// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        private async Task<PlasticMergeSession> BeginDirectoryMergePlanAsync(PlasticWorkspace workspace, PlasticMergePlan plan, CancellationToken token)
        {
            ValidateMergeStorageLocation(workspace.RootPath);
            if (plan.DirectoryConflicts.Any(item => item.ResolutionOptions.Count == 0)) throw new ArgumentException("This merge contains an unrecognized directory conflict type. No merge plan was started.");
            await RejectIgnoredDirectoryMergePathsAsync(workspace.RootPath, plan, token).ConfigureAwait(false);
            var state = new MergeSessionState { Selector = workspace.Selector, DirectoryFingerprint = DirectoryMergeFingerprint(workspace.RootPath, token),
                Session = new PlasticMergeSession { SessionId = Guid.NewGuid().ToString("N"), Plan = plan, AwaitingDirectoryResolution = true } };
            CreatePrivateMergeDirectory(MergeSessionDirectory(state.Session.SessionId));
            SaveMergeState(state); WriteMergeIndex(workspace.RootPath, state.Session.SessionId);
            string marker = DirectoryMergeMarker(workspace.RootPath); RejectReparsePath(marker);
            File.WriteAllText(marker, Path.Combine(MergeSessionDirectory(state.Session.SessionId), "session.xml"));
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, DirectoryMergeArguments(state)), token).ConfigureAwait(false);
            RequireSuccess(result);
            var native = ParseMergePlan(result.Output, workspace.RootPath, workspace.Repository, plan.SourceChangeset);
            VerifyDirectoryContributors(state, native);
            if (DirectoryMergeFingerprint(workspace.RootPath, token) != state.DirectoryFingerprint)
                throw new IOException("Workspace files changed while preparing the directory merge. The plan was stopped; no checkin is allowed.");
            SynchronizeDirectoryPlan(state, native);
            state.Ready = true; SaveMergeState(state); return state.Session;
        }

        public async Task<PlasticMergeSession> ResolveDirectoryConflictAsync(string root, long sourceChangeset, int conflictIndex, string resolution, string rename, CancellationToken token)
        {
            var workspace = await MergeWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadMergeState(workspace.RootPath, true);
                await ValidateDirectorySessionAsync(state, workspace, sourceChangeset, token).ConfigureAwait(false);
                var native = await RefreshDirectoryPlanAsync(state, workspace, token).ConfigureAwait(false);
                var selected = state.Session.Plan.DirectoryConflicts.SingleOrDefault(item => item.Index == conflictIndex);
                if (selected == null || selected.Resolved) throw new ArgumentException("Select an unresolved directory conflict from this session.");
                if (!selected.ResolutionOptions.Contains(resolution)) throw new ArgumentException("Choose one of the offered native directory resolution options.");
                if (resolution == "rename") ValidateDirectoryRename(workspace.RootPath, state.Session.Plan, selected, rename);
                else if (!String.IsNullOrEmpty(rename)) throw new ArgumentException("A new name is accepted only with the rename resolution.");
                var current = native.DirectoryConflicts.SingleOrDefault(item => SameDirectoryConflict(item, selected));
                if (current == null) throw new ArgumentException("The native directory conflict changed. Refresh before choosing a resolution.");
                var args = DirectoryMergeArguments(state);
                args.Add("--resolveconflict"); args.Add("--conflict=" + current.Index.ToString(CultureInfo.InvariantCulture));
                args.Add("--resolutionoption=" + resolution); if (resolution == "rename") args.Add("--resolutioninfo=" + rename);
                args.Add("--merge");
                // This operation changes only opaque native planning files on supported
                // clients. Mark it uncertain first; unexpected mutations cannot be committed.
                state.Applying = true; SaveMergeState(state);
                var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, args), token).ConfigureAwait(false);
                RequireSuccess(result);
                if (DirectoryMergeFingerprint(workspace.RootPath, token) != state.DirectoryFingerprint)
                    throw new IOException("Native directory resolution changed workspace files before plan application. Inspect the workspace; checkin is blocked.");
                var updated = ParseMergePlan(result.Output, workspace.RootPath, workspace.Repository, sourceChangeset);
                VerifyDirectoryContributors(state, updated);
                if (updated.DirectoryConflicts.Any(item => SameDirectoryConflict(item, selected)))
                    throw new IOException("Plastic did not resolve the selected directory conflict. The session remains blocked for inspection.");
                selected.Resolution = resolution; selected.Rename = rename ?? "";
                SynchronizeDirectoryPlan(state, updated);
                state.Applying = false; SaveMergeState(state); return state.Session;
            }
        }

        public async Task<PlasticMergeSession> ContinueMergeAsync(string root, long sourceChangeset, CancellationToken token)
        {
            var workspace = await MergeWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadMergeState(workspace.RootPath, true);
                await ValidateDirectorySessionAsync(state, workspace, sourceChangeset, token).ConfigureAwait(false);
                var current = await RefreshDirectoryPlanAsync(state, workspace, token).ConfigureAwait(false);
                if (current.DirectoryConflicts.Count != 0) throw new ArgumentException("Resolve every directory conflict before applying the merge plan.");
                await RejectIgnoredDirectoryMergePathsAsync(workspace.RootPath, state.Session.Plan, token).ConfigureAwait(false);
                foreach (var conflict in state.Session.Plan.DirectoryConflicts.Where(item => item.Resolution == "rename"))
                    ValidateDirectoryRename(workspace.RootPath, state.Session.Plan, conflict, conflict.Rename);
                var args = DirectoryMergeArguments(state);
                args.Add("--merge"); args.Add("--mergetype=onlyone");
                // From this point native execution may mutate the workspace. Recovery
                // uses explicit workspace undo rather than planning-only cancellation.
                state.Ready = false; state.Session.AwaitingDirectoryResolution = false; SaveMergeState(state);
                var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, args), token).ConfigureAwait(false);
                RequireSuccess(result);
                var remaining = await PreviewMergeAsync(workspace.RootPath, sourceChangeset, token).ConfigureAwait(false);
                if (remaining.DirectoryConflicts.Count != 0) throw new IOException("Plastic still reports directory conflicts after applying the plan. Inspect the workspace before continuing.");
                // Keep initial file conflicts as well as any conflicts revealed by resolving
                // the tree. Native preview after begin determines which remain unresolved.
                foreach (var conflict in remaining.FileConflicts)
                    if (!state.Session.Plan.FileConflicts.Any(item => item.RepositoryPath == conflict.RepositoryPath)) state.Session.Plan.FileConflicts.Add(conflict);
                foreach (var conflict in state.Session.Plan.FileConflicts)
                {
                    conflict.Resolved = !remaining.FileConflicts.Any(item => item.RepositoryPath == conflict.RepositoryPath);
                    if (!conflict.Resolved)
                    {
                        string local = MergeLocalPath(workspace.RootPath, conflict.RepositoryPath);
                        if (!File.Exists(local)) throw new IOException("Native merge left no local conflict file: " + conflict.RepositoryPath);
                        state.Hashes[conflict.RepositoryPath] = MergeHash(local);
                    }
                }
                state.Session.AwaitingDirectoryResolution = false;
                state.Ready = true; SaveMergeState(state);
                RemoveDirectoryOpaqueState(state);
                RemoveDirectoryMergeMarker(workspace.RootPath);
                return state.Session;
            }
        }

        public async Task CancelDirectoryMergeAsync(string root, CancellationToken token)
        {
            var workspace = await MergeWorkspaceAsync(root, token).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadMergeState(workspace.RootPath, true);
                if (!state.Session.AwaitingDirectoryResolution) throw new ArgumentException("Only an unapplied directory merge plan can be cancelled here. Undo applied workspace changes explicitly.");
                if (File.Exists(Path.Combine(workspace.RootPath, ".plastic", "plastic.mergeprogress")))
                    throw new ArgumentException("A native merge is already in progress; inspect or undo the working changes before cancelling its plan.");
                if ((state.Applying || !state.Ready) && DirectoryMergeFingerprint(workspace.RootPath, token) != state.DirectoryFingerprint)
                    throw new ArgumentException("An interrupted native directory planning call may have changed workspace files. Inspect and restore them before cancelling; the checkin guard remains active.");
                // Never alter files, selector, or user edits when abandoning a plan.
                RemoveDirectoryOpaqueState(state);
                File.Delete(MergeIndex(workspace.RootPath));
                RemoveDirectoryMergeMarker(workspace.RootPath);
            }
        }

        private async Task ValidateDirectorySessionAsync(MergeSessionState state, PlasticWorkspace workspace, long sourceChangeset, CancellationToken token)
        {
            if (!state.Session.AwaitingDirectoryResolution || state.Session.IsRollback) throw new ArgumentException("Start a directory merge plan before choosing resolutions.");
            if (state.Session.Plan.SourceChangeset != sourceChangeset) throw new ArgumentException("The source does not match this directory merge session.");
            await ValidateMergeSessionAsync(state, workspace, token).ConfigureAwait(false);
        }

        private async Task ValidateDirectoryPlanningWorkspaceAsync(MergeSessionState state, PlasticWorkspace workspace, CancellationToken token)
        {
            if (File.Exists(Path.Combine(workspace.RootPath, ".plastic", "plastic.mergeprogress"))) throw new ArgumentException("A native merge started outside this directory plan. Inspect or undo it before continuing.");
            if (await LoadedChangesetAsync(workspace.RootPath, false, token).ConfigureAwait(false) != state.Session.Plan.DestinationChangeset)
                throw new ArgumentException("Workspace revision changed since directory planning. Cancel this plan and start a new preview.");
            if ((await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false)).Count != 0 || DirectoryMergeFingerprint(workspace.RootPath, token) != state.DirectoryFingerprint)
                throw new ArgumentException("Workspace files changed since directory planning. Your bytes were preserved; cancel this plan before starting a new merge.");
        }

        private async Task<PlasticMergePlan> RefreshDirectoryPlanAsync(MergeSessionState state, PlasticWorkspace workspace, CancellationToken token)
        {
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, DirectoryMergeArguments(state)), token).ConfigureAwait(false);
            RequireSuccess(result);
            var plan = ParseMergePlan(result.Output, workspace.RootPath, workspace.Repository, state.Session.Plan.SourceChangeset);
            VerifyDirectoryContributors(state, plan); SynchronizeDirectoryPlan(state, plan); SaveMergeState(state); return plan;
        }

        private List<string> DirectoryMergeArguments(MergeSessionState state)
        {
            string directory = MergeSessionDirectory(state.Session.SessionId);
            string result = Path.Combine(directory, "native-directory-result.dat"), solved = Path.Combine(directory, "native-directory-solved.dat");
            RejectReparsePath(result); RejectReparsePath(solved);
            if (state.Ready && (!File.Exists(result) || !File.Exists(solved)))
                throw new IOException("Native directory planning state is missing. Cancel this plan and start a fresh preview; saved choices cannot be reused.");
            // Opaque native files may carry authentication context. Never parse, read,
            // print, hash, export or include them in public session models.
            return new List<string> { "merge", "cs:" + state.Session.Plan.SourceChangeset.ToString(CultureInfo.InvariantCulture), "--printcontributors", "--machinereadable", "--fieldseparator=|",
                "--nointeractiveresolution", "--mergeresultfile=" + result, "--solvedconflictsfile=" + solved };
        }

        private void RemoveDirectoryOpaqueState(MergeSessionState state)
        {
            foreach (string name in new[] { "native-directory-result.dat", "native-directory-solved.dat" })
            { string file = Path.Combine(MergeSessionDirectory(state.Session.SessionId), name); RejectReparsePath(file); if (File.Exists(file)) File.Delete(file); }
        }

        private static void VerifyDirectoryContributors(MergeSessionState state, PlasticMergePlan plan)
        {
            if (plan.SourceChangeset != state.Session.Plan.SourceChangeset || plan.DestinationChangeset != state.Session.Plan.DestinationChangeset || plan.BaseChangeset != state.Session.Plan.BaseChangeset || plan.AlreadyConnected)
                throw new ArgumentException("Native directory plan contributors changed. Cancel the saved plan and start a fresh preview.");
        }

        private static bool SameDirectoryConflict(PlasticDirectoryConflict left, PlasticDirectoryConflict right)
        { return left.ItemId == right.ItemId && left.Kind == right.Kind && left.IsDirectory == right.IsDirectory && left.SourcePath == right.SourcePath && left.DestinationPath == right.DestinationPath && left.SourceOriginalPath == right.SourceOriginalPath && left.DestinationOriginalPath == right.DestinationOriginalPath && left.SourceOperation == right.SourceOperation && left.DestinationOperation == right.DestinationOperation; }

        private static void SynchronizeDirectoryPlan(MergeSessionState state, PlasticMergePlan current)
        {
            foreach (var saved in state.Session.Plan.DirectoryConflicts) saved.Resolved = !current.DirectoryConflicts.Any(item => SameDirectoryConflict(item, saved));
            foreach (var item in current.DirectoryConflicts)
            {
                if (state.Session.Plan.DirectoryConflicts.Any(saved => SameDirectoryConflict(item, saved))) continue;
                // Preserve the current native index in the returned preview; the public
                // session index is stable and may differ after earlier rows disappeared.
                state.Session.Plan.DirectoryConflicts.Add(new PlasticDirectoryConflict {
                    Index = state.Session.Plan.DirectoryConflicts.Count == 0 ? 1 : state.Session.Plan.DirectoryConflicts.Max(saved => saved.Index) + 1,
                    ItemId = item.ItemId, Kind = item.Kind, IsDirectory = item.IsDirectory, Description = item.Description, SourcePath = item.SourcePath, DestinationPath = item.DestinationPath,
                    SourceOriginalPath = item.SourceOriginalPath, DestinationOriginalPath = item.DestinationOriginalPath,
                    SourceOperation = item.SourceOperation, DestinationOperation = item.DestinationOperation });
            }
            state.Session.Plan.Operations = current.Operations;
            foreach (var conflict in current.FileConflicts)
                if (!state.Session.Plan.FileConflicts.Any(item => item.RepositoryPath == conflict.RepositoryPath)) state.Session.Plan.FileConflicts.Add(conflict);
        }

        private static void ValidateDirectoryRename(string root, PlasticMergePlan plan, PlasticDirectoryConflict conflict, string name)
        {
            if (String.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) throw new ArgumentException("Rename requires a single new destination name, not a path.");
            ValidateRepositoryFilePath("/" + name);
            string old = MergeLocalPath(root, conflict.DestinationPath);
            string target = Path.Combine(Path.GetDirectoryName(old), name);
            // Validate Windows device names and ambiguity with existing output rules.
            ValidateHistoricalOutput(target, false);
            string repositoryTarget = conflict.DestinationPath.Substring(0, conflict.DestinationPath.LastIndexOf('/') + 1) + name;
            if (plan.DirectoryConflicts.Any(other => other != conflict && other.Resolution == "rename" && String.Equals(other.DestinationPath.Substring(0, other.DestinationPath.LastIndexOf('/') + 1) + other.Rename, repositoryTarget, StringComparison.OrdinalIgnoreCase)) ||
                plan.Operations.Any(operation => String.Equals(operation.Path, repositoryTarget, StringComparison.OrdinalIgnoreCase) || String.Equals(operation.DestinationPath, repositoryTarget, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The selected new name collides with another planned merge operation.");
        }

        private async Task RejectIgnoredDirectoryMergePathsAsync(string root, PlasticMergePlan plan, CancellationToken token)
        {
            var result = await ExecuteAsync(RevisionCommand(root, new[] { "status", root, "--ignored", "--xml", "--encoding=utf-8", "--fullpaths" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var ignored = ParseStatus(result.Output, root);
            var affected = plan.DirectoryConflicts.SelectMany(item => new[] { item.SourcePath, item.DestinationPath, item.SourceOriginalPath, item.DestinationOriginalPath }).Concat(plan.Operations.SelectMany(item => new[] { item.Path, item.DestinationPath }))
                .Where(path => !String.IsNullOrEmpty(path)).Select(path => MergeLocalPath(root, path)).ToList();
            if (ignored.Any(item => affected.Any(path => IsWithin(item.Path, path) || IsWithin(path, item.Path))))
                throw new ArgumentException("A directory merge affects ignored local files. Move those files aside before starting; their bytes were preserved.");
            foreach (var operation in plan.Operations.Where(item => item.Kind == "ADD"))
            {
                string destination = MergeLocalPath(root, operation.Path);
                bool reviewedReplacement = plan.DirectoryConflicts.Any(conflict => conflict.Resolved && (conflict.Resolution == "rename" || conflict.Resolution == "src") &&
                    !String.IsNullOrEmpty(conflict.DestinationPath) && (SamePath(destination, MergeLocalPath(root, conflict.DestinationPath)) ||
                        (conflict.IsDirectory && IsWithin(destination, MergeLocalPath(root, conflict.DestinationPath)))));
                if ((File.Exists(destination) || Directory.Exists(destination)) && !reviewedReplacement)
                    throw new ArgumentException("An incoming add would overwrite a local path: " + operation.Path);
            }
        }

        private static string DirectoryMergeFingerprint(string root, CancellationToken token)
        {
            RejectUnsafeDescendants(root, root, token);
            var records = new List<string>(); var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                string directory = pending.Pop();
                foreach (string path in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested();
                    if (directory == root && Path.GetFileName(path).Equals(".plastic", StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = path.Substring(root.Length).ToUpperInvariant();
                    if (Directory.Exists(path)) { records.Add("D|" + relative); pending.Push(path); }
                    else records.Add("F|" + relative + "|" + MergeHash(path));
                }
            }
            records.Sort(StringComparer.Ordinal); return MergeKey(String.Join("\n", records));
        }

        private static string DirectoryMergeMarker(string root)
        { return Path.Combine(root, ".plastic", "tortoisescm-directory.session"); }

        private static void RemoveDirectoryMergeMarker(string root)
        { string marker = DirectoryMergeMarker(root); RejectReparsePath(marker); if (File.Exists(marker)) File.Delete(marker); }

        private void ValidateDirectoryMergeOwner(string root)
        {
            string marker = DirectoryMergeMarker(root); RejectReparsePath(marker);
            if (!File.Exists(marker)) return;
            var state = LoadMergeState(root, false);
            if (state == null || !SamePath(File.ReadAllText(marker), Path.Combine(MergeSessionDirectory(state.Session.SessionId), "session.xml")))
                throw new ArgumentException("This workspace has a directory merge session owned by different client settings. Reopen it with the settings that started it before changing or checking in files.");
        }

        private static PlasticDirectoryConflict ParseDirectoryConflict(string[] fields, int index)
        {
            int position = 8;
            var conflict = new PlasticDirectoryConflict { Index = index, Kind = fields[1], Description = fields[3], ItemId = MergeNumber(fields[6]), IsDirectory = Boolean.Parse(fields[7]), Resolved = false };
            conflict.SourceOperation = fields[position++]; conflict.SourcePath = fields[position++]; conflict.SourceOriginalPath = "";
            if (conflict.SourceOperation == "MV") { conflict.SourceOriginalPath = conflict.SourcePath; conflict.SourcePath = fields[position++]; }
            if (position + 2 > fields.Length) throw new InvalidDataException("Incomplete directory conflict contributors.");
            conflict.DestinationOperation = fields[position++]; conflict.DestinationPath = fields[position++]; conflict.DestinationOriginalPath = "";
            if (conflict.DestinationOperation == "MV")
            {
                if (position >= fields.Length) throw new InvalidDataException("Incomplete directory move destination.");
                conflict.DestinationOriginalPath = conflict.DestinationPath; conflict.DestinationPath = fields[position++];
            }
            if (position != fields.Length) throw new InvalidDataException("Unrecognized directory conflict contributor format.");
            foreach (string path in new[] { conflict.SourcePath, conflict.DestinationPath, conflict.SourceOriginalPath, conflict.DestinationOriginalPath }.Where(path => !String.IsNullOrEmpty(path))) ValidateRepositoryFilePath(path);
            return conflict;
        }
    }
}
