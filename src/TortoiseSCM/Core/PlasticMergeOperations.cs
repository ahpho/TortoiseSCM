// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed class PlasticMergeConflict
    {
        public string RepositoryPath { get; set; }
        public long BaseChangeset { get; set; }
        public long SourceChangeset { get; set; }
        public long DestinationChangeset { get; set; }
        public long ItemId { get; set; }
        public bool Resolved { get; set; }
    }

    public sealed class PlasticDirectoryConflict
    {
        public int Index { get; set; }
        public string Kind { get; set; }
        public string Description { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string SourceOperation { get; set; }
        public string DestinationOperation { get; set; }
        public string SourceOriginalPath { get; set; }
        public string DestinationOriginalPath { get; set; }
        public bool IsDirectory { get; set; }
        public long ItemId { get; set; }
        public bool Resolved { get; set; }
        public string Resolution { get; set; }
        public string Rename { get; set; }
        public IList<string> ResolutionOptions { get { return Kind == "EVIL" ? new[] { "src", "dst", "rename" } :
            new[] { "DIV_MV", "CHG_RM", "RM_CHG" }.Contains(Kind) ? new[] { "src", "dst" } : new string[0]; } }
    }

    public sealed class PlasticMergeOperation
    {
        public string Kind { get; set; }
        public string Path { get; set; }
        public string DestinationPath { get; set; }
    }

    public sealed class PlasticMergePlan
    {
        public string WorkspaceRoot { get; set; }
        public string Repository { get; set; }
        public long SourceChangeset { get; set; }
        public long DestinationChangeset { get; set; }
        public long BaseChangeset { get; set; }
        public bool AlreadyConnected { get; set; }
        public IList<PlasticMergeConflict> FileConflicts { get; set; }
        public IList<PlasticDirectoryConflict> DirectoryConflicts { get; set; }
        public IList<PlasticMergeOperation> Operations { get; set; }
        public PlasticMergePlan()
        {
            BaseChangeset = -1;
            FileConflicts = new List<PlasticMergeConflict>();
            DirectoryConflicts = new List<PlasticDirectoryConflict>();
            Operations = new List<PlasticMergeOperation>();
        }
    }

    public sealed class PlasticMergeSession
    {
        public string SessionId { get; set; }
        public PlasticMergePlan Plan { get; set; }
        public bool IsRollback { get; set; }
        public bool AwaitingDirectoryResolution { get; set; }
    }

    public sealed class PlasticMergeConflictFiles
    {
        public string SessionId { get; set; }
        public string RepositoryPath { get; set; }
        public string BasePath { get; set; }
        public string LocalPath { get; set; }
        public string RemotePath { get; set; }
        public string ResultPath { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public async Task<PlasticMergePlan> PreviewMergeAsync(string root, long sourceChangeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(sourceChangeset);
            var workspace = await MergeWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "merge", "cs:" + sourceChangeset.ToString(CultureInfo.InvariantCulture),
                "--printcontributors", "--machinereadable", "--fieldseparator=|", "--nointeractiveresolution" }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            return ParseMergePlan(result.Output, workspace.RootPath, workspace.Repository, sourceChangeset);
        }

        public async Task<PlasticMergeSession> BeginMergeAsync(string root, long sourceChangeset, CancellationToken cancellationToken)
        {
            var workspace = await MergeWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                ValidateDirectoryMergeOwner(workspace.RootPath);
                var previousPlan = LoadMergeState(workspace.RootPath, false);
                if (previousPlan != null && previousPlan.Session.AwaitingDirectoryResolution)
                    throw new ArgumentException("A directory merge plan is already open. Continue or cancel it before starting another merge.");
                var pending = await GetStatusAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false);
                if (pending.Count != 0) throw new ArgumentException("Begin merge requires a clean workspace. Commit or undo existing changes first; no merge was started.");
                if (File.Exists(Path.Combine(workspace.RootPath, ".plastic", "plastic.mergeprogress")))
                    throw new ArgumentException("The workspace already has a native merge in progress. Complete or undo it before starting another merge.");
                RejectUnsafeDescendants(workspace.RootPath, workspace.RootPath, cancellationToken);
                var plan = await PreviewMergeAsync(workspace.RootPath, sourceChangeset, cancellationToken).ConfigureAwait(false);
                if (plan.AlreadyConnected) throw new ArgumentException("The source is already integrated; there is no merge to start.");
                if (plan.DirectoryConflicts.Count != 0)
                    return await BeginDirectoryMergePlanAsync(workspace, plan, cancellationToken).ConfigureAwait(false);
                foreach (var operation in plan.Operations)
                {
                    string path = MergeLocalPath(workspace.RootPath, operation.Path);
                    if (operation.Kind == "ADD" && (File.Exists(path) || Directory.Exists(path)))
                        throw new ArgumentException("An incoming add would overwrite an existing local path (possibly ignored): " + operation.Path);
                    if (!String.IsNullOrEmpty(operation.DestinationPath))
                    {
                        string destination = MergeLocalPath(workspace.RootPath, operation.DestinationPath);
                        if (File.Exists(destination) || Directory.Exists(destination)) throw new ArgumentException("An incoming move has an existing local destination: " + operation.DestinationPath);
                    }
                    if (Directory.Exists(path) && operation.Kind != "ADD")
                        throw new ArgumentException("Automatic directory changes require the official Plastic client so ignored descendants can be reviewed: " + operation.Path);
                }
                var state = new MergeSessionState { Session = new PlasticMergeSession { SessionId = Guid.NewGuid().ToString("N"), Plan = plan }, Selector = workspace.Selector };
                string directory = MergeSessionDirectory(state.Session.SessionId);
                CreatePrivateMergeDirectory(directory);
                // Persist before the native mutation so a partial failure remains inspectable.
                SaveMergeState(state);
                WriteMergeIndex(workspace.RootPath, state.Session.SessionId);
                var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "merge", "cs:" + sourceChangeset.ToString(CultureInfo.InvariantCulture), "--merge",
                    "--mergetype=onlyone", "--nointeractiveresolution", "--machinereadable", "--fieldseparator=|" }), cancellationToken).ConfigureAwait(false);
                RequireSuccess(result);
                var remaining = await PreviewMergeAsync(workspace.RootPath, sourceChangeset, cancellationToken).ConfigureAwait(false);
                foreach (var conflict in plan.FileConflicts)
                {
                    string local = MergeLocalPath(workspace.RootPath, conflict.RepositoryPath);
                    if (!File.Exists(local)) throw new IOException("The native merge did not leave a local conflict file: " + conflict.RepositoryPath);
                    state.Hashes[conflict.RepositoryPath] = MergeHash(local);
                    conflict.Resolved = !remaining.FileConflicts.Any(item => item.RepositoryPath == conflict.RepositoryPath);
                }
                state.Ready = true;
                SaveMergeState(state);
                return state.Session;
            }
        }

        public async Task<PlasticMergeSession> GetMergeSessionAsync(string root, CancellationToken cancellationToken)
        {
            var workspace = await MergeWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                ValidateDirectoryMergeOwner(workspace.RootPath);
                var state = LoadMergeState(workspace.RootPath, false);
                if (state == null) return null;
                if (await RetireCompletedMergeAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false)) return null;
                // A selector/tree change makes an old session inactive. Do not attach saved
                // resolutions to a workspace the user switched or committed in the meantime.
                await ValidateMergeSessionAsync(state, workspace, cancellationToken).ConfigureAwait(false);
                if (state.Session.IsRollback) return state.Session;
                if (state.Session.AwaitingDirectoryResolution)
                {
                    await RefreshDirectoryPlanAsync(state, workspace, cancellationToken).ConfigureAwait(false);
                    return state.Session;
                }
                var current = await PreviewMergeAsync(workspace.RootPath, state.Session.Plan.SourceChangeset, cancellationToken).ConfigureAwait(false);
                foreach (var conflict in state.Session.Plan.FileConflicts)
                    conflict.Resolved = !current.FileConflicts.Any(item => item.RepositoryPath == conflict.RepositoryPath);
                return state.Session;
            }
        }

        public async Task<PlasticMergeConflictFiles> PrepareMergeConflictAsync(string root, long sourceChangeset, string repositoryPath, CancellationToken cancellationToken)
        {
            ValidateRepositoryFilePath(repositoryPath);
            var workspace = await MergeWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadMergeState(workspace.RootPath, true);
                var conflict = await ValidateMergeConflictAsync(state, workspace, sourceChangeset, repositoryPath, cancellationToken).ConfigureAwait(false);
                string directory = Path.Combine(MergeSessionDirectory(state.Session.SessionId), MergeKey(repositoryPath));
                CreatePrivateMergeDirectory(directory);
                string extension = Path.GetExtension(repositoryPath);
                var paths = new PlasticMergeConflictFiles { SessionId = state.Session.SessionId, RepositoryPath = repositoryPath,
                    BasePath = Path.Combine(directory, "base" + extension), LocalPath = Path.Combine(directory, "local" + extension),
                    RemotePath = Path.Combine(directory, "remote" + extension), ResultPath = Path.Combine(directory, "result" + extension) };
                await EnsureMergeInputAsync(workspace, repositoryPath, conflict.ItemId, conflict.BaseChangeset, paths.BasePath, cancellationToken).ConfigureAwait(false);
                await EnsureMergeInputAsync(workspace, repositoryPath, conflict.ItemId, conflict.DestinationChangeset, paths.LocalPath, cancellationToken).ConfigureAwait(false);
                await EnsureMergeInputAsync(workspace, repositoryPath, conflict.ItemId, conflict.SourceChangeset, paths.RemotePath, cancellationToken).ConfigureAwait(false);
                // Preserve a result from an earlier tool run when reopening the dialog.
                return paths;
            }
        }

        public async Task<PlasticCommandResult> ApplyMergeFileResolutionAsync(string root, long sourceChangeset, string repositoryPath, string resultFile, CancellationToken cancellationToken)
        {
            ValidateRepositoryFilePath(repositoryPath);
            string resultPath = ToolInput(resultFile);
            var workspace = await MergeWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            using (var gate = OpenMergeGate(workspace.RootPath))
            {
                var state = LoadMergeState(workspace.RootPath, true);
                var conflict = await ValidateMergeConflictAsync(state, workspace, sourceChangeset, repositoryPath, cancellationToken).ConfigureAwait(false);
                string local = MergeLocalPath(workspace.RootPath, repositoryPath);
                if (SamePath(resultPath, local) || SameExistingFile(resultPath, local))
                    throw new ArgumentException("Keep the resolution in a separate result file; the workspace file must remain unchanged until apply.");
                string directory = MergeSessionDirectory(state.Session.SessionId);
                string backup = Path.Combine(directory, "backup-" + MergeKey(repositoryPath));
                RejectReparsePath(backup);
                string staged = Path.Combine(Path.GetDirectoryName(local), ".tortoisescm-merge-" + Guid.NewGuid().ToString("N") + ".tmp");
                string resultHash = MergeHash(resultPath);
                File.Copy(local, backup, true);
                state.Applying = true;
                SaveMergeState(state);
                bool replaced = false;
                bool nativeSucceeded = false;
                try
                {
                    File.Copy(resultPath, staged, false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (MergeHash(local) != state.Hashes[repositoryPath] || MergeHash(staged) != resultHash)
                        throw new ArgumentException("Conflict or resolution bytes changed during apply. Refresh and inspect the files before retrying.");
                    File.Replace(staged, local, null);
                    replaced = true;
                    var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "merge", "cs:" + sourceChangeset.ToString(CultureInfo.InvariantCulture), repositoryPath,
                        "--merge", "--keepdestination", "--nointeractiveresolution", "--machinereadable", "--fieldseparator=|" }), cancellationToken).ConfigureAwait(false);
                    if (!result.Succeeded)
                    {
                        // Restore only our exact just-written bytes. Never replace an edit
                        // another application made during the failed native operation.
                        if (File.Exists(local) && MergeHash(local) == resultHash) File.Copy(backup, local, true);
                        return result;
                    }
                    nativeSucceeded = true;
                    var pending = await PreviewMergeAsync(workspace.RootPath, sourceChangeset, cancellationToken).ConfigureAwait(false);
                    if (pending.FileConflicts.Any(item => item.RepositoryPath == repositoryPath))
                        throw new IOException("Plastic still reports the file conflict. The result and original backup were preserved in the merge session; no checkin was performed.");
                    if (MergeHash(local) != resultHash) throw new IOException("Plastic changed the selected result bytes. Inspect the workspace and retained backup before continuing.");
                    conflict.Resolved = true;
                    state.Hashes[repositoryPath] = resultHash;
                    state.Applying = false;
                    SaveMergeState(state);
                    result.Output += Environment.NewLine + "Resolution applied with the native merge link. Review all changes and check in separately.";
                    return result;
                }
                catch
                {
                    // Cancellation/launch failures must not strand the replacement bytes
                    // before Plastic accepted the resolution. The original is retained.
                    if (replaced && !nativeSucceeded && File.Exists(local) && MergeHash(local) == resultHash) File.Copy(backup, local, true);
                    throw;
                }
                finally { if (File.Exists(staged)) File.Delete(staged); }
            }
        }

        private async Task<PlasticWorkspace> MergeWorkspaceAsync(string root, CancellationToken cancellationToken)
        {
            var validated = await BuildReadCommandAsync(root, cancellationToken).ConfigureAwait(false);
            if (!SamePath(validated.Arguments[1], validated.WorkingDirectory)) throw new ArgumentException("Merge requires the explicit workspace root.");
            var workspace = await GetWorkspaceAsync(validated.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            if (workspace.IsPartial) throw new ArgumentException("Branch merge requires a Standard workspace. Use the official client for partial workspace incoming conflicts.");
            return workspace;
        }

        private async Task ValidateMergeSessionAsync(MergeSessionState state, PlasticWorkspace workspace, CancellationToken cancellationToken)
        {
            if (!state.Ready) throw new InvalidOperationException("The previous merge start did not finish. Inspect pending changes in the official client before retrying.");
            if (state.Applying) throw new InvalidOperationException("A previous resolution application did not finish reliably. Checkin is blocked: inspect native conflicts and retained result/backup in the official client, then complete or undo the merge there.");
            if (!SamePath(state.Session.Plan.WorkspaceRoot, workspace.RootPath) || state.Session.Plan.Repository != workspace.Repository || NormalizeMergeSelector(state.Selector) != NormalizeMergeSelector(workspace.Selector))
                throw new ArgumentException("Workspace configuration changed since the merge began. The saved session cannot be applied.");
            if (state.Session.IsRollback)
            {
                if (await LoadedChangesetAsync(workspace.RootPath, false, cancellationToken).ConfigureAwait(false) != state.Session.Plan.DestinationChangeset ||
                    RollbackProgressHash(workspace.RootPath) != state.RollbackProgress)
                    throw new ArgumentException("The native rollback state changed. Inspect or undo it before checking in.");
                return;
            }
            if (state.Session.AwaitingDirectoryResolution)
            {
                await ValidateDirectoryPlanningWorkspaceAsync(state, workspace, cancellationToken).ConfigureAwait(false);
                return;
            }
            var current = await PreviewMergeAsync(workspace.RootPath, state.Session.Plan.SourceChangeset, cancellationToken).ConfigureAwait(false);
            if (current.DestinationChangeset != state.Session.Plan.DestinationChangeset)
                throw new ArgumentException("The workspace revision changed since the merge began. Start a fresh merge preview.");
        }

        private async Task<PlasticMergeConflict> ValidateMergeConflictAsync(MergeSessionState state, PlasticWorkspace workspace, long sourceChangeset, string repositoryPath, CancellationToken cancellationToken)
        {
            await ValidateMergeSessionAsync(state, workspace, cancellationToken).ConfigureAwait(false);
            if (state.Session.Plan.SourceChangeset != sourceChangeset) throw new ArgumentException("The source changeset does not match the active merge session.");
            var conflict = state.Session.Plan.FileConflicts.SingleOrDefault(item => item.RepositoryPath == repositoryPath);
            if (conflict == null || conflict.Resolved) throw new ArgumentException("Select an unresolved file from the active merge session.");
            var current = await PreviewMergeAsync(workspace.RootPath, sourceChangeset, cancellationToken).ConfigureAwait(false);
            var remaining = current.FileConflicts.SingleOrDefault(item => item.RepositoryPath == repositoryPath);
            if (remaining == null || remaining.ItemId != conflict.ItemId || remaining.BaseChangeset != conflict.BaseChangeset || remaining.SourceChangeset != conflict.SourceChangeset || remaining.DestinationChangeset != conflict.DestinationChangeset)
                throw new ArgumentException("The native conflict changed or was resolved outside this session. Refresh before continuing.");
            string local = MergeLocalPath(workspace.RootPath, repositoryPath);
            string hash;
            if (!state.Hashes.TryGetValue(repositoryPath, out hash) || !File.Exists(local) || MergeHash(local) != hash)
                throw new ArgumentException("The workspace conflict file was edited after merge start. Its bytes were preserved; save your resolution separately and inspect the merge in the official client.");
            return conflict;
        }

        private async Task EnsureMergeInputAsync(PlasticWorkspace workspace, string path, long itemId, long changeset, string destination, CancellationToken cancellationToken)
        {
            var listing = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", path, "--tree=cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository, "--xml", "--encoding=utf-8" }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(listing);
            var identities = SafeXml.Load(listing.Output).Descendants("LsItem").Where(item => (string)item.Element("CurrentPath") == path).ToList();
            if (identities.Count != 1 || MergeNumber((string)identities[0].Element("ItemId")) != itemId)
                throw new ArgumentException("The historical contributor path does not identify this conflict item. Resolve renamed/path-reused contributors in the official Plastic client.");
            RejectReparsePath(destination);
            string checksum = destination + ".sha256";
            RejectReparsePath(checksum);
            if (File.Exists(destination))
            {
                if (!File.Exists(checksum) || File.ReadAllText(checksum) != MergeHash(destination))
                    throw new IOException("A saved merge input is incomplete or modified. Inspect the session before continuing: " + destination);
                return;
            }
            string staged = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await DownloadHistoricalFileAsync(workspace, path, changeset, staged, cancellationToken).ConfigureAwait(false);
                File.WriteAllText(checksum, MergeHash(staged));
                File.Move(staged, destination);
                File.SetAttributes(destination, File.GetAttributes(destination) | FileAttributes.ReadOnly);
            }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }

        internal static PlasticMergePlan ParseMergePlan(string output, string root, string repository, long sourceChangeset)
        {
            var plan = new PlasticMergePlan { WorkspaceRoot = root, Repository = repository, SourceChangeset = sourceChangeset, DestinationChangeset = -1 };
            bool sourceFound = false;
            foreach (string line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] fields = line.Split('|');
                if (fields[0] == "CONTRIBUTOR" && fields.Length == 5)
                {
                    long changeset = MergeNumber(fields[2]);
                    if (fields[3] != "cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + repository)
                        throw new ArgumentException("Cross-repository or ambiguous merge contributors are not supported.");
                    if (fields[1] == "SRC") { if (changeset != sourceChangeset || sourceFound) throw new InvalidDataException("Unexpected merge source."); sourceFound = true; }
                    else if (fields[1] == "DST") plan.DestinationChangeset = changeset;
                    else if (fields[1] == "BASE") plan.BaseChangeset = changeset;
                    else throw new InvalidDataException("Unknown merge contributor.");
                }
                else if (fields[0] == "FILE_CONFLICT" && fields.Length == 6)
                {
                    ValidateRepositoryFilePath(fields[1]);
                    plan.FileConflicts.Add(new PlasticMergeConflict { RepositoryPath = fields[1], BaseChangeset = MergeNumber(fields[2]),
                        SourceChangeset = MergeNumber(fields[3]), DestinationChangeset = MergeNumber(fields[4]), ItemId = MergeNumber(fields[5]) });
                }
                else if (fields[0] == "DIR_CONFLICT" && fields.Length >= 12 && fields.Length <= 14)
                    plan.DirectoryConflicts.Add(ParseDirectoryConflict(fields, plan.DirectoryConflicts.Count + 1));
                else if (fields[0] == "APPLY" && (fields.Length == 3 || fields.Length == 4))
                {
                    ValidateRepositoryFilePath(fields[2]); if (fields.Length == 4) ValidateRepositoryFilePath(fields[3]);
                    plan.Operations.Add(new PlasticMergeOperation { Kind = fields[1], Path = fields[2], DestinationPath = fields.Length == 4 ? fields[3] : "" });
                }
                else if (fields[0] == "STATUS" && fields.Length >= 2 && fields[1] == "ALREADY_CONNECTED") plan.AlreadyConnected = true;
                else throw new InvalidDataException("Unsupported merge preview record. Use the official client for this merge: " + fields[0]);
            }
            if (!sourceFound || plan.DestinationChangeset < 0 || (!plan.AlreadyConnected && plan.BaseChangeset < 0)) throw new InvalidDataException("Merge preview has incomplete contributors.");
            if (plan.FileConflicts.Select(item => item.RepositoryPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != plan.FileConflicts.Count)
                throw new InvalidDataException("Merge preview contains duplicate conflict paths.");
            return plan;
        }

        private sealed class MergeSessionState
        {
            internal PlasticMergeSession Session;
            internal string Selector;
            internal bool Ready;
            internal bool Applying;
            internal string RollbackProgress;
            internal string DirectoryFingerprint;
            internal readonly Dictionary<string, string> Hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private string MergeStorageRoot()
        { return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(config.SettingsPath)), "merge-sessions"); }
        private string MergeSessionDirectory(string id)
        {
            Guid value;
            if (!Guid.TryParseExact(id, "N", out value)) throw new InvalidDataException("Invalid merge session identifier.");
            return Path.Combine(MergeStorageRoot(), id);
        }
        private static void CreatePrivateMergeDirectory(string directory)
        {
            RejectReparsePath(directory);
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(directory, security);
            Directory.SetAccessControl(directory, security);
        }
        private FileStream OpenMergeGate(string root)
        {
            CreatePrivateMergeDirectory(MergeStorageRoot());
            try { return new FileStream(Path.Combine(MergeStorageRoot(), MergeKey(root.ToUpperInvariant()) + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new InvalidOperationException("Another TortoiseSCM merge operation is using this workspace.", error); }
        }
        private string MergeIndex(string root)
        { return Path.Combine(MergeStorageRoot(), MergeKey(root.ToUpperInvariant()) + ".session"); }
        private void WriteMergeIndex(string root, string id)
        { RejectReparsePath(MergeIndex(root)); File.WriteAllText(MergeIndex(root), id, new UTF8Encoding(false)); }
        private MergeSessionState LoadMergeState(string root, bool required)
        {
            string index = MergeIndex(root);
            RejectReparsePath(index);
            if (!File.Exists(index)) { if (required) throw new ArgumentException("Start a TortoiseSCM merge before resolving conflicts."); return null; }
            string id = File.ReadAllText(index).Trim();
            string file = Path.Combine(MergeSessionDirectory(id), "session.xml");
            RejectReparsePath(file);
            var document = SafeXml.Load(File.ReadAllText(file));
            var element = document.Root;
            if (element == null || element.Name != "MergeSession") throw new InvalidDataException("Invalid merge session.");
            var plan = new PlasticMergePlan { WorkspaceRoot = (string)element.Element("Root"), Repository = (string)element.Element("Repository"),
                SourceChangeset = MergeNumber((string)element.Element("Source")), DestinationChangeset = MergeNumber((string)element.Element("Destination")),
                BaseChangeset = MergeNumber((string)element.Element("Base")) };
            if (!SamePath(plan.WorkspaceRoot, root)) throw new InvalidDataException("Merge session belongs to another workspace.");
            var state = new MergeSessionState { Session = new PlasticMergeSession { SessionId = id, Plan = plan, IsRollback = (bool?)element.Element("IsRollback") ?? false }, Selector = (string)element.Element("Selector"), Ready = (bool)element.Element("Ready"), Applying = (bool?)element.Element("Applying") ?? false, RollbackProgress = (string)element.Element("RollbackProgress") ?? "" };
            state.Session.AwaitingDirectoryResolution = (bool?)element.Element("AwaitingDirectoryResolution") ?? false;
            state.DirectoryFingerprint = (string)element.Element("DirectoryFingerprint") ?? "";
            foreach (var conflict in element.Elements("DirectoryConflict"))
                plan.DirectoryConflicts.Add(new PlasticDirectoryConflict { Index = (int)conflict.Attribute("index"), Kind = (string)conflict.Attribute("kind"),
                    ItemId = MergeNumber((string)conflict.Attribute("item")), Description = (string)conflict.Element("Description"),
                    SourcePath = (string)conflict.Element("SourcePath"), DestinationPath = (string)conflict.Element("DestinationPath"),
                    SourceOperation = (string)conflict.Element("SourceOperation"), DestinationOperation = (string)conflict.Element("DestinationOperation"),
                    SourceOriginalPath = (string)conflict.Element("SourceOriginalPath") ?? "", DestinationOriginalPath = (string)conflict.Element("DestinationOriginalPath") ?? "",
                    Resolved = (bool)conflict.Attribute("resolved"), IsDirectory = (bool?)conflict.Attribute("isDirectory") ?? false, Resolution = (string)conflict.Element("Resolution"), Rename = (string)conflict.Element("Rename") });
            foreach (var conflict in element.Elements("Conflict"))
            {
                string path = (string)conflict.Attribute("path"); ValidateRepositoryFilePath(path);
                plan.FileConflicts.Add(new PlasticMergeConflict { RepositoryPath = path, BaseChangeset = MergeNumber((string)conflict.Attribute("base")),
                    SourceChangeset = MergeNumber((string)conflict.Attribute("source")), DestinationChangeset = MergeNumber((string)conflict.Attribute("destination")),
                    ItemId = MergeNumber((string)conflict.Attribute("item")), Resolved = (bool)conflict.Attribute("resolved") });
                if (conflict.Attribute("hash") != null) state.Hashes[path] = (string)conflict.Attribute("hash");
            }
            foreach (var operation in element.Elements("Operation"))
                plan.Operations.Add(new PlasticMergeOperation { Kind = (string)operation.Attribute("kind"), Path = (string)operation.Attribute("path"), DestinationPath = (string)operation.Attribute("destination") });
            return state;
        }
        private void SaveMergeState(MergeSessionState state)
        {
            var plan = state.Session.Plan;
            var element = new XElement("MergeSession", new XElement("Root", plan.WorkspaceRoot), new XElement("Repository", plan.Repository),
                new XElement("Source", plan.SourceChangeset), new XElement("Destination", plan.DestinationChangeset), new XElement("Base", plan.BaseChangeset),
                new XElement("Selector", state.Selector), new XElement("Ready", state.Ready), new XElement("Applying", state.Applying));
            element.Add(new XElement("IsRollback", state.Session.IsRollback), new XElement("RollbackProgress", state.RollbackProgress ?? ""));
            element.Add(new XElement("AwaitingDirectoryResolution", state.Session.AwaitingDirectoryResolution), new XElement("DirectoryFingerprint", state.DirectoryFingerprint ?? ""));
            foreach (var conflict in plan.DirectoryConflicts)
                element.Add(new XElement("DirectoryConflict", new XAttribute("index", conflict.Index), new XAttribute("kind", conflict.Kind), new XAttribute("item", conflict.ItemId), new XAttribute("resolved", conflict.Resolved), new XAttribute("isDirectory", conflict.IsDirectory),
                    new XElement("Description", conflict.Description), new XElement("SourcePath", conflict.SourcePath), new XElement("DestinationPath", conflict.DestinationPath),
                    new XElement("SourceOperation", conflict.SourceOperation), new XElement("DestinationOperation", conflict.DestinationOperation),
                    new XElement("SourceOriginalPath", conflict.SourceOriginalPath ?? ""), new XElement("DestinationOriginalPath", conflict.DestinationOriginalPath ?? ""),
                    new XElement("Resolution", conflict.Resolution ?? ""), new XElement("Rename", conflict.Rename ?? "")));
            foreach (var conflict in plan.FileConflicts)
            {
                string hash; state.Hashes.TryGetValue(conflict.RepositoryPath, out hash);
                element.Add(new XElement("Conflict", new XAttribute("path", conflict.RepositoryPath), new XAttribute("base", conflict.BaseChangeset),
                    new XAttribute("source", conflict.SourceChangeset), new XAttribute("destination", conflict.DestinationChangeset), new XAttribute("item", conflict.ItemId),
                    new XAttribute("resolved", conflict.Resolved), hash == null ? null : new XAttribute("hash", hash)));
            }
            foreach (var operation in plan.Operations) element.Add(new XElement("Operation", new XAttribute("kind", operation.Kind), new XAttribute("path", operation.Path), new XAttribute("destination", operation.DestinationPath ?? "")));
            string target = Path.Combine(MergeSessionDirectory(state.Session.SessionId), "session.xml");
            string staged = target + ".new";
            RejectReparsePath(target); RejectReparsePath(staged);
            new XDocument(element).Save(staged);
            if (File.Exists(target)) File.Replace(staged, target, null); else File.Move(staged, target);
        }
        private static long MergeNumber(string text)
        { long value; if (!Int64.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value < 0) throw new InvalidDataException("Invalid merge changeset or item identifier."); return value; }
        private static string NormalizeMergeSelector(string text)
        { return (text ?? "").Replace("\r\n", "\n").Replace("\r", "\n"); }
        private static string MergeLocalPath(string root, string repositoryPath)
        {
            ValidateRepositoryFilePath(repositoryPath);
            string path = Path.GetFullPath(Path.Combine(root, repositoryPath.Substring(1).Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(path, root)) throw new ArgumentException("Merge path escapes its workspace.");
            RejectReparsePath(path);
            return path;
        }
        private static string MergeKey(string value)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", "").ToLowerInvariant(); }
        private static string MergeHash(string path)
        { RejectReparsePath(path); using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", ""); }

        public bool HasSavedMergeSession(string root) { return File.Exists(MergeIndex(Path.GetFullPath(root))); }
        private static string RollbackProgressHash(string root)
        { string path = Path.Combine(root, ".plastic", "plastic.mergeprogress"); return File.Exists(path) ? MergeHash(path) : ""; }

        private MergeSessionState TrackWorkspaceRollback(PlasticWorkspace workspace, long loaded, long target)
        {
            var state = new MergeSessionState { Selector = workspace.Selector,
                Session = new PlasticMergeSession { SessionId = Guid.NewGuid().ToString("N"), IsRollback = true,
                    Plan = new PlasticMergePlan { WorkspaceRoot = workspace.RootPath, Repository = workspace.Repository,
                        SourceChangeset = loaded, DestinationChangeset = loaded, BaseChangeset = target } } };
            CreatePrivateMergeDirectory(MergeSessionDirectory(state.Session.SessionId)); SaveMergeState(state);
            WriteMergeIndex(workspace.RootPath, state.Session.SessionId); return state;
        }

        private async Task<bool> RetireCompletedMergeAsync(string root, CancellationToken cancellationToken)
        {
            ValidateDirectoryMergeOwner(root);
            var planned = LoadMergeState(root, false);
            if (planned != null && planned.Session.AwaitingDirectoryResolution) return false;
            if (File.Exists(Path.Combine(root, ".plastic", "plastic.mergeprogress"))) return false;
            if ((await GetStatusAsync(root, cancellationToken).ConfigureAwait(false)).Count != 0) return false;
            string index = MergeIndex(root); RejectReparsePath(index);
            if (File.Exists(index)) File.Delete(index);
            RemoveDirectoryMergeMarker(root);
            // Keep reviewed inputs, result and original backup for recovery; only detach.
            return true;
        }

        private async Task<PlasticCommandResult> ExecuteWithMergeGuardAsync(PlasticProcessCommand command, PlasticCommandRequest request, CancellationToken cancellationToken)
        {
            if (request.Command != PlasticCommand.Checkin && request.Command != PlasticCommand.Undo)
                return await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            ValidateDirectoryMergeOwner(command.WorkingDirectory);
            if (!File.Exists(MergeIndex(command.WorkingDirectory)))
            {
                if (request.Command == PlasticCommand.Checkin && File.Exists(Path.Combine(command.WorkingDirectory, ".plastic", "plastic.mergeprogress")))
                    throw new ArgumentException("This native merge has no session for the current client settings. Complete it in the client that started it; no checkin was performed.");
                return await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
            }
            using (var gate = OpenMergeGate(command.WorkingDirectory))
            {
                if (request.Command == PlasticCommand.Undo && LoadMergeState(command.WorkingDirectory, true).Session.AwaitingDirectoryResolution)
                    throw new ArgumentException("This directory merge is still an unapplied plan. Cancel the plan from the merge dialog instead of undoing workspace files.");
                if (request.Command == PlasticCommand.Checkin && !await RetireCompletedMergeAsync(command.WorkingDirectory, cancellationToken).ConfigureAwait(false))
                {
                    var workspace = await MergeWorkspaceAsync(command.WorkingDirectory, cancellationToken).ConfigureAwait(false);
                    var state = LoadMergeState(workspace.RootPath, true);
                    await ValidateMergeSessionAsync(state, workspace, cancellationToken).ConfigureAwait(false);
                    if (state.Session.AwaitingDirectoryResolution) throw new ArgumentException("Apply or cancel the directory merge plan before checking in. No checkin was performed.");
                    if (!state.Session.IsRollback)
                    {
                        var plan = await PreviewMergeAsync(workspace.RootPath, state.Session.Plan.SourceChangeset, cancellationToken).ConfigureAwait(false);
                        if (plan.FileConflicts.Count != 0 || plan.DirectoryConflicts.Count != 0)
                            throw new ArgumentException("Resolve all native merge conflicts before checking in. No checkin was performed.");
                    }
                    if (request.Paths.Count != 1 || !SamePath(Path.GetFullPath(Path.IsPathRooted(request.Paths[0]) ? request.Paths[0] : Path.Combine(request.WorkingDirectory, request.Paths[0])), workspace.RootPath))
                        throw new ArgumentException("Check in the explicit workspace root to include the complete native merge.");
                }
                var result = await ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
                if (result.Succeeded) await RetireCompletedMergeAsync(command.WorkingDirectory, cancellationToken).ConfigureAwait(false);
                return result;
            }
        }
    }
}
