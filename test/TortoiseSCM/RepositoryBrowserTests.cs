// GPL-2.0-or-later. Fixed snapshot repository browser safety tests.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class RepositoryBrowserTests
{
    private const string Repository = "test@server:8087";
    private static int assertions;
    private static string root;
    private static int Main(string[] args)
    {
        if (args.Length > 0) return FakeCm(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-browser-test-" + Guid.NewGuid().ToString("N"));
        string metadata = Path.Combine(root, ".plastic");
        Directory.CreateDirectory(metadata);
        try
        {
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "browser-fixture\nguid\nStandard\n");
            File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"" + Repository + "\"\n path \"/\"\n  smartbranch \"/main\"\n");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(5) });
            var listing = client.GetRepositoryDirectoryAsync(root, "/", 17, CancellationToken.None).GetAwaiter().GetResult();
            Check(listing.Changeset == 17 && listing.Repository == Repository && listing.RootPath == root && listing.DirectoryPath == "/", "Snapshot identity retained");
            Check(listing.Entries.Count == 3 && listing.Entries[0].IsDirectory && listing.Entries[0].Name == "folder", "Directory first, self excluded");
            Check(listing.Entries.Single(item => item.Name == "中文 & space.txt").Size == 42, "XML Unicode and size retained");
            Check(listing.Entries.Single(item => item.Name == "link").IsSymbolicLink, "Symlink marked without traversing");
            string call = File.ReadAllText(Path.Combine(metadata, "call"));
            Check(call == "ls\t/\t--tree=cs:17@" + Repository + "\t--xml\t--encoding=utf-8\t--symlink", "Exact pinned nonrecursive read-only command");
            Check(PlasticClient.ParseRepositoryDirectory(Wrap(Item(".", "/", "目录", 3)), "/", Repository).Count == 0, "Empty directory remains distinguishable");
            Check(PlasticClient.ParseRepositoryDirectory(Wrap(Item(".", "/folder", "dir", 3), Item("child", "/folder/child", "txt", 4)), "/folder", Repository).Count == 1, "Nested direct child accepted");
            foreach (string invalid in new[] { "", "relative", "/../escape", "/x/*", "/x//y", "/x/", "/x@repo", "/x#cs:1", "/.plastic", "/a\\b" })
                Reject<ArgumentException>(() => client.GetRepositoryDirectoryAsync(root, invalid, 1, CancellationToken.None).GetAwaiter().GetResult(), "Invalid input path refused");
            Reject<ArgumentOutOfRangeException>(() => client.GetRepositoryDirectoryAsync(root, "/", -1, CancellationToken.None).GetAwaiter().GetResult(), "Negative changeset refused");
            Reject<OperationCanceledException>(() => client.GetRepositoryDirectoryAsync(root, "/", 1, new CancellationToken(true)).GetAwaiter().GetResult(), "Canceled request refused");
            Reject<ArgumentException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(), "/", Repository), "Missing self is not empty directory");
            Reject<ArgumentException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(Item("file", "/file", "txt", 3)), "/file", Repository), "File cannot be opened as directory");
            Reject<ArgumentException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(Item(".", "/", "dir", 3, "target")), "/", Repository), "Symlink self refused");
            var self = Item(".", "/", "dir", 3);
            foreach (var invalid in new[] {
                Item("x", "/x/y", "txt", 4), Item("wrong", "/x", "txt", 4), Item("..", "/..", "dir", 4),
                Item("x", "/x", "xlink", 4), Item("x", "/x", "txt", 0),
                Item("x", "/x", "txt", 3), Item(".", "/", "dir", 4) })
                Reject<InvalidDataException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(self, invalid), "/", Repository), "Malformed child rejected");
            var foreign = Item("x", "/x", "txt", 4); foreign.Element("Repository").Value = "rep:other@server:8087";
            Reject<InvalidDataException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(self, foreign), "/", Repository), "Foreign repository rejected");
            var badSize = Item("x", "/x", "txt", 4); badSize.Element("Size").Value = "-1";
            Reject<InvalidDataException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(self, badSize), "/", Repository), "Negative size rejected");
            var missing = Item("x", "/x", "txt", 4); missing.Element("SymlinkTarget").Remove();
            Reject<InvalidDataException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(self, missing), "/", Repository), "Missing field rejected");
            var duplicate = Item("x", "/x", "txt", 4); duplicate.Add(new XElement("ItemId", "9"));
            Reject<InvalidDataException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(self, duplicate), "/", Repository), "Duplicate field rejected");
            Reject<InvalidDataException>(() => PlasticClient.ParseRepositoryDirectory(Wrap(self, Item("x", "/x", "txt", 4), Item("X", "/X", "txt", 5)), "/", Repository), "Case-ambiguous duplicate path rejected");
            Reject<InvalidDataException>(() => PlasticClient.ParseRepositoryDirectory("<wrong/>", "/", Repository), "Wrong XML root rejected");
            File.WriteAllText(Path.Combine(metadata, "mode"), "fail");
            Reject<PlasticCommandException>(() => client.GetRepositoryDirectoryAsync(root, "/", 17, CancellationToken.None).GetAwaiter().GetResult(), "Native failure not shown as empty directory");
            File.WriteAllText(Path.Combine(metadata, "mode"), "switch");
            Reject<InvalidOperationException>(() => client.GetRepositoryDirectoryAsync(root, "/", 17, CancellationToken.None).GetAwaiter().GetResult(), "Selector change during native query invalidates result");
            Console.WriteLine("PASS: " + assertions + " repository browser assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static XElement Item(string name, string path, string type, long id, string link = "")
    {
        return new XElement("LsItem", new XElement("Name", name), new XElement("CurrentPath", path),
            new XElement("Type", type), new XElement("ItemId", id), new XElement("Size", 42),
            new XElement("Repository", "rep:" + Repository), new XElement("SymlinkTarget", link));
    }
    private static string Wrap(params XElement[] items) { return new XElement("LsResults", new XElement("LsItems", items)).ToString(); }
    private static int FakeCm(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        File.WriteAllText(Path.Combine(metadata, "call"), String.Join("\t", args));
        if (args[0] != "ls") return 90;
        string mode = File.Exists(Path.Combine(metadata, "mode")) ? File.ReadAllText(Path.Combine(metadata, "mode")) : "";
        if (mode == "fail") { Console.Write(Wrap()); return 7; }
        if (mode == "switch") File.AppendAllText(Path.Combine(metadata, "plastic.selector"), "\n changeset \"18\"\n");
        Console.Write(Wrap(Item(".", "/", "目录", 3), Item("中文 & space.txt", "/中文 & space.txt", "文本文件", 4),
            Item("folder", "/folder", "目录", 5), Item("link", "/link", "txt", 6, "target")));
        return 0;
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
}
