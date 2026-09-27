// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace TortoiseSCM
{
    public sealed class PlasticBranchTreeNode
    {
        public PlasticBranch Branch { get; set; }
        public int Depth { get; set; }
        public bool IsMatch { get; set; }
        public bool ParentMissing { get; set; }
        // Direct children present in the original input, before filtering.
        public int ChildCount { get; set; }
    }

    public sealed partial class PlasticClient
    {
        // This is branch parentage, not changeset ancestry or merge topology.
        // Native Parent metadata is authoritative, including names that do not
        // resemble their parent. Never fabricate an inaccessible parent branch.
        public static IList<PlasticBranchTreeNode> BuildBranchHierarchy(IList<PlasticBranch> branches, string filter)
        {
            if (branches == null) throw new ArgumentNullException("branches");
            var byName = new Dictionary<string, PlasticBranch>(StringComparer.Ordinal);
            var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            string repository = null;
            foreach (var branch in branches)
            {
                if (branch == null) throw new ArgumentException("The branch list contains a null entry.", "branches");
                ValidateBranchName(branch.Name);
                if (branch.Parent == null) throw new ArgumentException("A branch has no parent metadata.", "branches");
                if (branch.Parent.Length != 0) ValidateBranchName(branch.Parent);
                ValidateBranchRepository(branch.Repository);
                if (branch.HeadChangeset < 0) throw new ArgumentException("A branch has an invalid head changeset.", "branches");
                if (repository == null) repository = branch.Repository;
                else if (!String.Equals(repository, branch.Repository, StringComparison.Ordinal))
                    throw new ArgumentException("A branch hierarchy must belong to one repository.", "branches");
                if (byName.ContainsKey(branch.Name)) throw new ArgumentException("The branch list contains a duplicate name: " + branch.Name, "branches");
                byName.Add(branch.Name, branch);
                children.Add(branch.Name, new List<string>());
            }

            var roots = new List<string>();
            foreach (var branch in branches)
            {
                if (branch.Parent.Length != 0 && byName.ContainsKey(branch.Parent)) children[branch.Parent].Add(branch.Name);
                else roots.Add(branch.Name);
            }
            var order = Comparer<string>.Create((left, right) => {
                int compared = StringComparer.OrdinalIgnoreCase.Compare(left, right);
                return compared == 0 ? StringComparer.Ordinal.Compare(left, right) : compared;
            });
            roots.Sort(order);
            foreach (var siblings in children.Values) siblings.Sort(order);

            // Iterative preorder also detects cycles anywhere in the full input,
            // even when a filter would otherwise hide the malformed component.
            var ordered = new List<PlasticBranchTreeNode>(branches.Count);
            var pending = new Stack<KeyValuePair<string, int>>();
            for (int i = roots.Count - 1; i >= 0; --i) pending.Push(new KeyValuePair<string, int>(roots[i], 0));
            while (pending.Count != 0)
            {
                var entry = pending.Pop();
                var branch = byName[entry.Key];
                ordered.Add(new PlasticBranchTreeNode { Branch = branch, Depth = entry.Value,
                    ParentMissing = branch.Parent.Length != 0 && !byName.ContainsKey(branch.Parent), ChildCount = children[entry.Key].Count });
                var descendants = children[entry.Key];
                for (int i = descendants.Count - 1; i >= 0; --i)
                    pending.Push(new KeyValuePair<string, int>(descendants[i], entry.Value + 1));
            }
            if (ordered.Count != branches.Count) throw new ArgumentException("The branch parent metadata contains a cycle.", "branches");

            string query = (filter ?? "").Trim();
            var included = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in ordered)
            {
                var branch = node.Branch;
                node.IsMatch = query.Length == 0 || new[] { branch.Name, branch.Owner, branch.CreationDate, branch.Comment,
                    "cs:" + branch.HeadChangeset.ToString(CultureInfo.InvariantCulture) }.Any(value =>
                        value != null && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
                if (!node.IsMatch) continue;
                // Each retained ancestor is visited at most once across all matches.
                var ancestor = branch;
                while (included.Add(ancestor.Name) && ancestor.Parent.Length != 0 && byName.TryGetValue(ancestor.Parent, out ancestor)) { }
            }
            return ordered.Where(node => included.Contains(node.Branch.Name)).ToList();
        }
    }
}
