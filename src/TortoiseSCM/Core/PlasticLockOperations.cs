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
    public sealed class PlasticLockItem
    {
        public Guid LockId { get; set; }
        public string Repository { get; set; }
        public long ItemId { get; set; }
        public string Date { get; set; }
        public string DestinationBranch { get; set; }
        public long DestinationRevision { get; set; }
        public string HolderBranch { get; set; }
        public long HolderRevision { get; set; }
        public string Status { get; set; }
        public string Owner { get; set; }
        public string Workspace { get; set; }
        public string Path { get; set; }
        // Snapshot hint for UI only; UnlockOwnAsync always checks again. Server
        // permissions can still deny unlocking a lock owned by the current user.
        public bool CanUnlock { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public async Task<IList<PlasticLockItem>> GetLocksAsync(string root, CancellationToken cancellationToken)
        {
            var workspace = await ValidateLockWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            var all = await QueryLocksAsync(workspace, false, cancellationToken).ConfigureAwait(false);
            var own = await QueryLocksAsync(workspace, true, cancellationToken).ConfigureAwait(false);
            foreach (var item in all)
                item.CanUnlock = own.Any(candidate => candidate.Workspace == workspace.Name && SameLock(item, candidate));
            return all;
        }

        public async Task<PlasticCommandResult> UnlockOwnAsync(string root, Guid lockId, CancellationToken cancellationToken)
        {
            if (lockId == Guid.Empty) throw new ArgumentException("Select a valid lock GUID.", "lockId");
            var workspace = await ValidateLockWorkspaceAsync(root, cancellationToken).ConfigureAwait(false);
            var all = await QueryLocksAsync(workspace, false, cancellationToken).ConfigureAwait(false);
            var selected = all.SingleOrDefault(item => item.LockId == lockId);
            if (selected == null) throw new InvalidOperationException("The selected lock no longer exists in this repository.");

            // Never trust ownership from a previously displayed list. Ask cm using both
            // identity filters immediately before mutation and require an unchanged row.
            var own = await QueryLocksAsync(workspace, true, cancellationToken).ConfigureAwait(false);
            var current = own.SingleOrDefault(item => item.LockId == lockId);
            if (current == null || current.Workspace != workspace.Name || !SameLock(selected, current))
                throw new InvalidOperationException("Unlock refused: the lock must still belong to the current user and current workspace.");
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new [] {
                "lock", "unlock", workspace.Repository.Substring(workspace.Repository.LastIndexOf('@') + 1), lockId.ToString("D")
            }), cancellationToken).ConfigureAwait(false);
            // Some servers restrict even an owner's unlock to administrators. Preserve
            // their error. cm provides no atomic ownership-condition argument: server
            // authorization remains authoritative during the query-to-unlock race.
            RequireSuccess(result);
            return result;
        }

        private async Task<PlasticWorkspace> ValidateLockWorkspaceAsync(string root, CancellationToken cancellationToken)
        {
            var command = await BuildReadCommandAsync(root, cancellationToken).ConfigureAwait(false);
            if (!SamePath(command.Arguments[1], command.WorkingDirectory))
                throw new ArgumentException("Lock operations require the workspace root.");
            var workspace = DiscoverWorkspace(command.WorkingDirectory);
            int separator = workspace.Repository.LastIndexOf('@');
            if (separator < 1 || separator == workspace.Repository.Length - 1 ||
                workspace.Repository.IndexOfAny(new [] { '\r', '\n', '\0' }) >= 0 || String.IsNullOrWhiteSpace(workspace.Name))
                throw new InvalidDataException("The workspace does not specify a repository and server for lock operations.");
            string server = workspace.Repository.Substring(separator + 1);
            if (server.StartsWith("-", StringComparison.Ordinal)) throw new InvalidDataException("Invalid lock server specification.");
            return workspace;
        }

        private async Task<IList<PlasticLockItem>> QueryLocksAsync(PlasticWorkspace workspace, bool own, CancellationToken cancellationToken)
        {
            var args = new List<string> { "lock", "list", "--repository=" + workspace.Repository,
                "--machinereadable", "--smartlocks", "--anystatus", "--fieldseparator=|",
                "--startlineseparator=TSLOCK|", "--endlineseparator=|END" };
            if (own) { args.Add("--onlycurrentuser"); args.Add("--onlycurrentworkspace"); }
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, args), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            return ParseLocks(result.Output);
        }

        // Documented --smartlocks --machinereadable field order:
        // https://docs.unity.com/ja-jp/unity-version-control/uvcs-cli/lock-list
        // Do not parse localized table headings or guess whitespace-delimited columns.
        internal static IList<PlasticLockItem> ParseLocks(string output)
        {
            var items = new List<PlasticLockItem>();
            var ids = new HashSet<Guid>();
            foreach (string line in (output ?? "").Split(new [] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("TSLOCK|", StringComparison.Ordinal) || !line.EndsWith("|END", StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected lock output framing.");
                string[] fields = line.Substring(7, line.Length - 11).Split('|');
                Guid id; long item, destination, holder;
                if (fields.Length != 12 || !Guid.TryParse(fields[2], out id) || id == Guid.Empty || !ids.Add(id) ||
                    !Int64.TryParse(fields[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out item) || item < 0 ||
                    !Int64.TryParse(fields[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out destination) || destination < -1 ||
                    !Int64.TryParse(fields[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out holder) || holder < -1 ||
                    (fields[8] != "Locked" && fields[8] != "Retained") ||
                    String.IsNullOrWhiteSpace(fields[0]) || String.IsNullOrWhiteSpace(fields[9]) ||
                    String.IsNullOrWhiteSpace(fields[10]) || !fields[11].StartsWith("/", StringComparison.Ordinal))
                    throw new InvalidDataException("Unexpected or ambiguous lock output fields.");
                items.Add(new PlasticLockItem { Repository = fields[0], ItemId = item, LockId = id,
                    Date = fields[3], DestinationBranch = fields[4], DestinationRevision = destination,
                    HolderBranch = fields[6], HolderRevision = holder, Status = fields[8],
                    Owner = fields[9], Workspace = fields[10], Path = fields[11] });
            }
            return items;
        }

        private static bool SameLock(PlasticLockItem left, PlasticLockItem right)
        {
            return left.LockId == right.LockId && left.Repository == right.Repository && left.ItemId == right.ItemId &&
                left.Date == right.Date && left.DestinationBranch == right.DestinationBranch && left.DestinationRevision == right.DestinationRevision &&
                left.HolderBranch == right.HolderBranch && left.HolderRevision == right.HolderRevision && left.Status == right.Status &&
                left.Owner == right.Owner && left.Workspace == right.Workspace && left.Path == right.Path;
        }
    }
}
