// GPL-2.0-or-later. Exercise the real Inno wizard using its native controls.
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
using Microsoft.Win32;

internal static class SetupWizardUiTests
{
    private const string Arp = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{D7305D7A-348E-4E30-88D3-6BCE9CE856BA}_is1";
    private static string artifacts, executable, root;
    private static int assertions;
    private static IntPtr wizard;

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 2 || !args[0].EndsWith("-Setup-Test.exe", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("SetupWizardUiTests <TestSetup.exe> <new artifact directory>");
            executable = Path.GetFullPath(args[0]); artifacts = Path.GetFullPath(args[1]);
            if (Directory.Exists(artifacts) || Installed()) throw new InvalidOperationException("Use a fresh fixture and no existing TestSetup installation.");
            Directory.CreateDirectory(artifacts); root = Path.Combine(artifacts, "自选安装目录 with spaces &");
            Launch(true); Click(wizard, "下一步");
            Wait(() => Children(wizard).Any(h => Text(h) == "选择目标位置"));
            var edit = Children(wizard).Single(h => Class(h) == "TNewPathEdit");
            Require((GetWindowLong(edit, -16) & 0x800) == 0, "First installation exposes an editable destination");
            Require(Children(wizard).Any(h => Text(h).StartsWith("浏览") && IsWindowEnabled(h)), "Directory browse is enabled");
            Require(Text(edit) == root, "Unicode custom path remains intact in the wizard");
            Save(wizard, "select-directory.png");
            Click(wizard, "下一步"); Wait(() => Children(wizard).Any(h => Text(h).StartsWith("安装(&I)")));
            Click(wizard, "安装(&I)"); Wait(() => Children(wizard).Any(h => Text(h).StartsWith("完成")), 180);
            Save(wizard, "installed.png"); Click(wizard, "完成"); Wait(() => !IsWindow(wizard));
            Require(Installed(), "Custom-path install registers only the test Apps entry");
            using (var key = Registry.CurrentUser.OpenSubKey(Arp))
                Require(((string)key.GetValue("InstallLocation")).TrimEnd('\\') == root, "Apps entry records chosen path");
            Require(File.Exists(Path.Combine(root, "current-install.json")), "Payload is installed under the chosen directory");
            string pointer = File.ReadAllText(Path.Combine(root, "current-install.json"));

            Launch(); Click(wizard, "下一步");
            Wait(() => Children(wizard).Any(h => Text(h) == "维护已有安装"));
            Save(wizard, "maintenance.png");
            Click(wizard, "下一步"); Wait(() => Children(wizard).Any(h => Class(h) == "TNewPathEdit"));
            edit = Children(wizard).Single(h => Class(h) == "TNewPathEdit");
            Require(Text(edit) == root && (GetWindowLong(edit, -16) & 0x800) != 0, "Repair remembers and protects existing custom path");
            Save(wizard, "repair-directory.png");
            Click(wizard, "上一步"); Wait(() => Children(wizard).Any(h => Text(h) == "维护已有安装"));
            var options = Children(wizard).Single(h => Class(h) == "TNewCheckListBox");
            SendMessage(options, 0x0186, (IntPtr)1, IntPtr.Zero); // LB_SETCURSEL
            PostMessage(options, 0x0100, (IntPtr)32, IntPtr.Zero); // space selects the radio item
            PostMessage(options, 0x0101, (IntPtr)32, IntPtr.Zero);
            Thread.Sleep(100); Save(wizard, "uninstall-selected.png");
            Click(wizard, "下一步");
            IntPtr confirmation = WaitDialog("是否卸载 TortoiseSCM？");
            Save(confirmation, "uninstall-confirmation.png"); Click(confirmation, "否"); Wait(() => !IsWindow(confirmation));
            Require(Installed() && File.ReadAllText(Path.Combine(root, "current-install.json")) == pointer, "Cancelling uninstall preserves the installation");
            Click(wizard, "下一步"); confirmation = WaitDialog("是否卸载 TortoiseSCM？"); Click(confirmation, "是");
            IntPtr success = WaitDialog("卸载成功。工作区和用户设置已保留。", 180);
            Require(!Installed() && !File.Exists(Path.Combine(root, "current-install.json")), "Uninstall from the setup removes the payload and Apps entry");
            Save(success, "uninstalled.png"); Click(success, "确定"); Wait(() => !IsWindow(wizard));
            Wait(() => !File.Exists(Path.Combine(root, "setup", "unins000.exe")));
            Require(!File.Exists(Path.Combine(root, "setup", "unins000.exe")), "Uninstall does not reinstall setup support files");
            Console.WriteLine("PASS: setup wizard UI (" + assertions + " assertions). " + artifacts); return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (wizard != IntPtr.Zero && IsWindow(wizard))
            {
                Console.Error.WriteLine(String.Join("\n", Children(wizard).Select(h => Class(h) + " : " + Text(h))));
                Save(wizard, "failure.png");
            }
            return 1;
        }
    }

    private static bool Installed() { using (var key = Registry.CurrentUser.OpenSubKey(Arp)) return key != null; }
    private static void Launch(bool customPath = false)
    {
        Require(!Windows().Any(h => Text(h) == "安装 - TortoiseSCM Installer Test"), "No other test wizard is running");
        Process.Start(new ProcessStartInfo(executable, "/NORESTART /LOG=\"" + Path.Combine(artifacts, "wizard-" + assertions + ".log") + "\"" +
            (customPath ? " /DIR=\"" + root + "\"" : ""))
            { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        Wait(() => Windows().Any(h => Text(h) == "安装 - TortoiseSCM Installer Test"));
        wizard = Windows().Single(h => Text(h) == "安装 - TortoiseSCM Installer Test");
        Wait(() => Children(wizard).Any(h => Text(h).StartsWith("下一步")));
    }
    private static IntPtr WaitDialog(string text, int seconds = 30)
    {
        IntPtr dialog = IntPtr.Zero;
        Wait(() => {
            uint owner; GetWindowThreadProcessId(wizard, out owner);
            dialog = Windows().FirstOrDefault(h => {
                uint process; GetWindowThreadProcessId(h, out process);
                return process == owner && h != wizard && Children(h).Any(c => Text(c).StartsWith(text));
            }); return dialog != IntPtr.Zero;
        }, seconds); return dialog;
    }
    private static void Click(IntPtr window, string caption)
    {
        var matches = Children(window).Where(h => Class(h).EndsWith("Button") && Text(h).StartsWith(caption) && IsWindowEnabled(h)).ToArray();
        if (matches.Length != 1) throw new Exception("Expected one button: " + caption + "; found " + matches.Length);
        PostMessage(matches[0], 0x00F5, IntPtr.Zero, IntPtr.Zero);
    }
    private static void Wait(Func<bool> condition, int seconds = 45)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Setup wizard UI timed out"); Thread.Sleep(100); }
    }
    private static List<IntPtr> Windows()
    {
        var result = new List<IntPtr>(); EnumWindows((h, unused) => { if (IsWindowVisible(h)) result.Add(h); return true; }, IntPtr.Zero); return result;
    }
    private static List<IntPtr> Children(IntPtr window)
    {
        var result = new List<IntPtr>(); EnumChildWindows(window, (h, unused) => { if (IsWindowVisible(h)) result.Add(h); return true; }, IntPtr.Zero); return result;
    }
    private static string Text(IntPtr h) { var text = new StringBuilder(4096); GetWindowText(h, text, text.Capacity); return text.ToString(); }
    private static string Class(IntPtr h) { var text = new StringBuilder(256); GetClassName(h, text, text.Capacity); return text.ToString(); }
    private static void Save(IntPtr window, string name)
    {
        RECT bounds; GetWindowRect(window, out bounds);
        using (var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top))
        {
            using (var graphics = Graphics.FromImage(bitmap))
            { IntPtr dc = graphics.GetHdc(); try { PrintWindow(window, dc, 2); } finally { graphics.ReleaseHdc(dc); } }
            bitmap.Save(Path.Combine(artifacts, name), ImageFormat.Png);
        }
    }
    private static void Require(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr p);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumProc callback, IntPtr p);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder text, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT bounds);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint message, IntPtr w, IntPtr l);
}
