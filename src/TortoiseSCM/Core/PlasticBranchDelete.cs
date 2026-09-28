// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        // Copy before asynchronous work: a refreshed UI row cannot retarget deletion.
        public Task<PlasticCommandResult> DeleteBranchAsync(string path, PlasticBranch expected,
            string expectedSelector, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (expected == null) throw new ArgumentNullException("expected");
            var snapshot = new PlasticBranch { BranchId = expected.BranchId, Guid = expected.Guid, Name = expected.Name,
                Parent = expected.Parent, Repository = expected.Repository, HeadChangeset = expected.HeadChangeset,
                IsCurrent = expected.IsCurrent };
            ValidateDeleteIdentity(snapshot);
            if (expectedSelector == null) throw new ArgumentNullException("expectedSelector");
            var original = DiscoverWorkspace(path);
            if (original == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            var workspace = new PlasticWorkspace { RootPath = original.RootPath, Name = original.Name,
                Repository = snapshot.Repository, Selector = expectedSelector, IsPartial = original.IsPartial };
            ValidateDeleteWorkspace(workspace);
            return DeleteBranchCoreAsync(path, snapshot, workspace, token);
        }

        private async Task<PlasticCommandResult> DeleteBranchCoreAsync(string path, PlasticBranch expected,
            PlasticWorkspace workspace, CancellationToken token)
        {
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            if (!SamePath(command.WorkingDirectory, workspace.RootPath))
                throw new InvalidOperationException("The workspace root changed. Refresh before deleting.");
            string root = workspace.RootPath;
            using (var structureGate = StructureGate(root)) using (var mergeGate = OpenMergeGate(root))
            {
                // Repeat both identity and reference checks, since empty branches inherit
                // their parent's head and head equality alone cannot prove emptiness.
                for (int pass = 0; pass < 2; pass++)
                {
                    ValidateDeleteWorkspace(workspace);
                    ValidateDeletePreflight(await GetBranchesAsync(root, token).ConfigureAwait(false), expected, workspace);
                    await ValidateDeleteReferencesAsync(root, expected, token).ConfigureAwait(false);
                }
                ValidateDeleteWorkspace(workspace);
                token.ThrowIfCancellationRequested();
                const string advisory = " The server branch may already have been deleted. Refresh branches and workspace status before retrying; no automatic retry, recreation or workspace switch was performed.";
                try
                {
                    // Native deletion accepts a name spec, not an atomic identity condition.
                    // Another server client can still race the final preflight.
                    var result = await ExecuteAsync(RevisionCommand(root, new[] { "branch", "delete",
                        "br:" + expected.Name + "@" + expected.Repository }), token).ConfigureAwait(false);
                    if (!result.Succeeded) result.Error += advisory;
                    else
                    {
                        try
                        {
                            ValidateDeleteWorkspace(workspace);
                            var branches = await GetBranchesAsync(root, token).ConfigureAwait(false);
                            Guid expectedGuid = System.Guid.Parse(expected.Guid);
                            if (branches.Any(item => item.BranchId <= 0 || String.IsNullOrEmpty(item.Guid)))
                                throw new InvalidOperationException("The server omitted branch identities, so deletion cannot be verified across renamed branches.");
                            if (branches.Any(item => String.Equals(item.Name, expected.Name, StringComparison.OrdinalIgnoreCase) ||
                                item.BranchId == expected.BranchId || DeleteGuidEquals(item.Guid, expectedGuid)))
                                throw new InvalidOperationException("The deleted branch name or identity still exists. Its deletion could not be verified.");
                            ValidateDeleteWorkspace(workspace);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception error) { result.ExitCode = 1; result.Error += "Branch deletion verification failed: " + error.Message + advisory; }
                    }
                    return result;
                }
                catch (OperationCanceledException error) { throw new OperationCanceledException("Branch deletion or verification was cancelled." + advisory, error, token); }
                catch (Exception error) { throw new InvalidOperationException("Branch deletion did not finish reliably." + advisory, error); }
            }
        }

        private static void ValidateDeleteIdentity(PlasticBranch branch)
        {
            Guid guid;
            if (branch.BranchId <= 0 || !System.Guid.TryParse(branch.Guid, out guid) || guid == System.Guid.Empty)
                throw new ArgumentException("Branch deletion requires a positive native branch ID and GUID. Refresh the branch list.");
            ValidateBranchRepository(branch.Repository);
            ValidateBranchName(branch.Name);
            // These names are embedded in native find string literals, not shell commands.
            // Reject ambiguous escaping instead of attempting query-language interpolation.
            if ((branch.Name + branch.Repository).IndexOfAny(new[] { '\'', '\\', '"' }) >= 0)
                throw new ArgumentException("Branch deletion cannot safely query names or repositories containing quotes or backslashes.");
            if (String.IsNullOrEmpty(branch.Parent) || branch.Name.LastIndexOf('/') <= 0)
                throw new ArgumentException("Root and top-level branches cannot be deleted.");
            ValidateBranchName(branch.Parent);
            if (branch.Name.Substring(0, branch.Name.LastIndexOf('/')) != branch.Parent || branch.HeadChangeset < 0)
                throw new ArgumentException("Branch parent or head is invalid. Refresh the branch list.");
            if (branch.IsCurrent) throw new ArgumentException("The current workspace branch cannot be deleted. Switch to another branch first.");
        }

        private static bool DeleteGuidEquals(string value, Guid expected)
        { Guid actual; return System.Guid.TryParse(value, out actual) && actual == expected; }

        private static void ValidateDeletePreflight(IList<PlasticBranch> branches, PlasticBranch expected, PlasticWorkspace workspace)
        {
            var selected = branches.SingleOrDefault(item => item.Name == expected.Name);
            if (selected == null || selected.BranchId != expected.BranchId || !DeleteGuidEquals(selected.Guid, System.Guid.Parse(expected.Guid)) ||
                selected.Repository != expected.Repository || selected.Parent != expected.Parent || selected.HeadChangeset != expected.HeadChangeset)
                throw new InvalidOperationException("The selected branch identity, parent or head changed. Refresh before deleting.");
            if (selected.IsCurrent || Regex.Matches(workspace.Selector, @"(?m)^\s*(?:smartbranch|branch|br|co)\s+""([^""]+)""\s*$")
                .Cast<Match>().Any(match => String.Equals(match.Groups[1].Value, expected.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The current workspace branch cannot be deleted. Switch to another branch first.");
            if (!branches.Any(item => item.Name == expected.Parent)) throw new InvalidOperationException("The parent branch no longer exists. Refresh before deleting.");
            if (branches.Any(item => String.Equals(item.Parent, expected.Name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Only leaf branches without child branches can be deleted.");
        }

        private async Task ValidateDeleteReferencesAsync(string root, PlasticBranch branch, CancellationToken token)
        {
            string spec = "br:" + branch.Name + "@" + branch.Repository;
            await RequireEmptyDeleteQueryAsync(root, "changeset", "where branch = '" + spec + "'",
                "Only empty branches can be deleted. The selected branch has its own changesets.", token).ConfigureAwait(false);
            await RequireEmptyDeleteQueryAsync(root, "attribute", "where srcobj = '" + spec + "'",
                "The selected branch has attributes. Remove or review these references before deleting.", token).ConfigureAwait(false);
            await RequireEmptyDeleteQueryAsync(root, "shelve", "where parent = " + branch.HeadChangeset.ToString(CultureInfo.InvariantCulture),
                "A shelveset references the selected branch's inherited head. Deletion is conservatively blocked even if that shelveset was created on another branch.", token).ConfigureAwait(false);
        }

        private async Task RequireEmptyDeleteQueryAsync(string root, string type, string query, string reason, CancellationToken token)
        {
            var result = await ExecuteAsync(RevisionCommand(root, new[] { "find", type, query + " limit 1", "--xml", "--nototal", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var document = SafeXml.Load(result.Output);
            if (document.Root == null || document.Root.Name != "PLASTICQUERY")
                throw new InvalidDataException("Unexpected Plastic " + type + " reference XML; branch deletion was blocked.");
            if (document.Root.Elements().Any()) throw new ArgumentException(reason);
            if (!String.IsNullOrWhiteSpace(document.Root.Value))
                throw new InvalidDataException("Unexpected Plastic " + type + " reference text; branch deletion was blocked.");
        }

        private void ValidateDeleteWorkspace(PlasticWorkspace expected)
        {
            ValidateBranchWorkspaceUnchanged(expected);
            var current = DiscoverWorkspace(expected.RootPath);
            if (current == null || current.Name != expected.Name || current.IsPartial != expected.IsPartial)
                throw new InvalidOperationException("The workspace identity or mode changed. Refresh before deleting.");
        }
    }
}
