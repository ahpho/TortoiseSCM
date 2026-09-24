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
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string artifacts = args[0];
                Directory.CreateDirectory(artifacts);
                string pathfile = Path.Combine(Path.GetTempPath(), "tscm-ui-" + Guid.NewGuid() + ".paths");
                File.WriteAllLines(pathfile, new[] { @"D:\中文 空格\file & name.txt", @"D:\中文 空格\two.txt" }, new System.Text.UTF8Encoding(false));
                var request = LaunchRequest.Parse(new[] { "--command", "checkin", "--pathfile", pathfile });
                Require(request.Paths.Count == 2 && request.Paths[0].Contains("中文 空格"), "UTF-8 Explorer multi-selection");
                Require(!File.Exists(pathfile), "Consumed path file cleanup");
                try { LaunchRequest.Parse(new[] { "--command", "unknown" }); throw new Exception("Unknown command accepted"); }
                catch (ArgumentException) { }
                try { LaunchRequest.Parse(new[] { "--pathfile", Path.Combine(artifacts, "foreign.txt") }); throw new Exception("Foreign path file accepted"); }
                catch (ArgumentException) { }
                using (var settings = new SettingsForm())
                {
                    Prepare(settings);
                    Save(settings, Path.Combine(artifacts, "settings.png"));
                    var tree = Descendants(settings).OfType<TreeView>().Single();
                    tree.SelectedNode = tree.Nodes[1];
                    Application.DoEvents();
                    Require(((TextBox)Field(settings, "diffTool")).Visible && !((TextBox)Field(settings, "cm")).Visible, "Settings navigation shows the selected diff page");
                    Save(settings, Path.Combine(artifacts, "settings-diff.png"));
                    tree.SelectedNode = tree.Nodes[1].Nodes[0];
                    Application.DoEvents();
                    Require(((TextBox)Field(settings, "mergeTool")).Visible && !((TextBox)Field(settings, "diffTool")).Visible, "Settings navigation shows the selected merge page");
                    settings.Size = settings.MinimumSize;
                    Application.DoEvents();
                    Save(settings, Path.Combine(artifacts, "settings-merge-minimum.png"));
                    settings.Close();
                }
                using (var merge = new ToolLaunchForm(new PlasticClient(PlasticClientConfig.Load())))
                { Prepare(merge); Save(merge, Path.Combine(artifacts, "merge-tool.png")); merge.Close(); }
                if (args.Length > 1)
                {
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
                        Require(!((Button)Field(merge, "start")).Enabled, "Directory conflicts block unsupported merge start");
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
                            Require(list.ContextMenuStrip != null && list.ContextMenuStrip.Items.Cast<ToolStripItem>().Count(item => item is ToolStripMenuItem) == 6, "Pending context menu provides history, diff, discard, move, remove and ignore");
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
                            filter.Clear();
                            WaitUntil(() => ((Button)Field(history, "restore")).Enabled, "Clearing history filter reloads details");
                            Require(revisions.Items.Count == total, "Clearing history filter restores complete loaded history");
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
                            if (readable != null)
                            {
                                readable.Selected = true;
                                Require(((Button)Field(history, "historicalFile")).Enabled, "Selecting a historical file enables compare and export");
                                var file = (PlasticChangesetFile)readable.Tag;
                                long revision = ((PlasticHistoryItem)revisions.SelectedItems[0].Tag).Changeset;
                                using (var fileForm = new HistoricalFileForm(new PlasticClient(PlasticClientConfig.Load()), args[1], file.Path, revision))
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
