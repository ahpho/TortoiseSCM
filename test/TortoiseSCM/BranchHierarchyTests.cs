// GPL-2.0-or-later. Pure branch-parent hierarchy and bounded stack usage.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TortoiseSCM;

internal static class BranchHierarchyTests
{
    private static int assertions;
    private static int Main()
    {
        try
        {
            var main = Branch("/main", "", 1);
            var feature = Branch("/feature 中文", "/main", 22); feature.Owner = "Alice"; feature.Comment = "release note";
            var grandchild = Branch("/unrelated-looking-name", "/feature 中文", 33); grandchild.CreationDate = "2026-09-27";
            var sibling = Branch("/main/beta", "/main", 4);
            var absent = Branch("/restricted/visible", "/restricted", 5);
            var absentChild = Branch("/restricted/visible/child", absent.Name, 6);
            var input = new List<PlasticBranch> { absentChild, sibling, feature, absent, grandchild, main };
            var original = input.ToArray();
            string fingerprint = Fingerprint(input);
            var nodes = PlasticClient.BuildBranchHierarchy(input, null);
            Check(nodes.Select(n => n.Branch.Name).SequenceEqual(new[] { "/main", "/feature 中文", "/unrelated-looking-name", "/main/beta", "/restricted/visible", "/restricted/visible/child" }), "Explicit parents override path-name guesses and are always emitted before children");
            Check(nodes.Select(n => n.Depth).SequenceEqual(new[] { 0, 1, 2, 1, 0, 1 }), "Depth follows explicit native parent relationships");
            Check(nodes.Select(n => n.ChildCount).SequenceEqual(new[] { 2, 1, 0, 0, 1, 0 }), "Direct child counts do not count grandchildren");
            Check(nodes.All(n => n.IsMatch), "No filter marks every node as a match");
            Check(nodes.Single(n => n.ParentMissing).Branch == absent, "Only inaccessible-parent root is flagged and no missing branch is invented");
            Check(input.SequenceEqual(original) && Fingerprint(input) == fingerprint, "Input ordering and branch metadata remain unchanged");
            Check(nodes.All(n => input.Contains(n.Branch)), "Output retains the original branch models for action identity");
            Check(PlasticClient.BuildBranchHierarchy(new List<PlasticBranch>(), "anything").Count == 0, "Empty input is an empty hierarchy");
            Check(PlasticClient.BuildBranchHierarchy(input, " \t ").Count == input.Count, "Whitespace filter means all branches");

            var filtered = PlasticClient.BuildBranchHierarchy(input, "2026-09-27");
            Check(filtered.Select(n => n.Branch.Name).SequenceEqual(new[] { main.Name, feature.Name, grandchild.Name }), "Matching date retains all existing ancestors and no sibling");
            Check(filtered.Select(n => n.IsMatch).SequenceEqual(new[] { false, false, true }), "Context ancestors are distinguished from direct matches");
            Check(filtered[0].ChildCount == 2 && filtered[0].Depth == 0 && filtered[2].Depth == 2, "Filtered tree preserves full direct child count and original depth");
            Check(PlasticClient.BuildBranchHierarchy(input, "alice").Last().Branch == feature, "Owner matching is case insensitive");
            Check(PlasticClient.BuildBranchHierarchy(input, "RELEASE NOTE").Last().Branch == feature, "Comment matching is case insensitive");
            Check(PlasticClient.BuildBranchHierarchy(input, "中文").Last().Branch == feature, "Unicode branch names are searchable");
            Check(PlasticClient.BuildBranchHierarchy(input, "cs:33").Last().Branch == grandchild, "Changeset prefix search is supported");
            Check(PlasticClient.BuildBranchHierarchy(input, "33").Last().Branch == grandchild, "Numeric changeset search is supported");
            Check(PlasticClient.BuildBranchHierarchy(input, "no-match-anywhere").Count == 0, "No matches yield no context-only roots");
            var restricted = PlasticClient.BuildBranchHierarchy(input, absentChild.Name);
            Check(restricted.Count == 2 && restricted[0].Branch == absent && restricted[0].ParentMissing && !restricted[0].IsMatch, "Filtered orphan descendant retains only accessible ancestors");
            Check(Fingerprint(input) == fingerprint, "Filtering does not mutate match flags into source models");

            var upperRoot = Branch("/MAIN", "", 10);
            var lowerRoot = Branch("/main", "", 11);
            var upperChild = Branch("/Z", "/MAIN", 12);
            var lowerChild = Branch("/a", "/main", 13);
            var wrongCaseParent = Branch("/orphan", "/Main", 14);
            var caseNodes = PlasticClient.BuildBranchHierarchy(new[] { wrongCaseParent, lowerChild, lowerRoot, upperChild, upperRoot }, "");
            Check(caseNodes.Select(n => n.Branch.Name).SequenceEqual(new[] { "/MAIN", "/Z", "/main", "/a", "/orphan" }), "Case-insensitive ordering uses ordinal tie break and preserves distinct exact names");
            Check(caseNodes.Last().ParentMissing && caseNodes.Last().Depth == 0, "Parent resolution is case exact, never guessed by case folding");
            var sortInput = new[] { Branch("/root", "", 0), Branch("/root/z", "/root", 0), Branch("/root/A", "/root", 0), Branch("/root/a", "/root", 0), Branch("/root/中文", "/root", 0) };
            string[] expectedOrder = PlasticClient.BuildBranchHierarchy(sortInput, "").Select(n => n.Branch.Name).ToArray();
            var random = new Random(42);
            for (int i = 0; i < 20; i++)
                Check(PlasticClient.BuildBranchHierarchy(sortInput.OrderBy(n => random.Next()).ToList(), "").Select(n => n.Branch.Name).SequenceEqual(expectedOrder), "Input order cannot change hierarchy order");

            Reject(() => PlasticClient.BuildBranchHierarchy(null, ""), "Null collection rejected");
            Reject(() => PlasticClient.BuildBranchHierarchy(new PlasticBranch[] { null }, ""), "Null branch rejected");
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { main, main }, ""), "Duplicate names rejected");
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { Branch(null, "", 0) }, ""), "Null name rejected");
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { Branch("relative", "", 0) }, ""), "Malformed name rejected");
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { Branch("/root", null, 0) }, ""), "Missing parent metadata rejected");
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { Branch("/root", "relative", 0) }, ""), "Malformed parent rejected");
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { Branch("/root", "", -1) }, ""), "Invalid head rejected");
            var foreign = Branch("/foreign", "", 0); foreign.Repository = "other@server:8087";
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { main, foreign }, ""), "Mixed repositories rejected");
            var missingRepo = Branch("/root", "", 0); missingRepo.Repository = null;
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { missingRepo }, ""), "Missing repository rejected");
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { Branch("/self", "/self", 0) }, "no-match"), "Self-cycle rejected even when filtered out");
            Reject(() => PlasticClient.BuildBranchHierarchy(new[] { main, Branch("/a", "/b", 0), Branch("/b", "/a", 0), Branch("/child", "/b", 0) }, "/main"), "Disconnected cycle and its descendants cannot hide behind valid matching roots");

            const int deepCount = 20000;
            var deep = Enumerable.Range(0, deepCount).Select(i => Branch("/node-" + i, i == 0 ? "" : "/node-" + (i - 1), i)).Reverse().ToList();
            var deepNodes = PlasticClient.BuildBranchHierarchy(deep, null);
            Check(deepNodes.Count == deepCount && deepNodes.Last().Depth == deepCount - 1, "Twenty-thousand-deep hierarchy uses no recursive call stack");
            var deepMatch = PlasticClient.BuildBranchHierarchy(deep, "cs:" + (deepCount - 1));
            Check(deepMatch.Count == deepCount && deepMatch.Count(n => n.IsMatch) == 1, "Deep filter retains full ancestor chain iteratively");
            const int wideCount = 12000;
            var wide = Enumerable.Range(0, wideCount).Select(i => Branch("/child-" + i.ToString("D5"), "/root", i)).ToList();
            wide.Add(Branch("/root", "", 0));
            var wideNodes = PlasticClient.BuildBranchHierarchy(wide, "");
            Check(wideNodes.Count == wideCount + 1 && wideNodes[0].ChildCount == wideCount && wideNodes.Skip(1).All(n => n.Depth == 1), "Wide hierarchy counts and orders all direct children");
            Console.WriteLine("PASS: " + assertions + " branch hierarchy assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static PlasticBranch Branch(string name, string parent, long changeset)
    { return new PlasticBranch { Name = name, Parent = parent, Repository = "test@server:8087", HeadChangeset = changeset, Owner = "", CreationDate = "", Comment = "" }; }
    private static string Fingerprint(IEnumerable<PlasticBranch> branches)
    { return String.Join("\n", branches.Select(b => b.Name + "|" + b.Parent + "|" + b.Repository + "|" + b.HeadChangeset + "|" + b.Owner + "|" + b.CreationDate + "|" + b.Comment + "|" + b.IsCurrent)); }
    private static void Check(bool condition, string message) { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (ArgumentException) { assertions++; return; }
        catch (InvalidDataException) { assertions++; return; }
        throw new Exception(message);
    }
}
