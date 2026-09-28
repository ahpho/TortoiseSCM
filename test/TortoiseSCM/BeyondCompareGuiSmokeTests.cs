// GPL-2.0-or-later. Opt-in real Beyond Compare window smoke test; no workspace writes.
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
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using TortoiseSCM;

internal static class BeyondCompareGuiSmokeTests
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    private static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("Expected BComp.exe and a new QA output directory.");
        string executable = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        if (!Path.GetFileName(executable).Equals("BComp.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("Supply the actual vendor BComp.exe.");
        string editor = Path.Combine(Path.GetDirectoryName(executable), "BCompare.exe");
        if (!File.Exists(editor) || Directory.Exists(output)) throw new ArgumentException("Complete BC installation and new output directory required.");
        Directory.CreateDirectory(output);
        string marker = "TSCM-smoke-" + Guid.NewGuid().ToString("N");
        string left = Path.Combine(output, "before 中文.txt"), right = Path.Combine(output, "after 中文.txt");
        byte[] before = Encoding.UTF8.GetBytes("original line\r\nshared line\r\n"), after = Encoding.UTF8.GetBytes("updated line\r\nshared line\r\n");
        File.WriteAllBytes(left, before); File.WriteAllBytes(right, after);
        var previousIds = new HashSet<int>(Process.GetProcessesByName("BCompare").Select(p => { using (p) return p.Id; }));
        var arguments = new[] { "/solo", "/readonly", left, right, "/lefttitle=" + marker + " before", "/righttitle=" + marker + " after" };
        Task<PlasticCommandResult> pending = BeyondCompareProcess.RunDiffAsync(executable, arguments, output, CancellationToken.None);
        Process owned = null;
        string title = null;
        bool captured = false;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (owned == null && DateTime.UtcNow < deadline)
            {
                foreach (var process in Process.GetProcessesByName("BCompare"))
                {
                    try
                    {
                        if (!previousIds.Contains(process.Id) && process.MainWindowHandle != IntPtr.Zero &&
                            String.Equals(process.MainModule.FileName, editor, StringComparison.OrdinalIgnoreCase) &&
                            process.MainWindowTitle.Contains(marker))
                        { owned = process; title = process.MainWindowTitle; break; }
                    }
                    finally { if (owned != process) process.Dispose(); }
                }
                if (pending.IsCompleted && owned == null) throw new InvalidOperationException("BC exited before opening the expected comparison: " + pending.GetAwaiter().GetResult().Error);
                if (owned == null) Thread.Sleep(100);
            }
            if (owned == null) throw new TimeoutException("No new vendor BC window with the uniquely titled comparison. Existing windows were left untouched.");
            // Read-only rendering capture; never click the desktop or interact with pre-existing editors.
            owned.WaitForInputIdle(5000);
            Thread.Sleep(500);
            Rect bounds;
            if (!GetWindowRect(owned.MainWindowHandle, out bounds)) throw new InvalidOperationException("Cannot read BC window bounds.");
            using (var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr dc = graphics.GetHdc();
                try { captured = PrintWindow(owned.MainWindowHandle, dc, 2); }
                finally { graphics.ReleaseHdc(dc); }
                bitmap.Save(Path.Combine(output, "beyond-compare-window.png"), ImageFormat.Png);
            }
            if (!captured) throw new InvalidOperationException("BC window could not be captured.");
            if (pending.IsCompleted) throw new InvalidOperationException("Product waiter released inputs while BC was still open.");
            if (!owned.CloseMainWindow()) throw new InvalidOperationException("The test-owned read-only BC window did not accept normal close.");
            if (!pending.Wait(15000)) throw new TimeoutException("Product waiter did not complete after closing the test window; do not kill editors.");
            var result = pending.GetAwaiter().GetResult();
            if (!result.Succeeded) throw new InvalidOperationException(result.Error);
            if (!File.ReadAllBytes(left).SequenceEqual(before) || !File.ReadAllBytes(right).SequenceEqual(after)) throw new InvalidOperationException("Read-only comparison changed its inputs.");
            File.WriteAllText(Path.Combine(output, "result.json"), new JavaScriptSerializer().Serialize(new {
                succeeded = true, executable, title, pid = owned.Id, arguments, captured, exitCode = result.ExitCode, result.Output,
                inputsUnchanged = true, existingWindowsUntouched = true, limitation = "Read-only two-way viewing only; no manual editing or three-way merge acceptance."
            }), new UTF8Encoding(false));
            Console.WriteLine("PASS: actual BC window, separate session, product wait, normal close and unchanged input bytes.");
        }
        finally
        {
            // Only the unique, new /solo read-only comparison belongs to this test.
            if (owned != null) { if (!owned.HasExited) owned.CloseMainWindow(); owned.Dispose(); }
        }
    }
}
