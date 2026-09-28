// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
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
    public sealed class PlasticRepositoryEntry
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public long ItemId { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsSymbolicLink { get; set; }
        public long Size { get; set; }
    }

    public sealed class PlasticRepositoryListing
    {
        public string Repository { get; set; }
        public string RootPath { get; set; }
        public string DirectoryPath { get; set; }
        public long Changeset { get; set; }
        public IList<PlasticRepositoryEntry> Entries { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public async Task<PlasticRepositoryListing> GetRepositoryDirectoryAsync(string workspacePath,
            string repositoryDirectory, long changeset, CancellationToken cancellationToken)
        {
            ValidateChangeset(changeset);
            ValidateRepositoryDirectoryPath(repositoryDirectory);
            cancellationToken.ThrowIfCancellationRequested();
            var context = DiscoverWorkspace(workspacePath);
            if (context == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            ValidateBranchRepository(context.Repository);
            var command = await BuildReadCommandAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            if (!SamePath(command.WorkingDirectory, context.RootPath))
                throw new InvalidOperationException("The selected workspace changed during repository browsing. Refresh before continuing.");
            ValidateRepositoryBrowserContext(context);
            var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "ls", repositoryDirectory,
                "--tree=cs:" + changeset.ToString(CultureInfo.InvariantCulture) + "@" + context.Repository,
                "--xml", "--encoding=utf-8", "--symlink" }), cancellationToken).ConfigureAwait(false);
            RequireSuccess(result);
            cancellationToken.ThrowIfCancellationRequested();
            var entries = ParseRepositoryDirectory(result.Output, repositoryDirectory, context.Repository);
            ValidateRepositoryBrowserContext(context);
            return new PlasticRepositoryListing { Repository = context.Repository, RootPath = context.RootPath,
                DirectoryPath = repositoryDirectory, Changeset = changeset, Entries = entries };
        }

        private void ValidateRepositoryBrowserContext(PlasticWorkspace expected)
        {
            var current = DiscoverWorkspace(expected.RootPath);
            if (current == null || current.Repository != expected.Repository || current.IsPartial != expected.IsPartial ||
                NormalizeMergeSelector(current.Selector) != NormalizeMergeSelector(expected.Selector))
                throw new InvalidOperationException("The workspace repository or selector changed during repository browsing. Refresh before continuing.");
        }

        private static void ValidateRepositoryDirectoryPath(string path)
        {
            if (path != "/") ValidateRepositoryFilePath(path);
        }

        internal static IList<PlasticRepositoryEntry> ParseRepositoryDirectory(string xml, string directory, string repository)
        {
            ValidateRepositoryDirectoryPath(directory);
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "LsResults" || document.Root.Elements().Count() != 1 ||
                document.Root.Element("LsItems") == null)
                throw new InvalidDataException("Unexpected repository directory listing.");
            var container = document.Root.Element("LsItems");
            if (container.Elements().Any(item => item.Name != "LsItem"))
                throw new InvalidDataException("Unexpected repository directory item.");
            var entries = new List<PlasticRepositoryEntry>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var identities = new HashSet<long>();
            bool foundDirectory = false;
            foreach (var item in container.Elements("LsItem"))
            {
                string name = RepositoryListingField(item, "Name"), path = RepositoryListingField(item, "CurrentPath");
                try { ValidateRepositoryDirectoryPath(path); }
                catch (ArgumentException error) { throw new InvalidDataException("Unsafe repository directory item path.", error); }
                string itemRepository = RepositoryListingField(item, "Repository");
                if (itemRepository != repository && itemRepository != "rep:" + repository)
                    throw new InvalidDataException("Repository browsing cannot traverse cross-repository links.");
                string type = RepositoryListingField(item, "Type");
                bool isDirectory = new[] { "dir", "directory", "目录" }.Contains(type, StringComparer.OrdinalIgnoreCase);
                bool isFile = new[] { "txt", "bin", "text", "binary", "file", "Text file", "Binary file", "文本文件", "二进制文件" }.Contains(type, StringComparer.OrdinalIgnoreCase);
                bool symbolicLink = !String.IsNullOrEmpty(RepositoryListingField(item, "SymlinkTarget"));
                if (!isDirectory && !isFile)
                    throw new InvalidDataException("Unsupported repository item type: " + type);
                long id = RepositoryListingNumber(item, "ItemId"), size = RepositoryListingNumber(item, "Size");
                if (id <= 0 || !paths.Add(path) || !identities.Add(id))
                    throw new InvalidDataException("Duplicate or invalid repository directory item identity.");
                if (path == directory)
                {
                    if (name != "." || !isDirectory || symbolicLink)
                        throw new ArgumentException("Select a historical directory; files and symbolic links cannot be browsed as directories.");
                    foundDirectory = true;
                    continue;
                }
                string prefix = directory == "/" ? "/" : directory + "/";
                if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Substring(prefix.Length).IndexOf('/') >= 0 ||
                    name != path.Substring(prefix.Length))
                    throw new InvalidDataException("Repository listing contains an item outside the selected directory or an invalid name.");
                entries.Add(new PlasticRepositoryEntry { Name = name, Path = path, ItemId = id,
                    IsDirectory = isDirectory, IsSymbolicLink = symbolicLink, Size = size });
            }
            // Even an empty directory has a '.' row. No rows must never masquerade as an empty directory.
            if (!foundDirectory) throw new ArgumentException("The directory does not exist at the selected changeset.");
            return entries.OrderByDescending(item => item.IsDirectory).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string RepositoryListingField(XElement item, string name)
        {
            var fields = item.Elements(name).ToList();
            if (fields.Count != 1 || fields[0].HasElements)
                throw new InvalidDataException("Missing or duplicate repository listing field: " + name);
            return fields[0].Value;
        }

        private static long RepositoryListingNumber(XElement item, string name)
        {
            long number;
            if (!Int64.TryParse(RepositoryListingField(item, name), NumberStyles.None, CultureInfo.InvariantCulture, out number))
                throw new InvalidDataException("Invalid repository listing number: " + name);
            return number;
        }
    }
}
