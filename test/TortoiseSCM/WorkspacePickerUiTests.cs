// GPL-2.0-or-later. Editable existing-workspace path and validation regression tests.
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class WorkspacePickerUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "bin/TortoiseSCM/qa/workspace-picker-ui" : args[0]); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-picker-" + Guid.NewGuid().ToString("N"));
            string selected = Path.Combine(root, "中文 & 路径 (工作区)"); Directory.CreateDirectory(selected);
            try {
                using (var form = new WorkspacePickerForm()) {
                    form.Show(); Application.DoEvents();
                    var text = Field<TextBox>(form, "directory");
                    Require(!text.ReadOnly && text.ShortcutsEnabled && !text.Multiline, "Path supports normal single-line editing and paste shortcuts");
                    Require(form.AcceptButton == Field<Button>(form, "open") && form.CancelButton == Field<Button>(form, "cancel"), "Enter opens and Escape cancels");
                    foreach (string invalid in new[] { "", Path.Combine(root, "missing"), "bad\0path", "\"unmatched" }) {
                        text.Text = invalid; Field<Button>(form, "open").PerformClick();
                        Require(form.Visible && form.SelectedPath == null && form.DialogResult != DialogResult.OK, "Invalid directory cannot finish selection");
                        Require(Field<Label>(form, "status").Text.Length > 0, "Invalid directory has inline feedback");
                    }
                    string file = Path.Combine(root, "file.txt"); File.WriteAllText(file, "test"); text.Text = file;
                    Field<Button>(form, "open").PerformClick(); Require(form.Visible && form.SelectedPath == null, "File cannot be selected as directory");
                    text.Text = selected;
                    Field<Label>(form, "status").Text = "选择已有 Plastic SCM 工作区的目录，然后点击“打开”。";
                    Bounds(form); Save(form, Path.Combine(artifacts, "workspace-picker.png"));
                    form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form); Save(form, Path.Combine(artifacts, "workspace-picker-minimum.png"));
                    Field<Button>(form, "cancel").PerformClick();
                    Require(form.SelectedPath == null && form.DialogResult == DialogResult.Cancel, "Cancel retains no selection");
                }
                foreach (string value in new[] { selected, "  \"" + selected + "\"  ", selected + Path.DirectorySeparatorChar + "." }) {
                    using (var form = new WorkspacePickerForm()) {
                        form.Show(); Application.DoEvents();
                        Field<TextBox>(form, "directory").SelectedText = value;
                        Field<Button>(form, "open").PerformClick();
                        Require(String.Equals(form.SelectedPath, selected, StringComparison.OrdinalIgnoreCase) && form.DialogResult == DialogResult.OK,
                            "Unicode, spaces, shell characters and quoted pasted paths open as literal directories");
                    }
                }
                TestStartup(selected);
                Console.WriteLine("PASS: workspace picker UI (" + assertions + " assertions)");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void TestStartup(string selected)
        {
            using (var form = new StartupForm()) {
                int calls = 0; form.Show(); Application.DoEvents();
                Set(form, "selectExisting", new Func<string>(() => null));
                Set(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => { calls++; throw new IOException("不是 Plastic 工作区"); }));
                Await((Task)form.GetType().GetMethod("OpenAsync", Flags).Invoke(form, null));
                Require(calls == 0 && form.Visible && form.SelectedWorkspacePath == null, "Cancel picker does not query or open a workspace");
                Set(form, "selectExisting", new Func<string>(() => selected));
                Await((Task)form.GetType().GetMethod("OpenAsync", Flags).Invoke(form, null));
                Require(calls == 1 && form.SelectedWorkspacePath == null && form.Visible && Field<Label>(form, "status").Text.Contains("不是 Plastic 工作区"), "Existing non-workspace directory still receives Plastic validation");
                var pending = new TaskCompletionSource<PlasticWorkspace>();
                Set(form, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => {
                    Require(path == selected, "Exact selected directory reaches asynchronous validation"); return pending.Task;
                }));
                Task opening = (Task)form.GetType().GetMethod("OpenAsync", Flags).Invoke(form, null);
                Require(!Field<Button>(form, "open").Enabled && form.SelectedWorkspacePath == null, "Validation completes before a workspace opens");
                string confirmed = Path.GetDirectoryName(selected); pending.SetResult(new PlasticWorkspace { RootPath = confirmed }); Await(opening);
                Require(form.SelectedWorkspacePath == confirmed && form.DialogResult == DialogResult.OK, "Workspace opens using backend-confirmed root");
            }
        }

        private static void Bounds(Control control)
        { foreach (Control child in control.Controls) { if (child.Visible) Require(control.ClientRectangle.Contains(child.Bounds), "Control fits: " + child.GetType().Name); Bounds(child); } }
        private static void Save(Form form, string path)
        { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void Await(Task task)
        { var timer = Stopwatch.StartNew(); while (!task.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(10)) { Application.DoEvents(); Thread.Sleep(5); } if (!task.IsCompleted) throw new TimeoutException(); task.GetAwaiter().GetResult(); Application.DoEvents(); }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Flags).SetValue(owner, value); }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception("Workspace picker: " + message); }
    }
}
