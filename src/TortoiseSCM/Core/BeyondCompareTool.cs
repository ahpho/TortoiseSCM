// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace TortoiseSCM
{
    /// <summary>The supported Beyond Compare profile. Detection never starts a process.</summary>
    public static class BeyondCompareTool
    {
        public const string DiffArguments = "/solo /readonly \"{base}\" \"{local}\" /lefttitle=\"Base\" /righttitle=\"Local\"";
        public const string MergeArguments = "/solo /readonly \"{local}\" \"{remote}\" \"{base}\" /mergeoutput=\"{merged}\" /lefttitle=\"Local\" /righttitle=\"Remote\" /centertitle=\"Base\" /outputtitle=\"Merged\"";

        public static string ResolveExecutable(string configured)
        {
            // An explicit setting is authoritative, even if a different installation is available.
            if (!String.IsNullOrWhiteSpace(configured)) return NormalizeExecutable(configured);
            string detected = FindExecutable();
            if (detected.Length != 0) return detected;
            throw new InvalidOperationException("未找到 Beyond Compare。请安装 Beyond Compare，并在设置中选择 BComp.exe；三方合并需要 Pro 版。其他 SCM 操作仍可使用。");
        }

        public static string NormalizeExecutable(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择 Beyond Compare 安装目录中的 BComp.exe。", "path");
            string full;
            try
            {
                path = path.Trim();
                if (!Path.IsPathRooted(path) || Path.GetPathRoot(path).Length < 3) throw new ArgumentException("Beyond Compare 路径必须是完整路径。", "path");
                full = Path.GetFullPath(path);
                string name = Path.GetFileName(full);
                if (String.Equals(name, "BCompare.exe", StringComparison.OrdinalIgnoreCase))
                    full = Path.Combine(Path.GetDirectoryName(full), "BComp.exe");
                else if (!String.Equals(name, "BComp.exe", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("请选择 BComp.exe（也可选择同目录的 BCompare.exe）。", "path");
            }
            catch (NotSupportedException) { throw new ArgumentException("Beyond Compare 路径无效。", "path"); }
            catch (PathTooLongException) { throw new ArgumentException("Beyond Compare 路径过长。", "path"); }
            if (!File.Exists(full)) throw new FileNotFoundException("找不到 Beyond Compare 的 BComp.exe。请检查安装或在设置中重新选择路径。", full);
            return full;
        }

        public static string FindExecutable()
        {
            var candidates = new List<string>();
            foreach (string version in new[] { "5", "4" })
                foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                    if (!String.IsNullOrWhiteSpace(root)) candidates.Add(Path.Combine(root, "Beyond Compare " + version, "BComp.exe"));
            foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    AddRegistryCandidate(candidates, hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\BComp.exe", null, false);
                    AddRegistryCandidate(candidates, hive, view, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\BCompare.exe", null, false);
                    foreach (string version in new[] { "5", "4" })
                    {
                        AddRegistryCandidate(candidates, hive, view, @"SOFTWARE\Scooter Software\Beyond Compare " + version, "ExePath", false);
                        AddRegistryCandidate(candidates, hive, view, @"SOFTWARE\Scooter Software\Beyond Compare " + version, "InstallPath", true);
                    }
                }
            return FindExecutable(AppDomain.CurrentDomain.BaseDirectory, candidates);
        }

        internal static string FindExecutable(string applicationDirectory, IEnumerable<string> installedCandidates)
        {
            // Keep the tool relative to this exact application version. Upgrades use
            // a new directory and must not persist the old bundled executable path.
            string bundled = Path.Combine(applicationDirectory, "Tools", "BeyondCompare");
            if (File.Exists(Path.Combine(bundled, "BCompare.exe")) && File.Exists(Path.Combine(bundled, "BComp.exe")))
                return NormalizeExecutable(Path.Combine(bundled, "BComp.exe"));
            return FindExecutable(installedCandidates);
        }

        internal static string FindExecutable(IEnumerable<string> candidates)
        {
            foreach (string candidate in candidates)
            {
                try { return NormalizeExecutable(candidate); }
                catch (ArgumentException) { }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (SecurityException) { }
            }
            return "";
        }

        private static void AddRegistryCandidate(IList<string> candidates, RegistryHive hive, RegistryView view, string subkey, string valueName, bool directory)
        {
            try
            {
                using (RegistryKey root = RegistryKey.OpenBaseKey(hive, view))
                using (RegistryKey key = root.OpenSubKey(subkey))
                {
                    string value = key == null ? null : key.GetValue(valueName) as string;
                    if (String.IsNullOrWhiteSpace(value)) return;
                    value = value.Trim().Trim('"');
                    candidates.Add(directory ? Path.Combine(value, "BComp.exe") : value);
                }
            }
            catch (ArgumentException) { }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (SecurityException) { }
        }
    }
}
