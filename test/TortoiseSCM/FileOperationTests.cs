// GPL-2.0-or-later. Isolated tests for versioned remove/move and exact ignore rules.
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using TortoiseSCM;

internal static class FileOperationTests
{
    private static int count;
    private static void Assert(bool condition, string message) { count++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { bool failed = false; try { action(); } catch (ArgumentException) { failed = true; } Assert(failed, message); }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr reserved);

    public static int Main(string[] args)
    {
        try
        {
            Unit();
            if (args.Length == 2 && args[0] == "--live") Live(args[1]);
            else if (args.Length != 0) throw new ArgumentException("Usage: --live <isolated-workspace>");
            Console.WriteLine("PASS: " + count + " file operation assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Unit()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-file-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string configuration = Path.Combine(temporary, "ignore.conf");
            File.WriteAllText(configuration, "# original\n/original\n", new UTF8Encoding(false));
            PlasticClient.AppendIgnoreRule(configuration, "/中文 file.txt", CancellationToken.None);
            Assert(File.ReadAllText(configuration) == "# original\n/original\n/中文 file.txt\n", "Ignore append preserves existing text and LF encoding");
            byte[] before = File.ReadAllBytes(configuration);
            PlasticClient.AppendIgnoreRule(configuration, "/中文 file.txt", CancellationToken.None);
            Assert(File.ReadAllBytes(configuration).SequenceEqual(before), "Duplicate rule does not rewrite file");
            File.WriteAllText(configuration, "# UTF16", Encoding.Unicode);
            PlasticClient.AppendIgnoreRule(configuration, "/another", CancellationToken.None);
            byte[] bytes = File.ReadAllBytes(configuration);
            Assert(bytes[0] == 0xff && bytes[1] == 0xfe && File.ReadAllText(configuration).Contains("/another"), "UTF16 BOM preserved");
            string shared = Path.Combine(temporary, "external.txt");
            File.WriteAllText(shared, "external must remain");
            File.Delete(configuration);
            Assert(CreateHardLink(configuration, shared, IntPtr.Zero), "Hardlink fixture created");
            Reject(() => PlasticClient.AppendIgnoreRule(configuration, "/must-not-write", CancellationToken.None), "Multiple hardlinks rejected before writing");
            Assert(File.ReadAllText(shared) == "external must remain", "External hardlink bytes unchanged");
            File.Delete(configuration);
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel(); bool rejected = false;
                try { PlasticClient.AppendIgnoreRule(configuration, "/no", canceled.Token); } catch (OperationCanceledException) { rejected = true; }
                Assert(rejected && !File.Exists(configuration), "Cancellation does not create ignore file");
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static void Live(string root)
    {
        root = Path.GetFullPath(root);
        if (!root.Contains("\\bin\\TortoiseSCM\\qa\\integration-")) throw new ArgumentException("Live writes require isolated QA workspace.");
        var client = new PlasticClient(PlasticClientConfig.Load());
        var token = CancellationToken.None;
        Assert(client.GetStatusAsync(root, token).GetAwaiter().GetResult().Count == 0, "Workspace starts clean");
        string folder = Path.Combine(root, "operations-fixture"), original = Path.Combine(folder, "original 中文.txt");
        string moved = Path.Combine(folder, "renamed 中文.txt"), ignored = Path.Combine(folder, "secret.ignore"), privateFile = Path.Combine(folder, "private.txt");
        string configuration = Path.Combine(root, "ignore.conf");
        if (!Directory.Exists(folder))
        {
            if (File.Exists(configuration)) throw new ArgumentException("Fixture expects no prior ignore.conf.");
            Directory.CreateDirectory(folder);
            File.WriteAllText(original, "controlled fixture contents");
            File.WriteAllText(configuration, "/operations-fixture/secret.ignore\r\n");
            Run(client, root, PlasticCommand.Add, new [] { folder, configuration }, true);
            Run(client, root, PlasticCommand.Checkin, new [] { folder, configuration }, true);
        }
        else if (!File.Exists(original) || File.ReadAllText(original) != "controlled fixture contents" ||
            File.ReadAllText(configuration) != "/operations-fixture/secret.ignore\r\n")
            throw new ArgumentException("Existing fixture does not match the known test contents.");
        var movedResult = client.MoveAsync(original, moved, token).GetAwaiter().GetResult();
        Assert(movedResult.Succeeded && File.Exists(moved) && !File.Exists(original), "Native move routes correctly");
        Assert(client.GetStatusAsync(root, token).GetAwaiter().GetResult().Any(x => x.StatusCode == "MV"), "Move creates pending renamed item");
        Run(client, root, PlasticCommand.Undo, new [] { moved }, false);
        Assert(File.Exists(original), "Undo restores original name");
        var removed = client.RemoveAsync(original, token).GetAwaiter().GetResult();
        Assert(removed.Succeeded && !File.Exists(original), "Native remove deletes controlled item");
        Assert(client.GetStatusAsync(root, token).GetAwaiter().GetResult().Any(x => x.StatusCode == "DE"), "Remove creates pending deletion");
        Run(client, root, PlasticCommand.Undo, new [] { original }, false);
        File.WriteAllText(privateFile, "private must remain");
        Reject(() => client.RemoveAsync(folder, token).GetAwaiter().GetResult(), "Private descendant blocks recursive removal");
        Assert(File.ReadAllText(privateFile) == "private must remain" && File.Exists(original), "Private rejection preserves all files");
        File.Delete(privateFile);
        File.WriteAllText(ignored, "ignored must remain");
        Reject(() => client.RemoveAsync(folder, token).GetAwaiter().GetResult(), "Ignored descendant blocks recursive removal");
        Reject(() => client.MoveAsync(folder, folder + "-renamed", token).GetAwaiter().GetResult(), "Ignored descendant blocks directory move");
        Assert(File.ReadAllText(ignored) == "ignored must remain" && File.Exists(original), "Ignored rejection preserves bytes");
        File.Delete(ignored);
        Reject(() => client.MoveAsync(original, configuration, token).GetAwaiter().GetResult(), "Move never replaces existing destination");
        File.WriteAllText(privateFile, "ignore target");
        var ignoreResult = client.IgnoreAsync(privateFile, token).GetAwaiter().GetResult();
        Assert(ignoreResult.Succeeded && File.ReadAllText(configuration).Contains("/operations-fixture/private.txt"), "Ignore adds exact workspace rule");
        Assert(!client.GetStatusAsync(root, token).GetAwaiter().GetResult().Any(x => x.Path == privateFile), "Ignored item leaves ordinary pending list");
        File.Delete(privateFile);
        Run(client, root, PlasticCommand.Undo, new [] { configuration }, false);
        var removedDirectory = client.RemoveAsync(folder, token).GetAwaiter().GetResult();
        Assert(removedDirectory.Succeeded && !Directory.Exists(folder), "Clean controlled directory removal succeeds");
        Run(client, root, PlasticCommand.Undo, new [] { folder }, true);
        Assert(File.ReadAllText(original) == "controlled fixture contents", "Directory undo restores controlled child");
        Assert(client.GetStatusAsync(root, token).GetAwaiter().GetResult().Count == 0, "Fixture ends clean");
    }

    private static void Run(PlasticClient client, string root, PlasticCommand command, string[] paths, bool recursive)
    {
        var result = client.RunAsync(new PlasticCommandRequest { Command = command, WorkingDirectory = root, Paths = paths,
            Recursive = recursive, Comment = "TortoiseSCM isolated file operation fixture" }, CancellationToken.None).GetAwaiter().GetResult();
        if (!result.Succeeded) throw new Exception(command + ": " + result.Error + result.Output);
    }
}
