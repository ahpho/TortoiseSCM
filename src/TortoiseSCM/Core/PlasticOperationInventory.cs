// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed partial class PlasticClient
    {
        // Status only reports pending changes. Operation dialogs also need the
        // clean controlled files underneath a recursive checkout scope.
        public async Task<IList<PlasticStatusItem>> GetControlledItemsAsync(string path, CancellationToken token)
        {
            var workspace = DiscoverWorkspace(path);
            if (workspace == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            string scope = Path.GetFullPath(path);
            var result = await ExecuteAsync(RevisionCommand(workspace.RootPath,
                new[] { "ls", scope, "-R", "--xml", "--encoding=utf-8" }), token).ConfigureAwait(false);
            RequireSuccess(result);
            XDocument document = SafeXml.Load(result.Output);
            if (document.Root == null || document.Root.Name != "LsResults") throw new InvalidDataException("Unexpected Plastic inventory XML.");
            var items = new List<PlasticStatusItem>();
            foreach (XElement entry in document.Descendants("LsItem"))
            {
                token.ThrowIfCancellationRequested();
                if (!String.IsNullOrEmpty((string)entry.Element("SymlinkTarget"))) continue;
                string current = (string)entry.Element("CurrentPath");
                if (String.IsNullOrWhiteSpace(current)) continue;
                string local = Path.IsPathRooted(current) ? current : Path.Combine(workspace.RootPath,
                    current.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
                local = Path.GetFullPath(local);
                if (!IsWithin(local, workspace.RootPath) || !IsWithin(local, scope)) continue;
                if (!File.Exists(local) && !Directory.Exists(local)) continue;
                var owner = DiscoverWorkspace(local);
                if (owner == null || !String.Equals(owner.RootPath, workspace.RootPath, StringComparison.OrdinalIgnoreCase)) continue;
                items.Add(new PlasticStatusItem { Path = local, StatusCode = "", Status = "", StatusDescription = "受控路径",
                    IsDirectory = Directory.Exists(local) || String.Equals((string)entry.Element("Type"), "dir", StringComparison.OrdinalIgnoreCase) });
            }
            return items.GroupBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        }
    }
}
