// TortoiseSCM - Windows Explorer interface to Plastic SCM / Unity Version Control.
// Licensed under GPL-2.0-or-later; see the repository LICENSE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 1 && args[0] == "--cache-worker") return OverlayCacheHost.Run();
            if (args.Length == 1 && args[0] == "--cache-stop") return OverlayCacheHost.Stop();
            // Dispatch before any WinForms setup: automation must never show a dialog,
            // including when parsing or loading settings fails.
            if (args.Any(argument => argument.Equals("--cli", StringComparison.OrdinalIgnoreCase)))
                return CliRunner.Run(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                var request = LaunchRequest.Parse(args);
                if (request.Command == "settings")
                {
                    Application.Run(new SettingsForm());
                    return 0;
                }
                if (request.Paths.Count == 0)
                {
                    using (var picker = new FolderBrowserDialog { Description = "选择 Plastic SCM 工作区", ShowNewFolderButton = false })
                    {
                        if (picker.ShowDialog() != DialogResult.OK) return 0;
                        request.Paths.Add(picker.SelectedPath);
                    }
                }
                Application.Run(new MainForm(request));
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "TortoiseSCM", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal sealed class LaunchRequest
    {
        internal string Command = "status";
        internal readonly List<string> Paths = new List<string>();

        internal static LaunchRequest Parse(string[] args)
        {
            var result = new LaunchRequest();
            for (int i = 0; i < args.Length; ++i)
            {
                if (i + 1 >= args.Length) throw new ArgumentException("参数缺少值：" + args[i]);
                string option = args[i++];
                string value = args[i];
                switch (option)
                {
                    case "--command": result.Command = value.ToLowerInvariant(); break;
                    case "--path": result.Paths.Add(Path.GetFullPath(value)); break;
                    case "--pathfile":
                        // Only consume the temporary files owned by our Explorer extension.
                        string path = Path.GetFullPath(value);
                        string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                        if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                            !Path.GetFileName(path).StartsWith("tscm", StringComparison.OrdinalIgnoreCase))
                            throw new ArgumentException("无效的 Explorer 路径列表。");
                        if (new FileInfo(path).Length > 4 * 1024 * 1024)
                            throw new ArgumentException("所选路径过多。");
                        var lines = File.ReadAllLines(path, Encoding.UTF8);
                        if (lines.Any(p => !Path.IsPathRooted(p))) throw new ArgumentException("路径列表必须包含绝对路径。");
                        result.Paths.AddRange(lines.Select(Path.GetFullPath));
                        File.Delete(path);
                        break;
                    default: throw new ArgumentException("未知参数：" + option);
                }
            }
            // Keep these names in sync with the verbs exposed by the native
            // Explorer extension.  File operations are deliberately routed
            // through MainForm so they retain the same confirmation and
            // workspace safety checks as the in-app menus.
            string[] commands = { "status", "checkin", "update", "add", "checkout", "undo", "diff", "history", "gluon", "settings",
                "move", "remove", "ignore", "locks", "unlock", "merge", "export", "rollback", "recover" };
            if (!commands.Contains(result.Command)) throw new ArgumentException("未知操作：" + result.Command);
            return result;
        }
    }
}
