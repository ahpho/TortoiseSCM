// GPL-2.0-or-later. Long source paths must not hide move instructions.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class PathInputUiTests
    {
        internal static void Run(string artifacts)
        {
            Directory.CreateDirectory(artifacts);
            string source = @"D:\工作区 & space\" + String.Concat(Enumerable.Repeat("long directory\\", 18)) + "源文件.txt";
            using (var form = new PathInputForm(source))
            {
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-24000, -24000);
                form.Show(); Application.DoEvents();
                var sourceBox = (TextBox)form.Controls.Find("sourcePath", true).Single();
                var destinationBox = (TextBox)form.Controls.Find("destinationPath", true).Single();
                Check(form.ActiveControl == destinationBox, "Move dialog starts at the editable destination");
                var note = (Label)form.Controls.Find("instructions", true).Single();
                Check(sourceBox.ReadOnly && sourceBox.Text == source, "Long source path remains readable and unchanged");
                sourceBox.SelectAll();
                Check(sourceBox.SelectedText == source, "Full source path can be selected for copying");
                foreach (bool minimum in new[] { false, true })
                {
                    if (minimum) form.Size = form.MinimumSize;
                    Application.DoEvents();
                    Size needed = TextRenderer.MeasureText(note.Text, note.Font, new Size(note.ClientSize.Width, Int32.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
                    Check(note.ClientSize.Height >= needed.Height, "Move instructions fit at " + (minimum ? "minimum" : "normal") + " size");
                    foreach (Control control in new Control[] { sourceBox, note, (Control)form.AcceptButton, (Control)form.CancelButton })
                        Check(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Move control remains visible: " + control.Text);
                    using (var bitmap = new Bitmap(form.Width, form.Height))
                    { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(artifacts, "rename-long-path" + (minimum ? "-minimum" : "") + ".png"), ImageFormat.Png); }
                }
                form.CancelButton.PerformClick();
                Check(form.DialogResult == DialogResult.Cancel && form.Destination == source, "Cancel leaves proposed destination unchanged");
            }
            Console.WriteLine("PASS: path input UI (14 assertions)");
        }
        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }
    }
}
