// GPL-2.0-or-later. Test-owned GUI process holding an actual mapped shell DLL.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class SetupLockHost
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 3) return 2;
        IntPtr module = LoadLibraryEx(Path.GetFullPath(args[0]), IntPtr.Zero, 1);
        if (module == IntPtr.Zero) return 3;
        try
        {
            using (var form = new Form { Text = "TortoiseSCM uninstall test lock", Width = 400, Height = 150 })
            {
                form.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "Installer test process. It will close automatically." });
                form.Shown += (s, e) => File.WriteAllText(args[1], "ready");
                form.FormClosing += (s, e) => {
                    File.AppendAllText(args[1], "\nclose requested");
                    e.Cancel = args[2] == "veto";
                };
                Application.Run(form);
            }
        }
        finally { FreeLibrary(module); }
        return 0;
    }
}
