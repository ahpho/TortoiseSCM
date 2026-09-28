// GPL-2.0-or-later. Native history button routing and asynchronous lifetime checks.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class HistoricalBeyondCompareUiTests
    {
        private static int assertions;
        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "tscm-history-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "history-ui\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"test@server\"");
            var client = new PlasticClient(new PlasticClientConfig());
            try
            {
                using (var form = new HistoricalFileForm(client, root, "/文件 中文 &.txt", 8, 3))
                {
                    int launches = 0;
                    var pending = new TaskCompletionSource<PlasticCommandResult>();
                    Set(form, "openRevisionTool", new Func<string, string, string, long, long, CancellationToken, Task<PlasticCommandResult>>((workspace, before, after, from, to, token) =>
                    {
                        launches++;
                        Require(workspace == root && before == "/文件 中文 &.txt" && after == before && from == 3 && to == 8, "Main history action passes the selected path and revisions to BC");
                        return pending.Task;
                    }));
                    form.Show(); Application.DoEvents();
                    var button = Field<Button>(form, "compare");
                    Require(button.Visible && button.Text == "比较工具" && !Field<Button>(form, "external").Visible, "History has one visible Beyond Compare action");
                    button.PerformClick(); Application.DoEvents();
                    Require(launches == 1 && Field<bool>(form, "busy"), "Clicking the actual history button dispatches the editor");
                    Require(!button.Enabled && !Field<Button>(form, "export").Enabled && !Field<NumericUpDown>(form, "fromRevision").Enabled, "Pending editor locks history operations and revision inputs");
                    form.Close(); Application.DoEvents();
                    Require(!form.IsDisposed && form.Visible, "History window cannot close while the editor session is pending");
                    button.PerformClick(); Require(launches == 1, "Disabled button cannot open duplicate comparison session");
                    pending.SetResult(new PlasticCommandResult { ExitCode = 17, Error = "测试工具错误" }); Pump(form);
                    Require(button.Enabled && Field<NumericUpDown>(form, "fromRevision").Enabled && Field<Label>(form, "status").Text.Contains("测试工具错误"), "Editor failure restores controls and reports the failure");
                    Require(Field<TextBox>(form, "preview").Text.Contains("测试工具错误"), "Editor failure details remain visible");
                    Save(form, Path.Combine(artifacts, "history-beyond-compare.png"));
                    form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form);
                    Save(form, Path.Combine(artifacts, "history-beyond-compare-minimum.png"));
                    form.Close();
                }

                foreach (string status in new[] { "A", "D", "M" })
                {
                    var comparison = new PlasticChangesetComparison { Repository = "test@server", RootPath = root, FromChangeset = 3, ToChangeset = 8 };
                    var row = new PlasticChangesetFile { Status = status, Path = "/新文件.txt", OldPath = status == "M" ? "/旧文件.txt" : "", ItemType = "F" };
                    using (var form = new HistoricalFileForm(client, root, comparison, row))
                    {
                        int launches = 0;
                        var pending = new TaskCompletionSource<PlasticCommandResult>();
                        Set(form, "openChangesetTool", new Func<string, PlasticChangesetComparison, PlasticChangesetFile, CancellationToken, Task<PlasticCommandResult>>((workspace, captured, file, token) =>
                        {
                            launches++;
                            Require(workspace == root && captured.Repository == "test@server" && captured.FromChangeset == 3 && captured.ToChangeset == 8 && file.Status == status && file.Path == "/新文件.txt", "Fixed pair sends the exact reviewed row to the proving BC adapter: " + status);
                            Require(file.OldPath == (status == "M" ? "/旧文件.txt" : ""), "Moved comparison retains source path");
                            return pending.Task;
                        }));
                        // A mutable parent list must not silently change an already opened dialog.
                        row.Path = "/changed-after-open.txt"; comparison.ToChangeset = 99;
                        form.Show(); Application.DoEvents();
                        var button = Field<Button>(form, "compare");
                        Require(button.Enabled && !Field<NumericUpDown>(form, "fromRevision").Enabled && !Field<NumericUpDown>(form, "toRevision").Enabled, "Fixed pairs permit BC and keep revision controls pinned: " + status);
                        Require(Field<Button>(form, "exportSource").Enabled == (status != "A") && Field<Button>(form, "export").Enabled == (status != "D"), "Exports remain limited to existing endpoints: " + status);
                        button.PerformClick(); Application.DoEvents();
                        Require(launches == 1 && Field<bool>(form, "busy") && !button.Enabled, "Fixed-pair main action dispatches once and locks while pending: " + status);
                        pending.SetResult(new PlasticCommandResult { ExitCode = 0 }); Pump(form);
                        Require(button.Enabled && Field<Label>(form, "status").Text == "比较工具 操作完成。" && !Field<NumericUpDown>(form, "toRevision").Enabled, "BC completion restores fixed-pair actions without unpinning revisions");
                        if (status == "A")
                        {
                            Require(Field<TextBox>(form, "preview").Text.Contains("空起点"), "Added file explains the proven empty side");
                            Save(form, Path.Combine(artifacts, "history-added-beyond-compare.png"));
                            form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form);
                            Save(form, Path.Combine(artifacts, "history-added-beyond-compare-minimum.png"));
                        }
                        form.Close();
                    }
                }
            }
            finally { Directory.Delete(root, true); }
            Console.WriteLine("PASS: historical Beyond Compare UI (" + assertions + " assertions)");
        }

        private static void Pump(HistoricalFileForm form)
        {
            DateTime limit = DateTime.UtcNow.AddSeconds(5);
            while (Field<bool>(form, "busy") && DateTime.UtcNow < limit) { Application.DoEvents(); Thread.Sleep(5); }
            Require(!Field<bool>(form, "busy"), "Editor completion returns the history window to idle");
        }
        private static void Bounds(Form form)
        {
            foreach (Control control in Descendants(form).Where(c => c.Visible && (c is Button || c is NumericUpDown || c is TextBox)))
                Require(control.Parent.RectangleToScreen(control.Parent.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Minimum history layout contains " + control.GetType().Name);
        }
        private static IEnumerable<Control> Descendants(Control control)
        { foreach (Control child in control.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
        private static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }
        private static void Set(object target, string name, object value)
        { target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value); }
        private static void Save(Form form, string path)
        { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void Require(bool value, string message)
        { assertions++; if (!value) throw new Exception(message); }
    }
}
