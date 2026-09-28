// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    /// <summary>Read-only review snapshot used by the GUI before a checkin.</summary>
    public sealed class PlasticCheckinPreview
    {
        private readonly string[] paths;
        private readonly PlasticStatusItem[] files;
        private readonly PlasticLockItem[] locks;
        internal readonly string Fingerprint;
        internal readonly string ExpectedName;
        internal PlasticCheckinPreview(string root, string repository, string selector, string name, bool partial,
            IList<string> paths, IList<PlasticStatusItem> files, IList<PlasticLockItem> locks, string lockWarning, string fingerprint, int excludedPrivateCount)
        {
            RootPath = root; Repository = repository; Selector = selector; ExpectedName = name; IsPartial = partial;
            this.paths = paths.ToArray(); this.files = files.Select(Clone).ToArray(); this.locks = locks.Select(Clone).ToArray();
            LockWarning = lockWarning ?? ""; Fingerprint = fingerprint; ExcludedPrivateCount = excludedPrivateCount;
        }
        public string RootPath { get; private set; }
        public string Repository { get; private set; }
        public string Selector { get; private set; }
        public bool IsPartial { get; private set; }
        public IList<string> Paths { get { return new ReadOnlyCollection<string>(paths.ToArray()); } }
        public IList<PlasticStatusItem> Files { get { return new ReadOnlyCollection<PlasticStatusItem>(files.Select(Clone).ToArray()); } }
        public IList<PlasticLockItem> Locks { get { return new ReadOnlyCollection<PlasticLockItem>(locks.Select(Clone).ToArray()); } }
        public string LockWarning { get; private set; }
        public int ExcludedPrivateCount { get; private set; }
        internal static PlasticStatusItem Clone(PlasticStatusItem item)
        { return new PlasticStatusItem { Path = item.Path, OldPath = item.OldPath, Status = item.Status, StatusCode = item.StatusCode, StatusDescription = item.StatusDescription, IsDirectory = item.IsDirectory }; }
        internal static PlasticLockItem Clone(PlasticLockItem item)
        { return new PlasticLockItem { LockId = item.LockId, Repository = item.Repository, ItemId = item.ItemId, Date = item.Date, DestinationBranch = item.DestinationBranch, DestinationRevision = item.DestinationRevision, HolderBranch = item.HolderBranch, HolderRevision = item.HolderRevision, Status = item.Status, Owner = item.Owner, Workspace = item.Workspace, Path = item.Path, CanUnlock = item.CanUnlock }; }
    }

    public sealed partial class PlasticClient
    {
        public async Task<PlasticCheckinPreview> PrepareCheckinAsync(string root, IList<string> paths,
            string expectedRepository, string expectedSelector, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (expectedSelector == null) throw new ArgumentNullException("expectedSelector");
            ValidateBranchRepository(expectedRepository);
            var expected = new PlasticWorkspace { RootPath = Path.GetFullPath(root), Repository = expectedRepository, Selector = expectedSelector };
            var discovered = DiscoverWorkspace(expected.RootPath);
            if (discovered == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            expected.Name = discovered.Name;
            ValidateCheckinContext(expected);
            var workspace = await GetWorkspaceAsync(expected.RootPath, cancellationToken).ConfigureAwait(false);
            ValidateCheckinContext(expected);
            var normalized = NormalizeCheckinPaths(workspace, paths);
            await Task.Run(() => Build(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = workspace.RootPath,
                Paths = normalized, Comment = "preflight", Recursive = true }, cancellationToken), cancellationToken).ConfigureAwait(false);
            var status = await GetStatusAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false);
            var selected = SelectCheckinStatus(status, workspace.RootPath, normalized);
            string fingerprint = await Task.Run(() => CheckinFingerprint(workspace.RootPath, selected), cancellationToken).ConfigureAwait(false);
            IList<PlasticLockItem> locks = new List<PlasticLockItem>(); string lockWarning = "";
            try { locks = (await GetLocksAsync(workspace.RootPath, cancellationToken).ConfigureAwait(false)).Where(item =>
                (item.Repository == workspace.Repository || item.Repository == workspace.Repository.Split('@')[0]) &&
                selected.Any(file => IsWithinScope(file.Path, MergeLocalPath(workspace.RootPath, item.Path)) || IsWithinScope(MergeLocalPath(workspace.RootPath, item.Path), file.Path)))
                .Select(PlasticCheckinPreview.Clone).ToList(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { lockWarning = "无法读取锁信息；签入时仍由 Plastic 服务器执行权限和锁校验：" + error.Message; }
            ValidateCheckinContext(expected);
            return new PlasticCheckinPreview(workspace.RootPath, workspace.Repository, workspace.Selector, workspace.Name, workspace.IsPartial,
                normalized, selected, locks, lockWarning, fingerprint, status.Count(item => IsPrivateCheckinStatus(item.StatusCode) && normalized.Any(path => IsWithinScope(item.Path, path))));
        }

        public async Task<PlasticCommandResult> CheckinPreparedAsync(PlasticCheckinPreview preview, string comment, CancellationToken cancellationToken)
        {
            if (preview == null) throw new ArgumentNullException("preview");
            if (String.IsNullOrWhiteSpace(comment)) throw new ArgumentException("A checkin comment is required.", "comment");
            cancellationToken.ThrowIfCancellationRequested();
            var workspace = new PlasticWorkspace { RootPath = preview.RootPath, Repository = preview.Repository, Selector = preview.Selector, Name = preview.ExpectedName, IsPartial = preview.IsPartial };
            ValidateCheckinContext(workspace);
            var request = new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = preview.RootPath, Paths = preview.Paths.ToList(), Comment = comment, Recursive = true };
            var command = Build(request, cancellationToken);
            var actual = await GetWorkspaceAsync(command.WorkingDirectory, cancellationToken).ConfigureAwait(false);
            if (actual.IsPartial != preview.IsPartial) throw new InvalidOperationException("工作区模式已改变，请重新预检签入范围。");
            ApplyWorkspaceMode(command, actual.IsPartial);
            return await ExecuteWithPartialConflictGuardAsync(command, request, cancellationToken,
                () => ValidatePreparedCheckinAsync(preview, cancellationToken)).ConfigureAwait(false);
        }

        private async Task ValidatePreparedCheckinAsync(PlasticCheckinPreview preview, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var expected = new PlasticWorkspace { RootPath = preview.RootPath, Repository = preview.Repository, Selector = preview.Selector, Name = preview.ExpectedName, IsPartial = preview.IsPartial };
            ValidateCheckinContext(expected);
            var actual = await GetWorkspaceAsync(preview.RootPath, token).ConfigureAwait(false);
            if (actual.IsPartial != preview.IsPartial) throw new InvalidOperationException("工作区模式已改变，请重新预检签入范围。");
            var status = await GetStatusAsync(preview.RootPath, token).ConfigureAwait(false);
            var selected = SelectCheckinStatus(status, preview.RootPath, preview.Paths);
            if (await Task.Run(() => CheckinFingerprint(preview.RootPath, selected), token).ConfigureAwait(false) != preview.Fingerprint)
                throw new InvalidOperationException("待签入文件的状态、路径或内容已改变，请重新预检签入范围。");
            ValidateCheckinContext(expected);
        }

        private IList<string> NormalizeCheckinPaths(PlasticWorkspace workspace, IList<string> paths)
        {
            if (paths == null || paths.Count == 0) throw new ArgumentException("Select at least one path for checkin.", "paths");
            var result = new List<string>();
            foreach (string value in paths)
            {
                if (String.IsNullOrWhiteSpace(value) || value.IndexOfAny(new[] { '\0', '\r', '\n', '*', '?' }) >= 0) throw new ArgumentException("Checkin paths must be explicit.", "paths");
                string absolute = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(workspace.RootPath, value));
                if (!IsWithin(absolute, workspace.RootPath)) throw new ArgumentException("All checkin paths must belong to the workspace.");
                RejectReparsePath(absolute);
                if (absolute.Split('\\', '/').Any(part => part.Equals(".plastic", StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Workspace metadata cannot be checked in.");
                var nested = DiscoverWorkspace(absolute);
                if (nested != null && !SamePath(nested.RootPath, workspace.RootPath)) throw new ArgumentException("Nested workspaces cannot be checked in.");
                if (!result.Contains(absolute, StringComparer.OrdinalIgnoreCase)) result.Add(absolute);
            }
            return result;
        }

        private static IList<PlasticStatusItem> SelectCheckinStatus(IList<PlasticStatusItem> status, string root, IList<string> paths)
        {
            var selected = status.Where(item => paths.Any(path => IsWithinScope(item.Path, path) || IsWithinScope(item.OldPath, path))).Select(PlasticCheckinPreview.Clone).ToList();
            foreach (string path in paths)
            {
                bool directoryScope = Directory.Exists(path) || selected.Any(item => SamePath(item.Path, path) && item.IsDirectory);
                var rows = directoryScope ? selected.Where(item => IsWithinScope(item.Path, path) || IsWithinScope(item.OldPath, path)).ToList() : selected.Where(item => SamePath(item.Path, path)).ToList();
                if (rows.Count == 0) throw new ArgumentException("Every selected checkin path must have a pending change: " + path);
                if (rows.Any(item => SamePath(item.Path, path) && IsPrivateCheckinStatus(item.StatusCode)))
                    throw new ArgumentException("Private or ignored files must be added explicitly before checkin: " + path);
            }
            selected.RemoveAll(item => IsPrivateCheckinStatus(item.StatusCode));
            if (selected.Count == 0) throw new ArgumentException("The selected scope has no controlled pending changes.");
            foreach (var directory in status.Where(item => item.IsDirectory && IsDirectoryPending(item.StatusCode)))
            {
                if (selected.Any(item => SamePath(item.Path, directory.Path))) continue;
                if (selected.Any(item => IsWithinScope(item.Path, directory.Path) || IsWithinScope(item.OldPath, directory.Path)))
                    throw new ArgumentException("A selected file depends on an unselected pending directory change; select the directory or review it separately: " + directory.Path);
            }
            return selected.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.StatusCode, StringComparer.Ordinal).ToList();
        }

        private static bool IsDirectoryPending(string code)
        { return code != "CH" && code != "CO" && !IsPrivateCheckinStatus(code); }

        private static bool IsPrivateCheckinStatus(string code)
        { return code == "PR" || code == "IG" || code == "P" || code == "I"; }

        private static string CheckinFingerprint(string root, IList<PlasticStatusItem> files)
        {
            var lines = files.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.StatusCode, StringComparer.Ordinal).Select(item =>
                String.Join("|", new[] { item.StatusCode ?? "", item.Status ?? "", item.Path ?? "", item.OldPath ?? "", item.IsDirectory.ToString(), LocalFingerprint(item.Path), String.IsNullOrEmpty(item.OldPath) ? "" : LocalFingerprint(item.OldPath) }));
            using (var hash = SHA256.Create()) return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(String.Join("\n", lines))));
        }

        private static string LocalFingerprint(string path)
        {
            RejectReparsePath(path);
            if (Directory.Exists(path)) return "directory";
            if (!File.Exists(path)) return "missing";
            using (var stream = File.OpenRead(path)) using (var hash = SHA256.Create()) return "file:" + Convert.ToBase64String(hash.ComputeHash(stream));
        }

        private void ValidateCheckinContext(PlasticWorkspace expected)
        {
            var current = DiscoverWorkspace(expected.RootPath);
            if (current == null || !SamePath(current.RootPath, expected.RootPath) || current.Repository != expected.Repository || current.Name != expected.Name || current.Selector != expected.Selector)
                throw new InvalidOperationException("工作区仓库、名称或 selector 已改变，请重新预检签入范围。");
        }
    }
}
