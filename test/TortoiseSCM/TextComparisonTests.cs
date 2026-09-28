// GPL-2.0-or-later. Pure file-backed tests for the bounded internal text engine.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TortoiseSCM.Core;

internal static class TextComparisonTests
{
    private static string root;
    private static int assertions;
    private static int Main()
    {
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-text-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            EncodingsAndLineEndings(); Rejections(); Diff(); SafeSave(); ReparseSave();
            Console.WriteLine("PASS: " + assertions + " text comparison assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static string Write(string name, string text, Encoding encoding)
    { string path = Path.Combine(root, name); File.WriteAllText(path, text, encoding); return path; }
    private static TextDocument Doc(string name, string text) { return TextDocument.Load(Write(name, text, new UTF8Encoding(false, true))); }
    private static void EncodingsAndLineEndings()
    {
        Encoding[] encodings = { new UTF8Encoding(false, true), new UTF8Encoding(true, true), new UnicodeEncoding(false, true, true),
            new UnicodeEncoding(true, true, true), new UTF32Encoding(false, true, true), new UTF32Encoding(true, true, true) };
        int index = 0;
        foreach (Encoding encoding in encodings)
        {
            foreach (string ending in new[] { "\r\n", "\n", "\r" })
            {
                string text = "中文 😀" + ending + "last";
                string path = Write("encoding" + index++, text, encoding);
                byte[] before = File.ReadAllBytes(path);
                TextDocument document = TextDocument.Load(path);
                Check(document.Text == text && document.EditorText == "中文 😀\r\nlast", "Decode Unicode and normalize editor view");
                Check(document.HasBom == (encoding.GetPreamble().Length > 0), "Detect BOM");
                document.Save(document.EditorText, TextLineEnding.Preserve, null);
                Check(File.ReadAllBytes(path).SequenceEqual(before), "Unchanged content round-trips exact bytes");
                document.Save(document.EditorText + "!", TextLineEnding.Preserve, null);
                Check(File.ReadAllBytes(path).SequenceEqual(encoding.GetPreamble().Concat(encoding.GetBytes(text + "!"))), "Edit preserves encoding, BOM, EOL and missing final newline");
            }
        }
        TextDocument mixed = Doc("mixed", "a\r\nb\nc\r");
        byte[] original = File.ReadAllBytes(mixed.FilePath);
        Check(mixed.HasMixedLineEndings && mixed.LineEndingDescription == "Mixed", "Identify mixed EOL");
        mixed.Save(mixed.EditorText, TextLineEnding.Preserve, null);
        Check(File.ReadAllBytes(mixed.FilePath).SequenceEqual(original), "Unchanged mixed EOL preserves exact bytes");
        Reject<InvalidDataException>(() => mixed.Save(mixed.EditorText + "x", TextLineEnding.Preserve, null), "Edited mixed EOL requires explicit selection");
        mixed.Save(mixed.EditorText + "x", TextLineEnding.Lf, null);
        Check(File.ReadAllText(mixed.FilePath) == "a\nb\nc\nx" && !mixed.HasMixedLineEndings, "Explicit LF normalizes mixed content");
        TextDocument empty = Doc("empty", "");
        Check(empty.Text == "" && empty.Lines.Count == 1 && empty.LineEndingDescription == "None", "Empty document is editable");
        empty.Save("", TextLineEnding.Preserve, null);
        Check(new FileInfo(empty.FilePath).Length == 0, "Empty save stays empty");
        empty.Save("a\nb\n", TextLineEnding.Cr, null);
        Check(File.ReadAllText(empty.FilePath) == "a\rb\r", "Explicit CR preserves final newline");
        TextDocument template = Doc("template", "a\nb\n");
        TextDocument result = TextDocument.CreateResult(Path.Combine(root, "new-result"), template);
        result.Save(result.EditorText, TextLineEnding.Preserve, new[] { template.FilePath });
        Check(File.ReadAllBytes(result.FilePath).SequenceEqual(File.ReadAllBytes(template.FilePath)), "New result inherits source bytes and encoding");
    }
    private static void Rejections()
    {
        foreach (byte[] bytes in new[] { new byte[] { 0xC0, 0xAF }, new byte[] { 0xFF, 0xFE, 65 }, new byte[] { 0, 0, 0xFE, 0xFF, 0, 0, 0 } })
        {
            string path = Path.Combine(root, "invalid"); File.WriteAllBytes(path, bytes);
            Reject<InvalidDataException>(() => TextDocument.Load(path), "Malformed encoding rejected");
        }
        Reject<InvalidDataException>(() => Doc("binary", "abc\0def"), "NUL binary rejected");
        Reject<InvalidDataException>(() => Doc("control", "abc\u0001def"), "Control binary rejected");
        string large = Path.Combine(root, "large");
        using (var file = File.Create(large)) file.SetLength(TextDocument.MaximumBytes + 1);
        Reject<InvalidDataException>(() => TextDocument.Load(large), "Oversized file rejected before decoding");
        Reject<InvalidDataException>(() => Doc("manylines", new string('\n', TextDocument.MaximumLines)), "Line count limit enforced");
        TextDocument edited = Doc("invalid-edited", "original");
        Reject<InvalidDataException>(() => edited.Save("\uD800", TextLineEnding.Preserve, null), "Invalid surrogate edit rejected");
        Check(File.ReadAllText(edited.FilePath) == "original", "Rejected edit preserves original");
        Reject<IOException>(() => TextDocument.Load(edited.FilePath + ":stream"), "Alternate data streams rejected");
        Reject<IOException>(() => TextDocument.Load(Path.Combine(root, "CON.txt")), "Reserved device names rejected");
        Reject<IOException>(() => TextDocument.Load(edited.FilePath + "."), "Trailing-dot aliases rejected");
    }
    private static void Diff()
    {
        TextDocument left = Doc("diff-left", "one\ntwo\nthree\nlast");
        TextDocument right = Doc("diff-right", "one\ninserted\ntwo\nTHREE\nlast");
        TextComparisonResult result = TextComparison.Compare(left, right);
        Check(result.Rows.Count == 5 && result.Rows[1].Kind == TextDiffKind.Added && result.Rows[1].LeftLineNumber == 0, "Insertion aligns subsequent equal lines");
        Check(result.Rows[2].Kind == TextDiffKind.Equal && result.Rows[2].LeftLineNumber == 2 && result.Rows[2].RightLineNumber == 3, "Source line numbers survive alignment");
        Check(result.Rows[3].Kind == TextDiffKind.Changed && result.Hunks.Count == 2 && result.Hunks[1].StartRow == 3, "Replacement and hunks identified");
        Check(TextComparison.Compare(right, left).Rows[1].Kind == TextDiffKind.Removed, "Deletion alignment");
        Check(TextComparison.Compare(left, left).Hunks.Count == 0, "Identical files have no hunks");
        TextComparisonResult mixed = TextComparison.Compare(Doc("mix-a", "a\r\nb\nc"), Doc("mix-b", "a\nb\r\nc"));
        Check(mixed.Hunks.Count == 0 && mixed.HasLineEndingChanges, "Mixed EOL order changes remain visible even with identical normalized text");
        TextComparisonResult bom = TextComparison.Compare(Doc("bom-a", "same"), TextDocument.Load(Write("bom-b", "same", new UTF8Encoding(true))));
        Check(bom.Hunks.Count == 0 && bom.HasEncodingChanges && !bom.HasLineEndingChanges, "BOM-only changes visible");
        Check(TextComparison.Compare(Doc("eof-a", "a"), Doc("eof-b", "a\n")).Hunks.Count == 1, "Final newline difference remains visible");
        var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Reject<OperationCanceledException>(() => TextComparison.Compare(left, right, cancelled.Token), "Cancellation honored");
        TextDocument manyA = Doc("many-a", String.Join("\n", Enumerable.Range(0, 10000).Select(i => "a" + i)));
        TextDocument manyB = Doc("many-b", String.Join("\n", Enumerable.Range(0, 10000).Select(i => "b" + i)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TextComparisonResult bounded = TextComparison.Compare(manyA, manyB);
        Check(bounded.IsApproximate && bounded.Rows.Count == 10000 && bounded.Hunks.Count == 1 && watch.Elapsed < TimeSpan.FromSeconds(5), "Unrelated large files use bounded coarse comparison");
        Check(bounded.Rows.Select(row => row.LeftLineNumber).SequenceEqual(Enumerable.Range(1, 10000)), "Fallback never drops source lines");
    }
    private static void SafeSave()
    {
        TextDocument changed = Doc("external", "original"); File.WriteAllText(changed.FilePath, "external");
        Reject<IOException>(() => changed.Save("mine", TextLineEnding.Preserve, null), "External modifications block save");
        Check(File.ReadAllText(changed.FilePath) == "external", "External edit is untouched");
        TextDocument removed = Doc("removed", "original"); File.Delete(removed.FilePath);
        Reject<IOException>(() => removed.Save("mine", TextLineEnding.Preserve, null), "Removed result blocks save");
        TextDocument alias = Doc("alias", "input");
        Reject<IOException>(() => alias.Save("mine", TextLineEnding.Preserve, new[] { alias.FilePath.ToUpperInvariant() }), "Input aliases block save");
        TextDocument hard = Doc("hard-target", "input");
        string link = Path.Combine(root, "hard-link");
        Check(CreateHardLink(link, hard.FilePath, IntPtr.Zero), "Create hardlink fixture");
        Reject<IOException>(() => hard.Save("mine", TextLineEnding.Preserve, null), "Hardlinked output rejected");
        Check(File.ReadAllText(link) == "input", "Hardlink input remains untouched");
        TextDocument newlyCreated = TextDocument.CreateResult(Path.Combine(root, "race"), alias);
        File.WriteAllText(newlyCreated.FilePath, "other");
        Reject<IOException>(() => newlyCreated.Save("mine", TextLineEnding.Preserve, null), "New destination appeared externally");
        TextDocument readOnly = Doc("readonly", "input"); File.SetAttributes(readOnly.FilePath, FileAttributes.ReadOnly);
        try { Reject<IOException>(() => readOnly.Save("mine", TextLineEnding.Preserve, null), "Read-only output rejected"); }
        finally { File.SetAttributes(readOnly.FilePath, FileAttributes.Normal); }
        TextDocument stable = Doc("atomic", "before");
        string[] filesBeforeSave = Directory.GetFiles(root).OrderBy(path => path).ToArray();
        stable.Save("after", TextLineEnding.Preserve, null); stable.Save("again", TextLineEnding.Preserve, null);
        Check(File.ReadAllText(stable.FilePath) == "again", "Repeated atomic saves refresh original snapshot");
        Check(Directory.GetFiles(root).OrderBy(path => path).SequenceEqual(filesBeforeSave), "Atomic saves leave no temporary files");
        string longDirectory = Path.Combine(root, new string('x', 225 - root.Length - 1)); Directory.CreateDirectory(longDirectory);
        string longResult = Path.Combine(longDirectory, "result.txt"); File.WriteAllText(longResult, "before", new UTF8Encoding(false));
        TextDocument longDocument = TextDocument.Load(longResult); longDocument.Save("after", TextLineEnding.Preserve, null);
        Check(File.ReadAllText(longResult) == "after" && Directory.GetFiles(longDirectory).Length == 1, "Atomic staging works in long valid Windows paths");
        string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
        TextDocument metadataDoc = TextDocument.CreateResult(Path.Combine(metadata, "selector"), stable);
        Reject<IOException>(() => metadataDoc.Save("mine", TextLineEnding.Preserve, null), "Saving workspace metadata rejected");
    }
    private static void ReparseSave()
    {
        string originalDirectory = Path.Combine(root, "swappable");
        string movedDirectory = Path.Combine(root, "moved");
        Directory.CreateDirectory(originalDirectory);
        string resultPath = Path.Combine(originalDirectory, "result.txt");
        File.WriteAllText(resultPath, "original", new UTF8Encoding(false));
        TextDocument document = TextDocument.Load(resultPath);
        Directory.Move(originalDirectory, movedDirectory);
        var start = new System.Diagnostics.ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            Arguments = "/c mklink /J \"" + originalDirectory + "\" \"" + movedDirectory + "\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using (var process = System.Diagnostics.Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(); Check(process.ExitCode == 0, "Create junction fixture: " + output);
        }
        try
        {
            Reject<IOException>(() => document.Save("mine", TextLineEnding.Preserve, null), "Directory changed to junction blocks save");
            Reject<IOException>(() => TextDocument.Load(resultPath), "Junction input rejected");
            Check(File.ReadAllText(Path.Combine(movedDirectory, "result.txt")) == "original", "Junction target preserved");
        }
        finally { Directory.Delete(originalDirectory); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
}
