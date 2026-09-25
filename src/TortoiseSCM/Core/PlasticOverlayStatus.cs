// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    // Numeric values are part of the native overlay cache wire format and
    // correspond to the icon resource index in TortoiseSCMShell.dll.
    public enum PlasticOverlayState
    {
        Normal = 1,
        Modified = 2,
        Conflict = 3,
        Added = 4,
        Deleted = 5,
        Ignored = 6,
        Locked = 7,
        Unversioned = 8
    }

    public sealed partial class PlasticClient
    {
        // Called only by the standalone cache process/CLI, never by Explorer.
        public async Task<IDictionary<string, PlasticOverlayState>> GetOverlayStatesAsync(string root, CancellationToken token)
        {
            var validated = await BuildReadCommandAsync(root, token).ConfigureAwait(false);
            if (!SamePath(validated.Arguments[1], validated.WorkingDirectory)) throw new ArgumentException("Overlay refresh requires a workspace root.");
            var workspace = await GetWorkspaceAsync(root, token).ConfigureAwait(false);
            var listing = await ExecuteAsync(RevisionCommand(root, new[] { "ls", root, "--recursive", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(listing);
            var tree = SafeXml.Load(listing.Output);
            if (tree.Root == null || tree.Root.Name != "LsResults") throw new InvalidDataException("Unexpected workspace inventory.");
            var controlled = new List<string>();
            foreach (var item in tree.Descendants("LsItem"))
            {
                token.ThrowIfCancellationRequested();
                long revision;
                if (!Int64.TryParse((string)item.Element("RevId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out revision) || revision < 0) continue;
                if (!String.IsNullOrEmpty((string)item.Element("SymlinkTarget"))) continue;
                string path = (string)item.Element("CurrentPath");
                if (String.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) throw new InvalidDataException("Inventory has no absolute workspace path.");
                path = Path.GetFullPath(path);
                if (!IsWithin(path, root)) throw new InvalidDataException("Inventory path escapes workspace.");
                // Partial workspaces may list unloaded items. Never decorate absent or nested-workspace paths.
                if (!File.Exists(path) && !Directory.Exists(path)) continue;
                try { RejectReparsePath(path); } catch (ArgumentException) { continue; }
                var owner = DiscoverWorkspace(path);
                if (owner == null || !SamePath(owner.RootPath, root)) continue;
                controlled.Add(path);
            }
            var status = await ExecuteAsync(RevisionCommand(root, new[] { "status", root, "--all", "--ignored", "--cutignored", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(status);
            var pending = ParseStatus(status.Output, root);
            var conflicts = new List<string>();
            bool nativeMerge = File.Exists(Path.Combine(root, ".plastic", "plastic.mergeprogress"));
            if (nativeMerge && !workspace.IsPartial)
            {
                try
                {
                    var session = await GetMergeSessionAsync(root, token).ConfigureAwait(false);
                    if (session == null) conflicts.Add(root);
                    else conflicts.AddRange(session.Plan.FileConflicts.Where(item => !item.Resolved).Select(item => Path.Combine(root, item.RepositoryPath.TrimStart('/').Replace('/', '\\'))));
                }
                catch (OperationCanceledException) { throw; }
                catch { conflicts.Add(root); } // Unknown/interrupted native merge must not look clean.
            }
            return BuildOverlayStates(root, controlled, pending, conflicts);
        }

        internal static IDictionary<string, PlasticOverlayState> BuildOverlayStates(string root, IEnumerable<string> controlled,
            IEnumerable<PlasticStatusItem> pending, IEnumerable<string> conflicts)
        {
            root = OverlayCanonicalPath(root);
            var result = new Dictionary<string, PlasticOverlayState>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in controlled) if (IsWithin(path, root)) result[OverlayCanonicalPath(path)] = PlasticOverlayState.Normal;
            foreach (var item in pending)
            {
                string code = (item.StatusCode ?? "").ToUpperInvariant();
                PlasticOverlayState state;
                switch (code)
                {
                    case "AD": state = PlasticOverlayState.Added; break;
                    case "DE": case "LD": state = PlasticOverlayState.Deleted; break;
                    case "IG": case "IGNORED": state = PlasticOverlayState.Ignored; break;
                    case "PR": case "PRIVATE": state = PlasticOverlayState.Unversioned; break;
                    // Plastic's checkout state is the closest local signal for
                    // a lock. Content changes are reported separately as CH;
                    // keep the distinction visible in Explorer.
                    case "CO": state = PlasticOverlayState.Locked; break;
                    case "CH": case "MV": case "LM": state = PlasticOverlayState.Modified; break;
                    default: state = PlasticOverlayState.Modified; break;
                }
                AddOverlayWithParents(result, root, item.Path, state);
                if (!String.IsNullOrEmpty(item.OldPath)) AddOverlayWithParents(result, root, item.OldPath, state);
            }
            foreach (string path in conflicts) AddOverlayWithParents(result, root, path, PlasticOverlayState.Conflict);
            return result;
        }

        private static void AddOverlayWithParents(IDictionary<string, PlasticOverlayState> states, string root, string path, PlasticOverlayState state)
        {
            if (String.IsNullOrEmpty(path) || !IsWithin(path, root)) return;
            path = OverlayCanonicalPath(path);
            while (IsWithin(path, root))
            {
                PlasticOverlayState previous;
                if (!states.TryGetValue(path, out previous) || OverlayRank(state) > OverlayRank(previous)) states[path] = state;
                if (SamePath(path, root)) break;
                path = Path.GetDirectoryName(path);
                if (path == null) break;
            }
        }

        private static string OverlayCanonicalPath(string path)
        { path = Path.GetFullPath(path); return path.Length > 3 ? path.TrimEnd('\\') : path; }

        private static int OverlayRank(PlasticOverlayState state)
        {
            switch (state)
            {
                case PlasticOverlayState.Conflict: return 100;
                case PlasticOverlayState.Added:
                case PlasticOverlayState.Deleted:
                case PlasticOverlayState.Modified: return 80;
                case PlasticOverlayState.Locked: return 60;
                case PlasticOverlayState.Ignored:
                case PlasticOverlayState.Unversioned: return 20;
                default: return 0;
            }
        }
    }
}
