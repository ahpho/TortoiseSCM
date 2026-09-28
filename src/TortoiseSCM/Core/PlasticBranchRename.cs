// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        // Snapshot the caller's mutable row before the first asynchronous operation.
        public Task<PlasticCommandResult> RenameBranchAsync(string path, PlasticBranch expected, string newName,
            string expectedSelector, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (expected == null) throw new ArgumentNullException("expected");
            var snapshot = new PlasticBranch { BranchId = expected.BranchId, Guid = expected.Guid, Name = expected.Name,
                Parent = expected.Parent, Repository = expected.Repository, HeadChangeset = expected.HeadChangeset,
                IsCurrent = expected.IsCurrent };
            ValidateRenameIdentity(snapshot);
            if (String.IsNullOrWhiteSpace(newName) || newName != newName.Trim() || newName.StartsWith("-", StringComparison.Ordinal) ||
                newName == "." || newName == ".." || newName.Any(Char.IsControl) || newName.IndexOfAny(new[] { '/', '\\', '@', '#', '"', ':', '?', '\'' }) >= 0)
                throw new ArgumentException("Provide a new leaf branch name without path separators, repository suffixes, leading hyphens or reserved punctuation.");
            if (expectedSelector == null) throw new ArgumentNullException("expectedSelector");
            var original = DiscoverWorkspace(path);
            if (original == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            var workspace = new PlasticWorkspace { RootPath = original.RootPath, Name = original.Name,
                Repository = snapshot.Repository, Selector = expectedSelector, IsPartial = original.IsPartial };
            ValidateRenameWorkspace(workspace);
            return RenameBranchCoreAsync(path, snapshot, newName, workspace, token);
        }

        private async Task<PlasticCommandResult> RenameBranchCoreAsync(string path, PlasticBranch expected, string newName,
            PlasticWorkspace workspace, CancellationToken token)
        {
            string target = expected.Parent + "/" + newName;
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            if (!SamePath(command.WorkingDirectory, workspace.RootPath)) throw new InvalidOperationException("The workspace root changed. Refresh before renaming.");
            string root = workspace.RootPath;
            using (var structureGate = StructureGate(root)) using (var mergeGate = OpenMergeGate(root))
            {
                ValidateRenameWorkspace(workspace);
                ValidateRenamePreflight(await GetBranchesAsync(root, token).ConfigureAwait(false), expected, target, workspace);
                // Refresh identity and head again immediately before the server mutation.
                ValidateRenameWorkspace(workspace);
                ValidateRenamePreflight(await GetBranchesAsync(root, token).ConfigureAwait(false), expected, target, workspace);
                ValidateRenameWorkspace(workspace);
                token.ThrowIfCancellationRequested();
                const string advisory = " The server branch may already have been renamed. Refresh branches and workspace status before retrying; no automatic reverse rename or workspace switch was performed.";
                try
                {
                    // Native rename accepts a name spec, not a stable ID/GUID spec. Server
                    // concurrency between this preflight and mutation cannot be made atomic.
                    var result = await ExecuteAsync(RevisionCommand(root, new[] { "branch", "rename",
                        "br:" + expected.Name + "@" + expected.Repository, newName }), token).ConfigureAwait(false);
                    if (!result.Succeeded) result.Error += advisory;
                    else
                    {
                        try
                        {
                            ValidateRenameWorkspace(workspace);
                            var branches = await GetBranchesAsync(root, token).ConfigureAwait(false);
                            var renamed = branches.SingleOrDefault(item => item.Name == target);
                            if (renamed == null || !SameRenameIdentity(renamed, expected, false) || renamed.IsCurrent ||
                                branches.Any(item => String.Equals(item.Name, expected.Name, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("The renamed branch's identity, parent and head could not be verified, or the old name still exists.");
                            ValidateRenameWorkspace(workspace);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) { result.ExitCode = 1; result.Error += "Branch rename verification failed: " + error.Message + advisory; }
                    }
                    return result;
                }
                catch (OperationCanceledException error) { throw new OperationCanceledException("Branch rename or verification was cancelled." + advisory, error, token); }
                catch (Exception error) { throw new InvalidOperationException("Branch rename did not finish reliably." + advisory, error); }
            }
        }

        private static void ValidateRenameIdentity(PlasticBranch branch)
        {
            Guid guid;
            if (branch.BranchId <= 0 || !System.Guid.TryParse(branch.Guid, out guid) || guid == System.Guid.Empty)
                throw new ArgumentException("Branch rename requires a positive native branch ID and GUID. Refresh the branch list.");
            ValidateBranchRepository(branch.Repository);
            ValidateBranchName(branch.Name);
            if (String.IsNullOrEmpty(branch.Parent) || branch.Name.LastIndexOf('/') <= 0)
                throw new ArgumentException("Root and top-level branches cannot be renamed.");
            ValidateBranchName(branch.Parent);
            if (branch.Name.Substring(0, branch.Name.LastIndexOf('/')) != branch.Parent || branch.HeadChangeset < 0)
                throw new ArgumentException("Branch parent or head is invalid. Refresh the branch list.");
            if (branch.IsCurrent) throw new ArgumentException("The current workspace branch cannot be renamed. Switch to another branch first.");
        }

        private static bool SameRenameIdentity(PlasticBranch actual, PlasticBranch expected, bool includeName)
        {
            Guid actualGuid, expectedGuid;
            return actual.BranchId == expected.BranchId && System.Guid.TryParse(actual.Guid, out actualGuid) &&
                System.Guid.TryParse(expected.Guid, out expectedGuid) && actualGuid == expectedGuid &&
                actual.Repository == expected.Repository && actual.Parent == expected.Parent && actual.HeadChangeset == expected.HeadChangeset &&
                (!includeName || actual.Name == expected.Name);
        }

        private static void ValidateRenamePreflight(IList<PlasticBranch> branches, PlasticBranch expected, string target, PlasticWorkspace workspace)
        {
            var selected = branches.SingleOrDefault(item => item.Name == expected.Name);
            if (selected == null || !SameRenameIdentity(selected, expected, true))
                throw new InvalidOperationException("The selected branch identity, parent or head changed. Refresh before renaming.");
            if (selected.IsCurrent || Regex.Matches(workspace.Selector, @"(?m)^\s*(?:smartbranch|branch|br|co)\s+""([^""]+)""\s*$")
                .Cast<Match>().Any(match => String.Equals(match.Groups[1].Value, expected.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The current workspace branch cannot be renamed. Switch to another branch first.");
            if (!branches.Any(item => item.Name == expected.Parent)) throw new InvalidOperationException("The parent branch no longer exists. Refresh before renaming.");
            // Native PARENT is authoritative; a branch can have unrelated-looking names.
            if (branches.Any(item => String.Equals(item.Parent, expected.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Only leaf branches without child branches can be renamed.");
            if (branches.Any(item => String.Equals(item.Name, target, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("That branch name already exists. Choose a different leaf name.");
        }

        private void ValidateRenameWorkspace(PlasticWorkspace expected)
        {
            ValidateBranchWorkspaceUnchanged(expected);
            var current = DiscoverWorkspace(expected.RootPath);
            if (current == null || current.Name != expected.Name || current.IsPartial != expected.IsPartial)
                throw new InvalidOperationException("The workspace identity or mode changed. Refresh before renaming.");
        }
    }
}
