// TortoiseSCM - GPL-2.0-or-later. Native editor interaction and normal/minimum rendering.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class TextEditorUiTests
    {
        private static int assertions;
        [STAThread]
        private static int Main(string[] args)
        {
            var oldContext = SynchronizationContext.Current;
            bool oldAutoInstall = WindowsFormsSynchronizationContext.AutoInstall;
            WindowsFormsSynchronizationContext context = null;
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                WindowsFormsSynchronizationContext.AutoInstall = false;
                context = new WindowsFormsSynchronizationContext(); SynchronizationContext.SetSynchronizationContext(context);
                Control.CheckForIllegalCrossThreadCalls = true;
                Run(args.Length == 0 ? Path.Combine(Path.GetTempPath(), "TortoiseSCM-editor-qa") : args[0]);
                Console.WriteLine("Text editor UI: " + assertions + " assertions passed."); return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            finally { SynchronizationContext.SetSynchronizationContext(oldContext); WindowsFormsSynchronizationContext.AutoInstall = oldAutoInstall; if (context != null) context.Dispose(); }
        }

        internal static void Run(string artifacts)
        {
            Directory.CreateDirectory(artifacts);
            string fixture = Path.Combine(artifacts, "editor-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(fixture);
            string baseline = Path.Combine(fixture, "base.txt"), local = Path.Combine(fixture, "local.txt"), remote = Path.Combine(fixture, "remote.txt"), output = Path.Combine(fixture, "result.txt");
            var lines = Enumerable.Range(1, 100).Select(i => "Line " + i + " — Unicode 中文").ToArray();
            File.WriteAllText(baseline, String.Join("\n", lines) + "\n", new UTF8Encoding(false));
            lines[4] = "Local edit 中文"; lines[60] = "Second local edit";
            File.WriteAllText(local, String.Join("\n", lines) + "\n", new UTF8Encoding(false));
            lines[4] = "Remote edit 中文"; lines[35] = "Remote insertion";
            File.WriteAllText(remote, String.Join("\n", lines) + "\n", new UTF8Encoding(false));
            using (var form = new TextDiffForm(baseline, local))
            {
                Prepare(form);
                var grids = Descendants(form).OfType<DataGridView>().ToArray();
                Check(grids.Length == 2 && grids.All(grid => grid.ReadOnly), "Diff has two read-only panes");
                Check(grids[0].RowCount == grids[1].RowCount, "Aligned diff rows have matching length");
                form.Navigate(-1); Check(grids.All(grid => grid.CurrentCell.RowIndex == 60), "Initial previous difference navigates to last block");
                form.Navigate(1); Check(grids.All(grid => grid.CurrentCell.RowIndex == 4), "Next difference navigates both panes");
                form.Navigate(1); Check(grids.All(grid => grid.CurrentCell.RowIndex == 60), "Next difference skips unchanged rows");
                form.Navigate(-1); Check(grids.All(grid => grid.CurrentCell.RowIndex == 4), "Previous difference returns to prior hunk");
                grids[0].FirstDisplayedScrollingRowIndex = 30; Application.DoEvents();
                Check(grids[1].FirstDisplayedScrollingRowIndex == 30, "Vertical scrolling remains synchronized");
                form.Navigate(1); form.Navigate(1);
                Capture(form, Path.Combine(artifacts, "text-diff.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); CheckBounds(form);
                Capture(form, Path.Combine(artifacts, "text-diff-minimum.png")); form.Close();
            }
            string beforeBase = File.ReadAllText(baseline), beforeLocal = File.ReadAllText(local), beforeRemote = File.ReadAllText(remote);
            using (var form = new TextMergeForm(baseline, local, remote, output))
            {
                Prepare(form);
                Check(!File.Exists(output) && !form.Saved, "Opening merge creates no output and claims no save");
                var grids = Descendants(form).OfType<DataGridView>().ToArray();
                Check(grids.Length == 3 && grids.All(grid => grid.ReadOnly), "All merge inputs are read-only");
                var editor = Descendants(form).OfType<TextBox>().Single();
                Check(!editor.ReadOnly && editor.Text.Contains("Local edit"), "Editable result starts with local content");
                editor.AppendText("Manual result 中文\r\n"); Check(form.Text.Contains("*"), "Editing marks result dirty");
                editor.SelectionStart = 0; editor.ScrollToCaret();
                Capture(form, Path.Combine(artifacts, "text-merge.png")); form.Size = form.MinimumSize; Application.DoEvents();
                CheckBounds(form); Capture(form, Path.Combine(artifacts, "text-merge-minimum.png"));
                Check(form.SaveResult() && form.Saved && !form.Text.Contains("*"), "Explicit save persists and clears dirty state");
                Check(File.ReadAllText(output).EndsWith("Manual result 中文\n") && !File.ReadAllText(output).Contains("\r"), "Save preserves local LF convention");
                editor.AppendText("Second edit\r\n"); Check(form.SaveResult(), "A second save uses updated disk identity"); form.Close();
            }
            Check(File.ReadAllText(baseline) == beforeBase && File.ReadAllText(local) == beforeLocal && File.ReadAllText(remote) == beforeRemote, "Saving never changes merge inputs");
            using (var recovered = new TextMergeForm(baseline, local, remote, output))
            {
                Prepare(recovered);
                Check(Descendants(recovered).OfType<TextBox>().Single().Text.Contains("Second edit"), "Reopening recovers existing result");
                Check(!recovered.Saved, "Reopening does not claim a new save or resolution"); recovered.Close();
            }
            string empty = Path.Combine(fixture, "empty.txt"); File.WriteAllText(empty, "");
            using (var form = new TextDiffForm(empty, empty)) { Prepare(form); form.Navigate(1); Check(!Descendants(form).OfType<Button>().First(button => button.Text == "下一个差异").Enabled, "Empty identical files disable navigation"); form.Close(); }
            string binary = Path.Combine(fixture, "binary.dat"); File.WriteAllBytes(binary, new byte[] { 0, 1, 2, 3 });
            bool rejected = false; try { using (var form = new TextDiffForm(binary, empty)) { } } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Binary files fail with a user-readable error before opening an editor");
            string bom = Path.Combine(fixture, "bom.txt"); File.WriteAllText(bom, "", new UTF8Encoding(true));
            using (var form = new TextDiffForm(empty, bom))
            {
                Prepare(form); Check(Descendants(form).OfType<Label>().Any(label => label.Text.Contains("编码/BOM 不同")), "BOM-only differences are explicitly disclosed"); form.Close();
            }
            string insertionBase = Path.Combine(fixture, "insertion-base.txt"), insertionLocal = Path.Combine(fixture, "insertion-local.txt"), insertionRemote = Path.Combine(fixture, "insertion-remote.txt");
            File.WriteAllText(insertionBase, "one\ntwo\nthree"); File.WriteAllText(insertionLocal, "one\nlocal insertion\ntwo\nthree"); File.WriteAllText(insertionRemote, "one\ntwo\nremote insertion\nthree");
            using (var form = new TextMergeForm(insertionBase, insertionLocal, insertionRemote, Path.Combine(fixture, "insertion-result.txt")))
            {
                Prepare(form); var grids = Descendants(form).OfType<DataGridView>().ToArray();
                Check(grids.All(grid => grid.RowCount == 5), "Merge inserts aligned gaps for independent insertions");
                Check((string)grids[0].Rows[1].Cells[1].Value == "" && (string)grids[1].Rows[1].Cells[1].Value == "local insertion" && (string)grids[2].Rows[1].Cells[1].Value == "", "Local insertion is aligned against explicit gaps");
                Check((string)grids[0].Rows[3].Cells[1].Value == "" && (string)grids[2].Rows[3].Cells[1].Value == "remote insertion", "Remote insertion preserves its base anchor");
                form.Navigate(-1); Check(grids.All(grid => grid.CurrentCell.RowIndex == 3), "Initial previous merge difference opens final block"); form.Close();
            }
            CheckHost(baseline, local, remote, Path.Combine(fixture, "host-result.txt"));
            string longLeft = Path.Combine(fixture, "long-left.txt"), longRight = Path.Combine(fixture, "long-right.txt");
            string longText = new string('x', 10000) + "changed end 中文";
            File.WriteAllText(longLeft, new string('x', 10000) + "original end"); File.WriteAllText(longRight, longText);
            using (var form = new TextDiffForm(longLeft, longRight))
            {
                Prepare(form); var pane = Descendants(form).OfType<TextLinePane>().Last();
                bool inspected = false; Exception failure = null;
                using (var timer = new System.Windows.Forms.Timer { Interval = 30 })
                {
                    timer.Tick += delegate
                    {
                        var inspector = Application.OpenForms.OfType<TextLineInspectorForm>().FirstOrDefault();
                        if (inspector == null) return;
                        timer.Stop();
                        try
                        {
                            var content = Descendants(inspector).OfType<TextBox>().Single();
                            Check(content.ReadOnly && content.WordWrap && content.Text == longText, "Long-line inspector exposes exact full content beyond grid width");
                            content.SelectionStart = content.TextLength; content.ScrollToCaret();
                            Check(content.SelectionStart > 10000, "Inspector can navigate to changed content near long-line end");
                            inspector.Size = inspector.MinimumSize; Application.DoEvents(); CheckBounds(inspector);
                            content.Focus(); content.SelectionStart = content.TextLength; content.ScrollToCaret(); Application.DoEvents();
                            Capture(inspector, Path.Combine(artifacts, "text-line-inspector-minimum.png")); inspected = true;
                        }
                        catch (Exception error) { failure = error; }
                        finally { inspector.Close(); }
                    };
                    timer.Start(); pane.ShowLine(0);
                }
                if (failure != null) throw failure;
                Check(inspected, "Long-line inspector opens from actual diff pane"); form.Close();
            }
        }

        private static void CheckHost(string baseline, string local, string remote, string output)
        {
            using (var owner = new Form())
            {
                Prepare(owner);
                var host = new WinFormsPlasticToolHost(owner);
                for (int mode = 0; mode < 3; mode++)
                {
                    int testMode = mode; bool sawDialog = false; Exception interactionError = null;
                    Task<PlasticCommandResult> operation = null;
                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                    using (var timer = new System.Windows.Forms.Timer { Interval = 30 })
                    {
                        timer.Tick += delegate
                        {
                            Form dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => testMode == 0 ? form is TextDiffForm : form is TextMergeForm);
                            if (dialog == null) return;
                            timer.Stop();
                            try
                            {
                                sawDialog = true;
                                Check(operation != null && !operation.IsCompleted, "Host task waits for modal editor close");
                                Check(!dialog.InvokeRequired, "Worker-requested editor runs on the owner UI thread");
                                if (testMode == 2)
                                {
                                    var editor = Descendants(dialog).OfType<TextBox>().Single();
                                    editor.AppendText("Host saved content\r\n");
                                    Check(((TextMergeForm)dialog).SaveResult(), "Host editor can save its designated result");
                                }
                            }
                            catch (Exception error) { interactionError = error; }
                            finally { dialog.Close(); }
                        };
                        operation = Task.Run(() => testMode == 0 ? host.ShowDiffAsync(baseline, local, CancellationToken.None) : host.ShowMergeAsync(baseline, local, remote, output, CancellationToken.None));
                        timer.Start();
                        while (!operation.IsCompleted && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
                    }
                    if (interactionError != null) throw interactionError;
                    Check(sawDialog && operation.IsCompleted, "Worker-requested modal editor opens and completes");
                    PlasticCommandResult response = operation.GetAwaiter().GetResult();
                    Check(response.ExitCode == 0, "Host returns success after editor closes");
                    if (testMode == 1) Check(!File.Exists(output) && response.Output.Contains("未保存"), "Closing merge without saving reports no saved result");
                    if (testMode == 2) Check(File.ReadAllText(output).Contains("Host saved content") && response.Output.Contains("已保存"), "Host reports actual saved result");
                }
                owner.Close();
            }
        }

        private static void Check(bool success, string message) { if (!success) throw new Exception(message); assertions++; }
        private static IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
        private static void Prepare(Form form) { form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show(); Application.DoEvents(); }
        private static void Capture(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void CheckBounds(Form form)
        {
            Rectangle client = form.RectangleToScreen(form.ClientRectangle);
            foreach (var control in Descendants(form).Where(control => control is Button || control is TextBox || control is DataGridView || control is ComboBox))
                Check(client.Contains(control.RectangleToScreen(control.ClientRectangle)), control.GetType().Name + " fits minimum form size");
        }
    }
}
