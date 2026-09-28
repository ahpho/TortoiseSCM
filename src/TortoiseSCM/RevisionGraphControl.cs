// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TortoiseSCM
{
    // The native list is the keyboard/accessibility equivalent of this drawing.
    internal sealed class RevisionGraphControl : ScrollableControl
    {
        private PlasticRevisionGraphPage page;
        private readonly Dictionary<long, Rectangle> boxes = new Dictionary<long, Rectangle>();
        private long? selected;
        internal event Action<long> NodeSelected;

        internal RevisionGraphControl()
        {
            AutoScroll = true; BackColor = SystemColors.Window; ForeColor = SystemColors.WindowText;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            AccessibleName = "提交关系图；使用右侧提交列表通过键盘查看全部关系";
            TabStop = false;
        }

        internal void SetPage(PlasticRevisionGraphPage value)
        {
            page = value; selected = null; boxes.Clear(); AutoScrollPosition = Point.Empty;
            if (page == null) { AutoScrollMinSize = Size.Empty; Invalidate(); return; }
            var lanes = page.Nodes.Select(n => n.Branch ?? "").Distinct(StringComparer.Ordinal).ToList();
            int top = 12;
            foreach (var node in page.Nodes)
            {
                boxes.Add(node.Changeset, new Rectangle(12 + lanes.IndexOf(node.Branch ?? "") * 190, top, 166, 42));
                int boundary = page.Edges.Count(e => e.DestinationChangeset == node.Changeset && !e.SourceLoaded);
                top += 76 + boundary * 19;
            }
            AutoScrollMinSize = new Size(Math.Max(490, lanes.Count * 190 + 300), top + 15); Invalidate();
        }

        internal void SelectNode(long? changeset)
        {
            selected = changeset;
            Rectangle box;
            if (changeset.HasValue && boxes.TryGetValue(changeset.Value, out box))
                AutoScrollPosition = new Point(Math.Max(0, box.X - 12), Math.Max(0, box.Y - ClientSize.Height / 3));
            Invalidate();
        }

        internal Rectangle NodeBounds(long changeset) { return boxes[changeset]; }
        internal long? HitNode(Point location)
        {
            location.Offset(-AutoScrollPosition.X, -AutoScrollPosition.Y);
            foreach (var pair in boxes) if (pair.Value.Contains(location)) return pair.Key;
            return null;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); var node = HitNode(e.Location);
            if (e.Button == MouseButtons.Left && node.HasValue && NodeSelected != null) NodeSelected(node.Value);
        }

        internal static DashStyle EdgeStyle(string kind)
        { return kind == "parent" ? DashStyle.Solid : kind == "merge" ? DashStyle.Dash : DashStyle.DashDot; }

        private int EdgeOrdinal(PlasticGraphEdge edge)
        {
            return page.Edges.TakeWhile(item => !Object.ReferenceEquals(item, edge))
                .Count(item => item.SourceLoaded && edge.SourceLoaded &&
                    item.SourceChangeset == edge.SourceChangeset && item.DestinationChangeset == edge.DestinationChangeset);
        }

        internal Point[] LoadedEdgePoints(long sourceChangeset, long destinationChangeset)
        {
            var edge = page == null ? null : page.Edges.FirstOrDefault(item => item.SourceLoaded &&
                item.SourceChangeset == sourceChangeset && item.DestinationChangeset == destinationChangeset);
            return edge == null ? LoadedEdgePoints(new PlasticGraphEdge { SourceChangeset = sourceChangeset, DestinationChangeset = destinationChangeset, SourceLoaded = true }) : LoadedEdgePoints(edge);
        }

        internal Point[] LoadedEdgePoints(PlasticGraphEdge edge)
        {
            var source = boxes[edge.SourceChangeset]; var destination = boxes[edge.DestinationChangeset];
            // Every vertical segment stays in the lane gutter. A real edge can skip
            // intermediate commits, and must never appear to enter their boxes.
            int ordinal = EdgeOrdinal(edge);
            int gutter = source.Right + 10 + ordinal * 9;
            int endX = destination.Left + destination.Width / 2 + (ordinal == 0 ? 0 : (ordinal % 2 == 0 ? 1 : -1) * ((ordinal + 1) / 2) * 8);
            int approach = destination.Bottom + 12;
            return new[] { new Point(source.Right, source.Top + source.Height / 2),
                new Point(gutter, source.Top + source.Height / 2), new Point(gutter, approach),
                new Point(endX, approach), new Point(endX, destination.Bottom) };
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); if (page == null) return;
            var graphics = e.Graphics; graphics.TranslateTransform(AutoScrollPosition.X, AutoScrollPosition.Y);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var edge in page.Edges)
            {
                Rectangle destination, source;
                if (!boxes.TryGetValue(edge.DestinationChangeset, out destination)) continue;
                using (var pen = new Pen(SystemColors.WindowText, edge.Kind == "parent" ? 1.3f : 1.7f))
                using (var cap = new AdjustableArrowCap(4, 5))
                {
                    pen.DashStyle = EdgeStyle(edge.Kind); pen.CustomEndCap = cap;
                    var target = new Point(destination.Left + destination.Width / 2, destination.Bottom);
                    if (edge.SourceLoaded && boxes.TryGetValue(edge.SourceChangeset, out source))
                    {
                        graphics.DrawLines(pen, LoadedEdgePoints(edge));
                    }
                    else
                    {
                        int index = page.Edges.Where(item => item.DestinationChangeset == edge.DestinationChangeset && !item.SourceLoaded).TakeWhile(item => !Object.ReferenceEquals(item, edge)).Count();
                        int y = destination.Bottom + 22 + index * 19;
                        graphics.DrawLines(pen, new[] { new Point(destination.Right + 16, y), new Point(target.X, y), target });
                        using (var ink = new SolidBrush(SystemColors.WindowText))
                        using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                            graphics.DrawString("cs:" + edge.SourceChangeset + " 未加载 · " + edge.Kind, Font, ink,
                                new Rectangle(destination.Right + 18, y - 9, 270, 19), format);
                    }
                }
            }
            foreach (var node in page.Nodes)
            {
                var box = boxes[node.Changeset]; bool active = selected == node.Changeset;
                using (var background = new SolidBrush(active ? SystemColors.Highlight : SystemColors.Window)) graphics.FillRectangle(background, box);
                using (var border = new Pen(active ? SystemColors.HighlightText : SystemColors.WindowText)) graphics.DrawRectangle(border, box);
                using (var ink = new SolidBrush(active ? SystemColors.HighlightText : SystemColors.WindowText))
                using (var format = new StringFormat { FormatFlags = StringFormatFlags.NoWrap, Trimming = StringTrimming.EllipsisCharacter })
                {
                    graphics.DrawString("cs:" + node.Changeset, Font, ink, new Rectangle(box.Left + 5, box.Top + 3, box.Width - 10, 16), format);
                    graphics.DrawString(node.Branch ?? "", Font, ink, new Rectangle(box.Left + 5, box.Top + 20, box.Width - 10, 16), format);
                }
            }
        }
    }
}
