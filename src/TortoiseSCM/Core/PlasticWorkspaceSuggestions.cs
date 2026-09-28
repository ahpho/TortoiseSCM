// GPL-2.0-or-later. Read-only checkout destination suggestions.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        public async Task<IList<PlasticWorkspace>> GetRegisteredWorkspacesAsync(CancellationToken token)
        {
            var response = await ExecuteAsync(CreationCommand(Path.GetTempPath(), new[] { "workspace", "list", "--format={wkname}{tab}{path}{tab}{wkid}" }), token).ConfigureAwait(false);
            RequireSuccess(response);
            var result = new List<PlasticWorkspace>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<Guid>();
            foreach (string line in CreationLines(response.Output))
            {
                string[] fields = line.Split('\t'); Guid id;
                if (fields.Length != 3 || String.IsNullOrWhiteSpace(fields[0]) || !Path.IsPathRooted(fields[1]) ||
                    !Guid.TryParse(fields[2], out id) || id == Guid.Empty || !names.Add(fields[0]) || !ids.Add(id))
                    throw new InvalidDataException("无法可靠读取工作区列表；请重试查询。");
                result.Add(new PlasticWorkspace { Name = fields[0], RootPath = Path.GetFullPath(fields[1]) });
            }
            return result;
        }
    }

    internal sealed class WorkspaceCreationSuggestion
    {
        internal string Name;
        internal string Path;

        internal static WorkspaceCreationSuggestion Choose(string parent, IList<PlasticWorkspace> registered)
        {
            parent = System.IO.Path.GetFullPath(parent);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var workspace in registered)
            {
                names.Add(workspace.Name);
                if (Within(parent, workspace.RootPath))
                    throw new InvalidOperationException("当前目录位于已有工作区内，请从工作区以外的目录拉取仓库。");
            }
            for (int suffix = 1; suffix < Int32.MaxValue; suffix++)
            {
                string name = "TestSCM" + (suffix == 1 ? "" : suffix.ToString(CultureInfo.InvariantCulture));
                string path = System.IO.Path.Combine(parent, name);
                bool occupied = names.Contains(name) || Directory.Exists(path) || File.Exists(path);
                foreach (var workspace in registered) occupied |= Within(workspace.RootPath, path);
                if (!occupied) return new WorkspaceCreationSuggestion { Name = name, Path = path };
            }
            throw new InvalidOperationException("没有可用的默认工作区名称，请手动指定名称和目录。");
        }

        internal static string ChooseName(IList<PlasticWorkspace> registered)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var workspace in registered) names.Add(workspace.Name);
            for (int suffix = 1; suffix < Int32.MaxValue; suffix++)
            {
                string name = "TestSCM" + (suffix == 1 ? "" : suffix.ToString(CultureInfo.InvariantCulture));
                if (!names.Contains(name)) return name;
            }
            throw new InvalidOperationException("没有可用的默认工作区名称。");
        }

        private static bool Within(string path, string root)
        {
            path = System.IO.Path.GetFullPath(path).TrimEnd('\\', '/');
            root = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/');
            return String.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
        }
    }
}
