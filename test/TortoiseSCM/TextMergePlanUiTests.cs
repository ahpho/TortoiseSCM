// TortoiseSCM - GPL-2.0-or-later. Block merge interactions and native layout evidence.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using TortoiseSCM.Core;

namespace TortoiseSCM
{
    internal static class TextMergePlanUiTests
    {
        private static int assertions;
        internal static void Run(string artifacts)
        {
            Directory.CreateDirectory(artifacts);
            string fixture = Path.Combine(artifacts, "merge-plan-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            string baseline = Path.Combine(fixture, "base.txt"), local = Path.Combine(fixture, "local.txt"), remote = Path.Combine(fixture, "remote.txt"), output = Path.Combine(fixture, "result.txt");
            Write(baseline, "header\nbase local\nseparator A\nbase conflict\nseparator B\nbase remote\nfooter\n");
            Write(local, "header\nLOCAL ONLY 中文\nseparator A\nLOCAL CONFLICT\nseparator B\nbase remote\nfooter\n");
            Write(remote, "header\nbase local\nseparator A\nREMOTE CONFLICT\nseparator B\nREMOTE ONLY 中文\nfooter\n");
            string beforeBase = File.ReadAllText(baseline), beforeLocal = File.ReadAllText(local), beforeRemote = File.ReadAllText(remote);
            using (var form = new TextMergeForm(baseline, local, remote, output))
            {
                Prepare(form);
                var editor = Descendants(form).OfType<TextBox>().Single();
                var list = Descendants(form).OfType<ListView>().Single();
                Check(!form.HasBlockMapping && !File.Exists(output), "Opening preserves local result without creating draft or audit");
                Check(!editor.Text.Contains("REMOTE ONLY"), "Opening does not silently perform automatic merge");
                Button(form, "自动合并").PerformClick(); Application.DoEvents();
                Check(form.HasBlockMapping && list.Enabled && list.Items.Count == 3, "Explicit auto-merge creates three independently selectable blocks");
                Check(editor.Text.Contains("LOCAL ONLY") && editor.Text.Contains("REMOTE ONLY") && editor.Text.Contains("LOCAL CONFLICT"), "Automatic draft combines independent contributions and preserves local conflict draft");
                Check(form.PendingBlockCount == 1 && list.Items[1].SubItems[1].Text == "待处理", "Conflict remains pending for explicit review");
                Check(Button(form, "保存草稿").Enabled, "Pending conflict labels save as draft");
                Check(form.SaveResult() && Labels(form).Any(text => text.Contains("仍有 1 个冲突块待审核")), "Saving pending draft discloses remaining conflict without claiming resolution");
                Check(!form.GenerateMerge(false), "Regeneration cannot discard an existing mapped draft without explicit consent");
                form.SelectBlock(1); Application.DoEvents();
                Check(editor.SelectedText.Contains("LOCAL CONFLICT"), "Selecting conflict highlights exact editable result span");
                var grids = Descendants(form).OfType<DataGridView>().ToArray();
                Check(grids.Length == 3 && grids.All(grid => grid.SelectedCells.Count > 0), "Selection highlights source contributions in all three inputs");
                Button(form, "采用远程块").PerformClick();
                Check(editor.Text.Contains("REMOTE CONFLICT") && !editor.Text.Contains("LOCAL CONFLICT") && editor.Text.Contains("LOCAL ONLY"), "Adopting remote replaces only selected conflict block");
                Check(Labels(form).Any(text => text.Contains("结果有未保存修改")) && !Labels(form).Any(text => text.Contains("结果已保存")), "Adopting after a save never labels new result content as saved");
                Check(form.PendingBlockCount == 0 && list.Items[1].SubItems[1].Text == "已核查", "Explicit adoption records reviewed status and clears pending count");
                Check(Labels(form).Any(text => text.Contains("无待选择冲突") && text.Contains("自动采用 2")), "Zero conflicts keeps automatic adoption distinct from human review");
                form.SelectBlock(0); Button(form, "采用基线块").PerformClick();
                Check(editor.Text.Contains("base local") && !editor.Text.Contains("LOCAL ONLY"), "Base block adoption restores only its own range");
                Button(form, "采用本地块").PerformClick();
                Check(editor.Text.Contains("LOCAL ONLY") && editor.Text.Contains("REMOTE CONFLICT"), "Local block adoption preserves prior block choice");
                form.SelectBlock(2); Button(form, "标记已核查").PerformClick();
                Check(list.Items[2].SubItems[1].Text == "已核查", "Automatic contribution can be explicitly marked reviewed");
                form.SelectBlock(1); Capture(form, Path.Combine(artifacts, "text-merge-plan.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); CheckBounds(form);
                Capture(form, Path.Combine(artifacts, "text-merge-plan-minimum.png"));
                editor.AppendText("MANUAL AUDIT 中文\r\n");
                Check(!form.HasBlockMapping && !list.Enabled && form.PendingBlockCount == -1, "Arbitrary manual edit invalidates offsets and audit state");
                Check(!Button(form, "采用基线块").Enabled && !Button(form, "标记已核查").Enabled, "Manual mode disables block modification and review");
                Check(!form.ChooseSelectedBlock(TextMergeChoice.Remote) && !form.ReviewSelectedBlock(), "Invalidated block operations cannot use stale positions");
                Check(!form.GenerateMerge(false) && editor.Text.Contains("MANUAL AUDIT"), "Regeneration guard protects manually edited work");
                Check(form.SaveResult() && Labels(form).Any(text => text.Contains("手工结果已保存，需全文审核")), "Manual result save requests complete manual audit");
                Capture(form, Path.Combine(artifacts, "text-merge-plan-manual.png"));
                form.Close();
            }
            using (var form = new TextMergeForm(baseline, local, remote, output))
            {
                Prepare(form); var editor = Descendants(form).OfType<TextBox>().Single();
                Check(editor.Text.Contains("MANUAL AUDIT") && editor.Text.Contains("REMOTE CONFLICT"), "Reopening preserves exact saved choices and manual changes");
                Check(!form.HasBlockMapping && !form.Saved, "Reopening never infers reviewed states from result contents");
                Check(!form.GenerateMerge(false) && editor.Text.Contains("MANUAL AUDIT"), "Recovered results require explicit discard consent before regeneration");
                Check(form.GenerateMerge(true) && form.PendingBlockCount == 1 && !editor.Text.Contains("MANUAL AUDIT"), "Explicit regeneration rebuilds plan and resets reviews");
                Check(form.SaveResult(), "Regenerated draft saves through original safe document path"); form.Close();
            }
            Check(File.ReadAllText(baseline) == beforeBase && File.ReadAllText(local) == beforeLocal && File.ReadAllText(remote) == beforeRemote, "Every block action preserves all input files");
            // A length-changing choice must shift following result spans before the next adoption.
            Write(baseline, "one\ntwo\nthree\nfour\nfive\n");
            Write(local, "one\nlocal insert A\nlocal insert B\ntwo\nthree\nLOCAL FOUR\nfive\n");
            Write(remote, "one\nremote insert\ntwo\nthree\nREMOTE FOUR\nfive\n");
            using (var form = new TextMergeForm(baseline, local, remote, Path.Combine(fixture, "insert-result.txt")))
            {
                Prepare(form); Check(form.GenerateMerge(false), "Insertion fixture generates plan");
                form.SelectBlock(0); Check(form.ChooseSelectedBlock(TextMergeChoice.Remote), "Length-changing insertion choice succeeds");
                form.SelectBlock(1); var editor = Descendants(form).OfType<TextBox>().Single();
                Check(editor.SelectedText.Contains("LOCAL FOUR"), "Later block selection follows new result offsets");
                Check(form.ChooseSelectedBlock(TextMergeChoice.Remote) && editor.Text.Contains("remote insert") && editor.Text.Contains("REMOTE FOUR") && !editor.Text.Contains("LOCAL FOUR"), "Sequential adoption edits exact shifted range");
                Check(form.SaveResult(), "Insertion choices save"); form.Close();
            }
            Write(baseline, "same\n"); Write(local, "same\n"); File.WriteAllText(remote, "same\n", new UTF8Encoding(true));
            using (var form = new TextMergeForm(baseline, local, remote, Path.Combine(fixture, "metadata-result.txt")))
            {
                Prepare(form);
                Check(!Button(form, "下一个差异").Enabled, "Metadata-only inputs have no initial text difference navigation");
                Check(form.GenerateMerge(false) && form.PendingBlockCount == 1, "Metadata-only differences create a conservative pending block");
                Check(Button(form, "下一个差异").Enabled && Labels(form).Any(text => text.Contains("格式差异")), "Metadata fallback enables block navigation and explains format review");
                Button(form, "下一个差异").PerformClick();
                Check(Descendants(form).OfType<TextBox>().Single().SelectedText.Contains("same"), "Metadata-only block navigation highlights draft content");
                Check(form.SaveResult(), "Metadata-only draft saves without adopting remote encoding"); form.Close();
            }
            Console.WriteLine("Text merge plan UI: " + assertions + " assertions passed.");
        }
        private static void Write(string path, string text) { File.WriteAllText(path, text, new UTF8Encoding(false)); }
        private static Button Button(Form form, string text) { return Descendants(form).OfType<Button>().Single(button => button.Text == text); }
        private static IEnumerable<string> Labels(Form form) { return Descendants(form).OfType<Label>().Select(label => label.Text); }
        private static void Check(bool success, string message) { if (!success) throw new Exception(message); assertions++; }
        private static IEnumerable<Control> Descendants(Control parent) { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
        private static void Prepare(Form form) { form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); form.Show(); Application.DoEvents(); }
        private static void Capture(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void CheckBounds(Form form)
        {
            Rectangle client = form.RectangleToScreen(form.ClientRectangle);
            foreach (var control in Descendants(form).Where(control => control is Button || control is TextBox || control is DataGridView || control is ComboBox || control is ListView))
                Check(client.Contains(control.RectangleToScreen(control.ClientRectangle)), control.GetType().Name + " fits minimum merge plan form size");
        }
    }
}
