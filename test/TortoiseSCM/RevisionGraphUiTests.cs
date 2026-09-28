// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class RevisionGraphUiTests
    {
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        private static object Field(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
        private static void Wait(Func<bool> condition) { DateTime deadline = DateTime.UtcNow.AddSeconds(55); while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(15); } Check(condition(), "Graph UI operation completes"); }
        private static void Prepare(Form form) { form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show(); Application.DoEvents(); }
        private static void Save(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static PlasticGraphNode Node(long cs, long? parent, string branch) { return new PlasticGraphNode { Id = cs + 1000, Changeset = cs, ParentChangeset = parent, Branch = branch, Owner = "作者", Date = "2026-09-28", Comment = "准确提交说明 中文 & details", Repository = "ui-graph@local" }; }
        private static PlasticRevisionGraphPage Fixture(long? before)
        {
            var nodes = new[] { Node(139, 138, "/main"), Node(138, 136, "/main"), Node(137, 136, "/main/feature 中文"), Node(136, 0, "/main"), Node(0, null, "/main") }.Where(n => !before.HasValue || n.Changeset < before).ToList();
            var edges = nodes.Where(n => n.ParentChangeset.HasValue).Select(n => new PlasticGraphEdge { Kind = "parent", SourceChangeset = n.ParentChangeset.Value, DestinationChangeset = n.Changeset, SourceLoaded = nodes.Any(source => source.Changeset == n.ParentChangeset) }).ToList();
            if (nodes.Any(n => n.Changeset == 139)) { edges.Add(new PlasticGraphEdge { Id = 2, Kind = "merge", SourceChangeset = 137, DestinationChangeset = 139, BaseChangeset = 136, SourceLoaded = true, BaseLoaded = true }); edges.Add(new PlasticGraphEdge { Id = 3, Kind = "intervalcherrypicksubtractive", SourceChangeset = 120, DestinationChangeset = 139, BaseChangeset = 119, SourceLoaded = false, BaseLoaded = false }); }
            return new PlasticRevisionGraphPage { Repository = "ui-graph@local", Nodes = nodes, Edges = edges, HasMore = !before.HasValue, NextBeforeChangeset = !before.HasValue ? (long?)136 : null };
        }

        internal static void Run(string artifacts)
        {
            Check(LaunchRequest.Parse(new[] { "--command", "revision-graph", "--before", "0" }).Before == 0, "Graph GUI accepts zero boundary");
            foreach (var args in new[] { new[] { "--command", "status", "--before", "5" }, new[] { "--command", "revision-graph", "--before", "-1" }, new[] { "--command", "revision-graph", "--before", "+1" }, new[] { "--command", "revision-graph", "--before", "1", "--before", "2" } }) {
                bool failed = false; try { LaunchRequest.Parse(args); } catch (ArgumentException) { failed = true; } Check(failed, "Graph GUI rejects invalid and out-of-scope boundary");
            }
            string root = Path.Combine(artifacts, "graph-fixture-" + Guid.NewGuid().ToString("N")); string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-graph\nunused\nPartial\n"); string selectorFile = Path.Combine(metadata, "plastic.selector");
            string selector = "repository \"ui-graph@local\"\n  path \"/\"\n    branch \"/main\"\n"; File.WriteAllText(selectorFile, selector);
            var client = new PlasticClient(PlasticClientConfig.Load()); int reads = 0; long requested = -1;
            Func<long?, CancellationToken, Task<PlasticRevisionGraphPage>> getGraph = (cursor, token) => { reads++; return Task.FromResult(Fixture(cursor)); };
            using (var form = new RevisionGraphForm(client, root, null))
            {
                Set(form, "getGraph", getGraph); Set(form, "getChanges", new Func<long, CancellationToken, Task<PlasticChangesetDetails>>((cs, token) => { requested = cs; return Task.FromResult(new PlasticChangesetDetails { Changeset = new PlasticHistoryItem { Changeset = cs }, Files = new[] { new PlasticChangesetFile { Status = "Changed", Path = "/中文 & directory/file.txt" } } }); }));
                Prepare(form); var list = (ListView)Field(form, "revisions"); var graph = (RevisionGraphControl)Field(form, "graph");
                Check(list.Items.Count == 5 && !((Button)Field(form, "browse")).Enabled, "Graph displays root and bounded nodes with no implicit selection");
                list.Items[0].Selected = true; Application.DoEvents();
                Check(requested == 139 && ((ListView)Field(form, "files")).Items.Count == 1, "Graph selection loads exact commit files");
                string relations = ((TextBox)Field(form, "description")).Text;
                Check(relations.Contains("merge: cs:137 → cs:139；基线 cs:136") && relations.Contains("intervalcherrypicksubtractive: cs:120（未加载）") && relations.Contains("基线 cs:119（未加载）"), "Graph exposes exact typed and unloaded relationships without invented ancestry");
                using (var browser = (RepositoryBrowserForm)typeof(RevisionGraphForm).GetMethod("CreateSnapshotBrowser", Flags).Invoke(form, null)) Check((long?)Field(browser, "initialChangeset") == 139 && (string)Field(browser, "expectedRepository") == "ui-graph@local", "Graph snapshot pins exact revision and repository");
                Check(RevisionGraphControl.EdgeStyle("parent") == DashStyle.Solid && RevisionGraphControl.EdgeStyle("merge") == DashStyle.Dash && RevisionGraphControl.EdgeStyle("intervalcherrypicksubtractive") == DashStyle.DashDot, "Edge types differ without relying on color");
                var route = graph.LoadedEdgePoints(136, 139); var skipped = graph.NodeBounds(138);
                Check(route[1].X > skipped.Right && route[2].X > skipped.Right && route[3].Y < skipped.Top, "Skipped-row edge travels through gutter without implying intermediate ancestry");
                Check(graph.AutoScrollMinSize.Width >= graph.NodeBounds(137).Right + 18 + 270, "Scrollable canvas includes rightmost boundary label extent");
                var currentPage = (PlasticRevisionGraphPage)Field(graph, "page");
                var duplicateParent = new PlasticGraphEdge { Kind = "parent", SourceChangeset = 137, DestinationChangeset = 139, SourceLoaded = true };
                currentPage.Edges.Add(duplicateParent); graph.SetPage(currentPage);
                var firstPath = graph.LoadedEdgePoints(currentPage.Edges.First(edge => edge.SourceChangeset == 137 && edge.DestinationChangeset == 139));
                var secondPath = graph.LoadedEdgePoints(duplicateParent);
                Check(firstPath[1] != secondPath[1] && firstPath[3] != secondPath[3], "Overlapping relationships use distinct gutter and target ports");
                var bounds = graph.NodeBounds(139); var point = new Point(bounds.Left + 5 + graph.AutoScrollPosition.X, bounds.Top + 5 + graph.AutoScrollPosition.Y);
                Check(graph.HitNode(point) == 139, "Canvas hit-test resolves actual node");
                graph.SelectNode(0); bounds = graph.NodeBounds(0); point = new Point(bounds.Left + 5 + graph.AutoScrollPosition.X, bounds.Top + 5 + graph.AutoScrollPosition.Y); Check(graph.HitNode(point) == 0, "Canvas scrolled hit-test preserves changeset zero"); graph.SelectNode(139);
                Save(form, Path.Combine(artifacts, "revision-graph.png")); form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string name in new[] { "graph", "revisions", "files", "description", "before", "jump", "latest", "previous", "older", "refresh", "cancel", "browse", "close", "status" }) { var control = (Control)Field(form, name); Check(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Graph " + name + " fits minimum size"); }
                Save(form, Path.Combine(artifacts, "revision-graph-minimum.png"));
                ((Button)Field(form, "older")).PerformClick(); Wait(() => !(bool)Field(form, "busy")); Check(list.Items.Count == 1 && ((PlasticGraphNode)list.Items[0].Tag).Changeset == 0 && ((Button)Field(form, "previous")).Enabled, "Older page replaces nodes and preserves root");
                ((Button)Field(form, "previous")).PerformClick(); Wait(() => !(bool)Field(form, "busy")); Check(list.Items.Count == 5 && !((Button)Field(form, "previous")).Enabled, "Back restores prior cursor and does not accumulate pages");
                ((NumericUpDown)Field(form, "before")).Value = 0; ((Button)Field(form, "jump")).PerformClick(); Wait(() => !(bool)Field(form, "busy")); Check(list.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("cs:0 之前"), "Zero exclusive boundary shows empty page");
                ((Button)Field(form, "latest")).PerformClick(); Wait(() => !(bool)Field(form, "busy"));
                var pending = new TaskCompletionSource<PlasticRevisionGraphPage>(); Set(form, "getGraph", new Func<long?, CancellationToken, Task<PlasticRevisionGraphPage>>((cursor, token) => pending.Task));
                ((Button)Field(form, "refresh")).PerformClick(); Check(!((Button)Field(form, "browse")).Enabled && list.Items.Count == 0 && ((Button)Field(form, "cancel")).Enabled, "Pending page read clears stale nodes and disables actions");
                ((Button)Field(form, "cancel")).PerformClick(); pending.SetResult(Fixture(null)); Wait(() => !(bool)Field(form, "busy")); Check(list.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("取消"), "Cancelled completed result cannot repopulate graph");
                Set(form, "getGraph", getGraph); ((Button)Field(form, "refresh")).PerformClick(); Wait(() => !(bool)Field(form, "busy"));
                var detail = new TaskCompletionSource<PlasticChangesetDetails>(); Set(form, "getChanges", new Func<long, CancellationToken, Task<PlasticChangesetDetails>>((cs, token) => detail.Task)); list.Items[0].Selected = true; Application.DoEvents();
                File.WriteAllText(selectorFile, selector.Replace("ui-graph@local", "changed@local")); detail.SetResult(new PlasticChangesetDetails { Changeset = new PlasticHistoryItem { Changeset = 139 }, Files = new PlasticChangesetFile[0] }); Wait(() => !(bool)Field(form, "busy"));
                Check(list.Items.Count == 0 && ((TextBox)Field(form, "description")).Text.Length == 0 && !((Button)Field(form, "browse")).Enabled, "Repository change during details clears graph and stale actions");
                int count = reads; ((Button)Field(form, "refresh")).PerformClick(); Wait(() => !(bool)Field(form, "busy")); Check(reads == count, "Repository mismatch blocks provider call"); File.WriteAllText(selectorFile, selector); form.Close();
            }
            Check(File.ReadAllText(selectorFile) == selector, "Graph GUI navigation preserves selector"); Console.WriteLine("Revision graph UI: " + assertions + " assertions passed.");
        }

        internal static void RunLive(string artifacts, string workspace, long? before)
        {
            var client = new PlasticClient(PlasticClientConfig.Load()); string selector = client.DiscoverWorkspace(workspace).Selector;
            using (var form = new RevisionGraphForm(client, workspace, before))
            {
                Prepare(form); Wait(() => !(bool)Field(form, "busy")); var list = (ListView)Field(form, "revisions"); Check(list.Items.Count > 0, "Live graph returns native commits");
                var selected = (PlasticGraphNode)list.Items[0].Tag;
                list.Items[0].Selected = true; Wait(() => !(bool)Field(form, "busy")); Check(((TextBox)Field(form, "description")).Text.StartsWith("cs:" + selected.Changeset + " · "), "Live graph reads exact selected native commit details");
                var details = client.GetGraphChangesetAsync(workspace, selected.Changeset, selected.Repository, CancellationToken.None); Wait(() => details.IsCompleted);
                Check(((ListView)Field(form, "files")).Items.Count == details.GetAwaiter().GetResult().Files.Count && ((Label)Field(form, "status")).Text.StartsWith("cs:" + selected.Changeset + " · "), "Live graph completes exact selected files and status");
                using (var browser = (RepositoryBrowserForm)typeof(RevisionGraphForm).GetMethod("CreateSnapshotBrowser", Flags).Invoke(form, null)) Check((long?)Field(browser, "initialChangeset") == selected.Changeset && (string)Field(browser, "expectedRepository") == selected.Repository, "Live graph snapshot navigation pins selected revision and repository");
                Save(form, Path.Combine(artifacts, "revision-graph-live.png")); form.Size = form.MinimumSize; Application.DoEvents(); Save(form, Path.Combine(artifacts, "revision-graph-live-minimum.png")); form.Close();
            }
            Check(client.DiscoverWorkspace(workspace).Selector == selector, "Live graph preserves selector");
        }
    }
}
