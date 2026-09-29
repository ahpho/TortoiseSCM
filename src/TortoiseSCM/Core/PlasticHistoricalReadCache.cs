// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        private readonly object historicalReadGate = new object();
        private readonly Dictionary<string, string> historicalReads = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Queue<string> historicalReadOrder = new Queue<string>();
        private int historicalReadCharacters;
        private readonly Dictionary<string, byte[]> historicalContent = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Queue<string> historicalContentOrder = new Queue<string>();
        private int historicalContentBytes;
        private static string HistoricalReadKey(string repository, string[] arguments)
        { return repository + "\n" + String.Join("\n", arguments.Select(value => value.Length + ":" + value)); }

        private void RememberParentComparison(string repository, long parent, long changeset, string output)
        {
            var arguments = new[] { "diff", "cs:" + parent.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@" + repository,
                "cs:" + changeset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@" + repository,
                "--repositorypaths", "--encoding=utf-8", "--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}" };
            RememberHistoricalRead(HistoricalReadKey(repository, arguments), output);
        }

        private void CacheHistoricalContent(string key, string path)
        {
            if (new System.IO.FileInfo(path).Length > 4 * 1024 * 1024) return;
            byte[] bytes = System.IO.File.ReadAllBytes(path);
            if (bytes.Length > 4 * 1024 * 1024) return;
            lock (historicalReadGate)
            {
                if (historicalContent.ContainsKey(key)) return;
                while (historicalContent.Count >= 64 || historicalContentBytes + bytes.Length > 16 * 1024 * 1024)
                {
                    string oldest = historicalContentOrder.Dequeue();
                    historicalContentBytes -= historicalContent[oldest].Length;
                    historicalContent.Remove(oldest);
                }
                historicalContent.Add(key, bytes); historicalContentOrder.Enqueue(key); historicalContentBytes += bytes.Length;
            }
        }

        // Only immutable, repository-qualified reads belong here. Never cache local
        // status, selectors, branch heads, or failed/cancelled responses. Strings
        // keep callers from mutating the authoritative cached result.
        private async Task<string> HistoricalReadAsync(string root, string repository, string[] arguments, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ValidateHistoryRepository(root, repository);
            string key = HistoricalReadKey(repository, arguments);
            string output;
            lock (historicalReadGate) if (historicalReads.TryGetValue(key, out output)) return output;
            var response = await ExecuteAsync(RevisionCommand(root, arguments), token).ConfigureAwait(false);
            RequireSuccess(response);
            token.ThrowIfCancellationRequested();
            ValidateHistoryRepository(root, repository);
            output = response.Output;
            RememberHistoricalRead(key, output);
            return output;
        }

        private void RememberHistoricalRead(string key, string output)
        {
            // Bound both entry count and memory; large commits still work uncached.
            if (output.Length <= 1024 * 1024)
                lock (historicalReadGate)
                {
                    if (historicalReads.ContainsKey(key)) return;
                    while (historicalReads.Count >= 128 || historicalReadCharacters + output.Length > 4 * 1024 * 1024)
                    {
                        string oldest = historicalReadOrder.Dequeue();
                        historicalReadCharacters -= historicalReads[oldest].Length;
                        historicalReads.Remove(oldest);
                    }
                    historicalReads.Add(key, output); historicalReadOrder.Enqueue(key);
                    historicalReadCharacters += output.Length;
                }
        }

        private Task<string> HistoryDiffAsync(string root, string repository, long changeset, CancellationToken token)
        {
            return HistoricalReadAsync(root, repository, new[] { "diff", "cs:" + changeset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "@" + repository,
                "--repositorypaths", "--encoding=utf-8", "--format={status}|{path}|{type}|{srccmpath}|{dstcmpath}|{revid}" }, token);
        }

        internal async Task<PlasticChangesetDetails> GetHistoryDetailsAsync(string path, PlasticHistoryItem entry, CancellationToken token)
        {
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            ValidateHistoryRepository(command.WorkingDirectory, entry.Repository);
            var files = await HistoryFilesCachedAsync(command.WorkingDirectory, entry.Repository, entry.Changeset, token).ConfigureAwait(false);
            ValidateHistoryRepository(command.WorkingDirectory, entry.Repository);
            return new PlasticChangesetDetails { Changeset = entry, Files = files };
        }
    }
}
