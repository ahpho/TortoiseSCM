// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed class PlasticRepositoryInfo
    {
        public string Name { get; set; }
        public string Server { get; set; }
        public string Specification { get; set; }
        public long Id { get; set; }
        public string Guid { get; set; }
        public override string ToString() { return Name; }
    }

    public sealed class PlasticWorkspaceCreationResult
    {
        public string WorkspacePath { get; set; }
        public bool WorkspaceCreated { get; set; }
        public bool UpdateCompleted { get; set; }
        public bool Succeeded { get { return WorkspaceCreated && UpdateCompleted && String.IsNullOrEmpty(Error); } }
        public string Stage { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
        public string RecoveryInstructions { get; set; }
        public bool OutcomeUncertain { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public async Task<IList<PlasticRepositoryInfo>> GetRepositoriesAsync(string server, CancellationToken token)
        {
            ValidateCreationServer(server);
            var response = await ExecuteAsync(CreationCommand(Path.GetTempPath(), new[] { "repository", "list", server,
                "--format={repid}{tab}{repname}{tab}{repserver}{tab}{repguid}" }), token).ConfigureAwait(false);
            RequireSuccess(response);
            var result = new List<PlasticRepositoryInfo>();
            var ids = new HashSet<long>(); var guids = new HashSet<Guid>(); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (string line in CreationLines(response.Output))
            {
                string[] fields = line.Split('\t'); long id; Guid guid;
                if (fields.Length != 4 || !Int64.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out id) || id <= 0 ||
                    !System.Guid.TryParse(fields[3], out guid) || guid == System.Guid.Empty || !ids.Add(id) || !guids.Add(guid) || !names.Add(fields[1]))
                    throw new InvalidDataException("仓库列表缺少有效且唯一的 ID/GUID；未执行创建。");
                ValidateCreationComponent(fields[1], "仓库名称", true); ValidateCreationServer(fields[2]);
                // Native output can canonicalize a hostname; retain the authoritative returned server.
                result.Add(new PlasticRepositoryInfo { Name = fields[1], Server = fields[2], Specification = fields[1] + "@" + fields[2], Id = id, Guid = guid.ToString() });
            }
            return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public Task<PlasticWorkspaceCreationResult> CreateWorkspaceAsync(PlasticRepositoryInfo repository, string name,
            string path, string branch, IProgress<string> progress, CancellationToken token)
        { return CreateWorkspaceAsync(repository, name, path, branch, false, progress, token); }

        public Task<PlasticWorkspaceCreationResult> CreateWorkspaceAsync(PlasticRepositoryInfo repository, string name,
            string path, string branch, bool partial, IProgress<string> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (repository == null) throw new ArgumentNullException("repository");
            // Snapshot caller-owned selection before the first await.
            var captured = new PlasticRepositoryInfo { Name = repository.Name, Server = repository.Server,
                Specification = repository.Specification, Id = repository.Id, Guid = repository.Guid };
            ValidateCreationComponent(captured.Name, "仓库名称", true); ValidateCreationServer(captured.Server);
            Guid identity;
            if (captured.Specification != captured.Name + "@" + captured.Server || captured.Id <= 0 ||
                !System.Guid.TryParse(captured.Guid, out identity) || identity == System.Guid.Empty)
                throw new ArgumentException("请重新查询并选择含有效 ID/GUID 的仓库。");
            ValidateCreationComponent(name, "工作区名称", true);
            if (name == "." || name == ".." || name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) throw new ArgumentException("工作区名称不能包含路径分隔符或冒号。");
            branch = String.IsNullOrEmpty(branch) ? "/main" : branch; ValidateBranchName(branch);
            path = ValidateCreationPath(path);
            return CreateWorkspaceCoreAsync(captured, name, path, branch, partial, progress, token);
        }

        private async Task<PlasticWorkspaceCreationResult> CreateWorkspaceCoreAsync(PlasticRepositoryInfo repository, string name,
            string path, string branch, bool partial, IProgress<string> progress, CancellationToken token)
        {
            // File locks work across awaited continuations and separate TortoiseSCM processes.
            using (var globalGate = CreationGate("all-workspace-creations"))
            using (var pathGate = CreationGate("path:" + path.ToUpperInvariant()))
            using (var nameGate = CreationGate("name:" + name.ToUpperInvariant()))
            {
                ReportCreation(progress, "正在核对仓库身份、分支与本地工作区…");
                await RequireCreationRepositoryAsync(repository, token).ConfigureAwait(false);
                var selected = await CreationBranchAsync(repository, branch, token).ConfigureAwait(false);
                await ValidateCreationWorkspacesAsync(name, path, token).ConfigureAwait(false);
                ValidateCreationDestination(path);
                // Requery identity after all asynchronous preflight work, immediately before mutation.
                await RequireCreationRepositoryAsync(repository, token).ConfigureAwait(false);
                var refreshed = await CreationBranchAsync(repository, branch, token).ConfigureAwait(false);
                RequireCreationBranchIdentity(selected, refreshed);
                await ValidateCreationWorkspacesAsync(name, path, token).ConfigureAwait(false);
                ValidateCreationDestination(path); token.ThrowIfCancellationRequested();
                var result = new PlasticWorkspaceCreationResult { WorkspacePath = path, Stage = "创建工作区", Output = "", Error = "", RecoveryInstructions = "" };
                bool launched = false;
                try
                {
                    ReportCreation(progress, "正在创建工作区；请等待完成…");
                    Directory.CreateDirectory(path);
                    ValidateCreationDestination(path);
                    // Creating metadata does not download /main. Only the explicit switch below downloads files.
                    launched = true;
                    var create = await ExecuteAsync(CreationCommand(path, new[] { "workspace", "create", name, path, "rep:" + repository.Specification }), CancellationToken.None).ConfigureAwait(false);
                    result.Output += create.Output;
                    if (!create.Succeeded) throw new PlasticCommandException(create);
                    await VerifyCreatedWorkspaceAsync(repository, name, path, false, CancellationToken.None).ConfigureAwait(false);
                    result.WorkspaceCreated = true;
                    result.Stage = "下载分支";
                    ReportCreation(progress, "工作区已创建，正在下载 " + branch + "…");
                    await RequireCreationRepositoryAsync(repository, CancellationToken.None).ConfigureAwait(false);
                    RequireCreationBranchIdentity(selected, await CreationBranchAsync(repository, branch, CancellationToken.None).ConfigureAwait(false));
                    RejectReparsePath(path); RejectUnsafeDescendants(path, path, CancellationToken.None);
                    if (Directory.EnumerateFileSystemEntries(path).Any(entry => !String.Equals(Path.GetFileName(entry), ".plastic", StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("创建后目录出现额外文件，未下载或覆盖这些文件；请保留目录并检查。");
                    var update = await ExecuteAsync(CreationCommand(path, new[] { "switch", "br:" + branch + "@" + repository.Specification,
                        "--workspace=" + path }), CancellationToken.None).ConfigureAwait(false);
                    result.Output += update.Output;
                    if (!update.Succeeded) throw new PlasticCommandException(update);
                    await RequireCreationRepositoryAsync(repository, CancellationToken.None).ConfigureAwait(false);
                    await VerifyCreatedWorkspaceAsync(repository, name, path, false, CancellationToken.None).ConfigureAwait(false);
                    var actualBranch = (await GetBranchesAsync(path, CancellationToken.None).ConfigureAwait(false)).SingleOrDefault(item => item.Name == branch && item.IsCurrent);
                    RequireCreationBranchIdentity(selected, actualBranch);
                    long downloadedChangeset = await ReadHistoryWorkspaceChangesetAsync(DiscoverWorkspace(path), CancellationToken.None).ConfigureAwait(false);
                    if (partial)
                    {
                        result.Stage = "配置 Gluon 工作区";
                        ReportCreation(progress, "分支已下载，正在配置 Gluon 工作区以支持部分更新…");
                        RejectReparsePath(path); RejectUnsafeDescendants(path, path, CancellationToken.None);
                        var configure = await ExecuteAsync(CreationCommand(path, new[] { "partial", "configure", "-/", "+/" }), CancellationToken.None).ConfigureAwait(false);
                        result.Output += configure.Output;
                        if (!configure.Succeeded) throw new PlasticCommandException(configure);
                        // Empty configure can retain the complete tree. A partial update establishes
                        // partial tree semantics even for an empty branch; metadata text is not authoritative.
                        result.Stage = "完成 Gluon 初始化";
                        var partialUpdate = await ExecuteAsync(CreationCommand(path, new[] { "partial", "update", ".", "--report",
                            "--changeset=" + downloadedChangeset.ToString(System.Globalization.CultureInfo.InvariantCulture) }), CancellationToken.None).ConfigureAwait(false);
                        result.Output += partialUpdate.Output;
                        if (!partialUpdate.Succeeded) throw new PlasticCommandException(partialUpdate);
                        await RequireCreationRepositoryAsync(repository, CancellationToken.None).ConfigureAwait(false);
                        await VerifyCreatedWorkspaceAsync(repository, name, path, true, CancellationToken.None).ConfigureAwait(false);
                        actualBranch = (await GetBranchesAsync(path, CancellationToken.None).ConfigureAwait(false)).SingleOrDefault(item => item.Name == branch && item.IsCurrent);
                        RequireCreationBranchIdentity(selected, actualBranch);
                    }
                    RecordHistoryRootLoaded(DiscoverWorkspace(path), downloadedChangeset);
                    result.UpdateCompleted = true; result.Stage = "完成";
                    return result;
                }
                catch (Exception error)
                {
                    result.Error = error.Message; result.OutcomeUncertain = launched;
                    result.RecoveryInstructions = result.WorkspaceCreated
                        ? "工作区创建已确认，但“" + result.Stage + "”阶段未确认完成。保留目录：" + path + "。请在官方 Plastic / Gluon 客户端检查仓库、分支与工作区模式，确认仍为 " + branch + " 后继续处理。不要再次创建或删除此目录。"
                        : "创建未确认完成，目录和可能已生成的元数据均已保留：" + path + "。请在官方 Plastic 客户端检查工作区列表及此目录，确认实际状态后再处理；不要直接重试创建或删除目录。";
                    return result;
                }
            }
        }

        private async Task RequireCreationRepositoryAsync(PlasticRepositoryInfo expected, CancellationToken token)
        {
            var actual = (await GetRepositoriesAsync(expected.Server, token).ConfigureAwait(false)).SingleOrDefault(item => item.Name == expected.Name);
            if (actual == null || actual.Specification != expected.Specification || actual.Id != expected.Id ||
                !String.Equals(actual.Guid, expected.Guid, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("仓库身份已变化或仓库不可用；请重新查询仓库，未继续执行。");
        }

        private async Task<PlasticBranch> CreationBranchAsync(PlasticRepositoryInfo repository, string branch, CancellationToken token)
        {
            var response = await ExecuteAsync(CreationCommand(Path.GetTempPath(), new[] { "find", "branch on repository '" + repository.Specification + "'",
                "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(response);
            var result = ParseBranches(response.Output, repository.Specification).SingleOrDefault(item => item.Name == branch);
            if (result == null || result.BranchId <= 0 || String.IsNullOrEmpty(result.Guid)) throw new InvalidOperationException("目标分支不存在或身份无法确认：" + branch);
            return result;
        }

        private static void RequireCreationBranchIdentity(PlasticBranch expected, PlasticBranch actual)
        {
            if (actual == null || actual.BranchId != expected.BranchId || !String.Equals(actual.Guid, expected.Guid, StringComparison.OrdinalIgnoreCase) ||
                actual.Name != expected.Name || actual.Repository != expected.Repository)
                throw new InvalidOperationException("所选分支身份发生变化或切换结果无法确认；未继续执行。");
        }

        private async Task ValidateCreationWorkspacesAsync(string name, string path, CancellationToken token)
        {
            var response = await ExecuteAsync(CreationCommand(Path.GetTempPath(), new[] { "workspace", "list", "--format={wkname}{tab}{path}{tab}{wkid}" }), token).ConfigureAwait(false);
            RequireSuccess(response);
            foreach (string line in CreationLines(response.Output))
            {
                string[] fields = line.Split('\t'); Guid id;
                if (fields.Length != 3 || String.IsNullOrWhiteSpace(fields[0]) || !Path.IsPathRooted(fields[1]) || !System.Guid.TryParse(fields[2], out id) || id == System.Guid.Empty)
                    throw new InvalidDataException("无法可靠读取工作区列表；未创建工作区。");
                string existing = Path.GetFullPath(fields[1]).TrimEnd('\\', '/');
                if (String.Equals(fields[0], name, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("工作区名称已存在，请指定新名称。");
                if (IsWithin(path, existing) || IsWithin(existing, path)) throw new ArgumentException("目标目录与已有工作区重叠，请选择独立的新目录。");
            }
        }

        private async Task VerifyCreatedWorkspaceAsync(PlasticRepositoryInfo repository, string name, string path, bool partial, CancellationToken token)
        {
            RejectReparsePath(path);
            var workspace = await GetWorkspaceAsync(path, token).ConfigureAwait(false);
            if (!String.Equals(workspace.RootPath, path, StringComparison.OrdinalIgnoreCase) || workspace.Name != name || workspace.IsPartial != partial || workspace.Repository != repository.Specification)
                throw new InvalidOperationException("创建后的工作区身份、路径或模式无法确认。");
            var response = await ExecuteAsync(CreationCommand(path, new[] { "status", path, "--header", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(response); ValidateBranchStatusRepository(SafeXml.Load(response.Output), repository.Specification);
        }

        private void ValidateCreationDestination(string path)
        {
            RejectReparsePath(path);
            if (File.Exists(path)) throw new ArgumentException("目标是文件；请选择空目录或尚不存在的新目录。");
            if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) throw new ArgumentException("目标目录必须为空；不会覆盖或清理已有文件。");
            if (DiscoverWorkspace(path) != null) throw new ArgumentException("不能在已有 Plastic 工作区内创建嵌套工作区。");
            for (string current = path; !String.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
                if (Directory.Exists(Path.Combine(current, ".plastic")) || File.Exists(Path.Combine(current, ".plastic")))
                    throw new ArgumentException("目标目录或父目录含 Plastic 元数据；请另选目录。");
        }

        private static string ValidateCreationPath(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || path != path.Trim() || !Path.IsPathRooted(path) || path.Any(Char.IsControl) ||
                path.StartsWith("\\\\?\\", StringComparison.Ordinal) || path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
                (!path.StartsWith("\\\\", StringComparison.Ordinal) && (path.Length < 3 || path[1] != ':' || (path[2] != '\\' && path[2] != '/'))))
                throw new ArgumentException("请选择完整的绝对工作区路径，例如 D:\\Work\\MyProject。");
            string root = Path.GetPathRoot(path);
            string remainder = path.Substring(root.Length);
            if (remainder.Split('\\', '/').Any(part => part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
                throw new ArgumentException("目标路径含不安全或有歧义的名称。");
            string full = Path.GetFullPath(path).TrimEnd('\\', '/');
            if (full.Length <= root.TrimEnd('\\', '/').Length) throw new ArgumentException("不能使用磁盘根目录作为工作区。");
            return full;
        }

        private static void ValidateCreationServer(string server)
        {
            string address = server != null && server.StartsWith("ssl://", StringComparison.OrdinalIgnoreCase) ? server.Substring(6) : server;
            ValidateCreationComponent(address, "服务器", false);
        }
        private static void ValidateCreationComponent(string value, string label, bool disallowAt)
        {
            if (String.IsNullOrWhiteSpace(value) || value != value.Trim() || value.StartsWith("-", StringComparison.Ordinal) || value.Any(Char.IsControl) ||
                value.IndexOfAny(new[] { '\'', '"', '#', '\\', '/', '*', '?' }) >= 0 || (disallowAt && value.IndexOf('@') >= 0))
                throw new ArgumentException(label + "包含不支持的字符或为空。");
        }
        private static IEnumerable<string> CreationLines(string output)
        { return (output ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries); }
        private PlasticProcessCommand CreationCommand(string directory, IEnumerable<string> arguments)
        { return new PlasticProcessCommand { FileName = config.CmPath, WorkingDirectory = directory, Arguments = arguments.ToList() }; }
        private static void ReportCreation(IProgress<string> progress, string message) { if (progress != null) progress.Report(message); }
        private static FileStream CreationGate(string key)
        {
            string directory = Path.Combine(Path.GetTempPath(), "TortoiseSCM-workspace-creation"); RejectReparsePath(directory); Directory.CreateDirectory(directory);
            string file;
            using (var hash = SHA256.Create()) file = Path.Combine(directory, BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "") + ".lock");
            RejectReparsePath(file);
            try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) { throw new InvalidOperationException("另一创建操作正在使用相同的目录或工作区名称，请等待其完成。", error); }
        }
    }
}
