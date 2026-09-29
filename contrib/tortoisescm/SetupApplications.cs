// GPL-2.0-or-later. Windows Restart Manager integration for installer maintenance.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace TortoiseSCM.Setup
{
    public sealed class BlockingApplication
    {
        public int Id;
        public long StartTime;
        public string Name;
        public bool CanClose;
    }

    public static class Applications
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess
        {
            public int Id;
            public System.Runtime.InteropServices.ComTypes.FILETIME Started;
            public long StartTime { get { return ((long)Started.dwHighDateTime << 32) | (uint)Started.dwLowDateTime; } }
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Name;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Service;
            public int Type;
            public uint Status, Session;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, uint flags, StringBuilder key);
        [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint fileCount, string[] files, uint processCount, UniqueProcess[] processes, uint serviceCount, string[] services);
        [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] apps, out uint rebootReasons);
        [DllImport("rstrtmgr.dll")] private static extern int RmShutdown(uint session, uint flags, IntPtr callback);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

        private static void Check(int error) { if (error != 0) throw new Win32Exception(error); }
        private static uint Start()
        {
            uint session;
            Check(RmStartSession(out session, 0, new StringBuilder(33)));
            return session;
        }
        private static ProcessInfo[] List(uint session)
        {
            uint needed, count = 0, reasons;
            ProcessInfo[] items = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int error = RmGetList(session, out needed, ref count, items, out reasons);
                if (error == 0) return items == null ? new ProcessInfo[0] : items.Take((int)count).ToArray();
                if (error != 234) Check(error);
                count = needed; items = new ProcessInfo[count];
            }
            throw new InvalidOperationException("The list of applications is changing. Please retry.");
        }
        private static bool CanClose(ProcessInfo item)
        {
            // Never stop services, Explorer, consoles, other sessions/users, or
            // critical applications. Explorer requires separate file-operation consent.
            if (item.Type < 0 || item.Type > 2 || item.Process.Id == Process.GetCurrentProcess().Id) return false;
            try
            {
                using (var process = Process.GetProcessById(item.Process.Id))
                {
                    if (process.SessionId != Process.GetCurrentProcess().SessionId || process.StartTime.ToFileTimeUtc() != item.Process.StartTime) return false;
                    IntPtr token;
                    if (!OpenProcessToken(process.Handle, 8, out token)) return false;
                    try
                    {
                        using (var identity = new WindowsIdentity(token))
                        using (var current = WindowsIdentity.GetCurrent())
                            return identity.User == current.User;
                    }
                    finally { CloseHandle(token); }
                }
            }
            catch { return false; }
        }
        private static string DisplayName(ProcessInfo item)
        {
            if (!String.IsNullOrWhiteSpace(item.Name)) return item.Name;
            try { using (var process = Process.GetProcessById(item.Process.Id)) return process.ProcessName; }
            catch { return "Application"; }
        }
        public static BlockingApplication[] Find(string[] files)
        {
            if (files.Length == 0) return new BlockingApplication[0];
            uint session = Start();
            try
            {
                Check(RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null));
                return List(session).Select(item => new BlockingApplication {
                    Id = item.Process.Id, StartTime = item.Process.StartTime, Name = DisplayName(item), CanClose = CanClose(item)
                }).ToArray();
            }
            finally { RmEndSession(session); }
        }
        public static void Close(BlockingApplication[] approved, string[] files)
        {
            var current = Find(files);
            // Revalidate the exact identities from the confirmation; never broaden
            // approval to a newly started process or a recycled PID.
            var targets = current.Where(item => item.CanClose && approved.Any(a => a.Id == item.Id && a.StartTime == item.StartTime)).ToArray();
            if (targets.Length == 0) return;
            uint session = Start();
            try
            {
                var unique = targets.Select(item => new UniqueProcess { Id = item.Id, Started = new System.Runtime.InteropServices.ComTypes.FILETIME {
                    dwLowDateTime = (int)item.StartTime, dwHighDateTime = (int)(item.StartTime >> 32) } }).ToArray();
                Check(RmRegisterResources(session, 0, null, (uint)unique.Length, unique, 0, null));
                var affected = List(session);
                if (affected.Any(item => !CanClose(item) || !unique.Any(a => a.Id == item.Process.Id && a.StartTime == item.Process.StartTime)))
                    throw new InvalidOperationException("The affected applications changed. Please retry uninstall.");
                // No RmForceShutdown: applications may veto exit to preserve work.
                Check(RmShutdown(session, 0, IntPtr.Zero));
            }
            finally { RmEndSession(session); }
        }
    }
}
