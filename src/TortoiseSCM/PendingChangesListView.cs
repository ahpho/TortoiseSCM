// GPL-2.0-or-later. Keep comparison activation separate from check-in selection.
using System;
using System.Drawing;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class PendingChangesListView : ListView
    {
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0203) // WM_LBUTTONDBLCLK
            {
                var point = new Point((short)(message.LParam.ToInt64() & 0xffff), (short)(message.LParam.ToInt64() >> 16));
                var hit = HitTest(point);
                // WinForms toggles CheckBoxes on double-clicking a row. Consume
                // that native message and activate comparison without changing it.
                // A checkbox click remains a checkbox gesture, never a comparison.
                if (hit.Item != null && (hit.Location & ListViewHitTestLocations.StateImage) == 0)
                {
                    OnDoubleClick(EventArgs.Empty);
                    OnMouseDoubleClick(new MouseEventArgs(MouseButtons.Left, 2, point.X, point.Y, 0));
                }
                return;
            }
            base.WndProc(ref message);
        }
    }
}
