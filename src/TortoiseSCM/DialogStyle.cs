// Native dialog conventions shared with the upstream Tortoise dialogs.
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class DialogStyle
    {
        internal const int Margin = 12;
        internal const int Gap = 6;
        internal const int ButtonWidth = 88;
        internal const int ButtonHeight = 26;

        internal static void Apply(Form form)
        {
            form.Font = SystemFonts.MessageBoxFont;
            form.BackColor = SystemColors.Control;
            form.ForeColor = SystemColors.ControlText;
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.StartPosition = FormStartPosition.CenterParent;
            try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (ArgumentException) { }
        }

        internal static void ApplyList(ListView list)
        {
            list.GridLines = false;
            list.BorderStyle = BorderStyle.Fixed3D;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.BackColor = SystemColors.Window;
            list.ForeColor = SystemColors.WindowText;
            list.HandleCreated += delegate { SetWindowTheme(list.Handle, "Explorer", null); };
            if (list.IsHandleCreated) SetWindowTheme(list.Handle, "Explorer", null);
        }

        internal static Button Button(string text)
        {
            return new Button { Text = text, Size = new Size(ButtonWidth, ButtonHeight),
                UseVisualStyleBackColor = true, Margin = new Padding(Gap, 0, 0, 0) };
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr window, string subAppName, string subIdList);
    }
}
