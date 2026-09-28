// GPL-2.0-or-later. Conservative, bounded three-way text merge planning.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TortoiseSCM.Core
{
    public enum TextMergeKind { Local, Remote, Identical, Conflict }
    public enum TextMergeStatus { Automatic, Unresolved, Reviewed }
    public enum TextMergeChoice { Base, Local, Remote }

    public sealed class TextMergeBlock
    {
        public int BaseStart { get; internal set; }
        public int BaseCount { get { return BaseLines.Length; } }
        public int LocalStart { get; internal set; }
        public int LocalCount { get { return LocalLines.Length; } }
        public int RemoteStart { get; internal set; }
        public int RemoteCount { get { return RemoteLines.Length; } }
        public string BaseText { get { return String.Join("\r\n", BaseLines); } }
        public string LocalText { get { return String.Join("\r\n", LocalLines); } }
        public string RemoteText { get { return String.Join("\r\n", RemoteLines); } }
        public TextMergeKind Kind { get; internal set; }
        public TextMergeStatus Status { get; internal set; }
        public TextMergeChoice Choice { get; internal set; }
        public int ResultStart { get; internal set; }
        public int ResultLength { get; internal set; }
        internal string[] BaseLines, LocalLines, RemoteLines;
        internal string[] SelectedLines
        {
            get { return Choice == TextMergeChoice.Base ? BaseLines : Choice == TextMergeChoice.Local ? LocalLines : RemoteLines; }
        }
    }

    public sealed class TextMergePlan
    {
        private string[] baseLines;
        public IList<TextMergeBlock> Blocks { get; private set; }
        public string ResultText { get; private set; }
        public bool SupportsAutomatic { get; private set; }
        public bool HasMetadataDifferences { get; private set; }
        public int UnresolvedCount { get { return Blocks.Count(block => block.Status == TextMergeStatus.Unresolved); } }

        public static TextMergePlan Create(TextDocument baseline, TextDocument local, TextDocument remote)
        { return Create(baseline, local, remote, CancellationToken.None); }

        public static TextMergePlan Create(TextDocument baseline, TextDocument local, TextDocument remote, CancellationToken cancellationToken)
        {
            if (baseline == null || local == null || remote == null)
                throw new ArgumentNullException(baseline == null ? "baseline" : local == null ? "local" : "remote");
            cancellationToken.ThrowIfCancellationRequested();
            var left = TextComparison.Compare(baseline, local, cancellationToken);
            var right = TextComparison.Compare(baseline, remote, cancellationToken);
            var plan = new TextMergePlan {
                baseLines = baseline.Lines.ToArray(),
                HasMetadataDifferences = left.HasEncodingChanges || right.HasEncodingChanges ||
                    left.HasLineEndingChanges || right.HasLineEndingChanges,
                SupportsAutomatic = !left.IsApproximate && !right.IsApproximate &&
                    !MetadataOnlyChange(baseline, local) && !MetadataOnlyChange(baseline, remote)
            };
            var blocks = new List<TextMergeBlock>();
            if (!plan.SupportsAutomatic)
            {
                // An approximate LCS is useful for display, but cannot establish independent edits.
                // Encoding and line endings are saved by TextDocument, never silently merged here.
                blocks.Add(new TextMergeBlock { BaseLines = plan.baseLines, LocalLines = local.Lines.ToArray(),
                    RemoteLines = remote.Lines.ToArray(), Kind = TextMergeKind.Conflict,
                    Status = TextMergeStatus.Unresolved, Choice = TextMergeChoice.Local });
            }
            else
            {
                List<Edit> localEdits = GetEdits(left, local, true), remoteEdits = GetEdits(right, remote, false);
                var all = localEdits.Concat(remoteEdits).OrderBy(edit => edit.Start).ThenBy(edit => edit.Count).ToList();
                int basePosition = 0, localPosition = 0, remotePosition = 0;
                for (int i = 0; i < all.Count;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int start = all[i].Start, end = start + all[i].Count;
                    var group = new List<Edit> { all[i++] };
                    while (i < all.Count && Touches(start, end, all[i]))
                    {
                        end = Math.Max(end, all[i].Start + all[i].Count);
                        group.Add(all[i++]);
                    }
                    int gap = start - basePosition;
                    localPosition += gap; remotePosition += gap;
                    var localGroup = group.Where(edit => edit.IsLocal).ToList();
                    var remoteGroup = group.Where(edit => !edit.IsLocal).ToList();
                    string[] a = Slice(plan.baseLines, start, end - start);
                    string[] b = Apply(plan.baseLines, start, end, localGroup);
                    string[] c = Apply(plan.baseLines, start, end, remoteGroup);
                    TextMergeKind kind = b.SequenceEqual(c) ? TextMergeKind.Identical :
                        b.SequenceEqual(a) ? TextMergeKind.Remote : c.SequenceEqual(a) ? TextMergeKind.Local : TextMergeKind.Conflict;
                    blocks.Add(new TextMergeBlock { BaseStart = start, LocalStart = localPosition, RemoteStart = remotePosition,
                        BaseLines = a, LocalLines = b, RemoteLines = c, Kind = kind,
                        Status = kind == TextMergeKind.Conflict ? TextMergeStatus.Unresolved : TextMergeStatus.Automatic,
                        Choice = kind == TextMergeKind.Remote ? TextMergeChoice.Remote : TextMergeChoice.Local });
                    basePosition = end; localPosition += b.Length; remotePosition += c.Length;
                }
            }
            plan.Blocks = blocks.AsReadOnly();
            plan.Compose();
            cancellationToken.ThrowIfCancellationRequested();
            return plan;
        }

        public void Choose(int blockIndex, TextMergeChoice choice)
        {
            if (!Enum.IsDefined(typeof(TextMergeChoice), choice)) throw new ArgumentOutOfRangeException("choice");
            TextMergeBlock block = GetBlock(blockIndex);
            block.Choice = choice; block.Status = TextMergeStatus.Reviewed;
            Compose();
        }

        public void MarkReviewed(int blockIndex) { GetBlock(blockIndex).Status = TextMergeStatus.Reviewed; }

        private TextMergeBlock GetBlock(int index)
        {
            if (index < 0 || index >= Blocks.Count) throw new ArgumentOutOfRangeException("blockIndex");
            return Blocks[index];
        }

        private void Compose()
        {
            // Lines include the terminal empty sentinel, so join ONCE across the whole result.
            // Joining preformatted block strings separately loses insertion and EOF boundaries.
            var lines = new List<string>();
            var starts = new List<int>();
            int position = 0;
            foreach (TextMergeBlock block in Blocks)
            {
                for (; position < block.BaseStart; position++) lines.Add(baseLines[position]);
                starts.Add(lines.Count);
                lines.AddRange(block.SelectedLines);
                position = block.BaseStart + block.BaseCount;
            }
            for (; position < baseLines.Length; position++) lines.Add(baseLines[position]);
            ResultText = String.Join("\r\n", lines);
            int[] offsets = new int[lines.Count + 1];
            for (int i = 0; i < lines.Count; i++) offsets[i + 1] = offsets[i] + lines[i].Length + (i + 1 < lines.Count ? 2 : 0);
            for (int i = 0; i < Blocks.Count; i++)
            {
                TextMergeBlock block = Blocks[i];
                block.ResultStart = offsets[starts[i]];
                block.ResultLength = offsets[starts[i] + block.SelectedLines.Length] - block.ResultStart;
            }
        }

        private static bool MetadataOnlyChange(TextDocument baseline, TextDocument side)
        {
            return baseline.EditorText == side.EditorText && (baseline.Text != side.Text ||
                baseline.EncodingName != side.EncodingName || baseline.HasBom != side.HasBom);
        }

        private sealed class Edit
        {
            public int Start, Count;
            public string[] Lines;
            public bool IsLocal;
        }

        private static List<Edit> GetEdits(TextComparisonResult comparison, TextDocument side, bool isLocal)
        {
            var edits = new List<Edit>();
            int a = 0, b = 0;
            for (int i = 0; i < comparison.Rows.Count;)
            {
                if (comparison.Rows[i].Kind == TextDiffKind.Equal) { a++; b++; i++; continue; }
                int startA = a, startB = b;
                do
                {
                    TextDiffRow row = comparison.Rows[i++];
                    if (row.LeftLineNumber > 0) a++;
                    if (row.RightLineNumber > 0) b++;
                } while (i < comparison.Rows.Count && comparison.Rows[i].Kind != TextDiffKind.Equal);
                edits.Add(new Edit { Start = startA, Count = a - startA, Lines = Slice(side.Lines, startB, b - startB), IsLocal = isLocal });
            }
            return edits;
        }

        private static bool Touches(int start, int end, Edit edit)
        {
            // Adjacent replacements are independent. Insertions touching either boundary are
            // deliberately grouped; their ordering relative to a replacement needs review.
            return edit.Start < end || edit.Start == end && (edit.Count == 0 || start == end);
        }

        private static string[] Apply(string[] source, int start, int end, IList<Edit> edits)
        {
            var result = new List<string>();
            int position = start;
            foreach (Edit edit in edits)
            {
                for (; position < edit.Start; position++) result.Add(source[position]);
                result.AddRange(edit.Lines); position = edit.Start + edit.Count;
            }
            for (; position < end; position++) result.Add(source[position]);
            return result.ToArray();
        }

        private static string[] Slice(IList<string> source, int start, int count)
        {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = source[start + i];
            return result;
        }
    }
}
