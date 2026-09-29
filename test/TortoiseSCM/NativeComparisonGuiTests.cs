// GPL-2.0-or-later. Launch the real native parser and render two/three-way files.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TortoiseSCM;

internal static class NativeComparisonGuiTests
{
    private delegate bool EnumWindowsProc(IntPtr window, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int length);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wparam, IntPtr lparam);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || !Path.GetFileName(args[0]).Equals("TortoiseGitMerge.exe", StringComparison.OrdinalIgnoreCase) || Directory.Exists(args[1]))
                throw new ArgumentException("Expected real TortoiseGitMerge.exe and a new artifact directory.");
            string root = Path.GetFullPath(args[1]); Directory.CreateDirectory(root);
            foreach (bool merge in new[] { false, true }) Run(Path.GetFullPath(args[0]), root, merge);
            Console.WriteLine("PASS: real TortoiseGitMerge diff and merge load Unicode/spaced paths without errors, render, and close normally.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string executable, string artifacts, bool merge)
    {
        string marker = "native-" + Guid.NewGuid().ToString("N");
        string root = Path.Combine(artifacts, marker + " 中文 & space"); Directory.CreateDirectory(root);
        string before = Path.Combine(root, "base.json"), local = Path.Combine(root, marker + ".json"), remote = Path.Combine(root, "remote.json"), output = Path.Combine(root, "merged.json");
        File.WriteAllText(before, "{\r\n  \"value\": \"before\"\r\n}\r\n");
        File.WriteAllText(local, "{\r\n  \"value\": \"after\"\r\n}\r\n");
        File.WriteAllText(remote, File.ReadAllText(before));
        var originals = new[] { before, local, remote }.ToDictionary(file => file, File.ReadAllBytes);
        var ids = new HashSet<int>(Process.GetProcessesByName("TortoiseGitMerge").Select(p => { using (p) return p.Id; }));
        var arguments = merge ? PlasticToolArguments.Expand(BeyondCompareTool.MergeArguments,
            new Dictionary<string, string> { { "base", before }, { "local", local }, { "remote", remote }, { "merged", output } }, true)
            : new List<string> { "/solo", "/readonly", before, local, "/lefttitle=Base \"quoted\" 中文", "/righttitle=Local & value" };
        var pending = merge ? ComparisonTool.RunMergeAsync(executable, arguments, root, CancellationToken.None)
            : ComparisonTool.RunDiffAsync(executable, arguments, root, CancellationToken.None);
        Process owned = null;
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(25);
            while (owned == null && DateTime.UtcNow < deadline)
            {
                foreach (var candidate in Process.GetProcessesByName("TortoiseGitMerge"))
                {
                    if (!ids.Contains(candidate.Id) && String.Equals(candidate.MainModule.FileName, executable, StringComparison.OrdinalIgnoreCase))
                    { owned = candidate; break; }
                    candidate.Dispose();
                }
                if (owned == null) Thread.Sleep(100);
            }
            if (owned == null) throw new Exception("The test-owned native editor did not start.");
            owned.WaitForInputIdle(10000); Thread.Sleep(700);
            var windows = new List<IntPtr>();
            EnumWindows((window, state) => { uint pid; GetWindowThreadProcessId(window, out pid); if (pid == owned.Id && IsWindowVisible(window)) windows.Add(window); return true; }, IntPtr.Zero);
            foreach (IntPtr window in windows)
            {
                var name = new StringBuilder(256); GetClassName(window, name, name.Capacity);
                if (name.ToString() == "#32770") throw new Exception("Native editor displayed an unexpected dialog (such as a file-open error).");
            }
            owned.Refresh();
            if (pending.IsCompleted || owned.MainWindowHandle == IntPtr.Zero) throw new Exception("Native comparison closed before rendering.");
            Rect bounds; GetWindowRect(owned.MainWindowHandle, out bounds);
            using (var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
            {
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr dc = graphics.GetHdc();
                    try { if (!PrintWindow(owned.MainWindowHandle, dc, 2)) throw new Exception("Native window capture failed."); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                bitmap.Save(Path.Combine(artifacts, merge ? "native-merge.png" : "native-diff.png"), ImageFormat.Png);
            }
            if (merge)
            {
                // Save only this fixture's independent merge result, never an input.
                PostMessage(owned.MainWindowHandle, 0x0111, (IntPtr)0xE103, IntPtr.Zero); // ID_FILE_SAVE
                deadline = DateTime.UtcNow.AddSeconds(10);
                while (!File.Exists(output) && DateTime.UtcNow < deadline) Thread.Sleep(100);
                if (!File.Exists(output) || !File.ReadAllText(output).Contains("after")) throw new Exception("Native merge did not save to the requested output path.");
            }
            if (!owned.CloseMainWindow()) throw new Exception("Native editor refused normal close.");
            if (!pending.Wait(15000)) throw new Exception("Native editor did not finish after normal close.");
            if (!pending.Result.Succeeded) throw new Exception(pending.Result.Error);
            foreach (var original in originals) if (!File.ReadAllBytes(original.Key).SequenceEqual(original.Value)) throw new Exception("Comparison changed an input file.");
        }
        finally { if (owned != null) { if (!owned.HasExited) owned.CloseMainWindow(); owned.Dispose(); } }
    }
}
