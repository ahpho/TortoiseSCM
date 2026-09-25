// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        public async Task<PlasticCommandResult> RemoveAsync(string path, CancellationToken token)
        {
            var command = await PrepareFileMutationAsync(path, token).ConfigureAwait(false);
            var workspace = await GetWorkspaceAsync(path, token).ConfigureAwait(false);
            command.Arguments = workspace.IsPartial ? new[] { "partial", "remove", Path.GetFullPath(path) } : new[] { "remove", Path.GetFullPath(path) };
            return await ExecuteAsync(command, token).ConfigureAwait(false);
        }

        public async Task<PlasticCommandResult> MoveAsync(string path, string destination, CancellationToken token)
        {
            var command = await PrepareFileMutationAsync(path, token).ConfigureAwait(false);
            if (String.IsNullOrWhiteSpace(destination) || !Path.IsPathRooted(destination)) throw new ArgumentException("目标必须是完整绝对路径。");
            string source = Path.GetFullPath(path), target = Path.GetFullPath(destination);
            // Build validates the destination against the source workspace, including metadata and nested roots.
            Build(new PlasticCommandRequest { Command = PlasticCommand.History, WorkingDirectory = command.WorkingDirectory, Paths = new[] { target } });
            if (IsWithinScope(target, source) || SamePath(target, command.WorkingDirectory)) throw new ArgumentException("不能将项移动到自身、子目录或工作区根目录。");
            if (File.Exists(target) || Directory.Exists(target)) throw new ArgumentException("目标路径已存在；请选择新的完整名称，不会覆盖现有项。");
            if (!Directory.Exists(Path.GetDirectoryName(target))) throw new ArgumentException("目标的父目录必须已存在。");
            var workspace = await GetWorkspaceAsync(source, token).ConfigureAwait(false);
            command.Arguments = workspace.IsPartial ? new[] { "partial", "move", source, target } : new[] { "move", source, target };
            return await ExecuteAsync(command, token).ConfigureAwait(false);
        }

        private async Task<PlasticProcessCommand> PrepareFileMutationAsync(string path, CancellationToken token)
        {
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            string absolute = command.Arguments[1], root = command.WorkingDirectory;
            ThrowIfPartialStructureActive(root);
            if (SamePath(absolute, root)) throw new ArgumentException("不能删除或重命名工作区根目录。");
            if (!File.Exists(absolute) && !Directory.Exists(absolute)) throw new ArgumentException("所选文件或目录不存在。");
            if (Directory.Exists(absolute)) await Task.Run(() => RejectUnsafeDescendants(absolute, root, token), token).ConfigureAwait(false);
            var pending = await GetStatusAsync(root, token).ConfigureAwait(false);
            if (pending.Any(item => IsWithinScope(item.Path, absolute) || IsWithinScope(item.OldPath, absolute)))
                throw new ArgumentException("所选范围存在待提交更改或私有文件；请先处理，再删除或重命名。");
            if (Directory.Exists(absolute))
            {
                // Normal status deliberately omits ignored files. Native recursive removal
                // must never erase those private descendants just because they were hidden.
                var ignored = await ExecuteAsync(RevisionCommand(root, new [] { "status", absolute, "--ignored", "--cutignored", "--xml", "--encoding=utf-8", "--fullpaths" }), token).ConfigureAwait(false);
                RequireSuccess(ignored);
                if (ParseStatus(ignored.Output, root).Any(item => IsWithinScope(item.Path, absolute)))
                    throw new ArgumentException("所选目录包含已忽略的私有文件或目录；请先移出这些项，再删除或重命名。未执行任何修改。");
            }
            command.Arguments = new[] { "fileinfo", absolute, "--xml", "--encoding=utf-8" };
            var info = await ExecuteAsync(command, token).ConfigureAwait(false);
            RequireSuccess(info);
            var file = SafeXml.Load(info.Output).Descendants("FileInfo").FirstOrDefault();
            long revision;
            if (file == null || !Int64.TryParse((string)file.Element("RevisionChangeset"), out revision) || revision < 0)
                throw new ArgumentException("此操作只适用于已提交的受控文件或目录。");
            if (String.Equals((string)file.Element("IsXlink"), "true", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("跨仓库链接不支持此删除或重命名操作，请在官方客户端处理。");
            return command;
        }

        public async Task<PlasticCommandResult> IgnoreAsync(string path, CancellationToken token)
        {
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            string absolute = command.Arguments[1], root = command.WorkingDirectory;
            ThrowIfPartialStructureActive(root);
            if (SamePath(absolute, root)) throw new ArgumentException("不能忽略整个工作区。");
            var pending = await GetStatusAsync(root, token).ConfigureAwait(false);
            var selected = pending.FirstOrDefault(item => SamePath(item.Path, absolute));
            if (selected == null || !(selected.StatusCode == "PR" || selected.StatusCode == "IG"))
                throw new ArgumentException("只能将未版本控制的文件或目录加入忽略列表；已有版本不会因此取消跟踪。");
            if (Directory.Exists(absolute)) await Task.Run(() => RejectUnsafeDescendants(absolute, root, token), token).ConfigureAwait(false);
            string rule = "/" + absolute.Substring(root.TrimEnd('\\', '/').Length).TrimStart('\\', '/').Replace('\\', '/');
            string configuration = Path.Combine(root, "ignore.conf");
            RejectReparsePath(configuration);
            if (Directory.Exists(configuration)) throw new ArgumentException("ignore.conf 是目录，不能写入规则。");
            return await Task.Run(() => AppendIgnoreRule(configuration, rule, token), token).ConfigureAwait(false);
        }

        internal static PlasticCommandResult AppendIgnoreRule(string configuration, string rule, CancellationToken token)
        {
            // Exclusive file access prevents losing other editors' changes. Decode strictly,
            // preserve the original byte encoding and append only our exact workspace path.
            token.ThrowIfCancellationRequested();
            using (var stream = new FileStream(configuration, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                ToolFileInformation information;
                if (!GetFileInformationByHandle(stream.SafeFileHandle, out information))
                    throw new IOException("无法验证 ignore.conf 的文件链接信息，未写入规则。");
                if (information.Links != 1) throw new ArgumentException("ignore.conf 存在多个硬链接；为避免修改工作区外文件，未写入规则。");
                if (stream.Length > 1024 * 1024) throw new ArgumentException("ignore.conf 超过 1 MiB，请手动编辑。");
                byte[] original = new byte[(int)stream.Length];
                int read = 0;
                while (read < original.Length) { int count = stream.Read(original, read, original.Length - read); if (count == 0) throw new EndOfStreamException(); read += count; }
                Encoding encoding = new UTF8Encoding(false, true); int skip = 0;
                if (original.Length >= 3 && original[0] == 0xef && original[1] == 0xbb && original[2] == 0xbf) skip = 3;
                else if (original.Length >= 2 && original[0] == 0xff && original[1] == 0xfe) { encoding = new UnicodeEncoding(false, true, true); skip = 2; }
                else if (original.Length >= 2 && original[0] == 0xfe && original[1] == 0xff) { encoding = new UnicodeEncoding(true, true, true); skip = 2; }
                string text = encoding.GetString(original, skip, original.Length - skip);
                if (text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Contains(rule, StringComparer.OrdinalIgnoreCase))
                    return new PlasticCommandResult { Output = "忽略规则已存在：" + rule };
                string newline = text.Contains("\r\n") || text.Length == 0 ? "\r\n" : "\n";
                byte[] appended = encoding.GetBytes((text.Length != 0 && !text.EndsWith("\n", StringComparison.Ordinal) ? newline : "") + rule + newline);
                token.ThrowIfCancellationRequested();
                stream.Seek(0, SeekOrigin.End); stream.Write(appended, 0, appended.Length); stream.Flush();
            }
            return new PlasticCommandResult { Output = "已添加忽略规则：" + rule + Environment.NewLine + "配置文件：" + configuration };
        }
    }
}
