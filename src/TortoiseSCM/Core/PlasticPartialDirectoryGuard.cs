// GPL-2.0-or-later. Native Partial file updates can also unload incoming directory removals.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        private async Task ValidatePartialLoadedDirectoriesAtAsync(PlasticWorkspace workspace, long changeset, CancellationToken token)
        {
            if (changeset < 0) throw new ArgumentException("Directory scope requires a pinned incoming changeset.");
            // Native Partial maintenance can also consult the current head while
            // updating an older pinned file. Both directory trees must be safe.
            var head = await ReadStructureTreeAsync(workspace, token).ConfigureAwait(false);
            await ValidatePartialLoadedDirectoriesAsync(workspace, head, token).ConfigureAwait(false);
            if (head.Changeset == changeset) return;
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", "/", "--tree=cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + workspace.Repository, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            await ValidatePartialLoadedDirectoriesAsync(workspace, new StructureTree { Changeset = changeset, Items = SafeXml.Load(result.Output).Descendants("LsItem").ToList() }, token).ConfigureAwait(false);
        }
        private async Task ValidatePartialLoadedDirectoriesAsync(PlasticWorkspace workspace, StructureTree tree, CancellationToken token)
        {
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "ls", workspace.RootPath, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            var pending = await GetStatusAsync(workspace.RootPath, token).ConfigureAwait(false);
            foreach (var loaded in SafeXml.Load(result.Output).Descendants("LsItem"))
            {
                if (!new[] { "dir", "directory", "目录" }.Contains((string)loaded.Element("Type"), StringComparer.OrdinalIgnoreCase)) continue;
                string local = (string)loaded.Element("CurrentPath"), path = StructureRepositoryPath(workspace.RootPath, local);
                RejectReparsePath(local);
                string identity = (string)loaded.Element("ItemId"); long id;
                bool hasIdentity = Int64.TryParse(identity, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
                if (id < 0 && pending.Any(item => item.IsDirectory && item.StatusCode == "AD" && SamePath(item.Path, local))) continue;
                if (!hasIdentity && String.IsNullOrWhiteSpace(identity))
                {
                    var infoResult = await ExecuteAsync(RevisionCommand(workspace.RootPath, new[] { "fileinfo", local, "--fields=Type,Status,IsUnderXlink", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false); RequireSuccess(infoResult);
                    var info = SafeXml.Load(infoResult.Output).Descendants("FileInfo").Single();
                    if ((string)info.Element("Type") == "dir" && (string)info.Element("Status") == "private" && (string)info.Element("IsUnderXlink") == "false") continue;
                }
                var incoming = tree.Items.SingleOrDefault(item => String.Equals((string)item.Element("CurrentPath"), path, StringComparison.OrdinalIgnoreCase));
                if (id <= 0 || incoming == null || (long?)incoming.Element("ItemId") != id ||
                    !new[] { "dir", "directory", "目录" }.Contains((string)incoming.Element("Type"), StringComparer.OrdinalIgnoreCase) ||
                    !String.IsNullOrEmpty((string)loaded.Element("SymlinkTarget")) || !String.IsNullOrEmpty((string)incoming.Element("SymlinkTarget")) ||
                    (string)loaded.Element("Repository") != "rep:" + workspace.Repository || (string)incoming.Element("Repository") != "rep:" + workspace.Repository)
                    throw new ArgumentException("The loaded directory '" + path + "' was removed, moved, replaced or linked at the incoming revision. Resolve that directory separately first: even an exact-file Partial update can unload incoming directory changes.");
            }
        }
    }
}
