// GPL-2.0-or-later. Real server tests through the public executable, not Core calls.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

internal static class CliIntegrationTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
    private static readonly List<object> Events = new List<object>();
    private static string executable;
    private static string runDirectory;
    private static int assertions;

    private static void Assert(bool condition, string message)
    {
        assertions++;
        Events.Add(new { assertion = message, success = condition });
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS: " + message);
    }

    public static int Main(string[] args)
    {
        try
        {
            executable = Path.GetFullPath(args[0]);
            var manifest = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(args[1], Encoding.UTF8));
            runDirectory = (string)manifest["runDirectory"];
            string producer = (string)manifest["producer"], consumer = (string)manifest["consumer"], partial = (string)manifest["partial"];
            // Only use the dedicated fixtures created by New-TestWorkspace.ps1.
            string branch = (string)manifest["branch"];
            Assert(branch.StartsWith("/main/tortoisescm-autotest-", StringComparison.Ordinal), "Dedicated test branch manifest");
            foreach (var path in new[] { producer, consumer, partial })
            {
                Assert(Path.GetFullPath(path).StartsWith(Path.GetFullPath(runDirectory).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase), "Fixture path remains in this run");
                string selector = File.ReadAllText(Path.Combine(path, ".plastic", "plastic.selector"));
                Assert(selector.Contains(branch), "Fixture selector targets dedicated branch");
                Call("workspace", path);
            }

            string firstName = "中文 & 空格.txt", secondName = "unselected.txt";
            string first = Path.Combine(producer, firstName), second = Path.Combine(producer, secondName);
            if (args.Length > 2 && args[2] == "--revisions-only")
            {
                RevisionAndScopeScenarios(producer, consumer, partial, first, second);
                Save(true, null); Console.WriteLine("PASS: " + assertions + " live revision assertions"); return 0;
            }
            string consumerFirst = Path.Combine(consumer, firstName), consumerSecond = Path.Combine(consumer, secondName);
            const string original = "first line\r\n中文 base\r\nlast line\r\n";
            Create(first, original); Create(second, "second base\r\n");
            Assert(Status(producer).Contains("PR"), "Private files detected through public CLI");
            Call("add", new[] { first, second }, "--yes");
            Assert(Status(producer).Contains("AD"), "Add produces controlled pending entries");
            string firstComment = "TortoiseSCM CLI initial 中文 \"quoted\" & | commit";
            Call("checkin", new[] { first }, "--yes", "--comment", firstComment);
            Assert(Status(second).Contains("AD"), "Unselected added file stays pending after selected checkin");
            Call("update", consumer, "--yes");
            Assert(File.ReadAllText(consumerFirst, Encoding.UTF8) == original, "Second workspace receives checked-in bytes");
            Assert(!File.Exists(consumerSecond), "Unselected file was not committed to server");
            Assert(Json.Serialize(Call("history", first)).Contains("initial"), "History returns submitted comment");

            File.WriteAllText(first, "first line\r\n中文 edited\r\nlast line\r\n", new UTF8Encoding(false));
            Assert(Status(first).Contains("CH"), "Local edited file detected");
            string diff = Json.Serialize(Call("diff", first));
            Assert(diff.Contains("base") && diff.Contains("edited"), "Headless diff contains baseline and local edit");
            Call("checkout", first, "--yes");
            Assert(Status(first).Length > 2, "Checkout reports pending state");
            Call("undo", first, "--yes");
            Assert(File.ReadAllText(first, Encoding.UTF8) == original, "Undo restores exact checked-in content");
            Assert(Status(second).Contains("AD"), "Undo of one file preserves unrelated pending addition");
            Call("checkin", second, "--yes", "--comment", "TortoiseSCM CLI second file");
            Call("update", consumer, "--yes");
            Assert(File.ReadAllText(consumerSecond, Encoding.UTF8) == "second base\r\n", "Second explicit checkin arrives independently");

            const string newFirst = "first line\n中文 revision two\nlast line\n";
            const string newSecond = "second revision two\n";
            File.WriteAllText(first, newFirst, new UTF8Encoding(false));
            File.WriteAllText(second, newSecond, new UTF8Encoding(false));
            string commentFile = Path.Combine(runDirectory, "multiline-comment.txt");
            File.WriteAllText(commentFile, "TortoiseSCM CLI multi-file\n第二行 \"quote\" & |", new UTF8Encoding(false));
            Call("checkin", new[] { first, second }, "--yes", "--commentsfile", commentFile);
            Invoke("update", new[] { consumerFirst, consumerSecond }, 2, "--yes");
            Assert(File.ReadAllText(consumerFirst, Encoding.UTF8) == original && File.ReadAllText(consumerSecond, Encoding.UTF8) == "second base\r\n", "Standard scoped update rejected without changing either file");
            Call("update", consumer, "--yes");
            Assert(File.ReadAllText(consumerFirst, Encoding.UTF8) == newFirst && File.ReadAllText(consumerSecond, Encoding.UTF8) == newSecond, "Explicit whole-workspace update loads both exact file contents");
            Assert(Json.Serialize(Call("history", first)).Contains("第二行"), "Multiline UTF-8 comment survives server roundtrip");

            string partialFile = Path.Combine(partial, "partial 中文.txt");
            Create(partialFile, "partial original\n");
            var partialData = (Dictionary<string, object>)Call("workspace", partial)["data"];
            var partialWorkspace = (Dictionary<string, object>)partialData["workspace"];
            Assert((bool)partialWorkspace["isPartial"], "Partial workspace is reported");
            Call("add", partialFile, "--yes");
            Call("checkin", partialFile, "--yes", "--comment", "TortoiseSCM CLI partial workspace");
            File.WriteAllText(partialFile, "partial local edit\n", new UTF8Encoding(false));
            Call("checkout", partialFile, "--yes");
            Call("undo", partialFile, "--yes");
            Assert(File.ReadAllText(partialFile, Encoding.UTF8) == "partial original\n", "Partial workspace checkout and undo roundtrip");
            Call("update", consumer, "--yes");
            Assert(File.ReadAllText(Path.Combine(consumer, "partial 中文.txt"), Encoding.UTF8) == "partial original\n", "Standard workspace receives partial checkin");
            File.WriteAllText(Path.Combine(consumer, "partial 中文.txt"), "partial server revision two\n", new UTF8Encoding(false));
            Call("checkin", Path.Combine(consumer, "partial 中文.txt"), "--yes", "--comment", "TortoiseSCM CLI partial scoped update");
            Call("update", partialFile, "--yes");
            Assert(File.ReadAllText(partialFile, Encoding.UTF8) == "partial server revision two\n", "Partial workspace supports exact file-scoped update");
            Call("update", partial, "--yes");
            Assert(!Status(producer).Contains("AD"), "Producer has no leftover pending additions");
            Assert(!Status(partial).Contains("CH"), "Partial fixture has no leftover local changes");
            RevisionAndScopeScenarios(producer, consumer, partial, first, second);
            Console.WriteLine("PASS: " + assertions + " live CLI integration assertions");
            Save(true, null);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            Save(false, ex.ToString());
            return 1;
        }
    }

    private static void Create(string path, string content)
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        using (var writer = new StreamWriter(file, new UTF8Encoding(false))) writer.Write(content);
    }

    private static string Status(string path)
    { return Json.Serialize(Call("status", path)["data"]); }

    private static IList GetChangesets(string path)
    {
        var data = (Dictionary<string, object>)Call("history", path)["data"];
        return (IList)data["entries"];
    }

    private static long ChangesetNumber(object entry)
    { return Convert.ToInt64(((Dictionary<string, object>)entry)["changeset"], System.Globalization.CultureInfo.InvariantCulture); }

    private static long LatestChangeset(string path)
    {
        var entries = GetChangesets(path);
        if (entries.Count == 0) throw new InvalidOperationException("No changesets returned for " + path);
        long latest = 0;
        foreach (object entry in entries) latest = Math.Max(latest, ChangesetNumber(entry));
        return latest;
    }

    private static Dictionary<string, object> ChangesetDetails(string path, long changeset)
    { return Call("changeset", path, "--changeset", changeset.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

    private static void RevisionAndScopeScenarios(string producer, string consumer, string partial, string first, string second)
    {
        Call("update", producer, "--yes");
        long firstRevision = LatestChangeset(first);
        var firstHistory = GetChangesets(first);
        Assert(firstHistory.Count >= 2, "File history contains multiple changesets");
        long firstOlder = firstHistory.Cast<object>().Select(entry => ChangesetNumber(entry)).Min();
        Assert(firstOlder < firstRevision, "History exposes both older and current revision targets");

        // Rollback creates a local pending change; undo removes exactly that change.
        Call("rollback", first, "--changeset", firstOlder.ToString(), "--yes");
        string firstCurrent = "first line\n中文 revision two\nlast line\n";
        Assert(File.ReadAllText(first, Encoding.UTF8) == "first line\r\n中文 base\r\nlast line\r\n", "File rollback restores historical bytes");
        Assert(HasPending(first), "File rollback leaves pending content");
        Call("undo", first, "--yes");
        Assert(!HasPending(first) && File.ReadAllText(first, Encoding.UTF8) == firstCurrent, "Undo clears file rollback and restores latest bytes");

        // A directory rollback is scoped to its descendants and does not touch an outside pending file.
        string scope = Path.Combine(producer, "rollback-scope"), child = Path.Combine(scope, "child 中文.txt"), outside = Path.Combine(producer, "rollback-outside.txt");
        Directory.CreateDirectory(scope); Create(child, "scope v1\n");
        Call("add", child, "--yes"); Call("checkin", child, "--yes", "--comment", "directory rollback baseline");
        Create(outside, "outside pending\n");
        Call("add", outside, "--yes");
        long scopeRevision = LatestChangeset(child);
        File.WriteAllText(child, "scope v2\n", new UTF8Encoding(false));
        Call("checkin", child, "--yes", "--comment", "directory rollback second");
        long scopeNewRevision = LatestChangeset(child);
        Assert(scopeNewRevision > scopeRevision, "Directory child receives a second revision");
        // Keep outside pending while making the selected directory clean for rollback.
        Call("rollback", scope, "--changeset", scopeRevision.ToString(), "--yes");
        Assert(HasPending(child) && File.ReadAllText(child, Encoding.UTF8) == "scope v1\n", "Directory rollback restores historical child bytes as pending");
        Assert(Status(outside).Contains("AD"), "Rollback preserves pending file outside selected scope");
        Call("undo", scope, "--yes", "--recursive"); Call("undo", outside, "--yes");
        if (File.Exists(outside)) File.Delete(outside);

        // Directory history must see content-only edits below the directory; details expose the changed file.
        File.WriteAllText(child, "scope v3\n", new UTF8Encoding(false));
        Call("checkin", child, "--yes", "--comment", "directory content-only edit");
        var directoryHistory = GetChangesets(scope);
        Assert(directoryHistory.Count >= 3, "Directory history includes descendant content-only commits");
        long directoryRevision = ChangesetNumber(directoryHistory[0]);
        var details = ChangesetDetails(scope, directoryRevision);
        var files = (IList)((Dictionary<string, object>)details["data"])["files"];
        Assert(files.Count > 0 && ((Dictionary<string, object>)files[0])["path"].ToString().Contains("rollback-scope"), "Changeset details identify descendant file");

        // Directory checkin includes only selected descendants, preserving an external pending addition.
        string selected = Path.Combine(producer, "selected-dir"), selectedChild = Path.Combine(selected, "selected.txt"), external = Path.Combine(producer, "external-pending.txt");
        Directory.CreateDirectory(selected); Create(selectedChild, "selected\n"); Create(external, "external\n");
        Call("add", new[] { selectedChild, external }, "--yes");
        var scopedEntries = (IList)((Dictionary<string, object>)Call("status", selected)["data"])["entries"];
        Assert(scopedEntries.Count > 0 && scopedEntries.Cast<Dictionary<string, object>>().All(entry => entry["path"].ToString().Equals(selected, StringComparison.OrdinalIgnoreCase) ||
            entry["path"].ToString().StartsWith(selected + "\\", StringComparison.OrdinalIgnoreCase)), "Directory status excludes pending entries outside its selected scope");
        Call("checkin", selected, "--yes", "--comment", "directory scoped checkin");
        Assert(Status(external).Contains("AD"), "Directory checkin preserves external pending addition");
        Assert(!Status(selected).Contains("AD"), "Directory checkin includes selected descendants");
        Call("undo", external, "--yes");
        if (File.Exists(external)) File.Delete(external);

        // Root switch to an older snapshot, then update the explicit root to the latest snapshot.
        long rootLatest = LatestChangeset(producer);
        long rootOlder = firstOlder;
        if (rootOlder < rootLatest)
        {
            Call("switch", producer, "--changeset", rootOlder.ToString(), "--yes");
            Assert(!HasPending(producer) && File.ReadAllText(first, Encoding.UTF8) == "first line\r\n中文 base\r\nlast line\r\n" && !File.Exists(selectedChild), "Root switch loads exact historical snapshot cleanly");
            Call("update", producer, "--yes");
            Assert(!HasPending(producer) && File.ReadAllText(child, Encoding.UTF8) == "scope v3\n" && File.ReadAllText(selectedChild, Encoding.UTF8) == "selected\n", "Explicit root update restores latest snapshot bytes");
        }

        // Partial switch uses its loaded scope and can then be updated back to the latest revision.
        var partialHistory = GetChangesets(Path.Combine(partial, "partial 中文.txt"));
        if (partialHistory.Count >= 2)
        {
            long partialLatest = partialHistory.Cast<object>().Select(entry => ChangesetNumber(entry)).Max();
            long partialOlder = partialHistory.Cast<object>().Select(entry => ChangesetNumber(entry)).Min();
            if (partialOlder < partialLatest)
            {
                Call("switch", partial, "--changeset", partialOlder.ToString(), "--yes");
                Assert(!HasPending(partial) && File.ReadAllText(Path.Combine(partial, "partial 中文.txt"), Encoding.UTF8) == "partial original\n", "Partial switch restores exact historical bytes");
                Call("update", partial, "--yes");
                Assert(!HasPending(partial) && File.ReadAllText(Path.Combine(partial, "partial 中文.txt"), Encoding.UTF8) == "partial server revision two\n", "Partial root update restores latest bytes cleanly");
            }
        }
        Call("update", consumer, "--yes");
        Call("update", partial, "--yes");
        string partialScope = Path.Combine(partial, "rollback-scope"), partialChild = Path.Combine(partialScope, "child 中文.txt");
        string consumerChild = Path.Combine(consumer, "rollback-scope", "child 中文.txt");
        Assert(File.Exists(partialChild), "Partial directory scope is loaded");
        File.WriteAllText(consumerChild, "scope v4 partial directory update\n", new UTF8Encoding(false));
        Call("checkin", consumerChild, "--yes", "--comment", "partial directory update revision");
        Call("update", partialScope, "--yes");
        Assert(File.ReadAllText(partialChild, Encoding.UTF8) == "scope v4 partial directory update\n" && !HasPending(partialScope), "Partial subdirectory update loads descendant new bytes");
    }

    private static bool HasPending(string path)
    { return ((IList)((Dictionary<string, object>)Call("status", path)["data"])["entries"]).Count != 0; }

    private static Dictionary<string, object> Call(string command, string path, params string[] extra)
    { return Call(command, new[] { path }, extra); }

    private static Dictionary<string, object> Call(string command, string[] paths, params string[] extra)
    { return Invoke(command, paths, 0, extra); }

    private static Dictionary<string, object> Invoke(string command, string[] paths, int expectedExit, params string[] extra)
    {
        var args = new List<string> { "--cli", "--json", "--command", command };
        foreach (string path in paths) { args.Add("--path"); args.Add(path); }
        args.AddRange(extra);
        var info = new ProcessStartInfo(executable, string.Join(" ", args.Select(Quote)))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(executable)
        };
        using (var process = Process.Start(info))
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(90000)) { process.Kill(); throw new TimeoutException("CLI integration timed out: " + command); }
            string text = stdout.GetAwaiter().GetResult().TrimStart('\uFEFF'); string error = stderr.GetAwaiter().GetResult();
            Events.Add(new { command = command, paths = paths, exitCode = process.ExitCode, output = text, error = error });
            var result = Json.Deserialize<Dictionary<string, object>>(text);
            if (process.ExitCode != expectedExit || (bool)result["success"] != (expectedExit == 0))
                throw new InvalidOperationException(command + " failed: " + text + "\n" + error);
            return result;
        }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char ch in value)
        {
            if (ch == '\\') { slashes++; continue; }
            result.Append('\\', ch == '"' ? slashes * 2 + 1 : slashes); result.Append(ch); slashes = 0;
        }
        result.Append('\\', slashes * 2); result.Append('"'); return result.ToString();
    }

    private static void Save(bool success, string error)
    {
        if (runDirectory == null) return;
        File.WriteAllText(Path.Combine(runDirectory, "cli-integration-results.json"), Json.Serialize(new { success = success, assertions = assertions, error = error, events = Events }), new UTF8Encoding(false));
    }
}
