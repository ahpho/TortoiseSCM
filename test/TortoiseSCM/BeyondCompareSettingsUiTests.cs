// GPL-2.0-or-later. Settings checks never persist configuration or launch an editor.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class BeyondCompareSettingsUiTests
    {
        private static int assertions;

        internal static void Run(string artifacts)
        {
            assertions = 0;
            Directory.CreateDirectory(artifacts);
            using (var settings = new SettingsForm())
            {
                settings.Show(); Application.DoEvents();
                Save(settings, Path.Combine(artifacts, "settings.png"));
                var tree = Descendants(settings).OfType<TreeView>().Single();
                var diff = Field<TextBox>(settings, "diffTool");
                var merge = Field<TextBox>(settings, "mergeTool");
                Require(!Descendants(settings).OfType<CheckBox>().Any(), "Settings provide no built-in or custom editor choice");
                Require(!Descendants(settings).Any(control => control.AccessibleName == "参数模板"), "Tool arguments are fixed instead of editable templates");
                Require(Descendants(settings).Count(control => control.AccessibleName == "自动检测 Beyond Compare") == 2,
                    "Both editor pages offer Beyond Compare detection");
                Require(Descendants(settings).Any(control => control.Text.Contains("不限制 Beyond Compare 编辑时间")),
                    "Plastic timeout is distinct from the editor lifetime");
                tree.SelectedNode = tree.Nodes[1]; Application.DoEvents();
                Require(diff.Visible && !merge.Visible && !Field<TextBox>(settings, "cm").Visible, "Diff navigation shows the shared editor path");
                diff.Text = Path.Combine(Path.GetTempPath(), "tscm-missing-" + Guid.NewGuid().ToString("N"), "BComp.exe");
                Require(merge.Text == diff.Text, "Changing diff path synchronizes merge path");
                Require(Field<Label>(settings, "diffStatus").Text.Contains("未找到") && Field<Label>(settings, "diffStatus").Text.Contains("浏览"),
                    "Missing executable status gives an actionable installation or browse instruction");
                Require(Field<Label>(settings, "diffStatus").Text == Field<Label>(settings, "mergeStatus").Text,
                    "Editor availability status is shared");
                Descendants(settings).OfType<Button>().Single(button => button.Visible && button.AccessibleName == "自动检测 Beyond Compare").PerformClick();
                Require(diff.Text == String.Empty && merge.Text == String.Empty, "Automatic detection does not pin an installed version directory");
                diff.Text = Path.Combine(Path.GetTempPath(), "tscm-missing-" + Guid.NewGuid().ToString("N"), "BComp.exe");
                try
                {
                    typeof(SettingsForm).GetMethod("ApplyTools", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(settings, null);
                    throw new Exception("Missing explicit Beyond Compare path was accepted");
                }
                catch (TargetInvocationException ex) { Require(ex.InnerException is FileNotFoundException, "Missing explicit path fails validation"); }
                Save(settings, Path.Combine(artifacts, "settings-diff.png"));
                settings.Size = settings.MinimumSize; Application.DoEvents();
                CheckVisibleBounds(settings);
                Save(settings, Path.Combine(artifacts, "settings-diff-minimum.png"));
                tree.SelectedNode = tree.Nodes[1].Nodes[0]; Application.DoEvents();
                Require(merge.Visible && !diff.Visible, "Merge navigation shows the shared editor path");
                merge.Text = String.Empty;
                Require(diff.Text == String.Empty, "Clearing merge path enables automatic detection for both pages");
                typeof(SettingsForm).GetMethod("ApplyTools", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(settings, null);
                var config = Field<PlasticClientConfig>(settings, "config");
                Require(config.UseBeyondCompare && config.BeyondComparePath == String.Empty,
                    "Empty automatic path can be applied without requiring an installed editor or saving user settings");
                string mergeText = String.Join("\n", Descendants(settings).Where(control => control.Visible).Select(control => control.Text));
                Require(mergeText.Contains("Beyond Compare Pro") && mergeText.Contains("左侧：本地；右侧：远程；祖先：基线；输出：合并结果"),
                    "Merge page describes the Pro requirement and fixed input roles");
                Require(mergeText.Contains("不会自动解决冲突或签入"), "Merge page states the explicit apply boundary");
                CheckVisibleBounds(settings);
                Save(settings, Path.Combine(artifacts, "settings-merge-minimum.png"));
                settings.ClientSize = new Size(800, 440); Application.DoEvents();
                Save(settings, Path.Combine(artifacts, "settings-merge.png"));
                settings.Close();
            }
            Console.WriteLine("PASS: Beyond Compare settings UI (" + assertions + " assertions)");
        }

        private static void CheckVisibleBounds(Form form)
        {
            foreach (Control control in Descendants(form).Where(control => control.Visible && (control is Button || control is TextBox || control is Label)))
            {
                var bounds = control.RectangleToScreen(control.ClientRectangle);
                Require(form.RectangleToScreen(form.ClientRectangle).Contains(bounds), "Minimum settings window contains " + control.GetType().Name);
                Require(control.Parent.RectangleToScreen(control.Parent.ClientRectangle).Contains(bounds), "Settings page contains " + control.GetType().Name);
            }
        }

        private static T Field<T>(object target, string name)
        { return (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }

        private static IEnumerable<Control> Descendants(Control control)
        {
            foreach (Control child in control.Controls)
            {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }

        private static void Save(Form form, string path)
        {
            using (var image = new Bitmap(form.Width, form.Height))
            { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path, ImageFormat.Png); }
        }

        private static void Require(bool value, string message)
        { if (!value) throw new Exception(message); assertions++; }
    }
}
