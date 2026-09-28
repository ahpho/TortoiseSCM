// GPL-2.0-or-later. Local package identity; never contacts the SCM server.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal sealed class PackageVersionInfo
    {
        internal const int MaximumManifestBytes = 1024 * 1024;
        internal string Version { get; private set; }
        internal string SourceCommit { get; private set; }
        internal string Status { get; private set; }
        internal string ExecutablePath { get; private set; }
        internal string DirectoryPath { get; private set; }

        internal static PackageVersionInfo Read(string directory, string executablePath)
        {
            var result = new PackageVersionInfo { Version = "未知", SourceCommit = "未知", DirectoryPath = directory, ExecutablePath = executablePath };
            string path = Path.Combine(directory, "package-manifest.json");
            try {
                // Open first instead of File.Exists: inaccessible manifests must not look like development builds.
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) {
                    if (stream.Length > MaximumManifestBytes) throw new InvalidDataException("安装包信息过大。");
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true)) {
                        var serializer = new JavaScriptSerializer { MaxJsonLength = MaximumManifestBytes, RecursionLimit = 32 };
                        var data = serializer.DeserializeObject(reader.ReadToEnd()) as Dictionary<string, object>;
                        object product; object version; object commit;
                        if (data == null || !data.TryGetValue("product", out product) || !Object.Equals(product, "TortoiseSCM") ||
                            !data.TryGetValue("version", out version) || !(version is string) ||
                            !Regex.IsMatch((string)version, @"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}\z") ||
                            !data.TryGetValue("sourceCommit", out commit) || !(commit is string) ||
                            !Regex.IsMatch((string)commit, @"\A(?:[0-9a-fA-F]{40}|[0-9a-fA-F]{64}|unknown)\z"))
                            throw new InvalidDataException("安装包版本或源码提交信息无效。");
                        result.Version = (string)version;
                        result.SourceCommit = (string)commit == "unknown" ? "未知（安装包未记录源码提交）" : (string)commit;
                        result.Status = "已读取本次启动程序所在目录的安装包信息。";
                    }
                }
            }
            catch (FileNotFoundException) { result.Status = "开发构建 / 无安装包信息（未找到 package-manifest.json）。"; }
            catch (DirectoryNotFoundException) { result.Status = "开发构建 / 无安装包信息（未找到安装包目录）。"; }
            catch (Exception ex) {
                if (!(ex is IOException) && !(ex is InvalidDataException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException) &&
                    !(ex is InvalidOperationException) && !(ex is System.Security.SecurityException)) throw;
                result.Status = "安装包信息损坏或无法读取；无法确定安装包版本。";
            }
            return result;
        }

        internal string ToDisplayText()
        {
            return "TortoiseSCM\r\n\r\n安装包版本：" + Version + "\r\n源码提交：" + SourceCommit +
                "\r\n进程架构：" + (Environment.Is64BitProcess ? "x64" : "x86") +
                "\r\n\r\n当前程序：\r\n" + ExecutablePath + "\r\n\r\n所在目录：\r\n" + DirectoryPath +
                "\r\n\r\n" + Status + "\r\n\r\n以上为本次启动的程序信息，未查询远程最新版本。\r\n此窗口不能确认 Explorer 内存中已加载的扩展版本。";
        }
    }

    internal sealed class VersionInfoForm : Form
    {
        private readonly TextBox details = new TextBox();
        private readonly Button copy = DialogStyle.Button("复制信息");
        private readonly Button close = DialogStyle.Button("关闭");
        private readonly Label status = new Label();
        private Action<string> copyInformation = text => Clipboard.SetText(text);

        internal VersionInfoForm() : this(PackageVersionInfo.Read(AppDomain.CurrentDomain.BaseDirectory, Application.ExecutablePath)) { }

        internal VersionInfoForm(PackageVersionInfo information)
        {
            DialogStyle.Apply(this); Text = "版本信息 - TortoiseSCM";
            ClientSize = new Size(700, 440); MinimumSize = new Size(540, 390);
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(DialogStyle.Margin), ColumnCount = 1, RowCount = 3 };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            details.Multiline = true; details.ReadOnly = true; details.ScrollBars = ScrollBars.Both;
            details.WordWrap = false; details.Dock = DockStyle.Fill; details.Text = information.ToDisplayText();
            details.BackColor = SystemColors.Window;
            layout.Controls.Add(details, 0, 0);
            status.Dock = DockStyle.Fill; status.TextAlign = ContentAlignment.MiddleLeft; layout.Controls.Add(status, 0, 1);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            buttons.Controls.Add(close); buttons.Controls.Add(copy); layout.Controls.Add(buttons, 0, 2);
            Controls.Add(layout); AcceptButton = close; CancelButton = close; close.DialogResult = DialogResult.Cancel;
            Shown += delegate { details.Select(0, 0); ActiveControl = close; };
            close.Click += delegate { Close(); };
            copy.Click += delegate {
                try { copyInformation(details.Text); status.Text = "版本信息已复制。"; }
                catch (ExternalException) { status.Text = "剪贴板暂时不可用，请重试或手动选择并复制。"; }
            };
        }
    }
}
