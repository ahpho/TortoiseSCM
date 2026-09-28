// GPL-2.0-or-later. User-driven tool sessions outlive ordinary SCM command timeouts.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    internal sealed class BeyondCompareWaitException : InvalidOperationException
    {
        internal BeyondCompareWaitException(string directory, bool native = false)
            : base((native ? "TortoiseGitMerge 会话输出未关闭。" : "Beyond Compare 无法等待编辑窗口关闭（退出码 102）。") + "贡献文件已保留在：" + directory +
                "。请关闭该工具窗口后再重试；不要同时应用或重新编辑结果。") { }
    }

    public static class BeyondCompareProcess
    {
        // BC's comparison result codes describe equality/differences, not launch failures.
        // Keep this separate from merge completion: conflicts/output validation remain strict.
        public static async Task<PlasticCommandResult> RunDiffAsync(string executable, IList<string> arguments,
            string workingDirectory, CancellationToken token)
        {
            var result = await RunAsync(executable, arguments, workingDirectory, token).ConfigureAwait(false);
            int code = result.ExitCode;
            if (code == 0 || code == 1 || code == 2 || code == 11 || code == 12 || code == 13)
                return new PlasticCommandResult { ExitCode = 0, Error = "", Output =
                    "Beyond Compare 比较窗口已关闭（比较结果代码 " + code + "）。" };
            return result;
        }

        public static async Task<PlasticCommandResult> RunAsync(string executable, IList<string> arguments,
            string workingDirectory, CancellationToken token)
        { return await RunSessionAsync(executable, arguments, workingDirectory, token, false).ConfigureAwait(false); }

        internal static Task<PlasticCommandResult> RunNativeAsync(string executable, IList<string> arguments,
            string workingDirectory, CancellationToken token)
        { return RunSessionAsync(executable, arguments, workingDirectory, token, true); }

        private static async Task<PlasticCommandResult> RunSessionAsync(string executable, IList<string> arguments,
            string workingDirectory, CancellationToken token, bool native)
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo { FileName = executable,
                Arguments = String.Join(" ", arguments.Select(PlasticClient.QuoteArgument)),
                WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
            if (start.Arguments.Length > 30000) throw new ArgumentException("比较/合并工具参数过长。");
            using (var process = new Process { StartInfo = start })
            {
                process.Start();
                process.StandardInput.Close();
                Task<string> stdout = process.StandardOutput.ReadToEndAsync(), stderr = process.StandardError.ReadToEndAsync();
                // BComp /solo owns a dedicated comparison. Never kill an editor on timeout
                // or cancellation: keep inputs and workspace gates alive until it closes.
                while (!process.HasExited) await Task.Delay(75).ConfigureAwait(false);
                int code = process.ExitCode;
                if (!native && code == 102) throw new BeyondCompareWaitException(workingDirectory);
                // A stray child retaining pipes must not hang a completed session forever.
                var drained = Task.WhenAll(stdout, stderr);
                if (await Task.WhenAny(drained, Task.Delay(5000)).ConfigureAwait(false) != drained)
                    throw new BeyondCompareWaitException(workingDirectory, native);
                token.ThrowIfCancellationRequested();
                if (native) return new PlasticCommandResult { ExitCode = code,
                    Error = code == 0 ? "" : "TortoiseGitMerge 未正常完成，退出码：" + code,
                    Output = code == 0 ? "TortoiseGitMerge 已关闭；请核查保存结果，冲突状态未自动改变。" : "" };
                string error = code == 101 ? "Beyond Compare 检测到冲突，未写入合并结果。" :
                    code == 103 ? "BComp.exe 找不到 BCompare.exe；请检查 Beyond Compare 安装。" :
                    code == 104 ? "Beyond Compare 试用已到期；请检查授权。三方合并需要 Pro。" :
                    "Beyond Compare 未正常完成，退出码：" + code + "。请检查结果后重试。";
                return new PlasticCommandResult { ExitCode = code, Error = code == 0 ? "" : error,
                    Output = code == 0 ? "Beyond Compare 窗口已关闭；请核查已保存结果。Plastic 冲突状态未改变。" : "" };
            }
        }
    }
}
