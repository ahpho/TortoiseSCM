// GPL-2.0-or-later. Bounded text comparison and conservative manual-merge persistence.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace TortoiseSCM.Core
{
    public enum TextLineEnding { Preserve, CrLf, Lf, Cr }
    public enum TextDiffKind { Equal, Added, Removed, Changed }

    public sealed class TextDocument
    {
        public const int MaximumBytes = 2 * 1024 * 1024;
        public const int MaximumLines = 20000;
        private byte[] originalBytes;
        private Encoding encoding;
        private byte[] preamble;
        private FileIdentity originalIdentity;
        private bool existed;
        private string defaultEnding;
        public string FilePath { get; private set; }
        public string Text { get; private set; }
        public string EditorText { get { return Normalize(Text).Replace("\n", "\r\n"); } }
        public string EncodingName { get; private set; }
        public bool HasBom { get { return preamble.Length != 0; } }
        public bool HasMixedLineEndings { get; private set; }
        public string LineEndingDescription { get; private set; }
        public IList<string> Lines { get; private set; }
        internal string LineEndingSignature { get; private set; }

        public static TextDocument Load(string path)
        {
            path = ValidatePath(path);
            FileIdentity identity;
            byte[] bytes = ReadBytes(path, out identity);
            var document = new TextDocument { FilePath = path, originalBytes = bytes, originalIdentity = identity, existed = true };
            int offset = 0;
            if (Starts(bytes, 0xFF, 0xFE, 0, 0)) { document.encoding = new UTF32Encoding(false, true, true); offset = 4; document.EncodingName = "UTF-32 LE"; }
            else if (Starts(bytes, 0, 0, 0xFE, 0xFF)) { document.encoding = new UTF32Encoding(true, true, true); offset = 4; document.EncodingName = "UTF-32 BE"; }
            else if (Starts(bytes, 0xFF, 0xFE)) { document.encoding = new UnicodeEncoding(false, true, true); offset = 2; document.EncodingName = "UTF-16 LE"; }
            else if (Starts(bytes, 0xFE, 0xFF)) { document.encoding = new UnicodeEncoding(true, true, true); offset = 2; document.EncodingName = "UTF-16 BE"; }
            else { document.encoding = new UTF8Encoding(false, true); offset = Starts(bytes, 0xEF, 0xBB, 0xBF) ? 3 : 0; document.EncodingName = "UTF-8"; }
            document.preamble = bytes.Take(offset).ToArray();
            try { document.SetText(document.encoding.GetString(bytes, offset, bytes.Length - offset)); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Unsupported text encoding. Use valid UTF-8 or BOM-marked UTF-16/UTF-32.", error); }
            return document;
        }

        public static TextDocument CreateResult(string path, TextDocument template)
        {
            if (template == null) throw new ArgumentNullException("template");
            path = ValidatePath(path);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The result path already exists; load it before editing.");
            var result = new TextDocument { FilePath = path, encoding = template.encoding, preamble = (byte[])template.preamble.Clone(),
                EncodingName = template.EncodingName, originalBytes = new byte[0] };
            result.SetText(template.Text);
            return result;
        }

        public void Save(string editorText, TextLineEnding lineEnding, IEnumerable<string> protectedInputPaths)
        {
            if (editorText == null) throw new ArgumentNullException("editorText");
            if (!Enum.IsDefined(typeof(TextLineEnding), lineEnding)) throw new ArgumentOutOfRangeException("lineEnding");
            ValidatePath(FilePath);
            if (FilePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => String.Equals(part, ".plastic", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Plastic workspace metadata cannot be used as a merge result.");
            string normalized = Normalize(editorText);
            ValidateText(normalized);
            byte[] bytes;
            string savedText;
            if (lineEnding == TextLineEnding.Preserve && normalized == Normalize(Text))
            {
                savedText = Text;
                bytes = existed ? originalBytes : Encode(savedText);
            }
            else
            {
                if (lineEnding == TextLineEnding.Preserve && HasMixedLineEndings)
                    throw new InvalidDataException("This file uses mixed line endings. Choose CRLF, LF or CR explicitly before saving edited content.");
                string ending = lineEnding == TextLineEnding.CrLf ? "\r\n" : lineEnding == TextLineEnding.Lf ? "\n" : lineEnding == TextLineEnding.Cr ? "\r" : defaultEnding;
                savedText = normalized.Replace("\n", ending);
                bytes = Encode(savedText);
            }
            if (bytes.Length > MaximumBytes) throw new InvalidDataException("Text files larger than 2 MiB are not supported by the internal editor.");
            CheckDestination(protectedInputPaths);
            if (existed && bytes.SequenceEqual(originalBytes)) return;
            string temporary = Path.Combine(Path.GetDirectoryName(FilePath), Path.GetRandomFileName());
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                // Revalidate immediately before the atomic rename; never truncate the live result.
                // File.Replace has no compare-and-swap primitive: another process replacing the path
                // in this final check/rename interval remains a filesystem-level race.
                CheckDestination(protectedInputPaths);
                if (existed) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath);
                FileIdentity identity;
                byte[] actual = ReadBytes(FilePath, out identity);
                if (!actual.SequenceEqual(bytes)) throw new IOException("The result changed immediately after saving. Reload it before continuing.");
                originalBytes = actual; originalIdentity = identity; existed = true;
                SetText(savedText);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private byte[] Encode(string text)
        {
            try
            {
                int count = encoding.GetByteCount(text);
                if (count > MaximumBytes - preamble.Length) throw new InvalidDataException("Text files larger than 2 MiB are not supported by the internal editor.");
                byte[] bytes = new byte[preamble.Length + count];
                Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
                encoding.GetBytes(text, 0, text.Length, bytes, preamble.Length);
                return bytes;
            }
            catch (EncoderFallbackException error) { throw new InvalidDataException("The edited text contains an invalid Unicode sequence.", error); }
        }

        private void CheckDestination(IEnumerable<string> protectedInputPaths)
        {
            ValidatePath(FilePath);
            FileIdentity current = null;
            if (File.Exists(FilePath))
            {
                if (!existed) throw new IOException("The result was created by another process. Reload it before saving.");
                byte[] bytes = ReadBytes(FilePath, out current);
                if (!current.SameFile(originalIdentity) || !bytes.SequenceEqual(originalBytes))
                    throw new IOException("The result changed on disk after it was loaded. Reload it before saving.");
                if (current.Links != 1) throw new IOException("Saving a hard-linked result is not supported.");
                if ((File.GetAttributes(FilePath) & FileAttributes.ReadOnly) != 0) throw new IOException("The result file is read-only.");
            }
            else if (existed) throw new IOException("The result was removed after it was loaded.");
            if (protectedInputPaths == null) return;
            foreach (string input in protectedInputPaths)
            {
                string full = ValidatePath(input);
                if (String.Equals(full, FilePath, StringComparison.OrdinalIgnoreCase)) throw new IOException("The result must be a separate file from all comparison inputs.");
                if (current != null && File.Exists(full))
                {
                    FileIdentity identity;
                    ReadBytes(full, out identity);
                    if (current.SameFile(identity)) throw new IOException("The result aliases a comparison input.");
                }
            }
        }

        private void SetText(string text)
        {
            ValidateText(text);
            Text = text;
            Lines = Array.AsReadOnly(Normalize(text).Split(new[] { '\n' }));
            int crlf = 0, lf = 0, cr = 0;
            var signature = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\r') { if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; signature.Append('W'); } else { cr++; signature.Append('M'); } }
                else if (text[i] == '\n') { lf++; signature.Append('U'); }
            }
            LineEndingSignature = signature.ToString();
            HasMixedLineEndings = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0) > 1;
            defaultEnding = crlf > 0 ? "\r\n" : lf > 0 ? "\n" : cr > 0 ? "\r" : "\r\n";
            LineEndingDescription = HasMixedLineEndings ? "Mixed" : crlf > 0 ? "CRLF" : lf > 0 ? "LF" : cr > 0 ? "CR" : "None";
        }

        private static void ValidateText(string text)
        {
            if (text.Length > MaximumBytes) throw new InvalidDataException("Text files larger than 2 MiB are not supported by the internal editor.");
            int lines = 1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\r') { lines++; if (i + 1 < text.Length && text[i + 1] == '\n') i++; }
                else if (c == '\n') lines++;
                else if (c < 32 && c != '\t' || c == 0x7F) throw new InvalidDataException("Binary files and unsupported control characters cannot be opened in the internal text editor.");
                if (lines > MaximumLines) throw new InvalidDataException("Text files with more than 20,000 lines are not supported by the internal editor.");
            }
        }
        private static string Normalize(string value) { return value.Replace("\r\n", "\n").Replace('\r', '\n'); }
        private static bool Starts(byte[] bytes, params byte[] prefix) { return bytes.Length >= prefix.Length && prefix.Select((value, index) => bytes[index] == value).All(value => value); }

        private static string ValidatePath(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A text file path is required.", "path");
            if (path.IndexOf(':', Math.Min(2, path.Length)) >= 0) throw new IOException("Alternate data streams are not supported.");
            if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
                throw new IOException("Device paths are not supported by the internal editor.");
            string full = Path.GetFullPath(path);
            if (full.IndexOf(':', 2) >= 0) throw new IOException("Alternate data streams are not supported.");
            foreach (string part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (part == "." || part == ".." || part.Length == 0) continue;
                if (part.EndsWith(" ", StringComparison.Ordinal) || part.EndsWith(".", StringComparison.Ordinal))
                    throw new IOException("Trailing spaces and dots in file names are not supported.");
                string name = part.Split('.')[0].ToUpperInvariant();
                if (name == "CON" || name == "PRN" || name == "AUX" || name == "NUL" || name == "CONIN$" || name == "CONOUT$" ||
                    name.Length == 4 && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) &&
                    (name[3] >= '1' && name[3] <= '9' || name[3] == '\u00b9' || name[3] == '\u00b2' || name[3] == '\u00b3'))
                    throw new IOException("Reserved device file names are not supported.");
            }
            string current = full;
            while (!String.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Symbolic links, junctions and reparse-point paths are not supported by the internal editor.");
                current = Path.GetDirectoryName(current);
            }
            return full;
        }
        private static byte[] ReadBytes(string path, out FileIdentity identity)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumBytes) throw new InvalidDataException("Text files larger than 2 MiB are not supported by the internal editor.");
                identity = FileIdentity.Read(stream.SafeFileHandle);
                byte[] bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length) { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new EndOfStreamException(); offset += count; }
                return bytes;
            }
        }
        private sealed class FileIdentity
        {
            public uint Volume, High, Low, Links;
            public bool SameFile(FileIdentity other) { return other != null && Volume == other.Volume && High == other.High && Low == other.Low; }
            public static FileIdentity Read(SafeFileHandle handle)
            {
                NativeFileInformation info;
                if (!GetFileInformationByHandle(handle, out info)) throw new IOException("Cannot verify text file identity.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
                return new FileIdentity { Volume = info.VolumeSerialNumber, High = info.FileIndexHigh, Low = info.FileIndexLow, Links = info.NumberOfLinks };
            }
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out NativeFileInformation information);
    }

    public sealed class TextDiffRow
    {
        public int LeftLineNumber { get; internal set; }
        public int RightLineNumber { get; internal set; }
        public string LeftText { get; internal set; }
        public string RightText { get; internal set; }
        public TextDiffKind Kind { get; internal set; }
    }
    public sealed class TextDiffHunk
    {
        public int StartRow { get; internal set; }
        public int RowCount { get; internal set; }
    }
    public sealed class TextComparisonResult
    {
        public IList<TextDiffRow> Rows { get; internal set; }
        public IList<TextDiffHunk> Hunks { get; internal set; }
        public bool IsApproximate { get; internal set; }
        public bool HasLineEndingChanges { get; internal set; }
        public bool HasEncodingChanges { get; internal set; }
    }
    public static class TextComparison
    {
        private const long MaximumCells = 2000000;
        public static TextComparisonResult Compare(TextDocument left, TextDocument right) { return Compare(left, right, CancellationToken.None); }
        public static TextComparisonResult Compare(TextDocument left, TextDocument right, CancellationToken cancellationToken)
        {
            if (left == null || right == null) throw new ArgumentNullException(left == null ? "left" : "right");
            cancellationToken.ThrowIfCancellationRequested();
            IList<string> a = left.Lines, b = right.Lines;
            var rows = new List<TextDiffRow>();
            int prefix = 0;
            while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix]) { Add(rows, a, b, prefix, prefix); prefix++; }
            int ae = a.Count, be = b.Count;
            while (ae > prefix && be > prefix && a[ae - 1] == b[be - 1]) { ae--; be--; }
            int n = ae - prefix, m = be - prefix;
            bool approximate = (long)(n + 1) * (m + 1) > MaximumCells;
            if (approximate) AddBlock(rows, a, b, prefix, ae, prefix, be);
            else
            {
                // Intern each line once so repeated long lines cannot multiply string-comparison
                // work by the LCS cell count. Dictionary work is bounded by the input byte limits.
                var identifiers = new Dictionary<string, int>(StringComparer.Ordinal);
                int[] aIds = Identify(a, identifiers), bIds = Identify(b, identifiers);
                var lcs = new int[n + 1, m + 1];
                for (int i = n - 1; i >= 0; i--)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    for (int j = m - 1; j >= 0; j--) lcs[i, j] = aIds[prefix + i] == bIds[prefix + j] ? 1 + lcs[i + 1, j + 1] : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
                }
                int ai = 0, bi = 0, startA = 0, startB = 0;
                while (ai < n && bi < m)
                {
                    if (a[prefix + ai] == b[prefix + bi])
                    {
                        AddBlock(rows, a, b, prefix + startA, prefix + ai, prefix + startB, prefix + bi);
                        Add(rows, a, b, prefix + ai, prefix + bi); ai++; bi++; startA = ai; startB = bi;
                    }
                    else if (lcs[ai + 1, bi] >= lcs[ai, bi + 1]) ai++;
                    else bi++;
                }
                AddBlock(rows, a, b, prefix + startA, ae, prefix + startB, be);
            }
            while (ae < a.Count) { Add(rows, a, b, ae, be); ae++; be++; }
            var hunks = new List<TextDiffHunk>();
            for (int i = 0; i < rows.Count; i++)
            {
                if (rows[i].Kind == TextDiffKind.Equal) continue;
                int start = i;
                while (i + 1 < rows.Count && rows[i + 1].Kind != TextDiffKind.Equal) i++;
                hunks.Add(new TextDiffHunk { StartRow = start, RowCount = i - start + 1 });
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new TextComparisonResult { Rows = rows.AsReadOnly(), Hunks = hunks.AsReadOnly(), IsApproximate = approximate,
                HasLineEndingChanges = left.LineEndingSignature != right.LineEndingSignature,
                HasEncodingChanges = left.EncodingName != right.EncodingName || left.HasBom != right.HasBom };
        }
        private static int[] Identify(IList<string> lines, Dictionary<string, int> identifiers)
        {
            var result = new int[lines.Count];
            for (int i = 0; i < lines.Count; i++)
            {
                int id;
                if (!identifiers.TryGetValue(lines[i], out id)) { id = identifiers.Count; identifiers.Add(lines[i], id); }
                result[i] = id;
            }
            return result;
        }
        private static void AddBlock(List<TextDiffRow> rows, IList<string> a, IList<string> b, int ai, int ae, int bi, int be)
        { while (ai < ae || bi < be) { Add(rows, a, b, ai < ae ? ai : -1, bi < be ? bi : -1); if (ai < ae) ai++; if (bi < be) bi++; } }
        private static void Add(List<TextDiffRow> rows, IList<string> a, IList<string> b, int ai, int bi)
        {
            rows.Add(new TextDiffRow { LeftLineNumber = ai + 1, RightLineNumber = bi + 1,
                LeftText = ai < 0 ? "" : a[ai], RightText = bi < 0 ? "" : b[bi],
                Kind = ai < 0 ? TextDiffKind.Added : bi < 0 ? TextDiffKind.Removed : a[ai] == b[bi] ? TextDiffKind.Equal : TextDiffKind.Changed });
        }
    }
}
