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
                    settings.Close();
                }
                using (var merge = new ToolLaunchForm(new PlasticClient(PlasticClientConfig.Load())))
                { Prepare(merge); Save(merge, Path.Combine(artifacts, "merge-tool.png")); merge.Close(); }
                if (args.Length > 1)
                {
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
                            Require(list.ContextMenuStrip != null && list.ContextMenuStrip.Items.Count == 3, "Pending context menu provides history, diff and discard");
                            Save(form, Path.Combine(artifacts, "pending-changes.png"));
                            form.Size = form.MinimumSize;
                            Application.DoEvents();
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
