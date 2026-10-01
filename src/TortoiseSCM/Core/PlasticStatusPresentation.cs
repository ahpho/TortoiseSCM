// TortoiseSCM - GPL-2.0-or-later.
using System;

namespace TortoiseSCM
{
    // Keep the distinction between local pending state and the server lock
    // state visible wherever a status row is shown. Plastic reports CH and CO
    // as separate local states; neither state proves that the repository has
    // granted a server lock.
    internal static class PlasticStatusPresentation
    {
        internal const string WorkflowHint =
            "CH=直接修改，可直接签入；CO=显式签出。CO 不等于服务器锁；服务器锁由 Plastic 锁规则决定，请在“操作 → 锁管理”中查看。";

        internal static string PendingStatus(PlasticStatusItem item)
        {
            if (item == null) return "";
            string code = (item.StatusCode ?? "").Trim().ToUpperInvariant();
            if (code == "CH") return "CH · 已修改（可直接签入）";
            if (code == "CO") return "CO · 已签出（不代表服务器锁）";

            string description = (item.StatusDescription ?? item.Status ?? "").Trim();
            if (code.Length == 0 || String.Equals(code, description, StringComparison.OrdinalIgnoreCase))
                return description.Length == 0 ? code : description;
            return description.Length == 0 ? code : code + " · " + description;
        }
    }
}
