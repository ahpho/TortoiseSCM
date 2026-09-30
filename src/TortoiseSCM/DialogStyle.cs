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
            // TortoiseSVN/TortoiseGit refresh every dialog that exposes a
            // refresh button when F5 is pressed.  Install this once for all
            // dialogs so newly added views get the same keyboard behavior.
            form.KeyPreview = true;
            form.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.F5 || e.Modifiers != Keys.None) return;
                Button refresh = FindRefreshButton(form.Controls);
                if (refresh == null || !refresh.Enabled || !refresh.Visible) return;
                refresh.PerformClick();
                e.Handled = true;
                e.SuppressKeyPress = true;
            };
            try { form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch (ArgumentException) { }
        }

        private static Button FindRefreshButton(Control.ControlCollection controls)
        {
            foreach (Control control in controls)
            {
                Button button = control as Button;
                if (button != null && IsRefreshLabel(button.Text)) return button;
                if (control.HasChildren)
                {
                    button = FindRefreshButton(control.Controls);
                    if (button != null) return button;
                }
            }
            return null;
        }

        private static bool IsRefreshLabel(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            return text.IndexOf("刷新", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("refresh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("重新读取", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("恢复会话", StringComparison.OrdinalIgnoreCase) >= 0 ||
                text.IndexOf("预检", StringComparison.OrdinalIgnoreCase) >= 0;
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
