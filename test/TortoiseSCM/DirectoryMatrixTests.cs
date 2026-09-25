// GPL-2.0-or-later. Run only in isolated New-DirectoryMatrixFixture workspaces.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using TortoiseSCM;

internal static class DirectoryMatrixTests
{
    private static int assertions;
    private static readonly CancellationToken Token = CancellationToken.None;
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 3 || !new[] { "--matrix", "--plain", "--verify" }.Contains(args[0])) throw new ArgumentException("Select an explicit directory test mode.");
            string root = Path.GetFullPath(args[1]);
            ValidateFixture(root);
            if (args[0] == "--verify") { Verify(root, args[2]); return 0; }
            long source = Int64.Parse(args[2]); string choice = args[3];
            var client = new PlasticClient(new PlasticClientConfig { SettingsPath = Path.Combine(Path.GetDirectoryName(root), "directory-matrix-" + choice, "settings.xml") });
            if (args[0] == "--plain")
            {
                foreach (string name in new[] { "user-private.txt", "user.tscm-ignored" })
                {
                    string file = Path.Combine(root, "plain-delete", "nested", name);
                    File.WriteAllText(file, "preserved user bytes");
                    bool rejected = false;
                    try { client.BeginMergeAsync(root, source, Token).GetAwaiter().GetResult(); }
                    catch (ArgumentException) { rejected = true; }
                    Check(rejected && File.ReadAllText(file) == "preserved user bytes", "Private/ignored descendant prevents automatic directory deletion: " + name);
                    File.Delete(file);
                }
                var plain = client.BeginMergeAsync(root, source, Token).GetAwaiter().GetResult();
                Check(!plain.AwaitingDirectoryResolution && plain.Plan.DirectoryConflicts.Count == 0, "Conflict-free native directory move/delete applies");
                Verify(root, "src");
                Check(Commit(client, root, choice).Succeeded, "Explicit conflict-free merge checkin");
                Console.WriteLine("PASS: " + assertions + " conflict-free directory assertions"); return 0;
            }
            var before = Snapshot(root);
            var session = client.BeginMergeAsync(root, source, Token).GetAwaiter().GetResult();
            Check(session.AwaitingDirectoryResolution, "Native directory plan started");
            foreach (string kind in new[] { "MV_RM", "RM_MV", "MV_EVIL", "ADD_MV", "MV_ADD" })
                Check(session.Plan.DirectoryConflicts.Any(item => item.Kind == kind && item.IsDirectory), "Real directory type found: " + kind);
            int count = 0;
            while (session.Plan.DirectoryConflicts.Any(item => !item.Resolved))
            {
                if (++count > 20) throw new Exception("Too many dependent conflicts");
                var next = session.Plan.DirectoryConflicts.First(item => !item.Resolved);
                session = client.ResolveDirectoryConflictAsync(root, source, next.Index, choice, null, Token).GetAwaiter().GetResult();
                Check(Snapshot(root) == before, "Native choice preserves directory descendants before apply: " + next.Kind);
            }
            session = client.ContinueMergeAsync(root, source, Token).GetAwaiter().GetResult();
            Check(!session.AwaitingDirectoryResolution && session.Plan.FileConflicts.All(item => item.Resolved), "All native structural choices applied");
            Verify(root, choice);
            var committed = Commit(client, root, choice);
            Check(committed.Succeeded, "Explicit native merge checkin");
            Check(client.GetStatusAsync(root, Token).GetAwaiter().GetResult().Count == 0, "Clean after explicit checkin");
            Console.WriteLine("PASS: " + assertions + " directory matrix assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void ValidateFixture(string root)
    {
        string qa = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        string run = Path.GetDirectoryName(root), id = Path.GetFileName(run);
        if (!String.Equals(Path.GetDirectoryName(run), qa, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(id, "^integration-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$") ||
            !new[] { "producer", "consumer", "plain", "verifier" }.Contains(Path.GetFileName(root))) throw new ArgumentException("Only this checkout's isolated directory fixtures are accepted.");
        for (string path = root; !String.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Fixture paths cannot be redirected.");
        string selector = File.ReadAllText(Path.Combine(root, ".plastic", "plastic.selector"));
        if (!Regex.IsMatch(selector, "(?m)^\\s*smartbranch\\s+\"/main/tortoisescm-autotest-" + Regex.Escape(id) + "(?:/[^\"\\r\\n]+)?\"\\s*$"))
            throw new ArgumentException("The directory fixture selector must identify its isolated branch.");
    }
    private static void Verify(string root, string choice)
    {
        var expected = new Dictionary<string, string> { { "plain-moved", "plain-move" } };
        if (choice == "src")
        {
            expected.Add("md-source", "md"); expected.Add("am-target", "source add"); expected.Add("am-old", "am-old");
            expected.Add("ma-target", "ma-old"); expected.Add("twins-target", "twins-a"); expected.Add("twins-b", "twins-b");
        }
        else
        {
            expected.Add("dm-destination", "dm"); expected.Add("am-target", "am-old"); expected.Add("ma-old", "ma-old");
            expected.Add("ma-target", "destination add"); expected.Add("twins-target", "twins-b"); expected.Add("twins-a", "twins-a");
        }
        foreach (var entry in expected)
            Check(File.ReadAllText(Path.Combine(root, entry.Key, "nested", "child.txt")) == entry.Value, "Descendant bytes: " + entry.Key);
        var actual = Directory.GetDirectories(root).Select(Path.GetFileName).Where(name => name != ".plastic").OrderBy(name => name).ToArray();
        Check(actual.SequenceEqual(expected.Keys.OrderBy(name => name)), "Exact final directory set: " + String.Join(",", actual));
        Check(Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + ".plastic" + Path.DirectorySeparatorChar) && Path.GetFileName(path) != "ignore.conf").Count() == expected.Count,
            "Exact descendant file count");
        Console.WriteLine("Verified full directory tree: " + choice);
    }
    private static PlasticCommandResult Commit(PlasticClient client, string root, string choice)
    { return client.RunAsync(new PlasticCommandRequest { Command = PlasticCommand.Checkin, WorkingDirectory = root, Paths = new List<string> { root }, Comment = "verify directory matrix " + choice }, Token).GetAwaiter().GetResult(); }
    private static string Snapshot(string root)
    {
        return String.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories).Where(path => !path.Contains(Path.DirectorySeparatorChar + ".plastic" + Path.DirectorySeparatorChar))
            .OrderBy(path => path).Select(path => path.Substring(root.Length) + "|" + Convert.ToBase64String(File.ReadAllBytes(path))));
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); assertions++; }
}
