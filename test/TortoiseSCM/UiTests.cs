// GPL-2.0-or-later. In-process WinForms integration checks and layout artifacts.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class UiTests
    {
        [STAThread]
        private static int Main(string[] args)
        {
            var previousContext = SynchronizationContext.Current;
            bool previousAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext uiContext = null;
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // This harness pumps with DoEvents instead of Application.Run. Automatic
                // context installation is otherwise undone as child windows close, and
                // async event continuations can escape to the thread pool.
                WindowsFormsSynchronizationContext.AutoInstall = false;
                uiContext = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(uiContext);
                Control.CheckForIllegalCrossThreadCalls = true;
                if (args.Length == 2 && args[0] == "--bc-settings-ui")
                {
                    BeyondCompareSettingsUiTests.Run(args[1]); return 0;
                }
                if (args.Length == 2 && args[0] == "--graph-ui")
                {
                    Directory.CreateDirectory(args[1]); RevisionGraphUiTests.Run(args[1]); return 0;
                }
                if (args.Length == 4 && args[0] == "--graph-live")
                {
                    Directory.CreateDirectory(args[1]); RevisionGraphUiTests.RunLive(args[1], args[2], Int64.Parse(args[3])); return 0;
                }
                if (args.Length == 2 && args[0] == "--labels-ui")
                {
                    Directory.CreateDirectory(args[1]); CheckLabelsDialogs(args[1]); return 0;
                }
                if (args.Length == 4 && args[0] == "--labels-live")
                {
                    Directory.CreateDirectory(args[1]); CheckLiveLabels(args[1], args[2], args[3]); return 0;
                }
                if (args.Length == 2 && args[0] == "--repository-browser-ui")
                {
                    Directory.CreateDirectory(args[1]); CheckRepositoryBrowser(args[1]); return 0;
                }
                if (args.Length == 3 && args[0] == "--repository-browser-live")
                {
                    Directory.CreateDirectory(args[1]); CheckLiveRepositoryBrowser(args[1], args[2]); return 0;
                }
                if (args.Length == 3 && args[0] == "--branches-live")
                {
                    Directory.CreateDirectory(args[1]);
                    CheckBranches(args[1], args[2]);
                    return 0;
                }
                if (args.Length == 5 && args[0] == "--changeset-live")
                {
                    Directory.CreateDirectory(args[1]);
                    CheckLiveChangesetComparison(args[1], args[2], Int64.Parse(args[3]), Int64.Parse(args[4]));
                    return 0;
                }
                if (args.Length == 4 && args[0] == "--shelves-live")
                {
                    Directory.CreateDirectory(args[1]);
                    CheckLiveShelves(args[1], args[2], Int64.Parse(args[3]));
                    return 0;
                }
                string artifacts = args[0];
                Directory.CreateDirectory(artifacts);
                TextEditorUiTests.Run(artifacts);
                TextMergePlanUiTests.Run(artifacts);
                string pathfile = Path.Combine(Path.GetTempPath(), "tscm-ui-" + Guid.NewGuid() + ".paths");
                File.WriteAllLines(pathfile, new[] { @"D:\中文 空格\file & name.txt", @"D:\中文 空格\two.txt" }, new System.Text.UTF8Encoding(false));
                var request = LaunchRequest.Parse(new[] { "--command", "checkin", "--pathfile", pathfile });
                Require(request.Paths.Count == 2 && request.Paths[0].Contains("中文 空格"), "UTF-8 Explorer multi-selection");
                Require(!File.Exists(pathfile), "Consumed path file cleanup");
                try { LaunchRequest.Parse(new[] { "--command", "unknown" }); throw new Exception("Unknown command accepted"); }
                catch (ArgumentException) { }
                try { LaunchRequest.Parse(new[] { "--pathfile", Path.Combine(artifacts, "foreign.txt") }); throw new Exception("Foreign path file accepted"); }
                catch (ArgumentException) { }
                BeyondCompareSettingsUiTests.Run(artifacts);
                using (var merge = new ToolLaunchForm(new PlasticClient(PlasticClientConfig.Load())))
                { Prepare(merge); Save(merge, Path.Combine(artifacts, "merge-tool.png")); merge.Close(); }
                CheckConflictDialogs(artifacts);
                CheckBranchCreationDialogs(artifacts);
                CheckBranchTree(artifacts);
                CheckShelvesDialogs(artifacts);
                CheckBlameDialog(artifacts);
                CheckRepositoryBrowser(artifacts);
                CheckLabelsDialogs(artifacts);
                RevisionGraphUiTests.Run(artifacts);
                if (args.Length > 1)
                {
                    CheckLiveRepositoryBrowser(artifacts, args[1]);
                    CheckBranches(artifacts, args[1]);
                    using (var merge = new MergeForm(new PlasticClient(PlasticClientConfig.Load()), args[1]))
                    {
                        Prepare(merge);
                        WaitUntil(() => !(bool)Field(merge, "busy"), "Merge session lookup completes");
                        var plan = new PlasticMergePlan { SourceChangeset = 17, DestinationChangeset = 18, BaseChangeset = 16 };
                        ((NumericUpDown)Field(merge, "source")).Value = 17;
                        ((TextBox)Field(merge, "details")).Text = "将指定变更集合并到当前分支。\r\n文件冲突需使用三方工具编辑，然后单独确认解决；完成后回到待定更改界面提交。";
                        plan.FileConflicts.Add(new PlasticMergeConflict { RepositoryPath = "/中文 空格/conflict.txt" });
                        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        typeof(MergeForm).GetField("plan", flags).SetValue(merge, plan);
                        typeof(MergeForm).GetMethod("RenderPlan", flags).Invoke(merge, null);
                        Require(((Button)Field(merge, "start")).Enabled && !((Button)Field(merge, "apply")).Enabled, "Merge preview permits start but cannot resolve before native merge session");
                        plan.DirectoryConflicts.Add(new PlasticDirectoryConflict { Kind = "EVILTWIN", SourcePath = "/collision.txt", Description = "需要结构冲突处理" });
                        typeof(MergeForm).GetMethod("RenderPlan", flags).Invoke(merge, null);
                        Require(((Button)Field(merge, "start")).Enabled, "Directory conflicts allow entering explicit structural planning");
                        typeof(MergeForm).GetField("session", flags).SetValue(merge, new PlasticMergeSession { SessionId = "UI-directory-fixture", Plan = plan, AwaitingDirectoryResolution = true });
                        typeof(MergeForm).GetMethod("RenderPlan", flags).Invoke(merge, null);
                        var directoryRows = (ListView)Field(merge, "items");
                        directoryRows.Items.Cast<ListViewItem>().Single(row => row.Tag is PlasticDirectoryConflict).Selected = true;
                        Application.DoEvents();
                        Require(((Button)Field(merge, "directory")).Enabled && !((Button)Field(merge, "continueMerge")).Enabled && !((Button)Field(merge, "prepare")).Enabled && !((Button)Field(merge, "apply")).Enabled,
                            "Structural planning permits explicit directory choice and blocks content apply and incomplete continuation");
                        plan.DirectoryConflicts[0].Resolved = true;
                        typeof(MergeForm).GetMethod("RenderPlan", flags).Invoke(merge, null);
                        Require(((Button)Field(merge, "continueMerge")).Enabled, "Reviewed directory plan requires separate explicit continuation");
                        plan.DirectoryConflicts.Clear();
                        typeof(MergeForm).GetField("session", flags).SetValue(merge, new PlasticMergeSession { SessionId = "UI-fixture", Plan = plan });
                        typeof(MergeForm).GetMethod("RenderPlan", flags).Invoke(merge, null);
                        var conflicts = (ListView)Field(merge, "items"); conflicts.Items[0].Selected = true;
                        Application.DoEvents();
                        Require(((Button)Field(merge, "prepare")).Enabled && ((Button)Field(merge, "apply")).Enabled && !((NumericUpDown)Field(merge, "source")).Enabled, "Active selected conflict permits explicit resolution and fixes source changeset");
                        Save(merge, Path.Combine(artifacts, "merge-conflicts.png"));
                        merge.Size = merge.MinimumSize; Application.DoEvents();
                        var apply = (Button)Field(merge, "apply");
                        Require(merge.RectangleToScreen(merge.ClientRectangle).Contains(apply.RectangleToScreen(apply.ClientRectangle)), "Merge resolution button remains visible at minimum size");
                        Save(merge, Path.Combine(artifacts, "merge-conflicts-minimum.png"));
                        plan.FileConflicts[0].Resolved = true;
                        typeof(MergeForm).GetMethod("RenderPlan", flags).Invoke(merge, null);
                        conflicts.Items[0].Selected = true; Application.DoEvents();
                        Require(!((Button)Field(merge, "apply")).Enabled && !((Button)Field(merge, "prepare")).Enabled, "Resolved conflicts cannot be accidentally reapplied");
                        merge.Close();
                    }
                    using (var locks = new LocksForm(new PlasticClient(PlasticClientConfig.Load()), args[1]))
                    {
                        Prepare(locks);
                        WaitUntil(() => !(bool)Field(locks, "busy"), "Lock list read completes");
                        Require(!((Button)Field(locks, "unlock")).Enabled, "Lock release requires an explicit selected lock");
                        var list = (ListView)Field(locks, "items");
                        list.Items.Clear();
                        var foreign = new ListViewItem(new[] { "/someone-else.txt", "other", "other-workspace", "Locked" }) { Tag = new PlasticLockItem { CanUnlock = false } };
                        list.Items.Add(foreign); foreign.Selected = true; Application.DoEvents();
                        Require(!((Button)Field(locks, "unlock")).Enabled, "Foreign locks cannot be released from the UI");
                        foreign.Selected = false;
                        var owned = new ListViewItem(new[] { "/自己的文件.txt", "current user", "current workspace", "Locked" }) { Tag = new PlasticLockItem { CanUnlock = true } };
                        list.Items.Add(owned); owned.Selected = true; Application.DoEvents();
                        Require(((Button)Field(locks, "unlock")).Enabled, "Current user and workspace lock enables explicit release");
                        Save(locks, Path.Combine(artifacts, "locks.png"));
                        locks.Size = locks.MinimumSize; Application.DoEvents();
                        Save(locks, Path.Combine(artifacts, "locks-minimum.png"));
                        locks.Close();
                    }
                    string sample = Path.Combine(args[1], "TortoiseSCM-UI-" + Guid.NewGuid().ToString("N") + " 中文 空格.txt");
                    using (var stream = new FileStream(sample, FileMode.CreateNew, FileAccess.Write))
                    using (var writer = new StreamWriter(stream)) writer.Write("Disposable UI status fixture.");
                    try
                    {
                        using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", args[1] })))
                        {
                            Prepare(form);
                            var busy = typeof(MainForm).GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic);
                            var loaded = typeof(MainForm).GetField("loaded", BindingFlags.Instance | BindingFlags.NonPublic);
                            DateTime deadline = DateTime.UtcNow.AddSeconds(40);
                            while (DateTime.UtcNow < deadline && ((bool)busy.GetValue(form) || !(bool)loaded.GetValue(form)))
                            { Application.DoEvents(); Thread.Sleep(25); }
                            Require(!(bool)busy.GetValue(form) && (bool)loaded.GetValue(form), "Workspace load finished");
                            var output = (TextBox)typeof(MainForm).GetField("output", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                            Require(output.TextLength == 0, "Status load did not report an error");
                            var list = (ListView)typeof(MainForm).GetField("files", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
                            Require(list.Items.Cast<ListViewItem>().Any(i => ((PlasticStatusItem)i.Tag).Path.Equals(sample, StringComparison.OrdinalIgnoreCase)), "Live private UTF-8 file is displayed");
                            Require(list.CheckedItems.Count == 0, "No unreviewed changes selected automatically");
                            Require(list.ContextMenuStrip != null && list.ContextMenuStrip.Items.Cast<ToolStripItem>().Count(item => item is ToolStripMenuItem) == 7 &&
                                list.ContextMenuStrip.Items.Cast<ToolStripItem>().Any(item => item.Text.Contains("Blame")),
                                "Pending context menu provides history, blame, diff, discard, move, remove and ignore");
                            var message = (TextBox)Field(form, "comment");
                            Require(message.PointToScreen(Point.Empty).Y < list.PointToScreen(Point.Empty).Y, "Tortoise commit layout places message above changed files");
                            Require(!list.GridLines && list.Columns[0].Text == "路径", "Pending list uses native path-first layout without a grid");
                            Save(form, Path.Combine(artifacts, "pending-changes.png"));
                            form.Size = form.MinimumSize;
                            Application.DoEvents();
                            Require(form.RectangleToScreen(form.ClientRectangle).Contains(((Button)Field(form, "checkin")).RectangleToScreen(((Button)Field(form, "checkin")).ClientRectangle)), "Commit button remains visible at minimum size");
                            Save(form, Path.Combine(artifacts, "pending-changes-minimum.png"));
                            form.Close();
                        }
                        using (var scoped = new MainForm(LaunchRequest.Parse(new[] { "--path", sample })))
                        {
                            Prepare(scoped);
                            WaitUntil(() => !(bool)Field(scoped, "busy") && (bool)Field(scoped, "loaded"), "Scoped pending list load");
                            var list = (ListView)Field(scoped, "files");
                            Require(list.Items.Count == 1 && ((PlasticStatusItem)list.Items[0].Tag).Path.Equals(sample, StringComparison.OrdinalIgnoreCase), "Selected file excludes all other workspace pending rows");
                            var inScope = typeof(MainForm).GetMethod("InScope", BindingFlags.Instance | BindingFlags.NonPublic);
                            Require(!(bool)inScope.Invoke(scoped, new object[] { sample + "-outside" }), "Scope boundary excludes sibling prefix");
                            list.Items.Clear();
                            string directory = Path.Combine(args[1], "fake-deleted-dir");
                            var parent = new ListViewItem("directory") { Tag = new PlasticStatusItem { Path = directory, IsDirectory = true } };
                            var child = new ListViewItem("child") { Tag = new PlasticStatusItem { Path = Path.Combine(directory, "child.txt") } };
                            list.Items.Add(parent); list.Items.Add(child);
                            parent.Checked = true;
                            Require(child.Checked, "Checking directory selects visible descendants");
                            child.Checked = false;
                            Require(!parent.Checked, "Unchecking child clears recursive parent selection");
                            parent.Checked = true;
                            parent.Selected = false; child.Selected = true;
                            var highlighted = (System.Collections.Generic.List<string>)typeof(MainForm).GetMethod("HighlightedPaths", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(scoped, null);
                            Require(highlighted.Count == 1 && highlighted[0] == ((PlasticStatusItem)child.Tag).Path, "Context commands target highlighted rows independently from checked rows");
                            ((PlasticStatusItem)parent.Tag).StatusCode = "CO";
                            ((PlasticStatusItem)child.Tag).StatusCode = "PR";
                            var controlled = new ListViewItem("controlled") { Tag = new PlasticStatusItem { Path = Path.Combine(directory, "controlled.txt"), StatusCode = "CH" } };
                            list.Items.Add(controlled);
                            var clickLink = typeof(LinkLabel).GetMethod("OnLinkClicked", BindingFlags.Instance | BindingFlags.NonPublic);
                            var privateLink = Descendants(scoped).OfType<LinkLabel>().Single(link => link.Text == "未版本控制");
                            clickLink.Invoke(privateLink, new object[] { new LinkLabelLinkClickedEventArgs(privateLink.Links[0]) });
                            Require(child.Checked && !controlled.Checked && !parent.Checked, "Unversioned selection link selects only private rows");
                            var versionedLink = Descendants(scoped).OfType<LinkLabel>().Single(link => link.Text == "已版本控制");
                            clickLink.Invoke(versionedLink, new object[] { new LinkLabelLinkClickedEventArgs(versionedLink.Links[0]) });
                            Require(!child.Checked && controlled.Checked && !parent.Checked, "Versioned selection link excludes private children without recursive parent selection");
                            scoped.Close();
                        }
                        string scopeDirectory = Path.Combine(args[1], "TortoiseSCM-scope-" + Guid.NewGuid().ToString("N"));
                        string scopeFile = Path.Combine(scopeDirectory, "inside.txt");
                        Directory.CreateDirectory(scopeDirectory);
                        File.WriteAllText(scopeFile, "Temporary directory scope fixture.");
                        try
                        {
                            using (var scoped = new MainForm(LaunchRequest.Parse(new[] { "--path", scopeDirectory })))
                            {
                                Prepare(scoped);
                                WaitUntil(() => !(bool)Field(scoped, "busy") && (bool)Field(scoped, "loaded"), "Directory pending list load");
                                var list = (ListView)Field(scoped, "files");
                                Require(list.Items.Count > 0 && list.Items.Cast<ListViewItem>().All(i => {
                                    string itemPath = ((PlasticStatusItem)i.Tag).Path;
                                    return itemPath.Equals(scopeDirectory, StringComparison.OrdinalIgnoreCase) || itemPath.StartsWith(scopeDirectory + "\\", StringComparison.OrdinalIgnoreCase);
                                }), "Directory pending list excludes the outside private fixture");
                                scoped.Close();
                            }
                        }
                        finally { File.Delete(scopeFile); Directory.Delete(scopeDirectory); }
                        using (var history = new HistoryForm(new PlasticClient(PlasticClientConfig.Load()), args[1], args[1]))
                        {
                            Prepare(history);
                            WaitUntil(() => ((Button)Field(history, "restore")).Enabled, "History and selected changeset details load");
                            Require(((ListView)Field(history, "revisions")).Items.Count > 0, "Upper history pane contains real changesets");
                            Require(((ListView)Field(history, "changedFiles")).Items.Count > 0, "Lower history pane contains real changed files");
                            var revisions = (ListView)Field(history, "revisions");
                            WaitUntil(() => !(bool)Field(history, "loadingHistory"), "Initial bounded history page finishes");
                            Require(revisions.Items.Count <= 50, "History opens with at most fifty commits");
                            var historyFlags = BindingFlags.Instance | BindingFlags.NonPublic;
                            var marked = (PlasticHistoryItem)revisions.SelectedItems[0].Tag;
                            revisions.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "标记为比较起点").PerformClick();
                            Require((long?)Field(history, "comparisonChangeset") == marked.Changeset, "Revision context action marks the selected changeset for comparison");
                            Require(((ToolStripMenuItem)Field(history, "compareMarkedChangeset")).Enabled, "Marked revision enables full repository comparison");
                            using (var comparisonForm = (ChangesetComparisonForm)typeof(HistoryForm).GetMethod("CreateChangesetComparison", historyFlags).Invoke(history, null))
                            {
                                Require((long)Field(comparisonForm, "fromChangeset") == marked.Changeset && (long)Field(comparisonForm, "toChangeset") == marked.Changeset,
                                    "History hands the marked and selected changesets to the repository comparison");
                                CheckChangesetComparison(comparisonForm, args[1], artifacts, marked.Changeset);
                            }
                            Require((string)typeof(HistoryForm).GetMethod("SelectedRevisionText", historyFlags).Invoke(history, new object[] { false }) == "cs:" + marked.Changeset &&
                                (string)typeof(HistoryForm).GetMethod("SelectedRevisionText", historyFlags).Invoke(history, new object[] { true }) == marked.Comment,
                                "Revision copy actions preserve the selected identifier and full multiline comment without touching the clipboard");
                            var older = (Button)Field(history, "loadMore");
                            if (older.Enabled)
                            {
                                int firstPageCount = revisions.Items.Count;
                                older.PerformClick();
                                WaitUntil(() => !(bool)Field(history, "loadingHistory"), "Load older history page finishes");
                                Require(revisions.Items.Count > firstPageCount && revisions.Items.Cast<ListViewItem>().Select(item => ((PlasticHistoryItem)item.Tag).Changeset).Distinct().Count() == revisions.Items.Count,
                                    "Load older appends repository commits without duplicates");
                                var refreshButton = (Button)Field(history, "refreshHistory");
                                Require(refreshButton.Enabled && refreshButton.CanSelect, "History refresh is available after loading older records");
                                refreshButton.PerformClick();
                                WaitUntil(() => !(bool)Field(history, "loadingHistory"), "History refresh returns to newest page");
                                Require(older.Enabled && revisions.Items.Count <= 50, "Refreshed history retains its continuation");
                                int retained = revisions.Items.Count;
                                older.PerformClick();
                                Require(((Button)Field(history, "cancelHistory")).Enabled, "Paging exposes cancellation while loading");
                                ((Button)Field(history, "cancelHistory")).PerformClick();
                                WaitUntil(() => !(bool)Field(history, "loadingHistory"), "History page cancellation completes");
                                Require(revisions.Items.Count == retained && older.Enabled, "Cancelled page preserves loaded records and remains retryable");
                            }
                            var beforeRefresh = revisions.Items.Cast<ListViewItem>().Select(item => ((PlasticHistoryItem)item.Tag).Changeset).ToArray();
                            ((Button)Field(history, "refreshHistory")).PerformClick();
                            ((Button)Field(history, "cancelHistory")).PerformClick();
                            WaitUntil(() => !(bool)Field(history, "loadingHistory"), "History refresh cancellation completes");
                            Require(revisions.Items.Cast<ListViewItem>().Select(item => ((PlasticHistoryItem)item.Tag).Changeset).SequenceEqual(beforeRefresh), "Cancelled refresh preserves existing loaded history");
                            int total = revisions.Items.Count;
                            var filter = (TextBox)Field(history, "filter");
                            filter.Text = "no-matching-commit-" + Guid.NewGuid().ToString("N");
                            Application.DoEvents();
                            Require(revisions.Items.Count == 0 && ((ListView)Field(history, "changedFiles")).Items.Count == 0 && !((Button)Field(history, "restore")).Enabled, "Empty history filter clears stale details and disables restore");
                            Require(typeof(HistoryForm).GetMethod("SelectedRevisionText", historyFlags).Invoke(history, new object[] { false }) == null &&
                                typeof(HistoryForm).GetMethod("SelectedFilePath", historyFlags).Invoke(history, new object[] { false }) == null && !((ToolStripMenuItem)Field(history, "compareMarkedFile")).Enabled,
                                "Empty history selection cannot copy stale identifiers or compare a stale file");
                            filter.Clear();
                            WaitUntil(() => ((Button)Field(history, "restore")).Enabled, "Clearing history filter reloads details");
                            Require(revisions.Items.Count == total, "Clearing history filter restores complete loaded history");
                            Require((long?)Field(history, "comparisonChangeset") == marked.Changeset, "Comparison mark survives history filtering and refresh in the same repository");
                            if (revisions.Items.Count > 2)
                            {
                                revisions.Items[0].Selected = false; revisions.Items[1].Selected = true;
                                Application.DoEvents();
                                revisions.Items[1].Selected = false; revisions.Items[2].Selected = true;
                                var selected = (PlasticHistoryItem)revisions.Items[2].Tag;
                                WaitUntil(() => ((Button)Field(history, "restore")).Enabled, "Rapid history selection finishes loading");
                                Require(((Label)Field(history, "status")).Text.StartsWith("cs:" + selected.Changeset + " ·", StringComparison.Ordinal), "History detail response matches latest selected row");
                            }
                            Save(history, Path.Combine(artifacts, "history.png"));
                            history.Size = history.MinimumSize;
                            Application.DoEvents();
                            var restore = (Button)Field(history, "restore");
                            Require(history.RectangleToScreen(history.ClientRectangle).Contains(restore.RectangleToScreen(restore.ClientRectangle)), "History restore button remains visible at minimum size");
                            foreach (string name in new[] { "refreshHistory", "loadMore", "cancelHistory", "close" })
                            {
                                var button = (Button)Field(history, name);
                                Rectangle bounds = button.RectangleToScreen(button.ClientRectangle);
                                Require(history.RectangleToScreen(history.ClientRectangle).Contains(bounds) && button.Parent.RectangleToScreen(button.Parent.ClientRectangle).Contains(bounds),
                                    "History " + name + " remains visible without clipping at minimum size");
                            }
                            Require(((Label)Field(history, "historySummary")).Text.Contains("已扫描") && ((Label)Field(history, "historySummary")).Text.Contains("已加载"), "History summary distinguishes scanned and loaded records");
                            var summary = (Label)Field(history, "historySummary");
                            Require(summary.Width > 250 && summary.Height >= summary.Font.Height && summary.Visible, "History completeness summary has visible readable bounds at minimum size");
                            var snapshot = (Button)Field(history, "snapshot");
                            Require(snapshot.Visible && snapshot.Text != restore.Text, "Workspace history exposes pending rollback separately from snapshot switching");
                            var changed = (ListView)Field(history, "changedFiles");
                            var readable = changed.Items.Cast<ListViewItem>().FirstOrDefault(item => ((PlasticChangesetFile)item.Tag).ItemType == "F" && ((PlasticChangesetFile)item.Tag).Status != "D");
                            // Structural-only commits are common in the integration repository.
                            // Locate a real file-bearing commit so comparison coverage never silently skips.
                            foreach (var candidate in revisions.Items.Cast<ListViewItem>().Take(10).ToArray())
                            {
                                if (readable != null) break;
                                foreach (ListViewItem selectedRow in revisions.SelectedItems) selectedRow.Selected = false;
                                candidate.Selected = true;
                                WaitUntil(() => ((Button)Field(history, "restore")).Enabled, "File-bearing changeset candidate loads");
                                readable = changed.Items.Cast<ListViewItem>().FirstOrDefault(item => ((PlasticChangesetFile)item.Tag).ItemType == "F" && ((PlasticChangesetFile)item.Tag).Status != "D");
                            }
                            Require(readable != null, "Integration history includes a readable file for comparison behavior coverage");
                            if (readable != null)
                            {
                                readable.Selected = true;
                                Require(((Button)Field(history, "historicalFile")).Enabled, "Selecting a historical file enables compare and export");
                                var file = (PlasticChangesetFile)readable.Tag;
                                long revision = ((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset;
                                Require((string)typeof(HistoryForm).GetMethod("SelectedFilePath", historyFlags).Invoke(history, new object[] { false }) == file.Path,
                                    "Changed-file copy action returns the selected repository path");
                                Require(((ToolStripMenuItem)Field(history, "compareMarkedFile")).Enabled, "File context comparison becomes available for a marked changeset");
                                using (var markedForm = (HistoricalFileForm)typeof(HistoryForm).GetMethod("CreateHistoricalFileDialog", historyFlags).Invoke(history, new object[] { true }))
                                {
                                    Prepare(markedForm);
                                    Require(((NumericUpDown)Field(markedForm, "fromRevision")).Value == marked.Changeset && ((NumericUpDown)Field(markedForm, "toRevision")).Value == revision,
                                        "Marked-file comparison uses the marked source and currently selected destination");
                                    var suggestion = (System.Threading.Tasks.Task)typeof(HistoricalFileForm).GetMethod("SuggestEarlierRevisionAsync", historyFlags).Invoke(markedForm, new object[] { revision });
                                    WaitUntil(() => suggestion.IsCompleted, "Explicit comparison does not wait for a historical suggestion");
                                    Require(((NumericUpDown)Field(markedForm, "fromRevision")).Value == marked.Changeset, "Automatic earlier-version suggestion cannot overwrite an explicit comparison source");
                                    markedForm.Close();
                                }
                                using (var pathHistory = (HistoryForm)typeof(HistoryForm).GetMethod("CreateSelectedPathHistory", historyFlags).Invoke(history, null))
                                    Require((string)Field(pathHistory, "path") == Path.Combine(args[1], file.Path.TrimStart('/').Replace('/', '\\')) && !(bool)Field(pathHistory, "wholeWorkspace"),
                                        "Changed-file history targets its own validated workspace path rather than the entire repository");
                                using (var fileForm = new HistoricalFileForm(new PlasticClient(PlasticClientConfig.Load()), args[1], file.Path, revision, revision))
                                {
                                    Prepare(fileForm);
                                    var comparison = (System.Threading.Tasks.Task)typeof(HistoricalFileForm).GetMethod("CompareAsync", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(fileForm, new object[] { false });
                                    WaitUntil(() => comparison.IsCompleted, "Historical file comparison finishes without a dialog");
                                    Require(((TextBox)Field(fileForm, "preview")).Text == "两个版本的文件内容相同。", "Historical comparison renders exact same-version result");
                                    Save(fileForm, Path.Combine(artifacts, "historical-file.png"));
                                    fileForm.Size = fileForm.MinimumSize; Application.DoEvents();
                                    Save(fileForm, Path.Combine(artifacts, "historical-file-minimum.png"));
                                    fileForm.Close();
                                }
                            }
                            foreach (ListViewItem row in changed.SelectedItems) row.Selected = false;
                            var missingPath = "/TortoiseSCM-deleted-history-" + Guid.NewGuid().ToString("N") + ".txt";
                            var deletedRow = new ListViewItem(new[] { "D", missingPath, "", "F" }) { Tag = new PlasticChangesetFile { Status = "D", Path = missingPath, ItemType = "F" } };
                            changed.Items.Add(deletedRow); deletedRow.Selected = true; Application.DoEvents();
                            using (var deletedHistory = (HistoryForm)typeof(HistoryForm).GetMethod("CreateSelectedPathHistory", historyFlags).Invoke(history, null))
                                Require(!File.Exists((string)Field(deletedHistory, "path")) && ((string)Field(deletedHistory, "path")).EndsWith(missingPath.Substring(1)),
                                    "Deleted historical paths can open path history without requiring a current file");
                            ((PlasticChangesetFile)deletedRow.Tag).Path = "/../outside.txt";
                            try
                            {
                                typeof(HistoryForm).GetMethod("CreateSelectedPathHistory", historyFlags).Invoke(history, null);
                                throw new Exception("Historical path traversal was accepted");
                            }
                            catch (TargetInvocationException ex) { Require(ex.InnerException is ArgumentException, "Historical path navigation rejects traversal before opening a dialog"); }
                            ((PlasticChangesetFile)deletedRow.Tag).Path = "/.plastic/plastic.selector";
                            try
                            {
                                typeof(HistoryForm).GetMethod("CreateSelectedPathHistory", historyFlags).Invoke(history, null);
                                throw new Exception("Historical metadata path was accepted");
                            }
                            catch (TargetInvocationException ex) { Require(ex.InnerException is ArgumentException, "Historical path navigation reuses backend metadata protection"); }
                            deletedRow.Remove();
                            revisions.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "清除比较标记").PerformClick();
                            Require(Field(history, "comparisonChangeset") == null && !((ToolStripMenuItem)Field(history, "compareMarkedFile")).Enabled && !((ToolStripMenuItem)Field(history, "compareMarkedChangeset")).Enabled,
                                "Clearing a comparison mark disables the marked-file action");
                            object[] refreshKeys = { Message.Create(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero), Keys.F5 };
                            Require((bool)typeof(HistoryForm).GetMethod("ProcessCmdKey", historyFlags).Invoke(history, refreshKeys), "History consumes the F5 refresh shortcut");
                            WaitUntil(() => !(bool)Field(history, "loadingHistory"), "F5 history refresh finishes");
                            Require(((Button)Field(history, "restore")).Enabled && revisions.Items.Count <= 50, "F5 refresh restores the newest page with coherent selected details");
                            Save(history, Path.Combine(artifacts, "history-minimum.png"));
                            history.Close();
                        }
                    }
                    finally { File.Delete(sample); }
                }
                Console.WriteLine("PASS: UI argument handoff, workspace load and window rendering");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
                if (uiContext != null) uiContext.Dispose();
                WindowsFormsSynchronizationContext.AutoInstall = previousAutoInstall;
            }
        }
        private static void CheckConflictDialogs(string artifacts)
        {
            using (var choice = new PartialStructureChoiceForm("本地：/中文目录/file.txt\r\n传入：/中文目录/file.txt\r\n双方分别新增了同名文件。\r\n请检查已保存的备份和处理方式。", new[] { "take-incoming", "keep-local", "rename", "unknown" }, "add-add"))
            {
                Prepare(choice);
                var choices = (ComboBox)Field(choice, "choices");
                var rename = (TextBox)Field(choice, "rename");
                var accept = (Button)choice.AcceptButton;
                Require(choices.Items.Count == 3 && choices.SelectedIndex == -1 && !accept.Enabled, "Partial structural choice requires explicit supported selection");
                choices.SelectedIndex = 2;
                Require(rename.Enabled && !accept.Enabled, "Partial rename requires a nonempty reviewed name");
                rename.Text = "local-copy.txt";
                Require(accept.Enabled && choice.Rename == "local-copy.txt", "Partial rename exposes the reviewed name");
                Save(choice, Path.Combine(artifacts, "partial-structure-choice.png"));
                choice.Size = choice.MinimumSize; Application.DoEvents();
                Require(choice.RectangleToScreen(choice.ClientRectangle).Contains(accept.RectangleToScreen(accept.ClientRectangle)), "Partial structural confirmation remains visible at minimum size");
                Save(choice, Path.Combine(artifacts, "partial-structure-choice-minimum.png"));
                choices.SelectedIndex = 0;
                Require(accept.Enabled && choice.Rename == null && !rename.Enabled, "Taking incoming cannot accidentally retain the rename argument");
                choice.Close();
            }
            using (var choice = new PartialStructureChoiceForm("本地：/美术/人物/local.txt\r\n原始：/设计/original.txt\r\n服务器修改了原文件。请核对文件的新位置和内容来源。", new[] { "keep-local", "take-incoming", "rename" }, "local-move"))
            {
                Prepare(choice); ((ComboBox)Field(choice, "choices")).SelectedIndex = 2;
                Require(!choice.ChoiceText.Contains("保留双方") && ((Label)Field(choice, "explanation")).Text.Contains("原路径不再保留"), "Local move rename explains relocation without promising two copies");
                Require(((Label)Field(choice, "explanation")).Text.Contains("/目录/文件名") && ((Label)Field(choice, "explanation")).Text.Contains("本地移动后的目录"), "Cross-directory rename explains repository paths and relative filename base");
                ((TextBox)Field(choice, "rename")).Text = "/美术/已审核/人物.txt";
                Require(choice.Rename == "/美术/已审核/人物.txt" && ((Button)choice.AcceptButton).Enabled, "Cross-directory choice passes the reviewed repository path unchanged");
                Save(choice, Path.Combine(artifacts, "partial-cross-directory-choice.png"));
                choice.Size = choice.MinimumSize; Application.DoEvents();
                var explanation = (Label)Field(choice, "explanation");
                Require(explanation.ClientSize.Height >= TextRenderer.MeasureText(explanation.Text, explanation.Font, new Size(explanation.ClientSize.Width, Int32.MaxValue), TextFormatFlags.WordBreak).Height,
                    "Cross-directory explanation fits at minimum dialog size");
                Save(choice, Path.Combine(artifacts, "partial-cross-directory-choice-minimum.png"));
                ((ComboBox)Field(choice, "choices")).SelectedIndex = 0;
                Require(((Label)Field(choice, "explanation")).Text.Contains("采用备份中的本地内容"), "Local move decision explains content edits replacing incoming bytes");
                choice.Close();
            }
            using (var choice = new PartialStructureChoiceForm("本地：/设计/人物.txt\r\n服务器新位置：/美术/人物.txt\r\n服务器移动了文件，本地内容也有修改。", new[] { "take-incoming", "keep-local" }, "incoming-move"))
            {
                Prepare(choice); var choices = (ComboBox)Field(choice, "choices");
                Require(choices.SelectedIndex == -1 && choices.Items.Count == 2 && !((Button)choice.AcceptButton).Enabled, "Incoming move requires an explicit supported content choice");
                choices.SelectedIndex = 1;
                Require(choice.Rename == null && !((TextBox)Field(choice, "rename")).Enabled && ((Label)Field(choice, "explanation")).Text.Contains("在新位置采用备份中的本地内容"), "Incoming move keep-local explains server destination and local content");
                Save(choice, Path.Combine(artifacts, "partial-incoming-move-choice.png"));
                choice.Size = choice.MinimumSize; Application.DoEvents();
                Save(choice, Path.Combine(artifacts, "partial-incoming-move-choice-minimum.png"));
                choice.Close();
            }
            using (var directory = new DirectoryConflictForm("两个分支分别新增同名文件，请核对双方路径后选择。", "/中文 空格/collision.txt", "/中文 空格/collision.txt", new[] { "src", "dst", "rename", "unknown", "src" }))
            {
                Prepare(directory);
                var choices = (ComboBox)Field(directory, "choices");
                var rename = (TextBox)Field(directory, "rename");
                var accept = (Button)directory.AcceptButton;
                Require(choices.Items.Count == 3 && choices.SelectedIndex == -1 && !accept.Enabled, "Directory resolution filters unsupported choices and requires explicit selection");
                choices.SelectedIndex = 2; Application.DoEvents();
                Require(rename.Enabled && directory.Resolution == "rename", "Rename choice enables explicit destination name");
                accept.PerformClick(); Application.DoEvents();
                Require(!directory.IsDisposed && directory.DialogResult != DialogResult.OK, "Blank rename cannot confirm structural resolution");
                rename.Text = "  collision-local.txt  ";
                Require(directory.Rename == "collision-local.txt", "Reviewed rename trims surrounding whitespace");
                Save(directory, Path.Combine(artifacts, "directory-conflict.png"));
                directory.Size = directory.MinimumSize; Application.DoEvents();
                Require(directory.RectangleToScreen(directory.ClientRectangle).Contains(accept.RectangleToScreen(accept.ClientRectangle)), "Directory confirmation remains visible at minimum size");
                Save(directory, Path.Combine(artifacts, "directory-conflict-minimum.png"));
                choices.SelectedIndex = 1; Application.DoEvents();
                Require(!rename.Enabled && directory.Rename == null && directory.Resolution == "dst", "Destination choice cannot retain a stale rename argument");
                directory.Close();
            }
            using (var directory = new DirectoryConflictForm("unsupported", "/a", "/b", new[] { "future-option" }))
            {
                Prepare(directory);
                Require(!((Button)directory.AcceptButton).Enabled, "Unknown structural resolution cannot be accepted");
                directory.Close();
            }
            string invalidRoot = Path.Combine(Path.GetTempPath(), "tscm-ui-missing-" + Guid.NewGuid().ToString("N"));
            using (var directory = new PartialDirectoryForm(new PlasticClient(PlasticClientConfig.Load()), invalidRoot))
            {
                Prepare(directory); WaitUntil(() => !(bool)Field(directory, "busy"), "Partial directory unavailable workspace lookup completes");
                Require(!((Button)Field(directory, "prepare")).Enabled && !((Button)Field(directory, "recover")).Enabled, "Unavailable directory session cannot mutate files");
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var row = new PlasticPartialDirectoryConflict { RepositoryPath = "/中文目录", IncomingPath = "/资源/中文目录", Kind = "incoming-directory-move", IncomingChangeset = 30,
                    Reason = "完整加载的目录随服务器移动；本地修改由明确选择处理。", ResolutionOptions = new[] { "take-incoming", "keep-local" },
                    Items = new[] {
                        new PlasticPartialDirectoryItem { RepositoryPath = "/中文目录", IncomingPath = "/资源/中文目录", IsDirectory = true, ItemId = 91 },
                        new PlasticPartialDirectoryItem { RepositoryPath = "/中文目录/readme.txt", IncomingPath = "/资源/中文目录/readme.txt", HasLocalChanges = true, ItemId = 92 },
                        new PlasticPartialDirectoryItem { RepositoryPath = "/中文目录/sub/clean.txt", IncomingPath = "/资源/中文目录/sub/clean.txt", ItemId = 93 } } };
                typeof(PartialDirectoryForm).GetField("session", flags).SetValue(directory, null);
                typeof(PartialDirectoryForm).GetField("conflicts", flags).SetValue(directory, new System.Collections.Generic.List<PlasticPartialDirectoryConflict> { row });
                typeof(PartialDirectoryForm).GetMethod("Render", flags).Invoke(directory, null);
                ((ListView)Field(directory, "directories")).Items[0].Selected = true; Application.DoEvents();
                Require(((ListView)Field(directory, "descendants")).Items.Count == 3 && ((Button)Field(directory, "prepare")).Enabled && !((Button)Field(directory, "apply")).Enabled,
                    "Directory preview lists full scope and requires backup before application");
                var session = new PlasticPartialDirectorySession { SessionId = "UI-directory", Conflict = row, Ready = true, RecoveryDirectory = @"C:\UI-fixture\directory-backups" };
                typeof(PartialDirectoryForm).GetField("session", flags).SetValue(directory, session);
                typeof(PartialDirectoryForm).GetMethod("Render", flags).Invoke(directory, null);
                var choices = (ComboBox)Field(directory, "resolution");
                Require(choices.Enabled && choices.SelectedIndex == -1 && !((Button)Field(directory, "apply")).Enabled, "Prepared directory session never defaults to a destructive choice");
                choices.SelectedIndex = 1;
                Require(((Button)Field(directory, "apply")).Enabled && choices.Text.Contains("本地已修改") && ((TextBox)Field(directory, "details")).Text.Contains(session.RecoveryDirectory), "Directory keep-local explains edited files and exposes recovery location");
                Save(directory, Path.Combine(artifacts, "partial-directory.png")); directory.Size = directory.MinimumSize; Application.DoEvents();
                foreach (string name in new[] { "refresh", "prepare", "apply", "cancel", "recover", "close", "resolution" })
                {
                    var control = (Control)Field(directory, name); var bounds = control.RectangleToScreen(control.ClientRectangle);
                    Require(control.Parent.RectangleToScreen(control.Parent.ClientRectangle).Contains(bounds), "Partial directory " + name + " visible at minimum size");
                }
                Save(directory, Path.Combine(artifacts, "partial-directory-minimum.png"));
                row.Kind = "incoming-directory-delete"; row.IncomingPath = "";
                foreach (var child in row.Items) child.IncomingPath = "";
                row.Reason = "服务器已删除目录；选择保留时重新添加本地树。";
                typeof(PartialDirectoryForm).GetMethod("Render", flags).Invoke(directory, null);
                Require(choices.SelectedIndex == -1 && !((Button)Field(directory, "apply")).Enabled, "Deleted directory never defaults to removing or re-adding the tree");
                choices.SelectedIndex = 1;
                var scopeList = (ListView)Field(directory, "descendants");
                Require(choices.Text.Contains("新项待提交") && ((TextBox)Field(directory, "details")).Text.Contains("新的版本控制身份") &&
                    scopeList.Items.Cast<ListViewItem>().All(item => item.SubItems[1].Text.Contains("新添加")), "Keep-deleted preview describes new identities and shows original paths as new additions");
                directory.Size = new Size(1040, 760); Application.DoEvents();
                Save(directory, Path.Combine(artifacts, "partial-directory-keep-deleted.png"));
                directory.Size = directory.MinimumSize; Application.DoEvents();
                Save(directory, Path.Combine(artifacts, "partial-directory-keep-deleted-minimum.png"));
                choices.SelectedIndex = 0;
                Require(scopeList.Items.Cast<ListViewItem>().All(item => item.SubItems[1].Text == "（删除）") && ((TextBox)Field(directory, "details")).Text.Contains("移除目录树"),
                    "Switching to take-incoming updates every descendant outcome to deletion");
                session.Ready = false; session.Applying = true;
                typeof(PartialDirectoryForm).GetMethod("Render", flags).Invoke(directory, null);
                Require(!((Button)Field(directory, "apply")).Enabled && !((Button)Field(directory, "cancel")).Enabled && ((Button)Field(directory, "recover")).Enabled && !choices.Enabled, "Interrupted directory session permits only explicit recovery");
                session = null; row.ResolutionOptions = new string[0]; row.Reason = "目录含未加载后代，暂不能处理。";
                typeof(PartialDirectoryForm).GetField("session", flags).SetValue(directory, null);
                typeof(PartialDirectoryForm).GetMethod("Render", flags).Invoke(directory, null);
                ((ListView)Field(directory, "directories")).Items[0].Selected = true; Application.DoEvents();
                Require(!((Button)Field(directory, "prepare")).Enabled && ((TextBox)Field(directory, "details")).Text.Contains("未加载"), "Unsupported directory scope remains visible with a reason and cannot prepare");
                directory.Close();
            }
            using (var structure = new PartialStructureForm(new PlasticClient(PlasticClientConfig.Load()), invalidRoot))
            {
                Prepare(structure); WaitUntil(() => !(bool)Field(structure, "busy"), "Structure unavailable workspace lookup completes");
                Require(!((Button)Field(structure, "prepare")).Enabled && !((Button)Field(structure, "recover")).Enabled, "Unavailable structure session cannot mutate files");
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var row = new PlasticPartialStructureConflict { RepositoryPath = "/中文目录/local.txt", OriginalPath = "/中文目录/original.txt", IncomingPath = "/中文目录/original.txt",
                    Kind = "local-move", BaseChangeset = 20, IncomingChangeset = 22, ItemId = 99, Reason = "本地移动与服务器修改重叠。", ResolutionOptions = new[] { "keep-local", "take-incoming" } };
                typeof(PartialStructureForm).GetField("session", flags).SetValue(structure, null);
                typeof(PartialStructureForm).GetField("conflicts", flags).SetValue(structure, new System.Collections.Generic.List<PlasticPartialStructureConflict> { row });
                typeof(PartialStructureForm).GetMethod("Render", flags).Invoke(structure, null);
                ((ListView)Field(structure, "items")).Items[0].Selected = true; Application.DoEvents();
                Require(((Button)Field(structure, "prepare")).Enabled && !((Button)Field(structure, "apply")).Enabled, "Structural preview requires backup preparation before decisions");
                var session = new PlasticPartialStructureSession { SessionId = "UI-structure", Conflict = row, Ready = true, RecoveryDirectory = @"C:\UI-fixture\structure-backups" };
                typeof(PartialStructureForm).GetField("session", flags).SetValue(structure, session);
                typeof(PartialStructureForm).GetMethod("Render", flags).Invoke(structure, null);
                Require(!((Button)Field(structure, "prepare")).Enabled && ((Button)Field(structure, "apply")).Enabled && ((Button)Field(structure, "cancel")).Enabled && !((Button)Field(structure, "recover")).Enabled,
                    "Prepared structural session permits only explicit decision or cancellation");
                Save(structure, Path.Combine(artifacts, "partial-structure.png")); structure.Size = structure.MinimumSize; Application.DoEvents();
                foreach (string name in new[] { "refresh", "prepare", "apply", "cancel", "recover", "close" })
                {
                    var button = (Button)Field(structure, name); var bounds = button.RectangleToScreen(button.ClientRectangle);
                    Require(button.Parent.RectangleToScreen(button.Parent.ClientRectangle).Contains(bounds), "Structure " + name + " visible at minimum size");
                }
                Save(structure, Path.Combine(artifacts, "partial-structure-minimum.png"));
                session.Ready = false; session.Applying = true;
                typeof(PartialStructureForm).GetMethod("Render", flags).Invoke(structure, null);
                Require(!((Button)Field(structure, "apply")).Enabled && !((Button)Field(structure, "cancel")).Enabled && ((Button)Field(structure, "recover")).Enabled && ((TextBox)Field(structure, "details")).Text.Contains(session.RecoveryDirectory),
                    "Interrupted structural session exposes backup and explicit recovery, not normal application");
                structure.Close();
            }
            using (var partial = new PartialConflictForm(new PlasticClient(PlasticClientConfig.Load()), invalidRoot))
            {
                Prepare(partial);
                WaitUntil(() => !(bool)Field(partial, "busy"), "Partial unavailable workspace read completes safely");
                Require(!((Button)Field(partial, "apply")).Enabled && !((Button)Field(partial, "prepare")).Enabled, "Failed Partial lookup does not permit mutation");
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var rows = new System.Collections.Generic.List<PlasticPartialConflict> {
                    new PlasticPartialConflict { RepositoryPath = "/中文 空格/conflict.txt", BaseChangeset = 16, IncomingChangeset = 18, CanResolve = true },
                    new PlasticPartialConflict { RepositoryPath = "/结构变化.txt", BaseChangeset = 16, IncomingChangeset = 18, CanResolve = false, Reason = "传入项结构已变化，请先核对。" }
                };
                typeof(PartialConflictForm).GetField("conflicts", flags).SetValue(partial, rows);
                typeof(PartialConflictForm).GetField("session", flags).SetValue(partial, null);
                typeof(PartialConflictForm).GetMethod("RenderConflicts", flags).Invoke(partial, null);
                var items = (ListView)Field(partial, "items");
                items.Items[0].Selected = true; Application.DoEvents();
                Require(((Button)Field(partial, "prepare")).Enabled && !((Button)Field(partial, "apply")).Enabled && !((Button)Field(partial, "cancelPreparation")).Enabled,
                    "Partial new conflict requires preparation before result application");
                ((TextBox)Field(partial, "details")).Text = "先在三方工具中编辑独立结果文件，再明确确认应用。\r\n结果保留为待定更改，需要单独签入。\r\n原始内容与审核结果保存在会话恢复目录中。";
                Save(partial, Path.Combine(artifacts, "partial-conflicts.png"));
                partial.Size = partial.MinimumSize; Application.DoEvents();
                foreach (string name in new[] { "refresh", "prepare", "apply", "cancelPreparation", "close" })
                {
                    var button = (Button)Field(partial, name);
                    var bounds = button.RectangleToScreen(button.ClientRectangle);
                    Require(partial.RectangleToScreen(partial.ClientRectangle).Contains(bounds) && button.Parent.RectangleToScreen(button.Parent.ClientRectangle).Contains(bounds),
                        "Partial " + name + " remains visible at minimum size");
                }
                Save(partial, Path.Combine(artifacts, "partial-conflicts-minimum.png"));
                items.Items[0].Selected = false; items.Items[1].Selected = true; Application.DoEvents();
                Require(!((Button)Field(partial, "prepare")).Enabled && !((Button)Field(partial, "apply")).Enabled, "Unsupported Partial structure cannot enter content resolution");
                items.Items[1].Selected = false; items.Items[0].Selected = true;
                var session = new PlasticPartialConflictSession { SessionId = "UI-partial-fixture", Ready = true, Conflicts = rows.Take(1).ToList(), RecoveryDirectory = @"C:\UI-fixture\recovery" };
                typeof(PartialConflictForm).GetField("session", flags).SetValue(partial, session);
                typeof(PartialConflictForm).GetMethod("UpdateButtons", flags).Invoke(partial, null);
                Require(((Button)Field(partial, "cancelPreparation")).Enabled && ((Button)Field(partial, "apply")).Enabled, "Unapplied Partial preparation permits reviewed apply and explicit cancellation");
                session.Applying = true;
                typeof(PartialConflictForm).GetMethod("RenderConflicts", flags).Invoke(partial, null);
                Require(!((Button)Field(partial, "prepare")).Enabled && !((Button)Field(partial, "apply")).Enabled && !((Button)Field(partial, "cancelPreparation")).Enabled && ((TextBox)Field(partial, "details")).Text.Contains(session.RecoveryDirectory),
                    "Interrupted Partial apply disables writes and displays durable recovery directory");
                session.Applying = false; rows[0].Resolved = true;
                typeof(PartialConflictForm).GetMethod("RenderConflicts", flags).Invoke(partial, null);
                Require(!((Button)Field(partial, "prepare")).Enabled && !((Button)Field(partial, "apply")).Enabled && !((Button)Field(partial, "cancelPreparation")).Enabled,
                    "Applied Partial result cannot be reapplied or discarded as an unused preparation");
                var newer = new PlasticPartialConflict { RepositoryPath = rows[0].RepositoryPath, BaseChangeset = 18, IncomingChangeset = 19, CanResolve = true };
                var combined = (System.Collections.Generic.IList<PlasticPartialConflict>)typeof(PartialConflictForm).GetMethod("CombineConflicts", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { new System.Collections.Generic.List<PlasticPartialConflict> { newer }, session });
                typeof(PartialConflictForm).GetField("conflicts", flags).SetValue(partial, combined);
                typeof(PartialConflictForm).GetMethod("RenderConflicts", flags).Invoke(partial, null);
                items.Items[0].Selected = true; Application.DoEvents();
                Require(combined.Count == 1 && Object.ReferenceEquals(combined[0], newer) && ((Button)Field(partial, "prepare")).Enabled && !((Button)Field(partial, "apply")).Enabled && items.Items[0].SubItems[3].Text == "待准备三方文件",
                    "New incoming revision supersedes saved resolved row and requires fresh preparation before apply");
                newer.CanResolve = false; newer.Reason = "Incoming replacement";
                typeof(PartialConflictForm).GetMethod("RenderConflicts", flags).Invoke(partial, null);
                Require(!((Button)Field(partial, "prepare")).Enabled && !((Button)Field(partial, "apply")).Enabled,
                    "Fresh unsupported incoming identity cannot inherit saved resolvability");
                rows[0].Resolved = false;
                var failure = new Func<System.Threading.Tasks.Task>(delegate { throw new IOException("UI simulated operation failure"); });
                var recovery = (System.Threading.Tasks.Task)typeof(PartialConflictForm).GetMethod("WorkAsync", flags).Invoke(partial, new object[] { failure });
                WaitUntil(() => recovery.IsCompleted, "Partial failed operation finishes recovery lookup");
                Require(!recovery.IsFaulted && !((Button)Field(partial, "apply")).Enabled && !((Button)Field(partial, "cancelPreparation")).Enabled && ((TextBox)Field(partial, "details")).Text.Contains(session.RecoveryDirectory),
                    "Failed recovery lookup preserves known backup directory and fails closed");
                typeof(PartialConflictForm).GetField("busy", flags).SetValue(partial, true);
                typeof(PartialConflictForm).GetMethod("UpdateButtons", flags).Invoke(partial, null);
                Require(!((Button)Field(partial, "refresh")).Enabled && !((Button)Field(partial, "close")).Enabled, "Partial operation blocks concurrent refresh and premature close");
                typeof(PartialConflictForm).GetField("busy", flags).SetValue(partial, false);
                partial.Close();
            }
        }
        private static void CheckLiveChangesetComparison(string artifacts, string workspacePath, long from, long to)
        {
            var client = new PlasticClient(PlasticClientConfig.Load());
            var workspace = client.DiscoverWorkspace(workspacePath);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using (var form = new ChangesetComparisonForm(client, workspacePath, workspace.Repository, from, to))
            {
                Prepare(form);
                WaitUntil(() => !(bool)Field(form, "busy"), "Live cross-changeset comparison completes");
                var comparison = (PlasticChangesetComparison)Field(form, "comparison");
                Require(comparison != null && comparison.Files.Count > 0, "Live complete repository comparison displays actual changed paths");
                Require(new[] { "A", "D", "C", "M" }.All(code => comparison.Files.Any(file => file.Status == code)),
                    "Live changeset comparison includes added, deleted, modified and moved entries");
                var files = (ListView)Field(form, "files");
                foreach (var file in comparison.Files.Where(item => item.ItemType == "F" && item.Status == "M"))
                {
                    foreach (ListViewItem selected in files.SelectedItems) selected.Selected = false;
                    files.Items.Cast<ListViewItem>().Single(row => Object.ReferenceEquals(row.Tag, file)).Selected = true;
                    using (var historical = (HistoricalFileForm)typeof(ChangesetComparisonForm).GetMethod("CreateFileDialog", flags).Invoke(form, null))
                    {
                        Prepare(historical);
                        var operation = (System.Threading.Tasks.Task)typeof(HistoricalFileForm).GetMethod("CompareAsync", flags).Invoke(historical, new object[] { false });
                        WaitUntil(() => operation.IsCompleted, "Live moved file content comparison finishes");
                        Require(!operation.IsFaulted && ((Label)Field(historical, "status")).Text == "比较完成。", "Live moved file comparison reads old and new paths: " + file.Path);
                        if (file.Path == "/stable-moved.txt")
                            Require(((TextBox)Field(historical, "preview")).Text == "两个版本的文件内容相同。", "Move without edits retains equal content across distinct paths");
                        else
                            Require(((TextBox)Field(historical, "preview")).Text.Contains("--- " + file.OldPath) && ((TextBox)Field(historical, "preview")).Text.Contains("+++ " + file.Path) && ((TextBox)Field(historical, "preview")).Lines.Length >= 5,
                                "Moved and edited file preview identifies both historical paths on distinct native text lines");
                        Save(historical, Path.Combine(artifacts, file.Path == "/stable-moved.txt" ? "live-stable-move.png" : "live-edited-move.png"));
                        historical.Close();
                    }
                }
                Save(form, Path.Combine(artifacts, "live-changeset-comparison.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); Save(form, Path.Combine(artifacts, "live-changeset-comparison-minimum.png"));
                Require(SynchronizationContext.Current is WindowsFormsSynchronizationContext,
                    "Live comparison retains its UI synchronization context after child windows close");
                using (var ordinary = new HistoricalFileForm(client, workspacePath, "/changed.txt", to, from))
                {
                    Prepare(ordinary);
                    Require((string)Field(ordinary, "expectedRepository") == workspace.Repository && (string)Field(ordinary, "expectedRoot") == workspace.RootPath,
                        "Ordinary historical file windows capture their repository and workspace context");
                    typeof(HistoricalFileForm).GetField("expectedRepository", flags).SetValue(ordinary, "changed-repository");
                    var operation = (System.Threading.Tasks.Task)typeof(HistoricalFileForm).GetMethod("CompareAsync", flags).Invoke(ordinary, new object[] { false });
                    WaitUntil(() => operation.IsCompleted, "Ordinary historical file context rejection finishes");
                    Require(((TextBox)Field(ordinary, "preview")).Text.Contains("仓库已改变"), "Ordinary historical file actions reject a changed repository");
                    ordinary.Close();
                }
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    ((Button)Field(form, "refresh")).PerformClick();
                    Require(files.Items.Count == 0 && Field(form, "comparison") == null && !((Button)Field(form, "open")).Enabled,
                        "Repeated comparison refresh clears stale actions " + attempt);
                    ((Button)Field(form, "cancel")).PerformClick();
                    WaitUntil(() => !(bool)Field(form, "busy"), "Repeated comparison cancellation completes " + attempt);
                    Require(files.Items.Count == 0 && Field(form, "comparison") == null && !((Button)Field(form, "open")).Enabled && ((Button)Field(form, "refresh")).Enabled,
                        "Repeated cancelled comparison is empty and retryable " + attempt);
                }
                form.Close();
            }
        }

        private static void CheckChangesetComparison(ChangesetComparisonForm form, string workspace, string artifacts, long revision)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Prepare(form);
            WaitUntil(() => !(bool)Field(form, "busy"), "Changeset tree comparison completes");
            var comparison = (PlasticChangesetComparison)Field(form, "comparison");
            Require(comparison != null && comparison.Files.Count == 0, "Live same-changeset tree comparison has no differences");
            var list = (ListView)Field(form, "files");
            var rows = new[] {
                new PlasticChangesetFile { Status = "A", Path = "/目录/新增 文件.txt", ItemType = "F" },
                new PlasticChangesetFile { Status = "D", Path = "/目录/已删除.txt", ItemType = "F" },
                new PlasticChangesetFile { Status = "M", Path = "/目录/新名称.txt", OldPath = "/原目录/旧名称.txt", ItemType = "F" },
                new PlasticChangesetFile { Status = "C", Path = "/目录/修改.bin", ItemType = "B" },
                new PlasticChangesetFile { Status = "M", Path = "/新目录", OldPath = "/原目录", ItemType = "D" },
                new PlasticChangesetFile { Status = "C", Path = "/链接", ItemType = "S" },
                new PlasticChangesetFile { Status = "C", Path = "/挂载", ItemType = "X" }
            };
            comparison.Files = rows;
            typeof(ChangesetComparisonForm).GetMethod("RenderFiles", flags).Invoke(form, null);
            for (int index = 0; index < rows.Length; index++)
            {
                foreach (ListViewItem selected in list.SelectedItems) selected.Selected = false;
                list.Items[index].Selected = true; Application.DoEvents();
                Require(((Button)Field(form, "open")).Enabled == (index < 4), "Tree comparison gates historical content for item type " + rows[index].ItemType);
                if (index >= 4) continue;
                using (var historical = (HistoricalFileForm)typeof(ChangesetComparisonForm).GetMethod("CreateFileDialog", flags).Invoke(form, null))
                {
                    Prepare(historical);
                    Require(!((NumericUpDown)Field(historical, "fromRevision")).Enabled && !((NumericUpDown)Field(historical, "toRevision")).Enabled,
                        "Tree file actions keep their validated comparison endpoints fixed");
                    Require(((Button)Field(historical, "compare")).Enabled == (index >= 2) && ((Button)Field(historical, "external")).Enabled == (index >= 2),
                        "Added and deleted items cannot compare a fabricated empty side");
                    Require(((Button)Field(historical, "exportSource")).Enabled == (index != 0) && ((Button)Field(historical, "export")).Enabled == (index != 1),
                        "History export is enabled only for existing endpoint content");
                    if (index == 2)
                    {
                        Require((string)Field(historical, "fromRepositoryPath") == rows[index].OldPath && (string)Field(historical, "repositoryPath") == rows[index].Path,
                            "Moved files compare the original and destination paths");
                        Save(historical, Path.Combine(artifacts, "changeset-moved-file.png"));
                        historical.Size = historical.MinimumSize; Application.DoEvents();
                        foreach (var button in new[] { "compare", "external", "export", "exportSource" })
                        {
                            var control = (Button)Field(historical, button);
                            Require(control.Parent.RectangleToScreen(control.Parent.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)),
                                "Historical endpoint " + button + " remains visible at minimum size");
                        }
                        Save(historical, Path.Combine(artifacts, "changeset-moved-file-minimum.png"));
                    }
                    historical.Close();
                }
            }
            ((TextBox)Field(form, "filter")).Text = "旧名称";
            Require(list.Items.Count == 1 && ((PlasticChangesetFile)list.Items[0].Tag).Status == "M", "Comparison filter matches moved-file original paths");
            ((TextBox)Field(form, "filter")).Text = "no-match";
            Require(list.Items.Count == 0 && !((Button)Field(form, "open")).Enabled, "Empty comparison filter disables stale file actions");
            ((TextBox)Field(form, "filter")).Clear();
            list.Items[2].Selected = true; Application.DoEvents();
            Save(form, Path.Combine(artifacts, "changeset-comparison.png"));
            form.Size = form.MinimumSize; Application.DoEvents();
            foreach (var name in new[] { "refresh", "cancel", "open", "close" })
            {
                var button = (Button)Field(form, name);
                Require(form.RectangleToScreen(form.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)) &&
                    button.Parent.RectangleToScreen(button.Parent.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)),
                    "Changeset comparison " + name + " remains visible at minimum size");
            }
            Save(form, Path.Combine(artifacts, "changeset-comparison-minimum.png"));
            ((Button)Field(form, "refresh")).PerformClick();
            Require(list.Items.Count == 0 && !((Button)Field(form, "open")).Enabled && Field(form, "comparison") == null,
                "Refreshing tree comparison immediately removes stale actionable results");
            ((Button)Field(form, "cancel")).PerformClick();
            WaitUntil(() => !(bool)Field(form, "busy"), "Tree comparison cancellation completes");
            Require(list.Items.Count == 0 && !((Button)Field(form, "open")).Enabled && ((Button)Field(form, "refresh")).Enabled,
                "Cancelled comparison stays empty and can be retried (rows=" + list.Items.Count + ", open=" + ((Button)Field(form, "open")).Enabled + ", refresh=" + ((Button)Field(form, "refresh")).Enabled + ", status=" + ((Label)Field(form, "status")).Text + ")");
            typeof(ChangesetComparisonForm).GetField("expectedRepository", flags).SetValue(form, "changed-repository");
            ((Button)Field(form, "refresh")).PerformClick();
            WaitUntil(() => !(bool)Field(form, "busy"), "Changed repository context fails promptly");
            Require(Field(form, "comparison") == null && ((Label)Field(form, "status")).Text.Contains("仓库已改变"),
                "Tree comparison rejects a repository context change before reading another repository");
            form.Close();
        }

        private static void CheckBranches(string artifacts, string workspace)
        {
            CheckBranchMergeDestinationRace(artifacts);
            Require(LaunchRequest.Parse(new[] { "--command", "branches", "--path", workspace }).Command == "branches", "Branch GUI launch is accepted");
            var client = new PlasticClient(PlasticClientConfig.Load());
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using (var form = new BranchForm(client, workspace))
            {
                Prepare(form);
                WaitUntil(() => !(bool)Field(form, "busy"), "Live branch list loads");
                var list = (ListView)Field(form, "branches");
                Require(list.Items.Count > 0 && !((Button)Field(form, "head")).Enabled, "Branch list loads without implicitly selecting an action");
                var current = list.Items.Cast<ListViewItem>().FirstOrDefault(row => ((PlasticBranch)row.Tag).IsCurrent);
                if (current != null)
                {
                    current.Selected = true; Application.DoEvents();
                    Require(!((Button)Field(form, "merge")).Enabled && !((Button)Field(form, "switchBranch")).Enabled && ((Button)Field(form, "head")).Enabled,
                        "Current branch permits head details but not redundant merge or switch");
                    CheckBranchHistory(artifacts, client, workspace, ((PlasticBranch)current.Tag).Name);
                }
                var selected = list.Items.Cast<ListViewItem>().FirstOrDefault(row => !((PlasticBranch)row.Tag).IsCurrent) ?? list.Items[0];
                foreach (ListViewItem row in list.SelectedItems) row.Selected = false;
                selected.Selected = true; Application.DoEvents();
                var branch = (PlasticBranch)selected.Tag;
                ((Button)Field(form, "head")).PerformClick();
                WaitUntil(() => !(bool)Field(form, "busy"), "Live branch head detail loads");
                Require(((TextBox)Field(form, "description")).Text.Contains(branch.Name) && ((Label)Field(form, "status")).Text.StartsWith("头提交 cs:"), "Branch head displays pinned changeset details");
                Save(form, Path.Combine(artifacts, "branches.png"));
                form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string name in new[] { "head", "merge", "switchBranch", "refresh", "cancel", "close" })
                {
                    var button = (Button)Field(form, name);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)) &&
                        button.Parent.RectangleToScreen(button.Parent.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)),
                        "Branch " + name + " is visible at minimum size");
                }
                Save(form, Path.Combine(artifacts, "branches-minimum.png"));
                var view = (ComboBox)Field(form, "viewMode"); var liveTree = (TreeView)Field(form, "branchTree");
                view.SelectedIndex = 1; Application.DoEvents();
                Require(liveTree.SelectedNode != null && Object.ReferenceEquals(liveTree.SelectedNode.Tag, branch), "Live hierarchy preserves the list selection by exact branch identity");
                if (((Button)Field(form, "locateCurrent")).Enabled) ((Button)Field(form, "locateCurrent")).PerformClick();
                var treeBranch = (PlasticBranch)liveTree.SelectedNode.Tag;
                ((Button)Field(form, "head")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Live hierarchy head details finish");
                Require(((TextBox)Field(form, "description")).Text.Contains(treeBranch.Name), "Live hierarchy head action displays the selected branch details");
                form.Size = new Size(1080, 730); Application.DoEvents(); Save(form, Path.Combine(artifacts, "branch-hierarchy-live.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); Save(form, Path.Combine(artifacts, "branch-hierarchy-live-minimum.png"));
                view.SelectedIndex = 0; Application.DoEvents();
                typeof(BranchForm).GetField("partial", flags).SetValue(form, true);
                typeof(BranchForm).GetMethod("UpdateButtons", flags).Invoke(form, null);
                Require(!((Button)Field(form, "merge")).Enabled && !((Button)Field(form, "switchBranch")).Enabled && ((Button)Field(form, "head")).Enabled,
                    "Partial branch browser permits detail reads but disables workspace switch and merge");
                Require(((ToolStripMenuItem)Field(form, "createChild")).Enabled && ((ToolStripMenuItem)Field(form, "branchHistory")).Enabled,
                    "Partial branch browser permits metadata creation and exact branch history");
                typeof(BranchForm).GetField("writing", flags).SetValue(form, true);
                form.Close(); Require(!form.IsDisposed && !((CancellationTokenSource)Field(form, "lifetime")).IsCancellationRequested,
                    "Branch switch cannot be cancelled by closing the window");
                typeof(BranchForm).GetField("writing", flags).SetValue(form, false);
                ((TextBox)Field(form, "filter")).Text = "no-match-" + Guid.NewGuid();
                Require(list.Items.Count == 0 && !((Button)Field(form, "head")).Enabled && ((ListView)Field(form, "files")).Items.Count == 0,
                    "Filtering removes stale selection and head details");
                ((TextBox)Field(form, "filter")).Clear();
                ((Button)Field(form, "refresh")).PerformClick();
                Require(list.Items.Count == 0 && !((Button)Field(form, "head")).Enabled, "Refreshing removes stale branch actions immediately");
                ((Button)Field(form, "cancel")).PerformClick();
                WaitUntil(() => !(bool)Field(form, "busy"), "Branch refresh cancellation completes");
                Require(list.Items.Count == 0 && ((Button)Field(form, "refresh")).Enabled, "Cancelled branch list remains empty and retryable");
                typeof(BranchForm).GetField("repository", flags).SetValue(form, "changed-repository");
                ((Button)Field(form, "refresh")).PerformClick();
                WaitUntil(() => !(bool)Field(form, "busy"), "Changed branch repository fails promptly");
                Require(((Label)Field(form, "status")).Text.Contains("仓库已改变") && list.Items.Count == 0, "Branch context change cannot read another repository");
                form.Close();
            }
            using (var merge = new MergeForm(client, workspace, 17, "/test-source"))
            {
                Prepare(merge); WaitUntil(() => !(bool)Field(merge, "busy"), "Fixed branch merge session lookup completes");
                Require(((NumericUpDown)Field(merge, "source")).Value == 17 && !((NumericUpDown)Field(merge, "source")).Enabled &&
                    Field(merge, "plan") == null && !((Button)Field(merge, "start")).Enabled, "Branch merge fixes source and requires explicit preview before start");
                typeof(MergeForm).GetField("fixedSelector", flags).SetValue(merge, "changed-selector");
                ((Button)Field(merge, "preview")).PerformClick();
                WaitUntil(() => !(bool)Field(merge, "busy"), "Changed merge destination fails promptly");
                Require(((TextBox)Field(merge, "details")).Text.Contains("分支已改变") && Field(merge, "plan") == null,
                    "Fixed branch merge rejects changed destination before querying or writing");
                merge.Close();
            }
        }

        private static void CheckBranchMergeDestinationRace(string artifacts)
        {
            string root = Path.GetFullPath(Path.Combine(artifacts, "branch-race-" + Guid.NewGuid().ToString("N")));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-race\nunused\nStandard\n");
            string selectorFile = Path.Combine(metadata, "plastic.selector");
            string selectorA = "repository \"ui-race@local\"\n  path \"/\"\n    branch \"/main/a\"\n";
            string selectorB = selectorA.Replace("/main/a", "/main/b");
            File.WriteAllText(selectorFile, selectorA);
            var client = new PlasticClient(PlasticClientConfig.Load());
            var destination = client.DiscoverWorkspace(root);
            using (var branches = new BranchForm(client, root))
            {
                Func<string, string, CancellationToken, System.Threading.Tasks.Task<long>> resolve = async (path, branch, token) => {
                    await System.Threading.Tasks.Task.Yield();
                    File.WriteAllText(selectorFile, selectorB);
                    return 17;
                };
                var task = (System.Threading.Tasks.Task<MergeForm>)typeof(BranchForm).GetMethod("CreateMergeDialogAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(branches, new object[] { destination, new PlasticBranch { Name = "/source" }, CancellationToken.None, resolve });
                WaitUntil(() => task.IsCompleted, "Simulated external switch during branch-head resolution completes");
                Require(task.IsFaulted && task.Exception.GetBaseException() is InvalidOperationException && task.Exception.GetBaseException().Message.Contains("分支已改变"),
                    "Branch merge rejects an external destination switch during source resolution");
            }
            bool rejected = false;
            try { using (var merge = new MergeForm(client, root, 17, "/source", destination.Repository, destination.Selector)) { } }
            catch (InvalidOperationException ex) { rejected = ex.Message.Contains("分支已改变"); }
            Require(rejected, "Merge constructor rejects a destination change after source resolution instead of recapturing it");
        }

        private static void CheckBranchCreationDialogs(string artifacts)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string root = Path.GetFullPath(Path.Combine(artifacts, "create-fixture-" + Guid.NewGuid().ToString("N")));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-create\nunused\nPartial\n");
            string selectorFile = Path.Combine(metadata, "plastic.selector"), selector = "repository \"ui-create@local\"\n  path \"/\"\n    branch \"/main\"\n";
            File.WriteAllText(selectorFile, selector);
            var client = new PlasticClient(PlasticClientConfig.Load());
            int calls = 0;
            using (var form = new BranchCreateForm(client, root, "ui-create@local", selector, "/main", 17))
            {
                Prepare(form); Require(!((Button)Field(form, "create")).Enabled && (long)((NumericUpDown)Field(form, "revision")).Value == 17,
                    "Child branch creation starts with the resolved head and requires an explicit name and comment");
                ((TextBox)Field(form, "name")).Text = "feature 中文 & space";
                Require(!((Button)Field(form, "create")).Enabled, "Child branch creation requires a comment before contacting the server");
                ((TextBox)Field(form, "comment")).Text = "分支说明\r\n第二行";
                Require(((Button)Field(form, "create")).Enabled && ((Label)Field(form, "fullName")).Text == "/main/feature 中文 & space",
                    "Child branch creation preserves Unicode and spaces in a short name");
                foreach (string invalid in new[] { ".", "..", "child/grandchild", "child@repo", "child#17", "bad\"name", "bad\\name", "bad:name", "bad?name", "O'Brien" })
                {
                    ((TextBox)Field(form, "name")).Text = invalid;
                    Require(!((Button)Field(form, "create")).Enabled, "Child branch rejects invalid short name " + invalid);
                }
                ((TextBox)Field(form, "name")).Text = "feature 中文 & space";
                Save(form, Path.Combine(artifacts, "branch-create.png"));
                form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "create", "close", "comment", "revision", "name" })
                {
                    var control = (Control)Field(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)) &&
                        control.Parent.RectangleToScreen(control.Parent.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Create branch " + field + " visible at minimum size");
                }
                Save(form, Path.Combine(artifacts, "branch-create-minimum.png"));
                var completion = new System.Threading.Tasks.TaskCompletionSource<PlasticCommandResult>();
                Func<string, string, long, string, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>> fake = (path, branch, changeset, message, token) => {
                    calls++; Require(branch == "/main/feature 中文 & space" && changeset == 17 && message.Contains("第二行"), "Create branch sends the reviewed branch, base and multiline comment");
                    return completion.Task;
                };
                typeof(BranchCreateForm).GetField("createBranch", flags).SetValue(form, fake);
                File.WriteAllText(selectorFile, selector.Replace("/main", "/changed"));
                var guarded = (System.Threading.Tasks.Task)typeof(BranchCreateForm).GetMethod("SubmitAsync", flags).Invoke(form, null);
                WaitUntil(() => guarded.IsCompleted, "Changed create context fails promptly");
                Require(calls == 0 && ((Label)Field(form, "status")).Text.Contains("分支已改变"), "Child create refuses changed selector before issuing a server write");
                File.WriteAllText(selectorFile, selector);
                var submitting = (System.Threading.Tasks.Task)typeof(BranchCreateForm).GetMethod("SubmitAsync", flags).Invoke(form, null);
                Require(calls == 1 && (bool)Field(form, "busy") && !((Button)Field(form, "close")).Enabled && !((Button)Field(form, "create")).Enabled,
                    "Creating branch disables close, duplicate submission and editable fields");
                form.Close(); Require(!form.IsDisposed, "Closing cannot cancel a branch mutation already sent to the server");
                completion.SetResult(new PlasticCommandResult { ExitCode = 1, Error = "simulated uncertain result" });
                WaitUntil(() => submitting.IsCompleted, "Mock creation failure completes");
                Require(calls == 1 && !((Button)Field(form, "create")).Enabled && form.CreatedBranch == null && ((Button)Field(form, "close")).Enabled,
                    "Unconfirmed create is not retried and instructs the user to refresh before another attempt");
                form.Close();
            }
            using (var success = new BranchCreateForm(client, root, "ui-create@local", selector, "/main", 17))
            {
                Prepare(success);
                ((TextBox)Field(success, "name")).Text = "created 中文";
                ((TextBox)Field(success, "comment")).Text = "Confirmed mock creation";
                int successes = 0;
                Func<string, string, long, string, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>> fakeSuccess = (path, branch, changeset, message, token) => {
                    successes++; return System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                };
                typeof(BranchCreateForm).GetField("createBranch", flags).SetValue(success, fakeSuccess);
                var completed = (System.Threading.Tasks.Task)typeof(BranchCreateForm).GetMethod("SubmitAsync", flags).Invoke(success, null);
                WaitUntil(() => completed.IsCompleted, "Mock successful creation completes");
                Require(successes == 1 && success.CreatedBranch == "/main/created 中文" && success.DialogResult == DialogResult.OK &&
                    !(bool)Field(success, "busy") && success.IsDisposed, "Successful create returns exact branch and OK, then closes after clearing busy for parent refresh");
            }
            var original = client.DiscoverWorkspace(root);
            using (var browser = new BranchForm(client, root))
            {
                Func<string, string, CancellationToken, System.Threading.Tasks.Task<long>> resolve = async (path, branch, token) => {
                    await System.Threading.Tasks.Task.Yield(); File.WriteAllText(selectorFile, selector.Replace("/main", "/changed")); return 17;
                };
                var operation = (System.Threading.Tasks.Task<BranchCreateForm>)typeof(BranchForm).GetMethod("CreateChildDialogAsync", flags)
                    .Invoke(browser, new object[] { original, new PlasticBranch { Name = "/main" }, CancellationToken.None, resolve });
                WaitUntil(() => operation.IsCompleted, "External switch during create-head resolution completes");
                Require(operation.IsFaulted && operation.Exception.GetBaseException().Message.Contains("分支已改变"), "Create dialog refuses changed context across asynchronous parent-head lookup");
            }
        }

        private static void CheckBranchHistory(string artifacts, PlasticClient client, string workspace, string branch)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            using (var history = new HistoryForm(client, workspace, workspace, branch))
            {
                Prepare(history); WaitUntil(() => !(bool)Field(history, "loadingHistory"), "Exact branch history loads");
                var entries = (System.Collections.Generic.List<PlasticHistoryItem>)Field(history, "entries");
                Require(entries.All(entry => entry.Branch == branch) && ((Label)Field(history, "historySummary")).Text.Contains("仅本分支提交，不含祖先"),
                    "Branch history shows exact own commits and describes ancestry exclusion");
                Require(!((Label)Field(history, "status")).Text.StartsWith("读取失败"), "Branch history read succeeds");
                var scopeLabel = Descendants(history).OfType<Label>().Single(label => label.Name == "branchScope");
                Require(scopeLabel.Visible && scopeLabel.Text == "范围：" + workspace, "Branch history shows its path scope separately from the branch name");
                Save(history, Path.Combine(artifacts, "branch-history.png"));
                history.Size = history.MinimumSize; Application.DoEvents();
                Require(scopeLabel.Parent.RectangleToScreen(scopeLabel.Parent.ClientRectangle).Contains(scopeLabel.RectangleToScreen(scopeLabel.ClientRectangle)),
                    "Branch history scope occupies its own visible row at minimum size");
                foreach (string name in new[] { "loadMore", "refreshHistory", "cancelHistory", "restore", "snapshot", "close" })
                {
                    var control = (Control)Field(history, name);
                    Require(history.RectangleToScreen(history.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Branch history " + name + " visible at minimum size");
                }
                Save(history, Path.Combine(artifacts, "branch-history-minimum.png"));
                typeof(HistoryForm).GetField("branchRepository", flags).SetValue(history, "changed-repository");
                ((Button)Field(history, "refreshHistory")).PerformClick(); WaitUntil(() => !(bool)Field(history, "loadingHistory"), "Changed branch-history repository fails promptly");
                Require(((Label)Field(history, "status")).Text.Contains("仓库已改变"), "Branch history does not follow a changed repository on refresh");
                history.Close();
            }
            using (var pathHistory = new HistoryForm(client, Path.Combine(workspace, "other.txt"), workspace, branch))
            {
                // Check layout without showing the window or starting another server read.
                pathHistory.CreateControl(); pathHistory.PerformLayout();
                var scope = Descendants(pathHistory).OfType<Label>().Single(label => label.Name == "branchScope");
                Require(scope.Text == "范围：" + Path.Combine(workspace, "other.txt") && (string)Field(pathHistory, "branch") == branch,
                    "Branch path-history retains both the selected branch and file scope");
            }
            using (var history = new HistoryForm(client, workspace, workspace, "/main"))
            {
                Prepare(history); WaitUntil(() => !(bool)Field(history, "loadingHistory"), "Sparse branch-history first page loads");
                var entries = (System.Collections.Generic.List<PlasticHistoryItem>)Field(history, "entries");
                Require(entries.All(entry => entry.Branch == "/main") && ((Button)Field(history, "loadMore")).Enabled == (bool)Field(history, "hasMoreHistory"),
                    "Branch history keeps continuation available for empty matching pages");
                history.Close();
            }
        }

        private static void CheckBranchTree(string artifacts)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string root = Path.GetFullPath(Path.Combine(artifacts, "tree-fixture-" + Guid.NewGuid().ToString("N")));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-tree\nunused\nStandard\n");
            File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"ui-tree@local\"\n  path \"/\"\n    branch \"/main\"\n");
            var client = new PlasticClient(PlasticClientConfig.Load());
            using (var form = new BranchForm(client, root))
            {
                // Cancel the lifetime before showing: Shown cannot start any cm
                // process; all following hierarchy data is an in-memory fixture.
                ((CancellationTokenSource)Field(form, "lifetime")).Cancel(); Prepare(form);
                var entries = new System.Collections.Generic.List<PlasticBranch> {
                    new PlasticBranch { Name = "/main", Parent = "", Repository = "ui-tree@local", HeadChangeset = 1, Comment = "Root branch" },
                    new PlasticBranch { Name = "/main/features", Parent = "/main", Repository = "ui-tree@local", HeadChangeset = 2, Comment = "Feature group" },
                    new PlasticBranch { Name = "/main/features/中文 & space", Parent = "/main/features", Repository = "ui-tree@local", HeadChangeset = 3, IsCurrent = true, Comment = "Exact needle match" },
                    new PlasticBranch { Name = "/independent-name", Parent = "/main", Repository = "ui-tree@local", HeadChangeset = 4, Comment = "Native parent wins over name spelling" },
                    new PlasticBranch { Name = "/orphan/child", Parent = "/orphan", Repository = "ui-tree@local", HeadChangeset = 5, Comment = "Missing parent metadata" }
                };
                typeof(BranchForm).GetField("entries", flags).SetValue(form, entries);
                typeof(BranchForm).GetMethod("RenderBranches", flags).Invoke(form, null);
                var list = (ListView)Field(form, "branches"); var tree = (TreeView)Field(form, "branchTree"); var mode = (ComboBox)Field(form, "viewMode");
                var nodes = (System.Collections.Generic.Dictionary<string, TreeNode>)Field(form, "treeNodes");
                Require(mode.SelectedIndex == 0 && list.Visible && !tree.Visible, "Branch browser defaults to the existing list view");
                list.Items[2].Selected = true; Application.DoEvents(); mode.SelectedIndex = 1; Application.DoEvents();
                Require(tree.SelectedNode != null && Object.ReferenceEquals(tree.SelectedNode.Tag, entries[2]) && !list.Visible && tree.Visible,
                    "Switching to hierarchy preserves the exact selected branch object");
                Require(Object.ReferenceEquals(nodes["/independent-name"].Parent.Tag, entries[0]), "Hierarchy attaches branches by native Parent rather than splitting names");
                Require(nodes["/orphan/child"].Parent == null && nodes["/orphan/child"].Text.StartsWith("/orphan/child") && nodes["/orphan/child"].Text.Contains("父分支不可见") && !nodes.ContainsKey("/orphan"),
                    "Missing parent is flagged without fabricating a branch action target");
                Require(tree.ContextMenuStrip == list.ContextMenuStrip && ((ToolStripMenuItem)Field(form, "branchHistory")).Enabled,
                    "List and tree share context actions on the active selected branch");
                Require(!((Button)Field(form, "merge")).Enabled && !((Button)Field(form, "switchBranch")).Enabled,
                    "Current tree selection retains redundant merge and switch guards");
                tree.SelectedNode = nodes["/independent-name"]; Application.DoEvents();
                using (var history = (HistoryForm)typeof(BranchForm).GetMethod("CreateBranchHistoryDialog", flags).Invoke(form, null))
                    Require((string)Field(history, "branch") == "/independent-name", "Tree history action targets the exact selected native branch");
                mode.SelectedIndex = 0; Application.DoEvents();
                Require(list.SelectedItems.Count == 1 && Object.ReferenceEquals(list.SelectedItems[0].Tag, entries[3]), "Returning to list preserves hierarchy selection exactly");
                mode.SelectedIndex = 1;
                ((ListView)Field(form, "files")).Items.Add("stale detail"); ((TextBox)Field(form, "description")).Text = "stale comment";
                ((TextBox)Field(form, "filter")).Text = "needle"; Application.DoEvents();
                Require(nodes.Count == 3 && list.Items.Count == 1 && nodes["/main"].ForeColor == SystemColors.GrayText &&
                    nodes["/main/features"].ForeColor == SystemColors.GrayText && nodes[entries[2].Name].ForeColor == SystemColors.WindowText,
                    "Tree filtering retains dim ancestor context while list contains only identical matches");
                Require(Object.ReferenceEquals(nodes["/main"].Tag, entries[0]) && nodes["/main"].ToolTipText.Contains("直接子分支：2"),
                    "Filtered ancestors retain real identity and unfiltered direct-child count");
                Require(tree.SelectedNode == null && ((ListView)Field(form, "files")).Items.Count == 0 && ((TextBox)Field(form, "description")).TextLength == 0,
                    "Tree filtering clears stale selected branch and head details");
                ((Button)Field(form, "locateCurrent")).PerformClick(); Application.DoEvents();
                Require(((TextBox)Field(form, "filter")).TextLength == 0 && Object.ReferenceEquals(tree.SelectedNode.Tag, entries[2]) &&
                    tree.SelectedNode.Parent.IsExpanded && tree.SelectedNode.Parent.Parent.IsExpanded,
                    "Locate current clears hidden filters and expands its ancestor path");
                typeof(BranchForm).GetField("partial", flags).SetValue(form, true); tree.SelectedNode = nodes["/independent-name"]; Application.DoEvents();
                Require(!((Button)Field(form, "merge")).Enabled && !((Button)Field(form, "switchBranch")).Enabled &&
                    ((ToolStripMenuItem)Field(form, "branchHistory")).Enabled && ((ToolStripMenuItem)Field(form, "createChild")).Enabled,
                    "Partial hierarchy supports metadata actions while disabling workspace switch and merge");
                Save(form, Path.Combine(artifacts, "branch-hierarchy.png")); form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "viewMode", "locateCurrent", "filter", "head", "switchBranch", "close" })
                {
                    var control = (Control)Field(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)) &&
                        control.Parent.RectangleToScreen(control.Parent.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)),
                        "Hierarchy " + field + " visible at minimum size");
                }
                Save(form, Path.Combine(artifacts, "branch-hierarchy-minimum.png"));
                var deep = new System.Collections.Generic.List<PlasticBranch>();
                for (int i = 0; i < 1500; i++) deep.Add(new PlasticBranch { Name = "/node" + i, Parent = i == 0 ? "" : "/node" + (i - 1), Repository = "ui-tree@local", HeadChangeset = i });
                typeof(BranchForm).GetField("entries", flags).SetValue(form, deep); typeof(BranchForm).GetMethod("RenderBranches", flags).Invoke(form, null);
                Require(nodes.Count == 1500 && Object.ReferenceEquals(nodes["/node1499"].Parent.Tag, deep[1498]), "Deep hierarchy renders iteratively with native parent identity");
                var cycle = new[] {
                    new PlasticBranch { Name = "/a", Parent = "/b", Repository = "ui-tree@local", HeadChangeset = 1, IsCurrent = true },
                    new PlasticBranch { Name = "/b", Parent = "/a", Repository = "ui-tree@local", HeadChangeset = 2 }
                };
                typeof(BranchForm).GetField("entries", flags).SetValue(form, cycle); ((TextBox)Field(form, "filter")).Text = "cycle-filter";
                Require(tree.Nodes.Count == 0 && list.Items.Count == 0 && !((Button)Field(form, "head")).Enabled && !((Button)Field(form, "locateCurrent")).Enabled &&
                    ((Label)Field(form, "status")).Text.StartsWith("无法显示分支层级"), "Malformed hierarchy through filter event fails closed without stale branch actions");
                cycle[0].Parent = ""; cycle[0].Repository = "invalid-repository";
                typeof(BranchForm).GetField("entries", flags).SetValue(form, new[] { cycle[0] }); ((TextBox)Field(form, "filter")).Clear();
                Require(tree.Nodes.Count == 0 && !((Button)Field(form, "locateCurrent")).Enabled && ((Label)Field(form, "status")).Text.StartsWith("无法显示分支层级"),
                    "Invalid hierarchy repository is contained by the same clear-and-disable path");
                typeof(BranchForm).GetField("entries", flags).SetValue(form, new PlasticBranch[] { null }); ((TextBox)Field(form, "filter")).Text = "null-filter";
                Require(tree.Nodes.Count == 0 && list.Items.Count == 0 && !((Button)Field(form, "locateCurrent")).Enabled && ((Label)Field(form, "status")).Text.StartsWith("无法显示分支层级"),
                    "Null hierarchy entry is rejected without a follow-up button-state exception");
                ((Button)Field(form, "refresh")).PerformClick(); Application.DoEvents();
                Require(tree.Nodes.Count == 0 && list.Items.Count == 0 && !((Button)Field(form, "head")).Enabled && !((Button)Field(form, "locateCurrent")).Enabled,
                    "Refresh with cancelled lifetime clears both views and stale hierarchy actions");
                form.Close();
            }
        }

        private static void CheckShelvesDialogs(string artifacts)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string root = Path.GetFullPath(Path.Combine(artifacts, "shelves-fixture-" + Guid.NewGuid().ToString("N")));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-shelves\nunused\nPartial\n");
            string selectorFile = Path.Combine(metadata, "plastic.selector"), selector = "repository \"ui-shelves@local\"\n  path \"/\"\n    branch \"/main\"\n";
            File.WriteAllText(selectorFile, selector);
            var client = new PlasticClient(PlasticClientConfig.Load());
            string selectedPath = Path.Combine(root, "目录 中文", "file & name.txt");
            Require(LaunchRequest.Parse(new[] { "--command", "shelves", "--path", root }).Command == "shelves", "Explorer shelves launch command is accepted");
            using (var form = new ShelveCreateForm(client, root, "ui-shelves@local", selector, new[] { selectedPath }, ""))
            {
                Prepare(form); Require(!((Button)Field(form, "create")).Enabled, "Shelve save requires a nonempty comment");
                ((TextBox)Field(form, "comment")).Text = "中文暂存\r\n第二行";
                Require(((Button)Field(form, "create")).Enabled && ((ListView)Field(form, "files")).Items[0].Text == selectedPath,
                    "Shelve save previews the exact selected Unicode path and multiline comment");
                Save(form, Path.Combine(artifacts, "shelve-create.png")); form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "comment", "files", "create", "close", "status" }) {
                    var control = (Control)Field(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)) &&
                        control.Parent.RectangleToScreen(control.Parent.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Shelve create " + field + " visible at minimum size");
                }
                Save(form, Path.Combine(artifacts, "shelve-create-minimum.png"));
                int calls = 0; var completion = new System.Threading.Tasks.TaskCompletionSource<PlasticCommandResult>();
                Func<string, System.Collections.Generic.IList<string>, string, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>> save = (path, paths, comment, token) => {
                    calls++; Require(path == root && paths.SequenceEqual(new[] { selectedPath }) && comment.Contains("第二行"), "Shelve writes only reviewed files and exact comment"); return completion.Task;
                };
                typeof(ShelveCreateForm).GetField("createShelve", flags).SetValue(form, save);
                File.WriteAllText(selectorFile, selector.Replace("/main", "/changed"));
                var rejected = (System.Threading.Tasks.Task)typeof(ShelveCreateForm).GetMethod("SubmitAsync", flags).Invoke(form, null);
                WaitUntil(() => rejected.IsCompleted, "Shelve changed context returns promptly");
                Require(calls == 0 && ((Label)Field(form, "status")).Text.Contains("分支已改变"), "Shelve save rejects changed workspace before server write");
                File.WriteAllText(selectorFile, selector);
                var pending = (System.Threading.Tasks.Task)typeof(ShelveCreateForm).GetMethod("SubmitAsync", flags).Invoke(form, null);
                Require(calls == 1 && !((Button)Field(form, "create")).Enabled && !((Button)Field(form, "close")).Enabled, "Shelve save disables duplicate submit and close during write");
                form.Close(); Require(!form.IsDisposed, "Shelve write cannot be abandoned by closing the dialog");
                completion.SetResult(new PlasticCommandResult { ExitCode = 1, Error = "uncertain result" }); WaitUntil(() => pending.IsCompleted, "Shelve failed write completes");
                typeof(ShelveCreateForm).GetMethod("SubmitAsync", flags).Invoke(form, null);
                Require(calls == 1 && !form.Saved && !((Button)Field(form, "create")).Enabled && ((Button)Field(form, "close")).Enabled,
                    "Unconfirmed shelve save never retries and leaves refresh instructions"); form.Close();
            }
            using (var form = new ShelveCreateForm(client, root, "ui-shelves@local", selector, new[] { selectedPath }, "confirmed"))
            {
                Func<string, System.Collections.Generic.IList<string>, string, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>> save =
                    (path, paths, comment, token) => System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                typeof(ShelveCreateForm).GetField("createShelve", flags).SetValue(form, save); Prepare(form);
                var pending = (System.Threading.Tasks.Task)typeof(ShelveCreateForm).GetMethod("SubmitAsync", flags).Invoke(form, null);
                WaitUntil(() => pending.IsCompleted, "Shelve successful write completes");
                Require(form.Saved && form.DialogResult == DialogResult.OK && form.IsDisposed, "Shelve success closes with a confirmed saved result");
            }
            using (var form = new ShelvesForm(client, root))
            {
                var entries = new[] {
                    new PlasticShelve { ShelveId = 17, ObjectId = 1017, Repository = "ui-shelves@local", Owner = "作者 中文", Date = "2026-09-27", Comment = "中文暂存\r\n多行说明" },
                    new PlasticShelve { ShelveId = 18, ObjectId = 1018, Repository = "ui-shelves@local", Owner = "other", Date = "2026-09-26", Comment = "Second" }
                };
                Func<string, CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IList<PlasticShelve>>> list = (path, token) =>
                    System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IList<PlasticShelve>>(entries);
                Func<string, long, CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IList<PlasticChangesetFile>>> details = (path, id, token) => {
                    Require(id == 17, "Shelve details use ShelveId rather than database ObjectId");
                    return System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IList<PlasticChangesetFile>>(new[] {
                        new PlasticChangesetFile { Path = "/目录 中文/file & name.txt", Status = "M", OldPath = "/old.txt" }
                    });
                };
                typeof(ShelvesForm).GetField("getShelves", flags).SetValue(form, list); typeof(ShelvesForm).GetField("getChanges", flags).SetValue(form, details);
                Prepare(form); var shelves = (ListView)Field(form, "shelves"); var files = (ListView)Field(form, "files");
                Require(shelves.Items.Count == 2 && files.Items.Count == 0, "Shelves list loads independently of workspace pending selection");
                Require(shelves.ContextMenuStrip != null && shelves.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Count() == 4,
                    "Shelves list provides explicit apply, delete, compare and export actions in its context menu");
                shelves.Items[0].Selected = true; Application.DoEvents();
                Require(files.Items.Count == 1 && files.Items[0].SubItems[2].Text == "/old.txt" && ((TextBox)Field(form, "description")).Text.Contains("多行"),
                    "Selecting a shelve loads its full comment and changed path details");
                Save(form, Path.Combine(artifacts, "shelves.png")); form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "shelves", "files", "filter", "apply", "delete", "compare", "export", "refresh", "cancel", "close" }) {
                    var control = (Control)Field(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)) &&
                        control.Parent.RectangleToScreen(control.Parent.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Shelves " + field + " visible at minimum size");
                }
                Save(form, Path.Combine(artifacts, "shelves-minimum.png"));
                ((TextBox)Field(form, "filter")).Text = "other";
                Require(shelves.Items.Count == 1 && files.Items.Count == 0 && ((TextBox)Field(form, "description")).Text == "", "Shelve filter clears stale selection details");
                var completion = new System.Threading.Tasks.TaskCompletionSource<System.Collections.Generic.IList<PlasticChangesetFile>>();
                CancellationToken capturedToken = CancellationToken.None;
                details = (path, id, token) => { capturedToken = token; return completion.Task; };
                typeof(ShelvesForm).GetField("getChanges", flags).SetValue(form, details);
                shelves.Items[0].Selected = true; Application.DoEvents();
                Require((bool)Field(form, "busy") && !shelves.Enabled && ((Button)Field(form, "cancel")).Enabled, "Shelve read disables selection while allowing cancellation");
                ((Button)Field(form, "cancel")).PerformClick();
                Require(capturedToken.IsCancellationRequested, "Shelve cancel signals the in-flight read");
                completion.SetResult(new PlasticChangesetFile[0]); WaitUntil(() => !(bool)Field(form, "busy"), "Cancelled shelve read completes");
                Require(files.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("已取消"), "Cancelled shelve details never render stale results");
                ((TextBox)Field(form, "filter")).Clear();
                var failure = new System.Threading.Tasks.TaskCompletionSource<System.Collections.Generic.IList<PlasticChangesetFile>>();
                failure.SetException(new InvalidOperationException("simulated detail failure")); details = (path, id, token) => failure.Task;
                typeof(ShelvesForm).GetField("getChanges", flags).SetValue(form, details); shelves.Items[0].Selected = true; Application.DoEvents();
                Require(files.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("simulated detail failure"), "Shelve detail failure clears old files and reports the error");
                File.WriteAllText(selectorFile, selector.Replace("ui-shelves@local", "changed@local"));
                ((Button)Field(form, "refresh")).PerformClick(); Application.DoEvents();
                Require(shelves.Items.Count == 0 && files.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("仓库已改变"), "Shelve refresh rejects changed repository without keeping stale server records");
                form.Close(); File.WriteAllText(selectorFile, selector);
            }
            using (var form = new ShelvesForm(client, root))
            {
                var entries = new[] { new PlasticShelve { ShelveId = 21, ObjectId = 1021, Repository = "ui-shelves@local", Owner = "作者", Date = "2026-09-28", Comment = "可操作暂存集" } };
                bool deleted = false; int applied = 0; int removed = 0; DialogResult confirmation = DialogResult.Yes;
                Func<string, CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IList<PlasticShelve>>> list = (path, token) =>
                    System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IList<PlasticShelve>>(deleted ? new PlasticShelve[0] : entries);
                Func<string, long, CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IList<PlasticChangesetFile>>> details = (path, id, token) =>
                    System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IList<PlasticChangesetFile>>(new[] {
                        new PlasticChangesetFile { Path = "/操作/file.txt", Status = "M" }
                    });
                Func<string, long, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>> apply = (path, id, token) => {
                    applied++; return System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 0, Output = "applied" });
                };
                Func<string, long, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>> remove = (path, id, token) => {
                    removed++; deleted = true; return System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 0, Output = "deleted" });
                };
                typeof(ShelvesForm).GetField("getShelves", flags).SetValue(form, list);
                typeof(ShelvesForm).GetField("getChanges", flags).SetValue(form, details);
                typeof(ShelvesForm).GetField("applyShelve", flags).SetValue(form, apply);
                typeof(ShelvesForm).GetField("deleteShelve", flags).SetValue(form, remove);
                typeof(ShelvesForm).GetField("confirm", flags).SetValue(form, new Func<string, string, DialogResult>((message, title) => confirmation));
                Prepare(form); var shelves = (ListView)Field(form, "shelves");
                shelves.Items[0].Selected = true; Application.DoEvents();
                WaitUntil(() => !(bool)Field(form, "busy"), "Shelve action fixture details finish loading");
                Require(((Button)Field(form, "apply")).Enabled && ((Button)Field(form, "delete")).Enabled,
                    "Shelve actions enable only after the selected details are loaded");
                int comparisonsShown = 0;
                typeof(ShelvesForm).GetField("showComparison", flags).SetValue(form, new Action<PlasticShelveComparison>(value => comparisonsShown++));
                Func<string, long, CancellationToken, System.Threading.Tasks.Task<PlasticShelveComparison>> invalidComparison = (path, id, token) =>
                    System.Threading.Tasks.Task.FromResult(new PlasticShelveComparison { Repository = "foreign@server", ShelveId = id });
                typeof(ShelvesForm).GetField("compareShelve", flags).SetValue(form, invalidComparison);
                var invalidTask = (System.Threading.Tasks.Task)typeof(ShelvesForm).GetMethod("CompareAsync", flags).Invoke(form, null);
                WaitUntil(() => invalidTask.IsCompleted, "Invalid shelveset comparison completes");
                Require(comparisonsShown == 0 && ((Label)Field(form, "status")).Text.Contains("不匹配"),
                    "Foreign comparison results never open a preview after a handled error");
                var pendingComparison = new System.Threading.Tasks.TaskCompletionSource<PlasticShelveComparison>();
                typeof(ShelvesForm).GetField("compareShelve", flags).SetValue(form,
                    new Func<string, long, CancellationToken, System.Threading.Tasks.Task<PlasticShelveComparison>>((path, id, token) => pendingComparison.Task));
                var cancelledComparison = (System.Threading.Tasks.Task)typeof(ShelvesForm).GetMethod("CompareAsync", flags).Invoke(form, null);
                ((Button)Field(form, "cancel")).PerformClick();
                pendingComparison.SetResult(new PlasticShelveComparison { Repository = "ui-shelves@local", ShelveId = 21 });
                WaitUntil(() => cancelledComparison.IsCompleted, "Cancelled comparison completes");
                Require(comparisonsShown == 0, "Cancelled shelveset comparison never opens a stale preview");
                confirmation = DialogResult.No;
                var cancelledApply = (System.Threading.Tasks.Task)typeof(ShelvesForm).GetMethod("ApplyAsync", flags).Invoke(form, null);
                WaitUntil(() => cancelledApply.IsCompleted, "Shelve apply confirmation cancellation completes");
                Require(applied == 0, "Shelve apply never writes when confirmation is declined");
                typeof(ShelvesForm).GetField("showPreflightError", flags).SetValue(form, new Action<string>(message => { }));
                typeof(ShelvesForm).GetField("confirm", flags).SetValue(form, new Func<string, string, DialogResult>((message, title) => {
                    Require(message.Contains(root) && message.Contains(selector), "Apply confirmation shows the captured workspace and selector");
                    File.WriteAllText(selectorFile, selector.Replace("/main", "/main/changed-during-confirmation"));
                    return DialogResult.Yes;
                }));
                var switchedApply = (System.Threading.Tasks.Task)typeof(ShelvesForm).GetMethod("ApplyAsync", flags).Invoke(form, null);
                WaitUntil(() => switchedApply.IsCompleted, "Selector change during apply confirmation is handled");
                Require(applied == 0 && ((Label)Field(form, "status")).Text.Contains("分支已改变"),
                    "Changing branches during confirmation cannot invoke the apply backend");
                File.WriteAllText(selectorFile, selector);
                typeof(ShelvesForm).GetField("confirm", flags).SetValue(form, new Func<string, string, DialogResult>((message, title) => confirmation));
                confirmation = DialogResult.Yes;
                var pendingApply = new System.Threading.Tasks.TaskCompletionSource<PlasticCommandResult>();
                typeof(ShelvesForm).GetField("applyShelve", flags).SetValue(form,
                    new Func<string, long, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>>((path, id, token) => {
                        Require(path == root && id == 21, "Apply uses the reviewed root and shelveset identity"); applied++; return pendingApply.Task;
                    }));
                var appliedTask = (System.Threading.Tasks.Task)typeof(ShelvesForm).GetMethod("ApplyAsync", flags).Invoke(form, null);
                Require(!((Button)Field(form, "cancel")).Enabled && !((Button)Field(form, "apply")).Enabled && !((Button)Field(form, "close")).Enabled,
                    "Native shelveset writes disable cancellation, duplicate submission and close");
                form.Close(); Require(!form.IsDisposed, "Closing cannot abandon a shelveset write");
                pendingApply.SetResult(new PlasticCommandResult { ExitCode = 0 });
                WaitUntil(() => appliedTask.IsCompleted, "Shelve apply completes");
                Require(applied == 1 && ((Label)Field(form, "status")).Text.Contains("应用成功"),
                    "Shelve apply uses the selected id and reports confirmed success");
                shelves = (ListView)Field(form, "shelves"); shelves.Items[0].Selected = true; Application.DoEvents();
                WaitUntil(() => !(bool)Field(form, "busy"), "Shelve delete fixture details finish loading");
                var deletedTask = (System.Threading.Tasks.Task)typeof(ShelvesForm).GetMethod("DeleteAsync", flags).Invoke(form, null);
                WaitUntil(() => deletedTask.IsCompleted, "Shelve delete completes");
                Require(removed == 1 && shelves.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("删除成功"),
                    "Shelve delete refreshes the list and reports confirmed success");
                form.Close();
            }
            using (var comparison = new ShelveComparisonForm(new PlasticShelveComparison {
                Repository = "ui-shelves@local", ShelveId = 21, ParentChangeset = 20,
                Files = new[] { new PlasticShelveComparisonFile { Status = "M", Path = "/操作/file.txt",
                    Diff = new PlasticDiffResult { HasChanges = true, DiffText = "-before\n+after" } } }
            }))
            {
                Prepare(comparison);
                var comparisonFiles = (ListView)Field(comparison, "files");
                comparisonFiles.Items[0].Selected = true; Application.DoEvents();
                Require(comparisonFiles.Items.Count == 1 && ((TextBox)Field(comparison, "preview")).Text.Contains("-before\r\n+after"),
                    "Shelve comparison dialog renders the selected file diff preview");
                Save(comparison, Path.Combine(artifacts, "shelves-comparison.png"));
                comparison.Size = comparison.MinimumSize; Application.DoEvents();
                Save(comparison, Path.Combine(artifacts, "shelves-comparison-minimum.png"));
                Require(comparison.RectangleToScreen(comparison.ClientRectangle).Contains(comparisonFiles.RectangleToScreen(comparisonFiles.ClientRectangle)),
                    "Shelve comparison file list remains visible at minimum size");
                var close = (Button)comparison.CancelButton;
                Require(comparison.RectangleToScreen(comparison.ClientRectangle).Contains(close.RectangleToScreen(close.ClientRectangle)),
                    "Shelve comparison provides a visible close button and Escape action");
                comparison.Close();
            }
            using (var main = new MainForm(LaunchRequest.Parse(new[] { "--path", Path.Combine(root, "目录 中文") })))
            {
                typeof(MainForm).GetField("client", flags).SetValue(main, client); typeof(MainForm).GetField("workspace", flags).SetValue(main, client.DiscoverWorkspace(root));
                var pending = (ListView)Field(main, "files");
                pending.Items.Add(new ListViewItem("selected") { Tag = new PlasticStatusItem { Path = selectedPath, StatusCode = "CH" }, Checked = true });
                pending.Items.Add(new ListViewItem("unselected") { Tag = new PlasticStatusItem { Path = Path.Combine(root, "目录 中文", "other.txt"), StatusCode = "CH" } });
                using (var create = (ShelveCreateForm)typeof(MainForm).GetMethod("CreateShelveDialog", flags).Invoke(main, null))
                    Require(((System.Collections.Generic.IList<string>)Field(create, "paths")).SequenceEqual(new[] { selectedPath }), "Main pending creates shelve only from checked files, without scope fallback");
                var item = (PlasticStatusItem)pending.Items[0].Tag;
                foreach (string invalid in new[] { "directory", "private", "outside" }) {
                    item.IsDirectory = invalid == "directory"; item.StatusCode = invalid == "private" ? "PR" : "CH";
                    item.Path = invalid == "outside" ? Path.Combine(root, "outside.txt") : selectedPath;
                    bool rejected = false;
                    try { typeof(MainForm).GetMethod("CreateShelveDialog", flags).Invoke(main, null); }
                    catch (TargetInvocationException ex) { rejected = ex.InnerException is InvalidOperationException; }
                    Require(rejected, "Main shelve selection rejects " + invalid + " rows before opening confirmation");
                }
                pending.Items[0].Checked = false; bool emptyRejected = false;
                try { typeof(MainForm).GetMethod("CreateShelveDialog", flags).Invoke(main, null); }
                catch (TargetInvocationException ex) { emptyRejected = ex.InnerException is InvalidOperationException; }
                Require(emptyRejected, "No checked shelve paths never fall back to the whole directory");
            }
        }

        private static void CheckLiveShelves(string artifacts, string root, long shelveId)
        {
            var client = new PlasticClient(PlasticClientConfig.Load());
            var workspace = client.DiscoverWorkspace(root);
            Require(workspace != null, "Live shelves fixture resolves to a Plastic workspace");
            var liveEntries = client.GetShelvesAsync(root, CancellationToken.None).GetAwaiter().GetResult();
            var target = liveEntries.SingleOrDefault(item => item.ShelveId == shelveId);
            Require(target != null && target.Repository == workspace.Repository, "Live shelves list contains the requested repository record");
            using (var form = new ShelvesForm(client, root))
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                Func<string, CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IList<PlasticShelve>>> list =
                    (path, token) => System.Threading.Tasks.Task.FromResult(liveEntries);
                Func<string, long, CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IList<PlasticChangesetFile>>> details =
                    (path, id, token) => client.GetShelveChangesAsync(path, id, token);
                typeof(ShelvesForm).GetField("getShelves", flags).SetValue(form, list);
                typeof(ShelvesForm).GetField("getChanges", flags).SetValue(form, details);
                Prepare(form);
                WaitUntil(() => !(bool)Field(form, "busy"), "Live shelves list finishes loading");
                var shelves = (ListView)Field(form, "shelves");
                var row = shelves.Items.Cast<ListViewItem>().SingleOrDefault(item => ((PlasticShelve)item.Tag).ShelveId == shelveId);
                Require(row != null, "Live shelves browser renders the exact requested shelve id");
                row.Selected = true; Application.DoEvents();
                WaitUntil(() => !(bool)Field(form, "busy"), "Live shelve details finish loading");
                var files = (ListView)Field(form, "files");
                Require(files.Items.Count == 2, "Live shelve contains exactly two controlled file changes");
                Require(files.Items.Cast<ListViewItem>().All(item => !String.IsNullOrWhiteSpace(item.Text) && item.SubItems.Count >= 2), "Live shelve details expose paths and statuses");
                Save(form, Path.Combine(artifacts, "shelves-live.png")); form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "shelves", "files", "filter", "apply", "delete", "compare", "export", "refresh", "cancel", "close" })
                {
                    var control = (Control)Field(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Live shelves " + field + " fits at minimum size");
                }
                Save(form, Path.Combine(artifacts, "shelves-live-minimum.png"));
            }
        }

        private static void CheckBlameDialog(string artifacts)
        {
            string path = Path.Combine(artifacts, "blame-fixture.txt");
            using (var form = new BlameForm(new PlasticClient(PlasticClientConfig.Load()), path, artifacts))
            {
                var list = (ListView)Field(form, "lines");
                Require(list.Columns.Count == 6 && list.Columns[0].Text == "Line" && list.Columns[5].Text == "Content",
                    "Blame dialog uses Tortoise-style line metadata columns");
                Require(((Button)Field(form, "refresh")).Enabled && !((Button)Field(form, "cancel")).Enabled,
                    "Blame dialog starts idle with refresh enabled");
                form.Size = form.MinimumSize; form.CreateControl(); Application.DoEvents();
                Save(form, Path.Combine(artifacts, "blame-minimum.png"));
            }
        }

        private static void CheckLabelsDialogs(string artifacts)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            string root = Path.GetFullPath(Path.Combine(artifacts, "labels-fixture-" + Guid.NewGuid().ToString("N")));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-labels\nunused\nPartial\n");
            string selectorFile = Path.Combine(metadata, "plastic.selector");
            string selector = "repository \"ui-labels@local\"\n  path \"/\"\n    branch \"/main\"\n";
            File.WriteAllText(selectorFile, selector);
            var client = new PlasticClient(PlasticClientConfig.Load());
            Require(LaunchRequest.Parse(new[] { "--command", "labels", "--path", root }).Command == "labels", "Explorer labels launch is accepted");
            int created = 0;
            using (var form = new LabelCreateForm(client, root, "ui-labels@local", 17))
            {
                Prepare(form);
                Require(!((Button)Field(form, "create")).Enabled && !((NumericUpDown)Field(form, "revision")).Enabled, "Label creation requires name and message and locks reviewed historical changeset");
                ((TextBox)Field(form, "name")).Text = "release 中文 & 1"; ((TextBox)Field(form, "comment")).Text = "发布版本\r\n经过验证";
                Require(((Button)Field(form, "create")).Enabled, "Partial workspace permits explicit changeset label creation");
                Func<string, long, string, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>> create = (label, cs, comment, token) => {
                    created++; Require(label == "release 中文 & 1" && cs == 17 && comment.Contains("经过验证"), "Label creation preserves reviewed Unicode name, target and message");
                    return System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                };
                typeof(LabelCreateForm).GetField("createLabel", flags).SetValue(form, create);
                ((TextBox)Field(form, "name")).Text = "invalid@repository";
                var invalidName = (System.Threading.Tasks.Task)typeof(LabelCreateForm).GetMethod("ConfirmCreateAsync", flags).Invoke(form, null);
                WaitUntil(() => invalidName.IsCompleted, "Invalid label name is rejected locally");
                Require(created == 0 && !(bool)Field(form, "attempted") && ((Button)Field(form, "create")).Enabled,
                    "Local label validation leaves form editable without recording uncertain write");
                ((TextBox)Field(form, "name")).Text = "release 中文 & 1";
                typeof(LabelCreateForm).GetField("confirm", flags).SetValue(form, new Func<string, DialogResult>(message => {
                    Require(message.Contains("ui-labels@local") && message.Contains("release 中文 & 1 → cs:17"), "Label confirmation identifies exact repository, name and changeset"); return DialogResult.Cancel;
                }));
                var rejected = (System.Threading.Tasks.Task)typeof(LabelCreateForm).GetMethod("ConfirmCreateAsync", flags).Invoke(form, null);
                WaitUntil(() => rejected.IsCompleted, "Cancelled label create completes"); Require(created == 0, "Cancelled label confirmation never calls writer");
                Save(form, Path.Combine(artifacts, "label-create.png")); form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "name", "revision", "comment", "create", "close", "status" }) {
                    var control = (Control)Field(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Label create " + field + " fits at minimum size");
                }
                Save(form, Path.Combine(artifacts, "label-create-minimum.png"));
                typeof(LabelCreateForm).GetField("confirm", flags).SetValue(form, new Func<string, DialogResult>(message => {
                    File.WriteAllText(selectorFile, selector.Replace("ui-labels@local", "changed@local")); return DialogResult.OK;
                }));
                var switched = (System.Threading.Tasks.Task)typeof(LabelCreateForm).GetMethod("ConfirmCreateAsync", flags).Invoke(form, null);
                WaitUntil(() => switched.IsCompleted, "Changed repository label create is rejected");
                Require(created == 0 && ((TextBox)Field(form, "status")).Text.Contains("仓库已改变"), "Repository switch during label confirmation prevents server mutation");
                File.WriteAllText(selectorFile, selector);
                var pending = new System.Threading.Tasks.TaskCompletionSource<PlasticCommandResult>();
                typeof(LabelCreateForm).GetField("confirm", flags).SetValue(form, new Func<string, DialogResult>(message => DialogResult.OK));
                typeof(LabelCreateForm).GetField("createLabel", flags).SetValue(form,
                    new Func<string, long, string, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>>((label, cs, message, token) => { created++; return pending.Task; }));
                var submitted = (System.Threading.Tasks.Task)typeof(LabelCreateForm).GetMethod("ConfirmCreateAsync", flags).Invoke(form, null);
                Require(created == 1 && !((Button)Field(form, "create")).Enabled && !((Button)Field(form, "close")).Enabled, "Pending label write prevents duplicate submit and close");
                form.Close(); Require(!form.IsDisposed, "Label create cannot close during native write");
                pending.SetResult(new PlasticCommandResult { ExitCode = 1, Error = "fixture failure" }); WaitUntil(() => submitted.IsCompleted, "Failed label write completes");
                Require(!((Button)Field(form, "create")).Enabled && ((TextBox)Field(form, "status")).Text.Contains("结果未确认"), "Uncertain label result requires list refresh before retry");
                form.Close();
            }
            using (var form = new LabelCreateForm(client, root, "ui-labels@local", null))
            {
                Prepare(form); Require(((NumericUpDown)Field(form, "revision")).Enabled, "Label manager allows explicitly choosing a target changeset");
                ((TextBox)Field(form, "name")).Text = "confirmed 中文"; ((TextBox)Field(form, "comment")).Text = "reviewed target"; ((NumericUpDown)Field(form, "revision")).Value = 19;
                typeof(LabelCreateForm).GetField("confirm", flags).SetValue(form, new Func<string, DialogResult>(message => DialogResult.OK));
                typeof(LabelCreateForm).GetField("createLabel", flags).SetValue(form,
                    new Func<string, long, string, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>>((label, cs, message, token) => {
                        Require(label == "confirmed 中文" && cs == 19 && message == "reviewed target", "Confirmed label create submits chosen identity and message");
                        return System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                    }));
                var submitted = (System.Threading.Tasks.Task)typeof(LabelCreateForm).GetMethod("ConfirmCreateAsync", flags).Invoke(form, null);
                WaitUntil(() => submitted.IsCompleted, "Successful label creation completes"); Require(form.Saved, "Successful create reports saved result");
            }
            int listed = 0, deleted = 0; long requestedCs = -1; bool fail = false;
            var first = new PlasticLabel { Id = 111, Name = "release 中文 & 1", Changeset = 17, Repository = "ui-labels@local", Branch = "/main", Owner = "作者", Date = "2026-09-28", Comment = "发布标签\n第二行" };
            var second = new PlasticLabel { Id = 112, Name = "v2", Changeset = 18, Repository = "ui-labels@local", Branch = "/main/feature", Owner = "other", Date = "2026-09-27", Comment = "Second" };
            var entries = new System.Collections.Generic.List<PlasticLabel> { first, second };
            Func<CancellationToken, System.Threading.Tasks.Task<System.Collections.Generic.IList<PlasticLabel>>> list = token => {
                listed++; if (fail) throw new InvalidDataException("fixture label read failure");
                return System.Threading.Tasks.Task.FromResult<System.Collections.Generic.IList<PlasticLabel>>(entries.ToArray());
            };
            Func<long, CancellationToken, System.Threading.Tasks.Task<PlasticChangesetDetails>> details = (cs, token) => {
                requestedCs = cs;
                return System.Threading.Tasks.Task.FromResult(new PlasticChangesetDetails { Changeset = new PlasticHistoryItem { Changeset = cs, Comment = "目标提交说明" }, Files = new[] {
                    new PlasticChangesetFile { Status = "Changed", Path = "/未加载 中文目录/file.txt", OldPath = "" }
                } });
            };
            using (var form = new LabelsForm(client, root))
            {
                typeof(LabelsForm).GetField("getLabels", flags).SetValue(form, list); typeof(LabelsForm).GetField("getChanges", flags).SetValue(form, details);
                Prepare(form); var labels = (ListView)Field(form, "labels"); var files = (ListView)Field(form, "files");
                Require(labels.Items.Count == 2 && !((Button)Field(form, "delete")).Enabled, "Labels list loads all branch labels and requires selection for deletion");
                labels.Items[0].Selected = true; Application.DoEvents();
                Require(requestedCs == 17 && files.Items.Count == 1 && ((TextBox)Field(form, "description")).Text.Contains("目标提交说明"), "Selecting label reads exact target changeset and complete changed files");
                using (var browser = (RepositoryBrowserForm)typeof(LabelsForm).GetMethod("CreateSnapshotBrowser", flags).Invoke(form, null))
                    Require((long?)Field(browser, "initialChangeset") == 17 && (string)Field(browser, "expectedRepository") == "ui-labels@local", "Labels snapshot browser is pinned to selected changeset and repository");
                Save(form, Path.Combine(artifacts, "labels.png")); form.Size = form.MinimumSize; Application.DoEvents();
                foreach (string field in new[] { "labels", "files", "filter", "description", "create", "delete", "browse", "refresh", "cancel", "close" }) {
                    var control = (Control)Field(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Labels " + field + " fits at minimum size");
                }
                Save(form, Path.Combine(artifacts, "labels-minimum.png"));
                ((TextBox)Field(form, "filter")).Text = "feature"; Application.DoEvents();
                Require(labels.Items.Count == 1 && files.Items.Count == 0 && ((TextBox)Field(form, "description")).Text.Length == 0, "Label filter includes branch and clears stale details");
                ((TextBox)Field(form, "filter")).Clear(); labels.Items[0].Selected = true; Application.DoEvents();
                var pendingDetails = new System.Threading.Tasks.TaskCompletionSource<PlasticChangesetDetails>();
                typeof(LabelsForm).GetField("getChanges", flags).SetValue(form,
                    new Func<long, CancellationToken, System.Threading.Tasks.Task<PlasticChangesetDetails>>((cs, token) => pendingDetails.Task));
                var loading = (System.Threading.Tasks.Task)typeof(LabelsForm).GetMethod("LoadDetailsAsync", flags).Invoke(form, null);
                Require((bool)Field(form, "busy") && !labels.Enabled && ((Button)Field(form, "cancel")).Enabled, "Label details request disables competing actions and supports cancellation");
                ((Button)Field(form, "cancel")).PerformClick(); pendingDetails.SetResult(details(17, CancellationToken.None).Result);
                WaitUntil(() => loading.IsCompleted, "Cancelled label details completes");
                Require(files.Items.Count == 0 && ((TextBox)Field(form, "description")).Text.Length == 0, "Cancelled label details cannot publish stale content");
                Require(labels.SelectedItems.Count == 0 && !((Button)Field(form, "browse")).Enabled && !((Button)Field(form, "delete")).Enabled,
                    "Cancelled label details clears selection and disables stale target actions");
                typeof(LabelsForm).GetField("getChanges", flags).SetValue(form, details);
                labels.Items[0].Selected = true; Application.DoEvents();
                typeof(LabelsForm).GetField("deleteLabel", flags).SetValue(form,
                    new Func<PlasticLabel, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>>((label, token) => { deleted++; return System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                typeof(LabelsForm).GetField("confirm", flags).SetValue(form, new Func<string, DialogResult>(message => {
                    Require(message.Contains("release 中文 & 1 → cs:17") && message.Contains("ID 111") && message.Contains("ui-labels@local"), "Delete confirmation shows repository, name, target and immutable id"); return DialogResult.No;
                }));
                var cancelled = (System.Threading.Tasks.Task)typeof(LabelsForm).GetMethod("DeleteAsync", flags).Invoke(form, null);
                WaitUntil(() => cancelled.IsCompleted, "Cancelled label delete completes"); Require(deleted == 0, "Cancelled deletion never calls writer");
                typeof(LabelsForm).GetField("confirm", flags).SetValue(form, new Func<string, DialogResult>(message => {
                    File.WriteAllText(selectorFile, selector.Replace("ui-labels@local", "changed@local")); return DialogResult.Yes;
                }));
                var switched = (System.Threading.Tasks.Task)typeof(LabelsForm).GetMethod("DeleteAsync", flags).Invoke(form, null);
                WaitUntil(() => switched.IsCompleted, "Changed repository label delete is rejected");
                Require(deleted == 0 && ((Label)Field(form, "status")).Text.Contains("仓库已改变"), "Delete rechecks repository after confirmation");
                File.WriteAllText(selectorFile, selector);
                ((Button)Field(form, "refresh")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Labels refresh after rejected write");
                labels.Items[0].Selected = true; Application.DoEvents();
                var pendingDelete = new System.Threading.Tasks.TaskCompletionSource<PlasticCommandResult>();
                typeof(LabelsForm).GetField("confirm", flags).SetValue(form, new Func<string, DialogResult>(message => DialogResult.Yes));
                typeof(LabelsForm).GetField("deleteLabel", flags).SetValue(form,
                    new Func<PlasticLabel, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>>((label, token) => {
                        deleted++; Require(label.Id == 111 && label.Changeset == 17 && label.Name == "release 中文 & 1" && label.Repository == "ui-labels@local", "Deletion submits reviewed immutable identity"); return pendingDelete.Task;
                    }));
                var deleting = (System.Threading.Tasks.Task)typeof(LabelsForm).GetMethod("DeleteAsync", flags).Invoke(form, null);
                Require((bool)Field(form, "writing") && !((Button)Field(form, "cancel")).Enabled && !((Button)Field(form, "close")).Enabled, "Label mutation disables cancellation and close");
                form.Close(); Require(!form.IsDisposed, "Label manager cannot close during delete");
                entries.Remove(first); pendingDelete.SetResult(new PlasticCommandResult { ExitCode = 0 }); WaitUntil(() => deleting.IsCompleted, "Successful label delete refreshes list");
                Require(deleted == 1 && labels.Items.Count == 1 && ((Label)Field(form, "status")).Text.Contains("删除成功"), "Successful deletion removes stale selection after server refresh");
                labels.Items[0].Selected = true; Application.DoEvents();
                typeof(LabelsForm).GetField("deleteLabel", flags).SetValue(form,
                    new Func<PlasticLabel, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>>((label, token) =>
                        System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 1, Error = "uncertain native result" })));
                var failedDelete = (System.Threading.Tasks.Task)typeof(LabelsForm).GetMethod("DeleteAsync", flags).Invoke(form, null);
                WaitUntil(() => failedDelete.IsCompleted, "Uncertain label deletion completes");
                Require(labels.Items.Count == 0 && !((Button)Field(form, "delete")).Enabled && ((Label)Field(form, "status")).Text.Contains("结果未确认"),
                    "Uncertain deletion clears targets until explicit refresh");
                ((Button)Field(form, "refresh")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Refresh uncertain label result");
                labels.Items[0].Selected = true; Application.DoEvents();
                typeof(LabelsForm).GetField("deleteLabel", flags).SetValue(form,
                    new Func<PlasticLabel, CancellationToken, System.Threading.Tasks.Task<PlasticCommandResult>>((label, token) => {
                        fail = true; return System.Threading.Tasks.Task.FromResult(new PlasticCommandResult { ExitCode = 0 });
                    }));
                var refreshFailed = (System.Threading.Tasks.Task)typeof(LabelsForm).GetMethod("DeleteAsync", flags).Invoke(form, null);
                WaitUntil(() => refreshFailed.IsCompleted, "Successful delete with failed refresh completes");
                Require(labels.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("删除成功") && ((Label)Field(form, "status")).Text.Contains("fixture label read failure"),
                    "Successful delete preserves refresh failure instead of presenting empty list as authoritative");
                fail = false; second.Name = "release/legacy";
                ((Button)Field(form, "refresh")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Read-only legacy label name refreshes");
                labels.Items[0].Selected = true; Application.DoEvents();
                using (var browser = (RepositoryBrowserForm)typeof(LabelsForm).GetMethod("CreateSnapshotBrowser", flags).Invoke(form, null))
                    Require((long?)Field(browser, "initialChangeset") == 18, "Unsupported mutation name still browses its snapshot");
                bool unsupportedConfirmation = false;
                typeof(LabelsForm).GetField("confirm", flags).SetValue(form, new Func<string, DialogResult>(message => { unsupportedConfirmation = true; return DialogResult.Yes; }));
                var unsupportedDelete = (System.Threading.Tasks.Task)typeof(LabelsForm).GetMethod("DeleteAsync", flags).Invoke(form, null);
                WaitUntil(() => unsupportedDelete.IsCompleted, "Unsupported label deletion is rejected locally");
                Require(!unsupportedConfirmation && !fail && ((Label)Field(form, "status")).Text.StartsWith("无法删除标签：") && !((Label)Field(form, "status")).Text.Contains("结果未确认"),
                    "Unsupported delete name fails before confirmation and is not reported as uncertain mutation");
                fail = true; ((Button)Field(form, "refresh")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Failed label refresh finishes");
                Require(labels.Items.Count == 0 && files.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("fixture label read failure"), "Failed label refresh clears obsolete records");
                fail = false; int before = listed; File.WriteAllText(selectorFile, selector.Replace("ui-labels@local", "changed@local"));
                ((Button)Field(form, "refresh")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Label repository mismatch finishes");
                Require(listed == before && labels.Items.Count == 0, "Repository mismatch rejects reads before invoking provider");
                File.WriteAllText(selectorFile, selector); form.Close();
            }
            using (var history = new HistoryForm(client, root, root))
            {
                // Create the native list handle without showing the form (no live server request).
                var revisions = (ListView)Field(history, "revisions"); IntPtr handle = revisions.Handle;
                typeof(HistoryForm).GetField("filtering", flags).SetValue(history, true);
                typeof(HistoryForm).GetField("historyRepository", flags).SetValue(history, "ui-labels@local");
                revisions.Items.Add(new ListViewItem("cs:19") { Tag = new PlasticHistoryItem { Changeset = 19 } }).Selected = true;
                using (var dialog = (LabelCreateForm)typeof(HistoryForm).GetMethod("CreateLabelDialog", flags).Invoke(history, null))
                    Require(((NumericUpDown)Field(dialog, "revision")).Value == 19 && !((NumericUpDown)Field(dialog, "revision")).Enabled, "History creates labels at precisely the selected reviewed revision");
                Require(revisions.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "在此版本创建标签…"), "History exposes native create-label context action");
            }
            Require(File.ReadAllText(selectorFile) == selector, "All label GUI navigation preserves workspace selector");
        }

        private static void CheckLiveLabels(string artifacts, string workspacePath, string labelName)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var client = new PlasticClient(PlasticClientConfig.Load()); var workspace = client.DiscoverWorkspace(workspacePath);
            string selector = workspace.Selector;
            using (var form = new LabelsForm(client, workspacePath))
            {
                Prepare(form); WaitUntil(() => !(bool)Field(form, "busy"), "Live labels list finishes");
                var labels = (ListView)Field(form, "labels");
                var row = labels.Items.Cast<ListViewItem>().SingleOrDefault(item => ((PlasticLabel)item.Tag).Name == labelName);
                Require(row != null, "Live labels GUI finds exact created Unicode label"); row.Selected = true;
                WaitUntil(() => !(bool)Field(form, "busy"), "Live label changeset detail finishes");
                var selected = (PlasticLabel)row.Tag;
                Require(((TextBox)Field(form, "description")).Text.Contains("cs:" + selected.Changeset), "Live label GUI renders exact target commit details");
                using (var browser = (RepositoryBrowserForm)typeof(LabelsForm).GetMethod("CreateSnapshotBrowser", flags).Invoke(form, null))
                    Require((long?)Field(browser, "initialChangeset") == selected.Changeset, "Live label snapshot navigation pins exact target");
                Save(form, Path.Combine(artifacts, "labels-live.png")); form.Size = form.MinimumSize; Application.DoEvents(); Save(form, Path.Combine(artifacts, "labels-live-minimum.png")); form.Close();
            }
            Require(client.DiscoverWorkspace(workspacePath).Selector == selector, "Read-only live labels GUI preserves selector");
        }

        private static void CheckRepositoryBrowser(string artifacts)
        {
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            Require(LaunchRequest.Parse(new[] { "--command", "repository-browser", "--changeset", "0" }).Changeset == 0, "Repository browser launch accepts initial changeset zero");
            foreach (var value in new[] { "-1", "+1", "1.2", "9223372036854775808" })
            {
                try { LaunchRequest.Parse(new[] { "--command", "repository-browser", "--changeset", value }); throw new Exception("Invalid changeset accepted"); }
                catch (ArgumentException) { }
            }
            string root = Path.GetFullPath(Path.Combine(artifacts, "repository-fixture-" + Guid.NewGuid().ToString("N")));
            string metadata = Path.Combine(root, ".plastic"); Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "ui-repository\nunused\nPartial\n");
            string selectorFile = Path.Combine(metadata, "plastic.selector");
            string selector = "repository \"ui-repository@local\"\n  path \"/\"\n    branch \"/main\"\n";
            File.WriteAllText(selectorFile, selector);
            int calls = 0; long requestedCs = -1; string requestedPath = null;
            bool fail = false, wait = false;
            Func<string, string, long, CancellationToken, System.Threading.Tasks.Task<PlasticRepositoryListing>> load = async delegate(string workspace, string path, long cs, CancellationToken token)
            {
                calls++; requestedCs = cs; requestedPath = path;
                await System.Threading.Tasks.Task.Yield();
                if (wait) await System.Threading.Tasks.Task.Delay(30000, token);
                if (fail) throw new InvalidDataException("fixture read failure");
                return new PlasticRepositoryListing { Repository = "ui-repository@local", RootPath = root, DirectoryPath = path, Changeset = cs,
                    Entries = path == "/" ? new[] {
                        new PlasticRepositoryEntry { Name = "未加载 中文目录", Path = "/未加载 中文目录", IsDirectory = true, ItemId = 1 },
                        new PlasticRepositoryEntry { Name = "readme.txt", Path = "/readme.txt", Size = 30, ItemId = 2 },
                        new PlasticRepositoryEntry { Name = "link", Path = "/link", IsSymbolicLink = true, ItemId = 3 }
                    } : new[] { new PlasticRepositoryEntry { Name = "historical.txt", Path = path + "/historical.txt", Size = 10, ItemId = 4 } } };
            };
            using (var form = new RepositoryBrowserForm(new PlasticClient(PlasticClientConfig.Load()), root, 17, "ui-repository@local"))
            {
                typeof(RepositoryBrowserForm).GetField("loadDirectory", flags).SetValue(form, load);
                Prepare(form); WaitUntil(() => !(bool)Field(form, "busy"), "Repository root listing completes");
                var rows = (ListView)Field(form, "files"); var tree = (TreeView)Field(form, "directories");
                Require(calls == 1 && requestedCs == 17 && requestedPath == "/" && rows.Items.Count == 3 && tree.Nodes[0].Nodes.Count == 1,
                    "Repository browser lazily loads one snapshot directory including unloaded paths");
                rows.Items.Cast<ListViewItem>().Single(row => row.Text == "link").Selected = true; Application.DoEvents();
                Require(!((Button)Field(form, "view")).Enabled && !((Button)Field(form, "export")).Enabled, "Symbolic links cannot be followed or exported as ordinary files");
                foreach (ListViewItem row in rows.Items) row.Selected = false;
                rows.Items.Cast<ListViewItem>().Single(row => row.Text == "readme.txt").Selected = true; Application.DoEvents();
                Func<string, string, long, CancellationToken, System.Threading.Tasks.Task<PlasticHistoricalFile>> read = delegate(string workspace, string path, long cs, CancellationToken token)
                {
                    Require(path == "/readme.txt" && cs == 17, "Preview uses displayed snapshot file and changeset");
                    return System.Threading.Tasks.Task.FromResult(new PlasticHistoricalFile { RepositoryPath = path, Changeset = cs, Content = System.Text.Encoding.UTF8.GetBytes("中文第一行\n\nthird line\rfinal") });
                };
                typeof(RepositoryBrowserForm).GetField("readFile", flags).SetValue(form, read);
                ((Button)Field(form, "view")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Repository file preview completes");
                Require(((TextBox)Field(form, "preview")).Text == "中文第一行\r\n\r\nthird line\r\nfinal", "Repository UTF-8 preview preserves blank lines and normalizes native newlines");
                Save(form, Path.Combine(artifacts, "repository-browser.png"));
                form.Size = form.MinimumSize; Application.DoEvents();
                foreach (var field in new[] { "browse", "up", "refresh", "cancel", "view", "export", "files", "directories" })
                {
                    var control = (Control)Field(form, field);
                    Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Repository " + field + " fits at minimum size");
                }
                Save(form, Path.Combine(artifacts, "repository-browser-minimum.png"));
                tree.SelectedNode = tree.Nodes[0].Nodes[0]; WaitUntil(() => !(bool)Field(form, "busy"), "Repository child navigation completes");
                Require(requestedPath == "/未加载 中文目录" && calls == 2 && requestedCs == 17 && ((Button)Field(form, "up")).Enabled,
                    "Repository navigation preserves snapshot and lazily reads selected directory");
                ((Button)Field(form, "up")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Repository up navigation completes");
                Require(requestedPath == "/" && !((Button)Field(form, "up")).Enabled, "Repository parent navigation reaches root");
                ((NumericUpDown)Field(form, "revision")).Value = 0;
                Require(rows.Items.Count == 0 && !((Button)Field(form, "export")).Enabled, "Editing snapshot invalidates old file actions immediately");
                Require((string)Field(form, "requestedDirectory") == "/" && ((Label)Field(form, "location")).Text.Contains("cs:0（尚未加载）"), "Editing snapshot resets root navigation and labels the unloaded requested snapshot");
                ((Button)Field(form, "browse")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Repository changeset zero browsing completes");
                Require(requestedCs == 0 && rows.Items.Count == 3, "Repository browser supports empty-root initial changeset identifier");
                fail = true; ((Button)Field(form, "refresh")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Repository failed refresh completes");
                Require(rows.Items.Count == 0 && tree.Nodes.Count == 0 && ((Label)Field(form, "status")).Text.Contains("fixture read failure"), "Failed refresh clears stale snapshot rows and directory tree");
                fail = false; wait = true; ((Button)Field(form, "browse")).PerformClick(); Application.DoEvents();
                Require(((Button)Field(form, "cancel")).Enabled && !((Button)Field(form, "export")).Enabled, "Pending repository request exposes cancellation and blocks file actions");
                ((Button)Field(form, "cancel")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Repository cancelled request completes");
                Require(rows.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("取消"), "Cancelled listing cannot repopulate stale rows");
                wait = false; int before = calls; File.WriteAllText(selectorFile, selector.Replace("ui-repository@local", "other@local"));
                ((Button)Field(form, "browse")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Repository context change is rejected");
                Require(calls == before && rows.Items.Count == 0 && ((Label)Field(form, "status")).Text.Contains("仓库已改变"), "Repository mismatch prevents a read from another repository");
                form.Close();
            }
        }

        private static void CheckLiveRepositoryBrowser(string artifacts, string workspacePath)
        {
            var client = new PlasticClient(PlasticClientConfig.Load());
            var branchesTask = client.GetBranchesAsync(workspacePath, CancellationToken.None);
            WaitUntil(() => branchesTask.IsCompleted, "Live browser current branch lookup completes");
            var current = branchesTask.GetAwaiter().GetResult().Single(item => item.IsCurrent);
            using (var form = new RepositoryBrowserForm(client, workspacePath, null, current.Repository))
            {
                Prepare(form); WaitUntil(() => !(bool)Field(form, "busy"), "Live repository default snapshot loads");
                var listing = (PlasticRepositoryListing)Field(form, "listing");
                Require(listing != null && listing.Changeset == current.HeadChangeset && listing.DirectoryPath == "/", "Live browser defaults to current branch head even for Partial workspace");
                var rows = (ListView)Field(form, "files");
                var file = rows.Items.Cast<ListViewItem>().FirstOrDefault(row => {
                    var entry = (PlasticRepositoryEntry)row.Tag;
                    return !entry.IsDirectory && !entry.IsSymbolicLink && entry.Size <= 2 * 1024 * 1024 &&
                        new[] { ".md", ".txt", ".cs" }.Contains(Path.GetExtension(entry.Name).ToLowerInvariant());
                });
                if (file != null)
                {
                    file.Selected = true; Application.DoEvents(); ((Button)Field(form, "view")).PerformClick();
                    WaitUntil(() => !(bool)Field(form, "busy"), "Live repository file preview completes");
                    Require(((Label)Field(form, "status")).Text.Contains("只读 UTF-8 预览"), "Live historical file preview reads selected fixed snapshot");
                }
                Save(form, Path.Combine(artifacts, "repository-browser-live.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); Save(form, Path.Combine(artifacts, "repository-browser-live-minimum.png"));
                var tree = (TreeView)Field(form, "directories");
                if (tree.Nodes[0].Nodes.Count > 0)
                {
                    string path = (string)tree.Nodes[0].Nodes[0].Tag; tree.SelectedNode = tree.Nodes[0].Nodes[0];
                    WaitUntil(() => !(bool)Field(form, "busy"), "Live repository child loads");
                    var child = (PlasticRepositoryListing)Field(form, "listing");
                    Require(child != null && child.DirectoryPath == path && child.Changeset == listing.Changeset, "Live directory navigation retains explicit snapshot");
                    if (file == null)
                    {
                        var childFile = rows.Items.Cast<ListViewItem>().FirstOrDefault(row => {
                            var entry = (PlasticRepositoryEntry)row.Tag;
                            return !entry.IsDirectory && !entry.IsSymbolicLink && entry.Size <= 2 * 1024 * 1024 &&
                                new[] { ".md", ".txt", ".cs" }.Contains(Path.GetExtension(entry.Name).ToLowerInvariant());
                        });
                        if (childFile != null)
                        {
                            childFile.Selected = true; Application.DoEvents(); ((Button)Field(form, "view")).PerformClick();
                            WaitUntil(() => !(bool)Field(form, "busy"), "Live repository child file preview completes");
                            Require(((Label)Field(form, "status")).Text.Contains("只读 UTF-8 预览"), "Live child preview reads selected fixed snapshot");
                            Save(form, Path.Combine(artifacts, "repository-browser-live-child-preview.png"));
                        }
                    }
                    ((Button)Field(form, "up")).PerformClick(); WaitUntil(() => !(bool)Field(form, "busy"), "Live repository parent loads");
                    Require(((PlasticRepositoryListing)Field(form, "listing")).DirectoryPath == "/", "Live repository parent returns to root");
                }
                form.Close();
            }
        }

        private static void Require(bool value, string label)
        { if (!value) throw new Exception(label); Console.WriteLine("PASS: " + label); }
        private static object Field(object target, string name)
        { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            { yield return child; foreach (Control nested in Descendants(child)) yield return nested; }
        }
        private static void WaitUntil(Func<bool> ready, string label)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(55);
            while (!ready() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(25); }
            Require(ready(), label);
        }
        private static void Prepare(Form form)
        {
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(-20000, -20000);
            form.Show();
            Application.DoEvents();
        }
        private static void Save(Form form, string file)
        {
            using (var bitmap = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(file, ImageFormat.Png); }
        }
    }
}
