// GPL-2.0-or-later. Fixed native tool profiles share verified SCM input preparation.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public static class ComparisonTool
    {
        public const string DiffArguments = "/base:\"{base}\" /mine:\"{local}\" /readonly";
        public const string MergeArguments = "/base:\"{base}\" /mine:\"{local}\" /theirs:\"{remote}\" /merged:\"{merged}\" /saverequired";

        public static string ResolveExecutable(PlasticClientConfig config)
        {
            if (!config.UseTortoiseMerge) return BeyondCompareTool.ResolveExecutable(config.BeyondComparePath);
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", "TortoiseGit", "TortoiseGitMerge.exe");
            if (!File.Exists(path)) throw new FileNotFoundException("找不到包内 TortoiseGitMerge。请重新安装完整安装包，或在设置中选择 Beyond Compare。", path);
            return path;
        }

        internal static bool IsTortoise(string executable)
        { return String.Equals(Path.GetFileName(executable), "TortoiseGitMerge.exe", StringComparison.OrdinalIgnoreCase); }

        // Existing callers supply fixed BC-profile roles; translate once at the native boundary.
        // No user templates or shell interpolation are involved.
        internal static IList<string> NativeArguments(IList<string> arguments, bool merge)
        {
            var files = arguments.Where(arg => !arg.StartsWith("/", StringComparison.Ordinal)).ToArray();
            if (files.Length != (merge ? 3 : 2)) throw new ArgumentException("Unexpected comparison input roles.");
            Func<string, string, string> title = (key, fallback) => arguments.Where(arg => arg.StartsWith(key, StringComparison.Ordinal))
                .Select(arg => arg.Substring(key.Length)).DefaultIfEmpty(fallback).Single();
            if (!merge) return new[] { "/base:" + files[0], "/mine:" + files[1], "/readonly",
                "/basename:" + title("/lefttitle=", "Base"), "/minename:" + title("/righttitle=", "Local") };
            string output = arguments.Single(arg => arg.StartsWith("/mergeoutput=", StringComparison.Ordinal)).Substring(13);
            return new[] { "/base:" + files[2], "/mine:" + files[0], "/theirs:" + files[1], "/merged:" + output,
                "/basename:Base", "/minename:Local", "/theirsname:Remote", "/mergedname:Merged", "/saverequired" };
        }

        public static Task<PlasticCommandResult> RunDiffAsync(string executable, IList<string> arguments, string directory, CancellationToken token)
        {
            return IsTortoise(executable)
                ? BeyondCompareProcess.RunNativeAsync(executable, NativeArguments(arguments, false), directory, token)
                : BeyondCompareProcess.RunDiffAsync(executable, arguments, directory, token);
        }

        public static Task<PlasticCommandResult> RunMergeAsync(string executable, IList<string> arguments, string directory, CancellationToken token)
        {
            return IsTortoise(executable)
                ? BeyondCompareProcess.RunNativeAsync(executable, NativeArguments(arguments, true), directory, token)
                : BeyondCompareProcess.RunAsync(executable, arguments, directory, token);
        }
    }
}
