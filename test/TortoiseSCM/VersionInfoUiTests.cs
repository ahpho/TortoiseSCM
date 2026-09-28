// GPL-2.0-or-later. Package identity parsing and the standalone version dialog.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class VersionInfoUiTests
    {
        private static int assertions;
        private const string Commit = "0123456789abcdef0123456789abcdef01234567";
        private const string Manifest = "{\"product\":\"TortoiseSCM\",\"version\":\"0.1.0-test-version\",\"sourceCommit\":\"" + Commit + "\",\"files\":[]}";
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Application.EnableVisualStyles(); Run(args.Length == 0 ? "bin/TortoiseSCM/qa/version-ui" : args[0]); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        internal static void Run(string artifacts)
        {
            assertions = 0; Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-version-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string manifest = Path.Combine(root, "package-manifest.json");
            string executable = Path.Combine(root, "中文目录", "TortoiseSCM.exe");
            try {
                var request = LaunchRequest.Parse(new[] { "--command", "version" });
                Require(request.Command == "version" && request.Paths.Count == 0, "Version command accepts no workspace");
                request = LaunchRequest.Parse(new[] { "--command", "version", "--path", root });
                Require(request.Paths.Count == 1 && request.Command == "version", "Explorer version command accepts selected path");
                var info = PackageVersionInfo.Read(root, executable);
                Require(info.Status.Contains("开发构建 / 无安装包信息") && info.Version == "未知" && info.SourceCommit == "未知", "Missing package metadata is explicit and never invents version or commit");
                Require(!File.Exists(manifest), "Reading version information does not create a manifest");
                Write(manifest, Manifest); info = PackageVersionInfo.Read(root, executable);
                Require(info.Version == "0.1.0-test-version" && info.SourceCommit == Commit, "Package version and full source commit are read exactly");
                Require(info.ExecutablePath == executable && info.DirectoryPath == root, "Actual executable and package location are retained");
                Require(info.ToDisplayText().Contains(Environment.Is64BitProcess ? "x64" : "x86"), "Version display includes current process architecture");
                Require(info.ToDisplayText().Contains("未查询远程最新版本") && info.ToDisplayText().Contains("不能确认 Explorer"), "Version dialog states what local information can establish");
                string[] invalid = {
                    "{broken", "null", "[]", "{}", Manifest.Replace("TortoiseSCM", "OtherProduct"),
                    Manifest.Replace("\"0.1.0-test-version\"", "123"), Manifest.Replace("0.1.0-test-version", "bad\\nversion"),
                    Manifest.Replace("0.1.0-test-version", new string('a', 81)), Manifest.Replace(Commit, "abc"),
                    Manifest.Replace("\"" + Commit + "\"", "{}"), new string(' ', PackageVersionInfo.MaximumManifestBytes + 1)
                };
                foreach (string text in invalid) {
                    Write(manifest, text); var invalidInfo = PackageVersionInfo.Read(root, executable);
                    Require(invalidInfo.Version == "未知" && invalidInfo.SourceCommit == "未知" && invalidInfo.Status.Contains("损坏或无法读取"), "Malformed, oversized or wrong-type package data is not reported as a version");
                    Require(File.ReadAllText(manifest, Encoding.UTF8) == text, "Invalid package data is preserved");
                }
                File.WriteAllBytes(manifest, new byte[] { 0xff, 0xff });
                Require(PackageVersionInfo.Read(root, executable).Status.Contains("损坏或无法读取"), "Invalid text encoding is reported safely");
                Write(manifest, Manifest.Replace(Commit, "unknown"));
                Require(PackageVersionInfo.Read(root, executable).SourceCommit.Contains("未记录"), "Unknown source commit remains explicitly unknown");
                Write(manifest, Manifest);
                using (var locked = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.None))
                    Require(PackageVersionInfo.Read(root, executable).Status.Contains("损坏或无法读取"), "Inaccessible manifest is not mistaken for a development build");
                TestDialog(info, artifacts);
                using (var startup = new StartupForm()) {
                    startup.Show(); Application.DoEvents(); startup.Size = startup.MinimumSize; Application.DoEvents();
                    var button = Field<Button>(startup, "version");
                    Require(button.Visible && button.Parent.ClientRectangle.Contains(button.Bounds), "Startup version button remains visible at minimum size");
                    Save(startup, Path.Combine(artifacts, "version-startup-minimum.png")); startup.Close();
                }
                Console.WriteLine("PASS: version information UI (" + assertions + " assertions)");
            }
            finally { Directory.Delete(root, true); }
        }

        private static void TestDialog(PackageVersionInfo info, string artifacts)
        {
            using (var form = new VersionInfoForm(info)) {
                form.Show(); Application.DoEvents();
                var details = Field<TextBox>(form, "details");
                Require(details.ReadOnly && details.Multiline && details.Text.Contains(Commit), "Full version information is selectable and read-only");
                Require(form.AcceptButton == Field<Button>(form, "close") && form.CancelButton == Field<Button>(form, "close"), "Enter and Escape safely close the version dialog");
                string copied = null;
                typeof(VersionInfoForm).GetField("copyInformation", Flags).SetValue(form, new Action<string>(text => copied = text));
                Field<Button>(form, "copy").PerformClick();
                Require(copied == details.Text && Field<Label>(form, "status").Text.Contains("已复制"), "Copy action includes exact displayed package identity and paths");
                typeof(VersionInfoForm).GetField("copyInformation", Flags).SetValue(form, new Action<string>(text => { throw new ExternalException("busy"); }));
                Field<Button>(form, "copy").PerformClick();
                Require(Field<Label>(form, "status").Text.Contains("剪贴板暂时不可用"), "Busy clipboard provides an actionable nonfatal message");
                Field<Label>(form, "status").Text = "";
                Bounds(form); Save(form, Path.Combine(artifacts, "version-information.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); Bounds(form);
                Save(form, Path.Combine(artifacts, "version-information-minimum.png"));
                Field<Button>(form, "close").PerformClick(); Require(!form.Visible, "Close button closes the dialog");
            }
        }

        private static void Bounds(Control parent)
        {
            foreach (Control child in parent.Controls) {
                if (child.Visible) Require(parent.ClientRectangle.Contains(child.Bounds), "Visible version control stays inside parent: " + child.GetType().Name);
                Bounds(child);
            }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Flags).GetValue(owner); }
        private static void Write(string path, string value) { File.WriteAllText(path, value, new UTF8Encoding(false)); }
        private static void Save(Form form, string path) { using (var bitmap = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size)); bitmap.Save(path, ImageFormat.Png); } }
        private static void Require(bool condition, string message) { assertions++; if (!condition) throw new Exception("Version information: " + message); }
    }
}
