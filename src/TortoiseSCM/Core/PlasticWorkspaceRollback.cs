// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        // Reverse the exact (target, loaded-head] interval using Plastic's native
        // subtractive merge. This creates pending changes and preserves the selector.
        private async Task<PlasticCommandResult> RollbackWorkspaceAsync(string root, long target, CancellationToken token)
        {
            var workspace = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
            if (workspace.IsPartial) throw new ArgumentException("整仓回滚为待提交更改目前只支持完整工作区；部分工作区请使用文件回滚，或切换已加载内容的历史快照。");
            using (var gate = OpenMergeGate(root))
                return await RollbackWorkspaceLockedAsync(workspace, root, target, token).ConfigureAwait(false);
        }

        private async Task<PlasticCommandResult> RollbackWorkspaceLockedAsync(PlasticWorkspace workspace, string root, long target, CancellationToken token)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(root, ".plastic", "plastic.mergeprogress")))
                throw new ArgumentException("请先完成或撤销当前合并，再执行整仓回滚。");
            await ValidateCleanRevisionOperationAsync(root, root, token).ConfigureAwait(false);
            var selector = await ExecuteAsync(RevisionCommand(root, new [] { "showselector" }), token).ConfigureAwait(false);
            RequireSuccess(selector);
            if (Regex.Matches(selector.Output, @"(?m)^\s*smartbranch\s+""[^""\r\n]+""\s*$").Count != 1 ||
                Regex.IsMatch(selector.Output, @"(?mi)^\s*(changeset|label)\s+"))
                throw new ArgumentException("整仓回滚需要工作区选择器指向分支；请先更新到分支最新版本。未执行回滚。");
            long loaded = await LoadedChangesetAsync(root, false, token).ConfigureAwait(false);
            long head = await LoadedChangesetAsync(root, true, token).ConfigureAwait(false);
            if (loaded != head) throw new ArgumentException("工作区尚未更新到分支最新版本；请先更新，再执行整仓回滚。未执行回滚。");
            if (target == loaded) return new PlasticCommandResult { ExitCode = 0, Output = "工作区已经位于所选版本；无需生成回滚更改。" };
            var query = await ExecuteAsync(RevisionCommand(root, new [] { "find", "changeset", "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(query);
            if (!IsAncestorChangeset(query.Output, loaded, target))
                throw new ArgumentException("整仓回滚目标必须是当前分支版本的父链祖先；不支持跨分支快照替换。未执行回滚。");
            var args = new List<string> { "merge", "cs:" + loaded.ToString(CultureInfo.InvariantCulture), "--subtractive",
                "--interval-origin=cs:" + target.ToString(CultureInfo.InvariantCulture), "--nointeractiveresolution", "--machinereadable" };
            var preview = await ExecuteAsync(RevisionCommand(root, args), token).ConfigureAwait(false);
            RequireSuccess(preview);
            if (!IsSafeSubtractivePreview(preview.Output))
                throw new ArgumentException("整仓回滚预检包含冲突或无法识别的操作；未执行回滚。请使用官方客户端检查。\r\n" + preview.Output);
            // Recheck after potentially lengthy history/merge planning. Never auto-discard.
            await ValidateCleanRevisionOperationAsync(root, root, token).ConfigureAwait(false);
            if (await LoadedChangesetAsync(root, false, token).ConfigureAwait(false) != loaded ||
                await LoadedChangesetAsync(root, true, token).ConfigureAwait(false) != head)
                throw new ArgumentException("预检期间分支或工作区版本发生变化；请刷新后重试。未执行回滚。");
            var beforeMergeSelector = await ExecuteAsync(RevisionCommand(root, new [] { "showselector" }), token).ConfigureAwait(false);
            RequireSuccess(beforeMergeSelector);
            if (!String.Equals(selector.Output.Trim(), beforeMergeSelector.Output.Trim(), StringComparison.Ordinal))
                throw new ArgumentException("预检期间工作区选择器发生变化；请刷新后重试。未执行回滚。");
            args.Add("--merge");
            var tracking = TrackWorkspaceRollback(workspace, loaded, target);
            var result = await ExecuteAsync(RevisionCommand(root, args), token).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                result.Error += "\r\n原生减法合并未完成；部分待定更改可能已产生。请检查状态，修复或明确撤销后再重试；没有自动提交或删除服务器历史。";
                return result;
            }
            var after = await ExecuteAsync(RevisionCommand(root, new [] { "showselector" }), token).ConfigureAwait(false);
            if (!after.Succeeded || !String.Equals(selector.Output.Trim(), after.Output.Trim(), StringComparison.Ordinal))
                return new PlasticCommandResult { ExitCode = -1, Output = result.Output,
                    Error = "减法合并已执行，但选择器验证失败；请检查工作区状态。更改未自动提交。\r\n" + after.Error };
            result.Output += "\r\n已将整个工作区恢复为 cs:" + target + " 的待提交更改；分支选择器与已加载版本保持不变。请检查差异后提交。";
            tracking.RollbackProgress = RollbackProgressHash(root); tracking.Ready = true; SaveMergeState(tracking);
            return result;
        }

        private async Task<long> LoadedChangesetAsync(string root, bool head, CancellationToken token)
        {
            var args = new List<string> { "status", root, "--header", "--xml", "--encoding=utf-8" };
            if (head) args.Add("--head");
            var result = await ExecuteAsync(RevisionCommand(root, args), token).ConfigureAwait(false);
            RequireSuccess(result);
            var document = SafeXml.Load(result.Output);
            var status = document.Root == null ? null : document.Root.Element("WorkspaceStatus");
            status = status == null ? null : status.Element("Status");
            long number;
            if (status == null || !Int64.TryParse((string)status.Element("Changeset"), out number) || number < 0)
                throw new ArgumentException("无法确认完整工作区版本；未执行整仓回滚。");
            return number;
        }

        internal static bool IsAncestorChangeset(string xml, long loaded, long target)
        {
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "PLASTICQUERY") return false;
            var parents = new Dictionary<long, long>();
            foreach (var item in document.Root.Elements("CHANGESET"))
            {
                long id, parent;
                if (!Int64.TryParse((string)item.Element("CHANGESETID"), out id) || !Int64.TryParse((string)item.Element("PARENT"), out parent) || parents.ContainsKey(id)) return false;
                parents.Add(id, parent);
            }
            var visited = new HashSet<long>();
            while (loaded >= 0 && visited.Add(loaded))
            {
                if (loaded == target) return parents.ContainsKey(target);
                if (!parents.TryGetValue(loaded, out loaded)) return false;
            }
            return false;
        }

        internal static bool IsSafeSubtractivePreview(string output)
        {
            foreach (string line in output.Split(new [] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (!line.StartsWith("FILE_SRC ", StringComparison.Ordinal) && !line.StartsWith("APPLY ADD ", StringComparison.Ordinal) &&
                    !line.StartsWith("APPLY RM ", StringComparison.Ordinal) && !line.StartsWith("APPLY MV ", StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
