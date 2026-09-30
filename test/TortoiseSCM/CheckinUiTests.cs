// GPL-2.0-or-later. Check-in review gate and failed submission retention tests.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    internal static class CheckinUiTests
    {
        private static int assertions;
        private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static void Run(string artifacts)
        {
            assertions = 0;
            Directory.CreateDirectory(artifacts);
            string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-checkin-ui-" + Guid.NewGuid().ToString("N") + " 中文");
            Directory.CreateDirectory(Path.Combine(root, ".plastic"));
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "checkin-ui\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.selector"), "repository \"checkin@server\"");
            string executable = Assembly.GetExecutingAssembly().Location;
            var config = new PlasticClientConfig { CmPath = executable };
            var client = new PlasticClient(config);
            var initialRows = new List<PlasticStatusItem> {
                new PlasticStatusItem { Path = Path.Combine(root, "目录"), StatusCode = "CO", StatusDescription = "已签出目录", IsDirectory = true },
                new PlasticStatusItem { Path = Path.Combine(root, "目录", "a.txt"), StatusCode = "CH", StatusDescription = "已修改" },
                new PlasticStatusItem { Path = Path.Combine(root, "独立.txt"), StatusCode = "CH", StatusDescription = "已修改" }
            };
            try
            {
                PlasticCheckinPreview preview = Preview(root, initialRows);
                TestReviewDialog(preview, artifacts);
                TestSubmissionRetention(root, client, initialRows, preview, executable, artifacts);
                TestFailuresRefreshAndPendingClose(root, client, initialRows, preview);
                TestPrivateScopeAndReviewCancel(root, client, initialRows, preview);
                TestCommitLayoutAndInput(root, client, initialRows, artifacts);
                TestPartialStructuralSelection(root, client);
                TestDefaultChecksAndDoubleClick(root, client, initialRows, artifacts);
                Console.WriteLine("PASS: check-in UI (" + assertions + " assertions)");
            }
            finally { Directory.Delete(root, true); }
        }

        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wparam, IntPtr lparam);

        private static void TestPartialStructuralSelection(string root, PlasticClient client)
        {
            foreach (string code in new[] { "DE", "LD", "MV", "RM" })
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root }), false))
            {
                var workspace = client.DiscoverWorkspace(root); workspace.IsPartial = true;
                Set(form, "client", client); Set(form, "workspace", workspace); Set(form, "loaded", true);
                Set(form, "messageStore", new CommitMessageStore(Path.Combine(root, "structure-message-history")));
                var directory = new PlasticStatusItem { Path = Path.Combine(root, "structure-" + code), StatusCode = code, IsDirectory = true };
                var child = new PlasticStatusItem { Path = Path.Combine(directory.Path, "child.txt"), StatusCode = code };
                var rows = new[] { directory, child };
                var list = Field<ListView>(form, "files");
                foreach (var row in rows) list.Items.Add(new ListViewItem(row.Path) { Tag = row });
                form.Show(); Application.DoEvents();
                list.Items[0].Checked = true;
                Require(list.CheckedItems.Count == 2, "Partial structural directory cascades to its child: " + code);
                Field<TextBox>(form, "comment").Text = "QA directory operation " + code;
                int submissions = 0;
                Set(form, "reportError", new Action<string>(text => { throw new InvalidOperationException(text); }));
                Set(form, "prepareCheckin", new Func<string, IList<string>, string, string, CancellationToken, Task<PlasticCheckinPreview>>((path, selected, repository, selector, token) =>
                {
                    Require(selected.SequenceEqual(new[] { directory.Path }), "Partial structural directory scope is retained: " + code);
                    return Task.FromResult(Preview(root, rows));
                }));
                Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((preview, message, token) =>
                { submissions++; return Task.FromResult(new PlasticCommandResult { ExitCode = 0 }); }));
                Set(form, "getPending", new Func<string, CancellationToken, Task<IList<PlasticStatusItem>>>((path, token) => Task.FromResult<IList<PlasticStatusItem>>(new List<PlasticStatusItem>())));
                Await(form, "CheckinSelectionAsync", new object[] { null });
                Require(submissions == 1, "Partial structural selection dispatches once: " + code);
            }
        }

        private static void TestCommitLayoutAndInput(string root, PlasticClient client, IList<PlasticStatusItem> rows, string artifacts)
        {
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", rows[0].Path }), false))
            {
                var workspace = client.DiscoverWorkspace(root); workspace.IsPartial = true;
                Set(form, "client", client); Set(form, "workspace", workspace); Set(form, "loaded", true);
                Invoke(form, "UpdateCommitTarget");
                form.GetType().GetMethod("SetBusy", Flags).Invoke(form, new object[] { false, "" });
                form.Show(); Application.DoEvents();
                var target = Field<Label>(form, "scope");
                Require(target.Text == "Commit to: checkin@server:/目录  ·  Gluon / 部分工作区",
                    "Commit target uses selected repository path and workspace mode");
                Require(!target.Text.Contains(root) && !target.Text.Contains("范围") && !target.Text.Contains("\n"),
                    "Commit header is a single remote target line without duplicated local scope");
                var recent = Field<Button>(form, "messageLibrary");
                Require(recent.Text == "Recent messages" && recent.Dock == DockStyle.Left && recent.Left == 0,
                    "Recent messages button is named and aligned like the native commit dialog");
                var stdin = Field<CheckBox>(form, "useCheckinStdin");
                Require(!stdin.Checked && stdin.Enabled && (PlasticCheckinInputMode)form.GetType().GetMethod("SelectedCheckinInputMode", Flags).Invoke(form, null) == PlasticCheckinInputMode.Paths,
                    "Temporary check-in option defaults to explicit paths");
                stdin.Checked = true;
                Require((PlasticCheckinInputMode)form.GetType().GetMethod("SelectedCheckinInputMode", Flags).Invoke(form, null) == PlasticCheckinInputMode.StandardInput,
                    "Temporary option selects stdin input mode");
                form.GetType().GetMethod("SetBusy", Flags).Invoke(form, new object[] { true, "正在提交…" });
                Require(!stdin.Enabled, "Input method cannot change during a check-in");
                form.GetType().GetMethod("SetBusy", Flags).Invoke(form, new object[] { false, "" });
                Require(stdin.Enabled && stdin.Checked, "Completing a command restores the temporary input selection");
                Save(form, Path.Combine(artifacts, "checkin-target-and-input.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); CheckBounds(form);
                Require(stdin.Parent.ClientRectangle.Contains(stdin.Bounds), "Minimum commit layout contains temporary input option");
                Save(form, Path.Combine(artifacts, "checkin-target-and-input-minimum.png"));
                form.GetType().GetMethod("SetBusy", Flags).Invoke(form, new object[] { true, "正在提交…" });
                Require(Field<ProgressBar>(form, "progress").Parent.ClientRectangle.Contains(Field<ProgressBar>(form, "progress").Bounds),
                    "Minimum busy layout contains progress after temporary input option");
                form.GetType().GetMethod("SetBusy", Flags).Invoke(form, new object[] { false, "" });
            }
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root, "--path", rows[1].Path }), false))
            {
                Set(form, "workspace", client.DiscoverWorkspace(root)); Invoke(form, "UpdateCommitTarget");
                Require(Field<Label>(form, "scope").Text == "Commit to: checkin@server:/；checkin@server:/目录/a.txt  ·  Standard / 完整工作区",
                    "Multiple selected scopes remain identifiable as remote repository paths");
            }
        }
        private static void ClickAt(ListView list, Point point, bool twice)
        {
            var position = (IntPtr)((point.Y << 16) | (point.X & 0xffff));
            PostMessage(list.Handle, 0x0201, (IntPtr)1, position);
            PostMessage(list.Handle, 0x0202, IntPtr.Zero, position);
            if (twice)
            {
                PostMessage(list.Handle, 0x0203, (IntPtr)1, position);
                PostMessage(list.Handle, 0x0202, IntPtr.Zero, position);
            }
            Application.DoEvents();
        }

        private static void TestDefaultChecksAndDoubleClick(string root, PlasticClient client, IList<PlasticStatusItem> rows, string artifacts)
        {
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root }), false))
            {
                Set(form, "client", client); Set(form, "workspace", client.DiscoverWorkspace(root)); Set(form, "loaded", true);
                var pending = rows.ToList();
                pending.Add(new PlasticStatusItem { Path = Path.Combine(root, "untracked.txt"), StatusCode = "PR" });
                Set(form, "getPending", new Func<string, CancellationToken, Task<IList<PlasticStatusItem>>>((path, token) => Task.FromResult<IList<PlasticStatusItem>>(pending)));
                Set(form, "reportError", new Action<string>(message => { throw new Exception(message); }));
                form.Show(); Application.DoEvents();
                Await(form, "RefreshAsync", new object[0]);
                var list = Field<ListView>(form, "files");
                var showUnversioned = Field<CheckBox>(form, "showUnversioned");
                Require(list.Items.Count == rows.Count && list.CheckedItems.Count == rows.Count && !showUnversioned.Checked,
                    "Controlled pending changes start checked while unversioned files stay hidden");
                showUnversioned.Checked = true;
                PumpUntil(() => !(bool)Get(form, "busy") && list.Items.Cast<ListViewItem>().Any(item => ((PlasticStatusItem)item.Tag).StatusCode == "PR"),
                    "Show unversioned files refreshes the pending list");
                var unversioned = list.Items.Cast<ListViewItem>().Single(item => ((PlasticStatusItem)item.Tag).StatusCode == "PR");
                Require(!unversioned.Checked, "Revealed unversioned files remain unchecked");
                showUnversioned.Checked = false;
                PumpUntil(() => !(bool)Get(form, "busy") && !list.Items.Cast<ListViewItem>().Any(item => ((PlasticStatusItem)item.Tag).StatusCode == "PR"),
                    "Unversioned files can be hidden again");
                Save(form, Path.Combine(artifacts, "pending-default-checks.png"));
                list.Items[1].Checked = false;
                pending.Add(new PlasticStatusItem { Path = Path.Combine(root, "new-change.txt"), StatusCode = "CH" });
                Await(form, "RefreshAsync", new object[0]);
                Require(!list.Items[0].Checked && !list.Items[1].Checked && list.Items[2].Checked && list.Items[3].Checked,
                    "Refresh preserves explicit exclusions and checks newly discovered controlled changes");
                int activations = 0;
                list.DoubleClick += delegate { activations++; };
                Set(form, "loaded", false); // Exercise actual mouse messages without invoking SCM.
                foreach (int index in new[] { 0, 1, 2 })
                {
                    var before = list.Items.Cast<ListViewItem>().Select(item => item.Checked).ToArray();
                    Rectangle bounds = list.Items[index].GetBounds(ItemBoundsPortion.Label);
                    ClickAt(list, new Point(bounds.Left + 12, bounds.Top + bounds.Height / 2), true);
                    Require(list.Items.Cast<ListViewItem>().Select(item => item.Checked).SequenceEqual(before),
                        "Native row double-click preserves all checks, including parent/child selection: " + index);
                    Require(list.Items[index].Selected, "Double-click still highlights the comparison target: " + index);
                }
                Require(activations == 3, "Each row double-click activates comparison exactly once");
                Rectangle row = list.Items[1].Bounds;
                var checkbox = new Point(row.Left + 6, row.Top + row.Height / 2);
                Require((list.HitTest(checkbox).Location & ListViewHitTestLocations.StateImage) != 0, "Checkbox test targets the native state image");
                ClickAt(list, checkbox, false);
                Require(list.Items[1].Checked && activations == 3, "Single checkbox click still toggles selection without opening diff");
                Save(form, Path.Combine(artifacts, "pending-checks.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); CheckBounds(form);
                Save(form, Path.Combine(artifacts, "pending-checks-minimum.png"));
                form.Close();
            }
        }

        private static void TestReviewDialog(PlasticCheckinPreview preview, string artifacts)
        {
            using (var form = new CheckinReviewForm(preview, "发布说明 中文\r\n第二行", false))
            {
                form.Show(); Application.DoEvents();
                var list = Field<ListView>(form, "files");
                Require(list.Items.Count == 3 && list.Items[0].Text.Contains("目录"), "Review shows every effective file including directory descendants");
                Require(Field<TextBox>(form, "comment").ReadOnly && Field<TextBox>(form, "comment").Text.Contains("第二行"), "Review keeps the complete multiline comment read-only");
                Require(Field<Button>(form, "confirm").Enabled, "Normal review permits explicit confirmation");
                Require(Field<TextBox>(form, "scope").Text.Contains("checkin@server") && Field<TextBox>(form, "scope").Text.Contains("Standard") && Field<TextBox>(form, "scope").Text.Contains("selector"), "Review identifies repository, workspace mode and selector");
                Save(form, Path.Combine(artifacts, "checkin-review.png"));
                form.Size = form.MinimumSize; Application.DoEvents(); CheckBounds(form);
                Save(form, Path.Combine(artifacts, "checkin-review-minimum.png"));
                Invoke(form, "Confirm");
                Require(form.DialogResult == DialogResult.OK, "Normal review returns OK only after explicit confirmation");
            }
            using (var uncertain = new CheckinReviewForm(preview, "保留说明", true))
            {
                uncertain.Show(); Application.DoEvents();
                var gate = Field<CheckBox>(uncertain, "uncertainty");
                var confirm = Field<Button>(uncertain, "confirm");
                Require(gate.Visible && !confirm.Enabled && Field<Label>(uncertain, "status").Text.Contains("未确认"), "Uncertain result blocks retry until status/history acknowledgement");
                gate.Checked = true; Application.DoEvents();
                Require(confirm.Enabled, "Acknowledging status/history enables retry confirmation");
                Invoke(uncertain, "Confirm");
                Require(uncertain.DialogResult == DialogResult.OK && (bool)uncertain.AcceptedPreviousUncertainty, "Acknowledged uncertain review returns OK");
            }
        }

        private static void TestSubmissionRetention(string root, PlasticClient client, IList<PlasticStatusItem> rows,
            PlasticCheckinPreview preview, string executable, string artifacts)
        {
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root }), false))
            {
                Set(form, "messageStore", new CommitMessageStore(Path.Combine(root, "message-history")));
                Set(form, "client", client); Set(form, "workspace", client.DiscoverWorkspace(root)); Set(form, "loaded", true);
                var list = Field<ListView>(form, "files");
                foreach (PlasticStatusItem item in rows)
                    list.Items.Add(new ListViewItem(item.Path) { Tag = item, Checked = item.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase) || item.IsDirectory });
                Field<TextBox>(form, "comment").Text = "待提交说明 中文\r\n保留";
                string error = ""; int prepares = 0, submits = 0, reviews = 0; bool acknowledged = false;
                Set(form, "reportError", new Action<string>(message => error = message));
                Set(form, "prepareCheckin", new Func<string, IList<string>, string, string, CancellationToken, Task<PlasticCheckinPreview>>((path, selected, repository, selector, token) =>
                {
                    prepares++; Require(selected.SequenceEqual(new[] { rows[0].Path }), "Preparation collapses checked directory descendants"); return Task.FromResult(preview);
                }));
                Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((prepared, message, uncertain) =>
                { reviews++; acknowledged = uncertain; Require(message.Contains("保留"), "Review receives the complete original comment"); return DialogResult.OK; }));
                bool timeout = true;
                Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((prepared, message, token) =>
                {
                    submits++; return Task.FromResult(timeout ? new PlasticCommandResult { ExitCode = -1, TimedOut = true, Error = "timeout" } : new PlasticCommandResult { ExitCode = 0 });
                }));
                Set(form, "getPending", new Func<string, CancellationToken, Task<IList<PlasticStatusItem>>>((path, token) => Task.FromResult<IList<PlasticStatusItem>>(rows.ToList())));

                Await(form, "CheckinSelectionAsync", new object[] { null });
                Require(prepares == 1 && submits == 1 && reviews == 1 && !acknowledged, "Actual check-in flow prepares, reviews and dispatches once");
                Require((bool)Get(form, "submissionNeedsRefresh") && (bool)Get(form, "submissionUncertain"), "Timeout marks result uncertain and requires refresh");
                Require(Field<TextBox>(form, "comment").Text.Contains("保留") && list.CheckedItems.Count == 2, "Timeout preserves comment and checked directory scope");
                Require(error.Contains("timeout"), "Timeout exposes server error through the review host");
                Await(form, "CheckinSelectionAsync", new object[] { null });
                Require(submits == 1, "Unconfirmed result blocks immediate retry");
                Require((bool)Get(form, "submissionNeedsRefresh"), "Immediate retry remains gated");
                bool refresh = (bool)AwaitResult(form, "RefreshAsync");
                Require(refresh && !(bool)Get(form, "submissionNeedsRefresh") && (bool)Get(form, "submissionUncertain"), "Successful explicit refresh releases submission but retains uncertainty acknowledgement requirement");
                timeout = false;
                Await(form, "CheckinSelectionAsync", new object[] { null });
                Require(submits == 2 && reviews == 2 && acknowledged, "Retry passes the uncertain state to the review dialog");
                Require(!Get(form, "submissionNeedsRefresh").Equals(true) && !Get(form, "submissionUncertain").Equals(true), "Confirmed success clears both submission gates");
                Require(Field<TextBox>(form, "comment").Text.Length == 0, "Only confirmed success clears the comment");
                Save(form, Path.Combine(artifacts, "checkin-main-success.png"));
            }
        }

        private static void TestFailuresRefreshAndPendingClose(string root, PlasticClient client, IList<PlasticStatusItem> rows, PlasticCheckinPreview preview)
        {
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root }), false))
            {
                Set(form, "messageStore", new CommitMessageStore(Path.Combine(root, "message-history")));
                Set(form, "client", client); Set(form, "workspace", client.DiscoverWorkspace(root)); Set(form, "loaded", true);
                var list = Field<ListView>(form, "files");
                foreach (PlasticStatusItem item in rows)
                    list.Items.Add(new ListViewItem(item.Path) { Tag = item, Checked = item.IsDirectory || item.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase) });
                Field<TextBox>(form, "comment").Text = "异常保留说明";
                string reported = ""; int submits = 0;
                Set(form, "reportError", new Action<string>(message => reported = message));
                Set(form, "prepareCheckin", new Func<string, IList<string>, string, string, CancellationToken, Task<PlasticCheckinPreview>>((path, selected, repository, selector, token) => Task.FromResult(preview)));
                Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((prepared, message, uncertain) => DialogResult.OK));
                Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((prepared, message, token) =>
                { submits++; throw new IOException("连接断开"); }));
                Set(form, "getPending", new Func<string, CancellationToken, Task<IList<PlasticStatusItem>>>((path, token) => Task.FromResult<IList<PlasticStatusItem>>(rows.ToList())));
                Await(form, "CheckinSelectionAsync", new object[] { null });
                Require(submits == 1 && (bool)Get(form, "submissionNeedsRefresh") && (bool)Get(form, "submissionUncertain"), "Thrown submit exception enters the uncertain retry gate");
                Require(Field<TextBox>(form, "comment").Text == "异常保留说明" && list.CheckedItems.Count == 2 && reported.Contains("连接断开"), "Thrown submit exception preserves comment, checked rows and error");

                Set(form, "getPending", new Func<string, CancellationToken, Task<IList<PlasticStatusItem>>>((path, token) => { throw new IOException("状态服务不可用"); }));
                bool failedRefresh = !(bool)AwaitResult(form, "RefreshAsync");
                Require(failedRefresh && (bool)Get(form, "submissionNeedsRefresh") && Field<Label>(form, "status").Text.Contains("刷新失败"), "Failed refresh retains the submission gate and persistent failure status");
                Require(list.Items.Cast<ListViewItem>().Select(item => ((PlasticStatusItem)item.Tag).Path).SequenceEqual(rows.Select(item => item.Path)), "Failed refresh leaves the existing row order intact");

                var reordered = new List<PlasticStatusItem> { rows[2], rows[0], rows[1] };
                Set(form, "getPending", new Func<string, CancellationToken, Task<IList<PlasticStatusItem>>>((path, token) => Task.FromResult<IList<PlasticStatusItem>>(reordered)));
                Require((bool)AwaitResult(form, "RefreshAsync"), "Successful explicit refresh completes after a failed refresh");
                Require(!(bool)Get(form, "submissionNeedsRefresh") && (bool)Get(form, "submissionUncertain"), "Successful refresh releases the gate but keeps uncertainty for review");
                Require(list.Items.Cast<ListViewItem>().Select(item => ((PlasticStatusItem)item.Tag).Path).SequenceEqual(reordered.Select(item => item.Path)), "Refresh renders the newest server order");
                Require(list.CheckedItems.Cast<ListViewItem>().Select(item => ((PlasticStatusItem)item.Tag).Path).OrderBy(path => path).SequenceEqual(new[] { rows[0].Path, rows[1].Path }.OrderBy(path => path)), "Refresh reconstructs the exact checked set while leaving the sibling unchecked");
                form.Close();
            }

            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root }), false))
            {
                Set(form, "messageStore", new CommitMessageStore(Path.Combine(root, "message-history")));
                Set(form, "client", client); Set(form, "workspace", client.DiscoverWorkspace(root)); Set(form, "loaded", true);
                var list = Field<ListView>(form, "files");
                foreach (PlasticStatusItem item in rows) list.Items.Add(new ListViewItem(item.Path) { Tag = item, Checked = item.IsDirectory || item.Path.EndsWith("a.txt", StringComparison.OrdinalIgnoreCase) });
                Field<TextBox>(form, "comment").Text = "等待中的说明";
                var pending = new TaskCompletionSource<PlasticCommandResult>(); string reported = "";
                Set(form, "reportError", new Action<string>(message => reported = message));
                Set(form, "prepareCheckin", new Func<string, IList<string>, string, string, CancellationToken, Task<PlasticCheckinPreview>>((path, selected, repository, selector, token) => Task.FromResult(preview)));
                Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((prepared, message, uncertain) => DialogResult.OK));
                Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((prepared, message, token) => pending.Task));
                Set(form, "getPending", new Func<string, CancellationToken, Task<IList<PlasticStatusItem>>>((path, token) => Task.FromResult<IList<PlasticStatusItem>>(rows.ToList())));
                form.Show(); Application.DoEvents();
                Set(form, "changingChecks", true);
                list.Items[0].Checked = list.Items[1].Checked = true; list.Items[2].Checked = false;
                Set(form, "changingChecks", false);
                Field<Button>(form, "checkin").PerformClick();
                PumpUntil(() => (bool)Get(form, "busy"), "Pending check-in starts");
                Require(!Field<Button>(form, "checkin").Enabled, "Pending submit disables the main button");
                Require(list.CheckedItems.Count == 2, "Pending submit retains selected rows (count=" + list.CheckedItems.Count + ", states=" + String.Join(",", list.Items.Cast<ListViewItem>().Select(item => item.Checked ? "1" : "0").ToArray()) + ")");
                form.Close(); Application.DoEvents();
                Require(!form.IsDisposed && reported.Contains("操作正在进行"), "Closing during a pending submit is refused through the error host");
                pending.SetResult(new PlasticCommandResult { ExitCode = 7, Error = "用户取消" });
                PumpUntil(() => !(bool)Get(form, "busy"), "Pending check-in exits after submission task completion");
                Require((bool)Get(form, "submissionNeedsRefresh") && Field<TextBox>(form, "comment").Text == "等待中的说明", "Pending nonzero result still preserves review state");
                form.Close();
            }

            using (var cancel = new CheckinReviewForm(preview, "不写入", false))
            {
                cancel.Show(); Application.DoEvents();
                Field<Button>(cancel, "cancel").PerformClick();
                Require(cancel.DialogResult == DialogResult.Cancel, "Review cancellation returns Cancel without a submission");
            }
        }

        private static PlasticCheckinPreview Preview(string root, IList<PlasticStatusItem> rows)
        {
            return new PlasticCheckinPreview(root, "checkin@server", "repository \"checkin@server\"", "checkin-ui", false,
                rows.Select(item => item.Path).ToArray(), rows, new List<PlasticLockItem> {
                    new PlasticLockItem { Path = rows[1].Path, Owner = "other-user", Workspace = "other-workspace", HolderBranch = "/main" }
                }, "检测到相关锁；最终权限由 Plastic 服务器决定。", "test-fingerprint", 0);
        }

        private static void TestPrivateScopeAndReviewCancel(string root, PlasticClient client, IList<PlasticStatusItem> rows, PlasticCheckinPreview preview)
        {
            using (var form = new MainForm(LaunchRequest.Parse(new[] { "--path", root }), false))
            {
                Set(form, "messageStore", new CommitMessageStore(Path.Combine(root, "message-history")));
                Set(form, "client", client); Set(form, "workspace", client.DiscoverWorkspace(root)); Set(form, "loaded", true);
                var list = Field<ListView>(form, "files");
                Set(form, "changingChecks", true);
                list.Items.Add(new ListViewItem("directory") { Tag = rows[0], Checked = true });
                list.Items.Add(new ListViewItem("controlled") { Tag = rows[1], Checked = true });
                list.Items.Add(new ListViewItem("private child") { Tag = new PlasticStatusItem { Path = Path.Combine(rows[0].Path, "private.txt"), StatusCode = "PR" }, Checked = true });
                Set(form, "changingChecks", false);
                Field<TextBox>(form, "comment").Text = "检查递归目录";
                int prepares = 0, submits = 0, adds = 0; string error = "";
                Set(form, "reportError", new Action<string>(text => error = text));
                Set(form, "runCommand", new Func<PlasticCommandRequest, CancellationToken, Task<PlasticCommandResult>>((request, token) =>
                {
                    adds++;
                    Require(request.Command == PlasticCommand.Add && request.Paths.Count == 1 &&
                        request.Paths[0].EndsWith("private.txt", StringComparison.OrdinalIgnoreCase), "Checked private file is added before check-in");
                    return Task.FromResult(new PlasticCommandResult());
                }));
                Set(form, "prepareCheckin", new Func<string, IList<string>, string, string, CancellationToken, Task<PlasticCheckinPreview>>((path, paths, repo, selector, token) =>
                {
                    prepares++;
                    Require(prepares == 1
                        ? paths.SequenceEqual(new[] { rows[0].Path })
                        : paths.Count == 1 && paths[0].EndsWith("private.txt", StringComparison.OrdinalIgnoreCase),
                        prepares == 1 ? "Checked private descendants are excluded from explicit check-in while controlled directory scope is retained" :
                        "The added private file is passed to check-in preparation");
                    return Task.FromResult(preview);
                }));
                Set(form, "submitCheckin", new Func<PlasticCheckinPreview, string, CancellationToken, Task<PlasticCommandResult>>((item, message, token) => { submits++; return Task.FromResult(new PlasticCommandResult()); }));
                Set(form, "reviewCheckin", new Func<PlasticCheckinPreview, string, bool, DialogResult>((item, message, uncertain) => DialogResult.Cancel));
                Await(form, "CheckinSelectionAsync", new object[] { null });
                Require(prepares == 1 && submits == 0 && !(bool)Get(form, "submissionNeedsRefresh"), "Cancel at reviewed scope dispatches no write and does not introduce a failure gate");
                Require(Field<TextBox>(form, "comment").Text == "检查递归目录" && list.CheckedItems.Count == 3, "Review cancellation preserves all original user checks and comment");
                Set(form, "changingChecks", true);
                list.Items[0].Checked = list.Items[1].Checked = false;
                Set(form, "changingChecks", false);
                Await(form, "CheckinSelectionAsync", new object[] { null });
                Require(prepares == 2 && adds == 1 && submits == 0 && error.Length == 0,
                    "Standalone checked private file is added and then reviewed for check-in");
            }
        }

        private static object Get(object target, string name) { return target.GetType().GetField(name, Flags).GetValue(target); }
        private static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
        private static T Field<T>(object target, string name) { return (T)Get(target, name); }
        private static void Invoke(object target, string method) { target.GetType().GetMethod(method, Flags).Invoke(target, null); }
        private static void Await(object target, string method, object[] args)
        { ((Task)target.GetType().GetMethod(method, Flags).Invoke(target, args)).GetAwaiter().GetResult(); }
        private static object AwaitResult(object target, string method)
        { var task = (Task)target.GetType().GetMethod(method, Flags).Invoke(target, null); task.GetAwaiter().GetResult(); return task.GetType().GetProperty("Result").GetValue(task, null); }
        private static void Save(Form form, string path)
        { using (var image = new Bitmap(form.Width, form.Height)) { form.DrawToBitmap(image, new Rectangle(Point.Empty, form.Size)); image.Save(path, ImageFormat.Png); } }
        private static void CheckBounds(Form form)
        {
            foreach (Control control in Descendants(form).Where(item => item.Visible && (item is Button || item is ListView || item is TextBox || item is CheckBox)))
                Require(form.RectangleToScreen(form.ClientRectangle).Contains(control.RectangleToScreen(control.ClientRectangle)), "Review minimum layout contains " + control.Name);
        }
        private static IEnumerable<Control> Descendants(Control control)
        { foreach (Control child in control.Controls) { yield return child; foreach (Control nested in Descendants(child)) yield return nested; } }
        private static void Require(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
        private static void PumpUntil(Func<bool> condition, string message)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
            Require(condition(), message);
        }
    }
}
