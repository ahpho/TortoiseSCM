// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        // Called only while both workspace mutation gates are held. Native partial
        // switch retains loading rules; configure/force/automatic shelving are forbidden.
        private async Task<PlasticCommandResult> SwitchPartialBranchAsync(PlasticWorkspace workspace, string branch, CancellationToken token)
        {
            string root = workspace.RootPath;
            var loading = CapturePartialSwitchLoading(root);
            string tree = PartialSwitchMetadataHash(root, "plastic.wktree", true);
            string identity = PartialSwitchMetadataHash(root, "plastic.workspace", true);
            await ValidateBranchSwitchCleanAsync(root, token).ConfigureAwait(false);
            var expected = (await GetBranchesAsync(root, token).ConfigureAwait(false)).SingleOrDefault(item => item.Name == branch);
            Guid guid;
            if (expected == null || expected.BranchId <= 0 || !System.Guid.TryParse(expected.Guid, out guid) || guid == System.Guid.Empty || expected.HeadChangeset < 0)
                throw new ArgumentException("Partial branch switch requires the target's native ID, GUID and head. Refresh the branch list.");
            ValidatePartialSwitchPreflight(workspace, loading, tree, identity);
            var refreshed = (await GetBranchesAsync(root, token).ConfigureAwait(false)).SingleOrDefault(item => item.Name == branch);
            RequirePartialSwitchTarget(refreshed, expected);
            await ValidateBranchSwitchCleanAsync(root, token).ConfigureAwait(false);
            var current = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
            RequirePartialSwitchWorkspace(current, workspace);
            ValidatePartialSwitchPreflight(workspace, loading, tree, identity);
            token.ThrowIfCancellationRequested();
            const string advisory = " The Partial workspace may have changed. Refresh branches and workspace status, and inspect the loading configuration before continuing; no automatic undo, configuration restore or retry was performed.";
            try
            {
                // This command uses the root CWD: native partial switch rejects
                // --workspace. Name-based native selection is not atomic with preflight.
                var result = await ExecuteAsync(RevisionCommand(root, new[] { "partial", "switch", "br:" + branch + "@" + workspace.Repository, "--report" }), token).ConfigureAwait(false);
                if (!result.Succeeded) result.Error += advisory;
                else
                {
                    try
                    {
                        var updated = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
                        RequirePartialSwitchWorkspace(updated, workspace);
                        var selected = (await GetBranchesAsync(root, token).ConfigureAwait(false)).SingleOrDefault(item => item.Name == branch);
                        RequirePartialSwitchTarget(selected, expected);
                        if (!selected.IsCurrent) throw new InvalidOperationException("The requested branch is not the current Partial workspace selector.");
                        await ValidateBranchSwitchCleanAsync(root, token).ConfigureAwait(false);
                        RequirePartialSwitchWorkspace(await GetWorkspaceAsync(root, token).ConfigureAwait(false), workspace);
                        RequirePartialSwitchLoading(root, loading);
                        if (PartialSwitchMetadataHash(root, "plastic.workspace", true) != identity)
                            throw new InvalidOperationException("The workspace identity metadata changed during branch switching.");
                        // Selector and wktree are intentionally allowed to change after
                        // native switching: loaded items now refer to the target revisions.
                        ValidateBranchWorkspaceUnchanged(updated);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) { result.ExitCode = 1; result.Error += "Partial branch switch verification failed: " + error.Message + advisory; }
                }
                return result;
            }
            catch (OperationCanceledException error) { throw new OperationCanceledException("Partial branch switch or verification was cancelled." + advisory, error, token); }
            catch (Exception error) { throw new InvalidOperationException("Partial branch switch did not finish reliably." + advisory, error); }
        }

        private void ValidatePartialSwitchPreflight(PlasticWorkspace workspace, IDictionary<string, string> loading, string tree, string identity)
        {
            ValidateBranchWorkspaceUnchanged(workspace);
            var current = DiscoverWorkspace(workspace.RootPath);
            if (current == null || current.Name != workspace.Name || PartialSwitchMetadataHash(workspace.RootPath, "plastic.workspace", true) != identity)
                throw new InvalidOperationException("The workspace identity changed. Refresh before switching branches.");
            RequirePartialSwitchLoading(workspace.RootPath, loading);
            if (PartialSwitchMetadataHash(workspace.RootPath, "plastic.wktree", true) != tree)
                throw new InvalidOperationException("The loaded workspace tree changed. Refresh before switching branches.");
        }

        private static void RequirePartialSwitchWorkspace(PlasticWorkspace actual, PlasticWorkspace expected)
        {
            if (!actual.IsPartial || actual.Name != expected.Name || actual.Repository != expected.Repository || !SamePath(actual.RootPath, expected.RootPath))
                throw new InvalidOperationException("The workspace identity, repository or native Partial mode changed.");
        }

        private static void RequirePartialSwitchTarget(PlasticBranch actual, PlasticBranch expected)
        {
            if (actual == null || !SameRenameIdentity(actual, expected, true))
                throw new InvalidOperationException("The target branch identity, parent or head changed. Refresh before continuing.");
        }

        private static IDictionary<string, string> CapturePartialSwitchLoading(string root)
        {
            string path = Path.Combine(root, ".plastic", "plastic.fullycheckeddirectories");
            RejectReparsePath(path);
            if (!File.Exists(path)) throw new ArgumentException("Partial loading rules are missing. Repair or refresh the loading configuration before switching branches.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in File.ReadAllLines(path))
            {
                string[] parts = line.Split(':'); Guid guid; long item;
                if (parts.Length != 2 || !System.Guid.TryParseExact(parts[0], "D", out guid) || guid == System.Guid.Empty ||
                    !Int64.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out item) || item <= 0 || !seen.Add(guid.ToString("D") + ":" + item))
                    throw new ArgumentException("Partial loading rules are malformed or duplicated. Repair the loading configuration before switching branches.");
            }
            if (File.Exists(Path.Combine(root, ".plastic", "plastic.fullupdate")) && seen.Count != 0)
                throw new ArgumentException("Full-workspace loading cannot contain explicit directory rules. Repair the loading configuration first.");
            return new Dictionary<string, string> {
                { "plastic.fullycheckeddirectories", PartialSwitchMetadataHash(root, "plastic.fullycheckeddirectories", true) },
                { "plastic.fullupdate", PartialSwitchMetadataHash(root, "plastic.fullupdate", false) }
            };
        }

        private static void RequirePartialSwitchLoading(string root, IDictionary<string, string> expected)
        {
            var actual = CapturePartialSwitchLoading(root);
            if (expected.Any(pair => actual[pair.Key] != pair.Value))
                throw new InvalidOperationException("The Partial loading configuration changed. Refresh before continuing.");
        }

        private static string PartialSwitchMetadataHash(string root, string name, bool required)
        {
            string path = Path.Combine(root, ".plastic", name); RejectReparsePath(path);
            if (Directory.Exists(path)) throw new ArgumentException("Workspace metadata must be a regular file: " + name);
            if (!File.Exists(path))
            {
                if (required) throw new ArgumentException("Required Partial workspace metadata is missing: " + name);
                return "missing";
            }
            return MergeHash(path);
        }
    }
}
