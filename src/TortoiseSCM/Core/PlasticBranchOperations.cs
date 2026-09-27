// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed class PlasticBranch
    {
        public string Name { get; set; }
        public string Parent { get; set; }
        public string Owner { get; set; }
        public string CreationDate { get; set; }
        public string Comment { get; set; }
        public string Repository { get; set; }
        public long HeadChangeset { get; set; }
        public bool IsCurrent { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public async Task<IList<PlasticBranch>> GetBranchesAsync(string path, CancellationToken token)
        {
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            var workspace = DiscoverWorkspace(command.WorkingDirectory);
            ValidateBranchRepository(workspace.Repository);
            var header = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "status", workspace.RootPath, "--header", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(header);
            var status = SafeXml.Load(header.Output);
            ValidateBranchStatusRepository(status, workspace.Repository);
            string current = (string)status.Root.Element("WkConfigName") ?? "";
            // The selector distinguishes a branch from a label with the same textual name.
            var configured = Regex.Matches(workspace.Selector, @"(?m)^\s*(?:smartbranch|branch|br)\s+""([^""]+)""\s*$");
            string configuredBranch = configured.Count == 1 ? configured[0].Groups[1].Value : "";
            var response = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "find", "branch", "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(response);
            var branches = ParseBranches(response.Output, workspace.Repository);
            foreach (var branch in branches) branch.IsCurrent = branch.Name == configuredBranch && current == branch.Name + "@" + workspace.Repository;
            ValidateBranchWorkspaceUnchanged(workspace);
            return branches;
        }

        public async Task<long> ResolveBranchHeadAsync(string path, string branch, CancellationToken token)
        {
            ValidateBranchName(branch);
            // Native branch XML includes its head, including inherited heads on empty branches.
            // Exact client-side matching avoids interpreting names as a find expression.
            var branches = await GetBranchesAsync(path, token).ConfigureAwait(false);
            var selected = branches.SingleOrDefault(item => item.Name == branch);
            if (selected == null) throw new ArgumentException("The branch no longer exists in this repository. Refresh the branch list.");
            return selected.HeadChangeset;
        }

        public async Task<PlasticCommandResult> SwitchBranchAsync(string root, string branch, CancellationToken token)
        {
            ValidateBranchName(branch);
            var command = await BuildReadCommandAsync(root, token).ConfigureAwait(false);
            if (!SamePath(command.Arguments[1], command.WorkingDirectory)) throw new ArgumentException("Switch branch requires the explicit workspace root.");
            root = command.WorkingDirectory;
            using (var structureGate = StructureGate(root)) using (var mergeGate = OpenMergeGate(root))
            {
                var workspace = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
                ValidateBranchRepository(workspace.Repository);
                if (workspace.IsPartial) throw new ArgumentException("Switch branch currently requires a Standard workspace. Partial workspaces can browse branches; their loaded configuration is preserved.");
                if (Regex.Matches(workspace.Selector, @"(?m)^\s*repository\s+").Count != 1 ||
                    Regex.Matches(workspace.Selector, @"(?m)^\s*path\s+").Count != 1 ||
                    !Regex.IsMatch(workspace.Selector, @"(?m)^\s*path\s+""/""\s*$"))
                    throw new ArgumentException("Switch branch requires a single repository mapped at the workspace root.");
                await ValidateBranchSwitchCleanAsync(root, token).ConfigureAwait(false);
                await ResolveBranchHeadAsync(root, branch, token).ConfigureAwait(false);
                // Recheck after the server lookup, while holding both local mutation gates.
                await ValidateBranchSwitchCleanAsync(root, token).ConfigureAwait(false);
                ValidateBranchWorkspaceUnchanged(workspace);
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await ExecuteAsync(RevisionCommand(root, new[] { "switch", "br:" + branch + "@" + workspace.Repository, "--workspace=" + root }), token).ConfigureAwait(false);
                    if (!result.Succeeded) result.Error += Environment.NewLine + "Branch switch did not complete. Refresh workspace status before continuing; no automatic undo was performed.";
                    else
                    {
                        try
                        {
                            var updated = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
                            if (updated.IsPartial || updated.Repository != workspace.Repository ||
                                !(await GetBranchesAsync(root, token).ConfigureAwait(false)).Any(item => item.Name == branch && item.IsCurrent))
                                throw new InvalidOperationException("The requested branch is not the current Standard workspace selector.");
                            await ValidateBranchSwitchCleanAsync(root, token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error)
                        {
                            result.ExitCode = 1;
                            result.Error += Environment.NewLine + "Native switch finished, but workspace verification failed: " + error.Message +
                                " Refresh workspace status before continuing; no automatic undo was performed.";
                        }
                    }
                    return result;
                }
                catch (OperationCanceledException error)
                {
                    throw new OperationCanceledException("Branch switch or its verification was cancelled. The workspace may have changed; refresh status before continuing. No automatic undo was performed.", error, token);
                }
            }
        }

        private async Task ValidateBranchSwitchCleanAsync(string root, CancellationToken token)
        {
            if (HasSavedMergeSession(root) || HasSavedPartialConflictSession(root) || HasSavedPartialStructureSession(root) || HasSavedPartialDirectorySession(root) ||
                File.Exists(Path.Combine(root, ".plastic", "plastic.mergeprogress")))
                throw new ArgumentException("Finish or recover the existing merge/conflict session before switching branches.");
            await Task.Run(() => RejectUnsafeDescendants(root, root, token), token).ConfigureAwait(false);
            var status = await ExecuteAsync(RevisionCommand(root, new[] { "status", root, "--all", "--ignored", "--cutignored", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(status);
            if (ParseStatus(status.Output, root).Count != 0)
                throw new ArgumentException("Switch branch requires a clean workspace, including private and ignored files. Commit, undo or move these files first; no switch was performed.");
        }

        private void ValidateBranchWorkspaceUnchanged(PlasticWorkspace expected)
        {
            var current = DiscoverWorkspace(expected.RootPath);
            if (current == null || current.Repository != expected.Repository || NormalizeMergeSelector(current.Selector) != NormalizeMergeSelector(expected.Selector))
                throw new InvalidOperationException("The workspace selector changed during branch loading. Refresh before continuing.");
        }

        private static void ValidateBranchStatusRepository(XDocument status, string repository)
        {
            if (status.Root == null || status.Root.Name != "StatusOutput") throw new InvalidDataException("Unexpected branch workspace status XML.");
            var values = status.Root.Elements("WorkspaceStatus").Elements("Status").ToList();
            long changeset;
            if (values.Count != 1 || !Int64.TryParse((string)values[0].Element("Changeset"), out changeset) || changeset < -1)
                throw new InvalidDataException("Branch workspace status is ambiguous.");
            var rep = values[0].Element("RepSpec");
            if (rep == null || (string)rep.Element("Name") + "@" + (string)rep.Element("Server") != repository)
                throw new InvalidDataException("Branch workspace status belongs to another repository.");
        }

        public static IList<PlasticBranch> ParseBranches(string xml, string repository)
        {
            ValidateBranchRepository(repository);
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "PLASTICQUERY" || document.Root.Elements().Any(item => item.Name != "BRANCH"))
                throw new InvalidDataException("Unexpected Plastic branch XML.");
            var result = new List<PlasticBranch>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.Root.Elements("BRANCH"))
            {
                string name = (string)item.Element("NAME"), parent = (string)item.Element("PARENT") ?? "";
                try { ValidateBranchName(name); if (parent.Length != 0) ValidateBranchName(parent); }
                catch (ArgumentException error) { throw new InvalidDataException("Invalid branch name returned by the server.", error); }
                long head;
                if (!names.Add(name) || !Int64.TryParse((string)item.Element("CHANGESET"), NumberStyles.None, CultureInfo.InvariantCulture, out head) ||
                    (string)item.Element("REPNAME") + "@" + (string)item.Element("REPSERVER") != repository)
                    throw new InvalidDataException("Duplicate branch, invalid head or unexpected repository in branch listing.");
                result.Add(new PlasticBranch { Name = name, Parent = parent, Owner = (string)item.Element("OWNER") ?? "",
                    CreationDate = (string)item.Element("DATE") ?? "", Comment = (string)item.Element("COMMENT") ?? "", Repository = repository, HeadChangeset = head });
            }
            return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void ValidateBranchName(string name)
        {
            if (String.IsNullOrWhiteSpace(name) || !name.StartsWith("/", StringComparison.Ordinal) || name == "/" ||
                name.Any(Char.IsControl) || name.IndexOfAny(new[] { '@', '#', '"', '\\' }) >= 0 ||
                name.Substring(1).Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
                throw new ArgumentException("Specify a full branch name such as /main/task, without a repository suffix.");
        }

        private static void ValidateBranchRepository(string repository)
        {
            if (String.IsNullOrWhiteSpace(repository) || repository.Any(Char.IsControl) || repository.IndexOfAny(new[] { '"', '#' }) >= 0 || repository.IndexOf('@') <= 0)
                throw new InvalidDataException("The workspace does not identify a valid repository.");
        }
    }
}
