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
                CheckConflictDialogs(artifacts);
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
