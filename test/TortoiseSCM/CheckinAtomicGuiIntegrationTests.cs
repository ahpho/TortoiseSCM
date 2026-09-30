// GPL-2.0-or-later. Real MainForm handlers and cm on isolated autotest branches.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class CheckinAtomicGuiIntegrationTests
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        private static readonly List<object> Results = new List<object>();
        private static PlasticClient client;
        private static string artifacts;
        private static int assertions;

        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length < 2 || args.Length > 3) { Console.Error.WriteLine("Usage: CheckinAtomicGuiIntegrationTests <manifest.json> <artifacts> [read-only source directory]"); return 2; }
            artifacts = Path.GetFullPath(args[1]); Directory.CreateDirectory(artifacts);
            try
            {
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                WindowsFormsSynchronizationContext.AutoInstall = false;
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Control.CheckForIllegalCrossThreadCalls = true;
                var manifest = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(args[0], Utf8));
                string branch = (string)manifest["branch"], run = Path.GetFullPath((string)manifest["runDirectory"]);
                Require((bool)manifest["complete"] && branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Dedicated completed fixture");
                client = new PlasticClient(PlasticClientConfig.Load());
                foreach (string role in new[] { "producer", "partial" })
                {
                    string root = Path.GetFullPath((string)manifest[role]);
                    var workspace = Wait(client.GetWorkspaceAsync(root, CancellationToken.None));
                    Require(root.StartsWith(run + "\\", StringComparison.OrdinalIgnoreCase) && workspace.Selector.Contains(branch)
                        && workspace.Repository == (string)manifest["repository"], "Isolated write target " + role);
                    foreach (bool stdin in new[] { false, true })
                    {
                        Run(root, (string)manifest["consumer"], role, stdin, 1);
                        Run(root, (string)manifest["consumer"], role, stdin, 194);
                        if (args.Length == 3) Run(root, (string)manifest["consumer"], role, stdin, 194, Path.GetFullPath(args[2]));
                        if (args.Length == 3 && stdin) Run(root, (string)manifest["consumer"], role, true, 175, Path.GetFullPath(args[2]), true);
                    }
                }
                SaveResults(true, null);
                Console.WriteLine("PASS: " + assertions + " atomic GUI assertions; real WinForms handlers and cm, not desktop mouse automation");
                return 0;
            }
            catch (Exception ex) { SaveResults(false, ex.ToString()); Console.Error.WriteLine(ex); return 1; }
        }

        private static void Run(string root, string consumer, string role, bool stdin, int count, string source = null, bool filesOnly = false)
        {
            string name = role + "-" + (stdin ? "stdin" : "paths") + "-" + (source == null ? count.ToString() : filesOnly ? "source175-files" : "source194") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            string folder = Path.Combine(root, name + " 中文"); Directory.CreateDirectory(folder);
            string tree = Path.Combine(folder, "Source - 副本");
            string[] paths, selected;
            if (source == null)
            {
                paths = Enumerable.Range(0, count).Select(i => Path.Combine(folder, "f" + i.ToString("D3") + " 中文.txt")).ToArray();
                foreach (string path in paths) File.WriteAllText(path, name + "\r\n" + Path.GetFileName(path), Utf8);
                selected = paths;
            }
            else
            {
                Require(Directory.Exists(source), "Source fixture exists (read only)");
                Directory.CreateDirectory(tree);
                foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
                    Directory.CreateDirectory(Path.Combine(tree, directory.Substring(source.TrimEnd('\\').Length).TrimStart('\\')));
                foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                    File.Copy(path, Path.Combine(tree, path.Substring(source.TrimEnd('\\').Length).TrimStart('\\')));
                paths = Directory.GetFiles(tree, "*", SearchOption.AllDirectories);
                selected = new[] { tree }.Concat(Directory.GetDirectories(tree, "*", SearchOption.AllDirectories)).Concat(paths).ToArray();
                Require(paths.Length == 175 && selected.Length == 194, "Actual source fixture contains 175 files and 19 directories");
                if (filesOnly) selected = paths;
            }
            string sibling = Path.Combine(folder, "excluded.txt"); File.WriteAllText(sibling, "Unselected added sibling", Utf8);
            string privateFile = Path.Combine(folder, "private.txt"); File.WriteAllText(privateFile, "Unselected private sibling", Utf8);
            // Add is only staging. No checkin is split into batches.
            var add = Wait(client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Add, WorkingDirectory = root,
                Paths = (source == null ? paths : new[] { tree }).Concat(new[] { sibling }).ToList(), Recursive = source != null }, CancellationToken.None));
            Require(add.Succeeded, "Stage " + name + ": " + add.Error);
            string comment = "QA atomic GUI " + name + "\r\n中文说明与空格 \"quotes\"\r\nCommandResult 0";
            string error = "", log = ""; long changeset = -1;
            var clock = Stopwatch.StartNew();
            var launch = new LaunchRequest { Command = "checkin" }; launch.Paths.Add(folder);
            using (var form = new MainForm(launch))
            {
                Set(form, "reportError", new Action<string>(text => error = text));
                Set(form, "messageStore", new CommitMessageStore(Path.Combine(artifacts, "messages.json")));
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(60, 60); form.Show();
                Pump(() => Field<bool>(form, "loaded") && !Field<bool>(form, "busy") || error.Length > 0, "Load " + name);
                Require(error.Length == 0, "GUI initializes " + name + ": " + error);
                var rows = Field<ListView>(form, "files");
                foreach (ListViewItem row in rows.Items) row.Checked = false;
                foreach (ListViewItem row in rows.Items)
                    if (selected.Contains(((PlasticStatusItem)row.Tag).Path, StringComparer.OrdinalIgnoreCase)) row.Checked = true;
                Require(rows.CheckedItems.Count == count && (source != null && !filesOnly || !rows.CheckedItems.Cast<ListViewItem>().Any(row => ((PlasticStatusItem)row.Tag).IsDirectory)),
                    "Select exactly " + count + " intended items without an excluded sibling");
                Field<CheckBox>(form, "useCheckinStdin").Checked = stdin;
                Field<TextBox>(form, "comment").Text = comment;
                Save(form, name + "-before.png");
                Field<Button>(form, "checkin").PerformClick();
                Require(Field<bool>(form, "busy"), "GUI submission starts " + name);
                Pump(() => !Field<bool>(form, "busy"), "GUI checkin completes " + name);
                log = Field<TextBox>(form, "output").Text;
                File.WriteAllText(Path.Combine(artifacts, name + ".log"), log + "\r\nDISPLAYED ERROR:\r\n" + error, Utf8);
                Save(form, name + "-after.png");
                clock.Stop();
                MatchCollection created = Regex.Matches(log, @"Created changeset cs:(\d+)@");
                if (created.Count == 1) changeset = Int64.Parse(created[0].Groups[1].Value);
                Results.Add(new { name, role, stdin, selectedItemCount = selected.Length, actualFileCount = paths.Length, elapsedMs = clock.ElapsedMilliseconds,
                    error, changeset, createdChangesetCount = created.Count, output = log });
                SaveResults(false, "Running " + name);
                Require(error.Length == 0 && !Field<bool>(form, "submissionNeedsRefresh"), "GUI reports confirmed success " + name + ": " + error);
                Require(created.Count == 1, "One checkin produces exactly one changeset " + name);
                Require(Field<TextBox>(form, "comment").Text.Length == 0, "Success clears draft " + name);
            }
            var pending = Wait(client.GetStatusAsync(root, CancellationToken.None));
            Require(!pending.Any(item => paths.Contains(item.Path, StringComparer.OrdinalIgnoreCase)), "All selected files leave pending state " + name);
            Require(pending.Any(item => Same(item.Path, sibling) && item.StatusCode == "AD"), "Unselected AD sibling stays pending " + name);
            Require(pending.Any(item => Same(item.Path, privateFile) && item.StatusCode == "PR"), "Unselected private sibling stays private " + name);
            var details = Wait(client.GetChangesetAsync(root, changeset, CancellationToken.None));
            var expected = paths.Select(path => "/" + path.Substring(root.TrimEnd('\\').Length).TrimStart('\\').Replace('\\', '/')).ToArray();
            File.WriteAllText(Path.Combine(artifacts, name + "-server.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new {
                changeset, submittedComment = comment, returnedComment = details.Changeset.Comment, selectedPaths = expected,
                serverPaths = details.Files.Select(file => file.Path).ToArray() }), Utf8);
            // XML 1.0 readers normalize native cm log's literal CRLF to LF.
            Require(Lines(details.Changeset.Comment) == Lines(comment) && details.Files.Count(file => expected.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) == paths.Length,
                "Server changeset contains every selected path and exact multiline content " + name);
            Require(!details.Files.Any(file => file.Path.EndsWith("/excluded.txt", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith("/private.txt", StringComparison.OrdinalIgnoreCase)),
                "Server changeset excludes unselected files " + name);
            var update = Wait(client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Update, WorkingDirectory = consumer,
                Paths = new[] { consumer } }, CancellationToken.None));
            Require(update.Succeeded, "Consumer update " + name + ": " + update.Error);
            foreach (string path in paths)
            {
                string copy = Path.Combine(consumer, path.Substring(root.TrimEnd('\\').Length).TrimStart('\\'));
                Require(File.Exists(copy) && File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(path)), "Consumer receives exact bytes " + Path.GetFileName(path));
            }
            Require(!File.Exists(Path.Combine(consumer, sibling.Substring(root.TrimEnd('\\').Length).TrimStart('\\'))), "Consumer excludes pending sibling " + name);
            Console.WriteLine("RESULT " + name + " cs:" + changeset + " " + clock.ElapsedMilliseconds + "ms");
        }

        private static void SaveResults(bool success, string error)
        { File.WriteAllText(Path.Combine(artifacts, "results.json"), new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }.Serialize(new { success, assertions, error, scenarios = Results }), Utf8); }
        private static void Save(Form form, string name)
        { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(Path.Combine(artifacts, name), ImageFormat.Png); } }
        private static T Field<T>(object obj, string name)
        {
            FieldInfo field = obj.GetType().GetField(name, Flags);
            if (field == null) throw new InvalidOperationException("Missing GUI field: " + name);
            return (T)field.GetValue(obj);
        }
        private static void Set(object obj, string name, object value) { obj.GetType().GetField(name, Flags).SetValue(obj, value); }
        private static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static string Lines(string text) { return text.Replace("\r\n", "\n").Replace("\r", "\n"); }
        private static T Wait<T>(Task<T> task) { Pump(() => task.IsCompleted, "Native operation completes"); return task.GetAwaiter().GetResult(); }
        private static void Pump(Func<bool> condition, string description)
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(4); Application.DoEvents();
            while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Require(condition(), description);
        }
        private static void Require(bool value, string text)
        { assertions++; if (!value) throw new InvalidOperationException(text); }
    }
}
