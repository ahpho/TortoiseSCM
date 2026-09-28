// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TortoiseSCM
{
    // Core continuations run on worker threads. All dialogs belong to the UI
    // thread and their task completes only after the temporary inputs are released.
    internal sealed class WinFormsPlasticToolHost : IPlasticToolHost
    {
        private readonly Form dispatcher;

        internal WinFormsPlasticToolHost(Form dispatcher) { this.dispatcher = dispatcher; }

        internal static PlasticClient CreateClient(PlasticClientConfig config, Form owner)
        { return new PlasticClient(config) { ToolHost = new WinFormsPlasticToolHost(owner) }; }

        public Task<PlasticCommandResult> ShowDiffAsync(string basePath, string localPath, CancellationToken cancellationToken)
        {
            return ShowAsync(() => new TextDiffForm(basePath, localPath), false, cancellationToken);
        }

        public Task<PlasticCommandResult> ShowMergeAsync(string basePath, string localPath, string remotePath,
            string resultPath, CancellationToken cancellationToken)
        {
            return ShowAsync(() => new TextMergeForm(basePath, localPath, remotePath, resultPath), true, cancellationToken);
        }

        private Task<PlasticCommandResult> ShowAsync(Func<Form> create, bool merge, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<PlasticCommandResult>();
            Action show = delegate
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (dispatcher.IsDisposed) throw new ObjectDisposedException("Tool window owner");
                    PlasticCommandResult result;
                    using (var dialog = create())
                    {
                        // ActiveForm may be the conflict or historical-file dialog.
                        // Parenting to it keeps the editor above the modal workflow.
                        var owner = Form.ActiveForm ?? dispatcher;
                        // Create the handle before registering so cancellation cannot
                        // disappear in the gap before ShowDialog starts its message loop.
                        if (dialog.Handle == IntPtr.Zero) throw new InvalidOperationException("Cannot create the editor window.");
                        using (cancellationToken.Register(delegate
                        {
                            try
                            {
                                if (dialog.IsHandleCreated && !dialog.IsDisposed)
                                    dialog.BeginInvoke(new Action(dialog.Close));
                            }
                            catch (InvalidOperationException) { }
                        }))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            dialog.ShowDialog(owner);
                        }
                        cancellationToken.ThrowIfCancellationRequested();
                        bool saved = merge && ((TextMergeForm)dialog).Saved;
                        result = new PlasticCommandResult { ExitCode = 0,
                            Output = merge ? (saved ? "合并结果已保存。仍需在冲突窗口审核并确认应用。" :
                                "编辑器已关闭，未保存新的合并结果；冲突状态未改变。") : "差异查看器已关闭。" };
                    }
                    completion.TrySetResult(result);
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception error) { completion.TrySetException(error); }
            };
            try
            {
                if (dispatcher.IsDisposed || !dispatcher.IsHandleCreated)
                    throw new InvalidOperationException("The tool window owner is not available.");
                if (dispatcher.InvokeRequired) dispatcher.BeginInvoke(show); else show();
            }
            catch (Exception error) { completion.TrySetException(error); }
            return completion.Task;
        }
    }
}
