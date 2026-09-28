// GPL-2.0-or-later. Three-way merge invariants and conservative conflict boundaries.
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using TortoiseSCM.Core;

internal static class TextMergePlanTests
{
    private static string root;
    private static int assertions;
    private static int files;
    private static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-merge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Examples(); Boundaries(); MetadataAndLimits(); Projections();
            Console.WriteLine("PASS: " + assertions + " text merge assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static TextDocument Doc(string text) { return Doc(text, new UTF8Encoding(false, true)); }
    private static TextDocument Doc(string text, Encoding encoding)
    {
        string path = Path.Combine(root, "input-" + files++);
        File.WriteAllText(path, text, encoding); return TextDocument.Load(path);
    }
    private static TextMergePlan Plan(string baseline, string local, string remote)
    { return TextMergePlan.Create(Doc(baseline), Doc(local), Doc(remote)); }
    private static void Check(bool condition, string message)
    { assertions++; if (!condition) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; }
        throw new Exception(message);
    }
    private static void Examples()
    {
        TextMergePlan plan = Plan("a\nb\nc\nd\n", "A\nb\nc\nd\n", "a\nb\nc\nD\n");
        Check(plan.SupportsAutomatic && plan.UnresolvedCount == 0 && plan.Blocks.Count == 2, "Independent edits are automatic");
        Check(plan.ResultText == "A\r\nb\r\nc\r\nD\r\n", "Independent edits combined with final newline");
        Check(plan.Blocks[0].Kind == TextMergeKind.Local && plan.Blocks[1].Kind == TextMergeKind.Remote, "Origins retained");
        Check(plan.Blocks[0].ResultStart == 0 && plan.Blocks[0].ResultLength == 3 && plan.Blocks[1].ResultStart == 9, "Exact initial result spans");
        plan.Choose(0, TextMergeChoice.Base);
        Check(plan.ResultText == "a\r\nb\r\nc\r\nD\r\n" && plan.Blocks[0].Status == TextMergeStatus.Reviewed, "Explicit base choice reviewed");
        plan = Plan("a\nb\nc", "a\nshared\nc", "a\nshared\nc");
        Check(plan.UnresolvedCount == 0 && plan.Blocks.Single().Kind == TextMergeKind.Identical && plan.ResultText == "a\r\nshared\r\nc", "Identical overlapping replacement");
        plan = Plan("a\nb\nc", "a\nlocal\nc", "a\nremote\nc");
        Check(plan.UnresolvedCount == 1 && plan.ResultText == "a\r\nlocal\r\nc", "Conflict retains local draft");
        Check(plan.Blocks[0].BaseStart == 1 && plan.Blocks[0].BaseCount == 1 && plan.Blocks[0].BaseText == "b", "Conflict base range");
        plan.MarkReviewed(0);
        Check(plan.UnresolvedCount == 0 && plan.Blocks[0].Status == TextMergeStatus.Reviewed, "Review accepts local draft");
        plan.Choose(0, TextMergeChoice.Remote);
        Check(plan.ResultText == "a\r\nremote\r\nc", "Remote replaces selected conflict");
        Check(plan.ResultText.Substring(plan.Blocks[0].ResultStart, plan.Blocks[0].ResultLength) == "remote\r\n", "Span includes exact selected lines and following separator");
        plan = Plan("a\nb\nc\nd", "a\nL\nextra\nc\nd", "a\nb\nc\nR");
        int oldStart = plan.Blocks[1].ResultStart;
        plan.Choose(0, TextMergeChoice.Base);
        Check(plan.Blocks[1].ResultStart == oldStart - 7 && plan.ResultText == "a\r\nb\r\nc\r\nR", "Downstream spans update after line count changes");
        Reject<ArgumentOutOfRangeException>(() => plan.Choose(-1, TextMergeChoice.Base), "Invalid block rejected");
        Reject<ArgumentOutOfRangeException>(() => plan.Choose(0, (TextMergeChoice)99), "Invalid choice rejected");
        Reject<ArgumentOutOfRangeException>(() => plan.MarkReviewed(99), "Invalid review index rejected");
    }
    private static void Boundaries()
    {
        TextMergePlan plan = Plan("a\nb\nc", "A\nb\nc", "a\nB\nc");
        Check(plan.Blocks.Count == 2 && plan.UnresolvedCount == 0 && plan.ResultText == "A\r\nB\r\nc", "Adjacent replacements independent");
        plan = Plan("a\nb", "a\nleft\nb", "a\nright\nb");
        Check(plan.UnresolvedCount == 1 && plan.Blocks[0].BaseCount == 0, "Differing same-position insertions conflict");
        plan.Choose(0, TextMergeChoice.Remote);
        Check(plan.ResultText == "a\r\nright\r\nb", "Insertion choice includes separator");
        plan.Choose(0, TextMergeChoice.Base);
        Check(plan.ResultText == "a\r\nb" && plan.Blocks[0].ResultLength == 0, "Choosing base removes insertion");
        plan = Plan("a\nb", "a\nsame\nb", "a\nsame\nb");
        Check(plan.UnresolvedCount == 0 && plan.ResultText == "a\r\nsame\r\nb", "Identical insertions emitted once");
        plan = Plan("a\nb\nc", "a\nc", "a\nB\nc");
        Check(plan.UnresolvedCount == 1 && plan.ResultText == "a\r\nc", "Delete versus replacement conflict");
        plan = Plan("a\nb\nc", "a\nc", "a\nc");
        Check(plan.UnresolvedCount == 0 && plan.ResultText == "a\r\nc", "Identical deletion automatic");
        plan = Plan("a\nb\nc", "a\ninsert\nb\nc", "a\nB\nc");
        Check(plan.Blocks.Count == 1 && plan.UnresolvedCount == 1, "Insertion at replacement start conservatively grouped");
        plan = Plan("a\nb\nc", "a\nb\ninsert\nc", "a\nB\nc");
        Check(plan.Blocks.Count == 1 && plan.UnresolvedCount == 1, "Insertion at replacement end conservatively grouped");
        foreach (string text in new[] { "", "\n", "a", "a\n", "a\n\n", "\na", "a\rb\r", "a\r\nb\r\n" })
        {
            TextDocument document = Doc(text);
            plan = TextMergePlan.Create(document, document, document);
            Check(plan.Blocks.Count == 0 && plan.ResultText == document.EditorText, "Identical empty/newline documents exact");
            TextDocument empty = Doc("");
            plan = TextMergePlan.Create(empty, empty, document);
            Check(plan.ResultText == document.EditorText, "Remote empty-to-content preserves EOF");
            plan = TextMergePlan.Create(document, empty, document);
            Check(plan.ResultText == "", "Full deletion produces empty document");
        }
        plan = Plan("a", "a\n", "a");
        Check(plan.ResultText == "a\r\n", "Final newline insertion retained");
        plan.Choose(0, TextMergeChoice.Base);
        Check(plan.ResultText == "a", "Final newline insertion reversible");
        plan = Plan("a\n", "a", "a\n");
        Check(plan.ResultText == "a", "Final newline deletion retained");
        plan.Choose(0, TextMergeChoice.Base);
        Check(plan.ResultText == "a\r\n", "Final newline deletion reversible");
    }
    private static void MetadataAndLimits()
    {
        TextMergePlan plan = Plan("a\nb\n", "a\r\nb\r\n", "a\nb\n");
        Check(!plan.SupportsAutomatic && plan.HasMetadataDifferences && plan.UnresolvedCount == 1, "EOL-only edits never silently resolved");
        TextDocument baseline = Doc("a");
        plan = TextMergePlan.Create(baseline, Doc("a", new UTF8Encoding(true)), baseline);
        Check(!plan.SupportsAutomatic && plan.UnresolvedCount == 1 && plan.HasMetadataDifferences, "BOM-only edits need review");
        plan = TextMergePlan.Create(baseline, Doc("a", new UnicodeEncoding(false, true)), baseline);
        Check(!plan.SupportsAutomatic && plan.HasMetadataDifferences, "Encoding-only edits need review");
        string bigBase = String.Join("\n", Enumerable.Range(0, 1500).Select(i => "base" + i));
        string bigLocal = String.Join("\n", Enumerable.Range(0, 1500).Select(i => "local" + i));
        plan = Plan(bigBase, bigLocal, bigBase);
        Check(!plan.SupportsAutomatic && plan.Blocks.Count == 1 && plan.UnresolvedCount == 1, "Approximate alignment falls back to one conflict");
        Check(plan.ResultText == bigLocal.Replace("\n", "\r\n"), "Fallback draft remains complete local text");
        plan.Choose(0, TextMergeChoice.Remote);
        Check(plan.ResultText == bigBase.Replace("\n", "\r\n") && plan.UnresolvedCount == 0, "Fallback supports explicit whole-file choice");
        var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Reject<OperationCanceledException>(() => TextMergePlan.Create(baseline, baseline, baseline, cancellation.Token), "Cancelled planning aborts");
        Reject<ArgumentNullException>(() => TextMergePlan.Create(null, baseline, baseline), "Null base rejected");
        Reject<ArgumentNullException>(() => TextMergePlan.Create(baseline, null, baseline), "Null local rejected");
        Reject<ArgumentNullException>(() => TextMergePlan.Create(baseline, baseline, null), "Null remote rejected");
    }
    private static void Projections()
    {
        var random = new Random(8317);
        for (int iteration = 0; iteration < 250; iteration++)
        {
            TextDocument baseline = Doc(RandomText(random)), local = Doc(RandomText(random)), remote = Doc(RandomText(random));
            Check(TextMergePlan.Create(baseline, baseline, remote).ResultText == remote.EditorText, "Identity local preserves arbitrary remote");
            Check(TextMergePlan.Create(baseline, local, baseline).ResultText == local.EditorText, "Identity remote preserves arbitrary local");
            TextMergePlan identical = TextMergePlan.Create(baseline, local, local);
            Check(identical.ResultText == local.EditorText && identical.UnresolvedCount == 0, "Equal changes merge exactly");
            TextMergePlan plan = TextMergePlan.Create(baseline, local, remote);
            foreach (TextMergeBlock block in plan.Blocks)
            {
                Check(block.BaseText == String.Join("\r\n", baseline.Lines.Skip(block.BaseStart).Take(block.BaseCount)), "Base coordinates map exact lines");
                Check(block.LocalText == String.Join("\r\n", local.Lines.Skip(block.LocalStart).Take(block.LocalCount)), "Local coordinates map exact lines");
                Check(block.RemoteText == String.Join("\r\n", remote.Lines.Skip(block.RemoteStart).Take(block.RemoteCount)), "Remote coordinates map exact lines");
            }
            foreach (TextMergeChoice choice in new[] { TextMergeChoice.Base, TextMergeChoice.Local, TextMergeChoice.Remote })
            {
                for (int i = 0; i < plan.Blocks.Count; i++) plan.Choose(i, choice);
                string expected = choice == TextMergeChoice.Base ? baseline.EditorText : choice == TextMergeChoice.Local ? local.EditorText : remote.EditorText;
                Check(plan.ResultText == expected, "All-block projection reconstructs selected input exactly");
                Check(plan.UnresolvedCount == 0, "All choices resolve every block");
                int previousEnd = 0;
                foreach (TextMergeBlock block in plan.Blocks)
                {
                    Check(block.ResultStart >= previousEnd && block.ResultStart + block.ResultLength <= plan.ResultText.Length, "Result spans ordered and bounded");
                    string selected = choice == TextMergeChoice.Base ? block.BaseText : choice == TextMergeChoice.Local ? block.LocalText : block.RemoteText;
                    string actual = plan.ResultText.Substring(block.ResultStart, block.ResultLength);
                    Check(actual == selected || actual == selected + "\r\n", "Result spans match selected text and separators");
                    previousEnd = block.ResultStart + block.ResultLength;
                }
            }
        }
    }
    private static string RandomText(Random random)
    {
        return String.Join("\n", Enumerable.Range(0, random.Next(0, 10)).Select(i => new[] { "", "a", "b", "repeat", "repeat", "longer text" }[random.Next(6)]));
    }
}
