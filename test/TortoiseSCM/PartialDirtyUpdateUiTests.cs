// GPL-2.0-or-later. Update preflight, explicit resolution routing and byte-safe choices.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class PartialDirtyUpdateUiTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static int assertions;
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static void Set(object owner, string name, object value) { owner.GetType().GetField(name, Flags).SetValue(owner, value); }
        private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        private static void Pump(Func<bool> done)
        { var end = DateTime.UtcNow.AddSeconds(10); do { Application.DoEvents(); Thread.Sleep(10); } while (!done() && DateTime.UtcNow < end); Check(done(), "UI task completes"); }
        private static void Capture(Form form, string path)
        {
            foreach (string name in form is UpdateForm ? new[] { "pending", "conflicts", "update", "close" } : new[] { "prepare", "apply", "resolution", "close" })
            { Control control = Field<Control>(form, name); Check(control.Visible && control.Parent.ClientRectangle.Contains(control.Bounds), "Decision control fits: " + name); }
            using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path); }
        }
        internal static void Run(string artifacts)
        {
            assertions = 0;
            string root = Path.Combine(Path.GetFullPath(artifacts), "dirty-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            Directory.CreateDirectory(Path.Combine(root, "selected"));
            File.WriteAllText(Path.Combine(root, ".plastic/plastic.workspace"), "dirty-ui\nguid\nPartial\n");
            File.WriteAllText(Path.Combine(root, ".plastic/plastic.selector"), "repository \"ui@local\"\n path \"/\"\n branch \"/main\"\n");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = "must-not-run.exe", SettingsPath = Path.Combine(root, "settings.xml") });
            var workspace = client.DiscoverWorkspace(root); workspace.IsPartial = true;
            var selected = new List<string> { Path.Combine(root, "selected") };
            var conflict = new PlasticPartialConflict { RepositoryPath = "/selected/nested/image.bin", BaseChangeset = 1, IncomingChangeset = 2, CanResolve = true, IsBinary = true };
            var outside = new PlasticPartialConflict { RepositoryPath = "/selected-other/file.txt", CanResolve = true };
            IList<PlasticPartialConflict> incoming = new List<PlasticPartialConflict> { conflict, outside };
            int writes = 0, opened = 0;
            using (var update = new UpdateForm(client, selected))
            {
                Set(update, "getWorkspace", new Func<string, CancellationToken, Task<PlasticWorkspace>>((path, token) => Task.FromResult(workspace)));
                Set(update, "previewConflicts", new Func<string, CancellationToken, Task<IList<PlasticPartialConflict>>>((path, token) => Task.FromResult(incoming)));
                Set(update, "run", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>((request, token) => { writes++; Check(request.Paths.SequenceEqual(selected), "Update retains selected directory"); return Task.FromResult(new PlasticCommandResult()); }));
                Set(update, "showConflicts", new Action(() => opened++)); Set(update, "showPending", new Action(() => opened++));
                update.Show(); Pump(() => Field<Button>(update, "update").Enabled);
                Check(writes == 0 && opened == 0, "Opening update neither writes nor opens decisions");
                Field<Button>(update, "update").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                string output = Field<TextBox>(update, "output").Text;
                Check(writes == 0 && output.Contains(conflict.RepositoryPath) && !output.Contains(outside.RepositoryPath), "Scope preflight blocks before native update and excludes similar-prefix sibling");
                Check(output.Contains("二进制") && !Field<Button>(update, "update").Enabled, "Binary guidance and explicit refresh required");
                Capture(update, Path.Combine(root, "update-normal.png")); update.Size = update.MinimumSize; Application.DoEvents(); Capture(update, Path.Combine(root, "update-minimum.png"));
                Field<Button>(update, "conflicts").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                Field<Button>(update, "pending").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                Check(opened == 2 && writes == 0 && Field<Button>(update, "update").Enabled, "Returning/canceling either decision only refreshes, never updates automatically");
                incoming = new List<PlasticPartialConflict> { outside };
                Field<Button>(update, "update").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                Check(writes == 1, "Outside-only conflict does not block selected scope");
                Field<Button>(update, "refresh").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                Set(update, "previewConflicts", new Func<string, CancellationToken, Task<IList<PlasticPartialConflict>>>((path, token) => { throw new IOException("preview failed"); }));
                Field<Button>(update, "update").PerformClick(); Pump(() => !Field<bool>(update, "busy"));
                Check(writes == 1 && Field<TextBox>(update, "output").Text.Contains("preview failed"), "Failed preview prevents native update");
                update.Close();
            }
            Check(PartialConflictForm.InSelectedScope(root, selected, "/selected"), "Exact scope included");
            Check(PartialConflictForm.InSelectedScope(root, selected, "/"), "Ancestor structural conflict included");
            Check(!PartialConflictForm.InSelectedScope(root, selected, "/selected-other"), "Sibling prefix excluded");
            using (var form = new PartialConflictForm(client, root, selected))
            {
                Set(form, "busy", true); form.Show(); Application.DoEvents(); Set(form, "busy", false);
                Set(form, "conflicts", new List<PlasticPartialConflict> { conflict, outside });
                typeof(PartialConflictForm).GetMethod("RenderConflicts", Flags).Invoke(form, null);
                var list = Field<ListView>(form, "items"); Check(list.Items.Count == 1, "Conflict dialog respects directory scope"); list.Items[0].Selected = true; Application.DoEvents();
                int prepared = 0, launched = 0;
                Set(form, "prepareConflict", new Func<string, string, CancellationToken, Task<PlasticMergeConflictFiles>>((a, b, c) => { prepared++; throw new Exception("Unexpected preparation"); }));
                Set(form, "mergeTool", new Func<string, string, string, string, CancellationToken, Task<PlasticCommandResult>>((a, b, c, d, e) => { launched++; throw new Exception("Unexpected text tool"); }));
                Field<Button>(form, "prepare").PerformClick(); Application.DoEvents();
                Check(prepared == 0 && launched == 0 && Field<Label>(form, "status").Text.Contains("二进制"), "Binary default choice cannot prepare or launch text merger");
                Capture(form, Path.Combine(root, "conflicts-normal.png")); form.Size = form.MinimumSize; Application.DoEvents(); Capture(form, Path.Combine(root, "conflicts-minimum.png")); form.Close();
            }
            byte[] local = { 0, 255, 1, 2 }, remote = { 0, 254, 3, 4 };
            var files = new PlasticMergeConflictFiles { LocalPath = Path.Combine(root, "local.bin"), RemotePath = Path.Combine(root, "remote.bin"), ResultPath = Path.Combine(root, "result.bin") };
            File.WriteAllBytes(files.LocalPath, local); File.WriteAllBytes(files.RemotePath, remote);
            File.SetAttributes(files.LocalPath, FileAttributes.ReadOnly); File.SetAttributes(files.RemotePath, FileAttributes.ReadOnly);
            PartialConflictForm.PrepareChosenResult(files, 1); Check(File.ReadAllBytes(files.ResultPath).SequenceEqual(local), "Keep-local result is byte exact");
            PartialConflictForm.PrepareChosenResult(files, 2); Check(File.ReadAllBytes(files.ResultPath).SequenceEqual(remote), "Take-incoming result is byte exact and replaces only independent result");
            Check(File.ReadAllBytes(files.LocalPath).SequenceEqual(local) && File.ReadAllBytes(files.RemotePath).SequenceEqual(remote), "Choices preserve both immutable backups");
            Check((File.GetAttributes(files.ResultPath) & FileAttributes.ReadOnly) == 0, "Independent result remains editable");
            Console.WriteLine("PASS: " + assertions + " Partial dirty update UI assertions; " + root);
        }
    }
}
