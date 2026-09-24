// GPL-2.0-or-later. Nonempty responses are documented-format fixtures, not live locks.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using TortoiseSCM;

internal static class LockTests
{
    private const string Id = "f5d4a65f-5234-4369-b6d6-3ccff00fcc78";
    private const string Row = "TSLOCK|TestSCM|42|" + Id + "|2026-09-25 12:30|/main|100|/main/task|101|Locked|test-owner|test-workspace|/folder/file with spaces.txt|END";
    private static int count;
    private static void Assert(bool value, string message) { count++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { bool rejected = false; try { action(); } catch (T) { rejected = true; } Assert(rejected, message); }

    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "lock") return FakeCm(args);
        try
        {
            Unit();
            if (args.Length == 2 && args[0] == "--read-only")
            {
                var client = new PlasticClient(PlasticClientConfig.Load());
                var locks = client.GetLocksAsync(Path.GetFullPath(args[1]), CancellationToken.None).GetAwaiter().GetResult();
                Console.WriteLine("Live read-only lock rows: " + locks.Count);
                Assert(locks != null, "Live repository-scoped machine query succeeds");
            }
            else if (args.Length != 0) throw new ArgumentException("Usage: --read-only <workspace-root>");
            Console.WriteLine("PASS: " + count + " lock assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Unit()
    {
        Assert(PlasticClient.ParseLocks("").Count == 0, "Empty repository lock list is valid");
        var row = PlasticClient.ParseLocks(Row).Single();
        Assert(row.LockId == Guid.Parse(Id) && row.ItemId == 42 && row.Owner == "test-owner" &&
            row.Workspace == "test-workspace" && row.Path == "/folder/file with spaces.txt" &&
            row.HolderRevision == 101 && row.DestinationBranch == "/main", "All smart lock columns parsed without splitting spaces");
        Assert(PlasticClient.ParseLocks(Row.Replace("|Locked|", "|Retained|")).Single().Status == "Retained", "Retained lock supported");
        Reject<InvalidDataException>(() => PlasticClient.ParseLocks("localized header\n" + Row), "Unexpected human output rejected");
        Reject<InvalidDataException>(() => PlasticClient.ParseLocks(Row + "\n" + Row), "Ambiguous duplicate GUID rejected");
        Reject<InvalidDataException>(() => PlasticClient.ParseLocks(Row.Replace(Id, "bad-guid")), "Malformed GUID rejected");
        Reject<InvalidDataException>(() => PlasticClient.ParseLocks(Row.Replace("|Locked|", "|Unknown|")), "Unknown status rejected");
        Reject<InvalidDataException>(() => PlasticClient.ParseLocks(Row.Replace("|101|", "|not-number|")), "Malformed revision rejected");
        Reject<InvalidDataException>(() => PlasticClient.ParseLocks(Row.Replace("with spaces", "with|separator")), "Ambiguous field separator rejected");

        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-lock-tests-" + Guid.NewGuid().ToString("N"));
        string metadata = Path.Combine(temporary, ".plastic");
        Directory.CreateDirectory(metadata);
        try
        {
            File.WriteAllText(Path.Combine(metadata, "plastic.workspace"), "test-workspace\nStandard\n");
            File.WriteAllText(Path.Combine(metadata, "plastic.selector"), "repository \"TestSCM@fake-server:8087\"\n path \"/\"\n br \"/main\"\n");
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(5) });
            var token = CancellationToken.None;
            SetScenario(metadata, "own");
            Assert(client.GetLocksAsync(temporary, token).GetAwaiter().GetResult().Single().CanUnlock, "List executes structured repository query and indicates verified ownership");
            Assert(!File.ReadAllText(Path.Combine(metadata, "calls")).Contains("unlock"), "List is read only");
            SetScenario(metadata, "other-owner");
            Assert(!client.GetLocksAsync(temporary, token).GetAwaiter().GetResult().Single().CanUnlock, "Other owner's lock disables unlock hint");
            SetScenario(metadata, "own");
            Assert(client.UnlockOwnAsync(temporary, Guid.Parse(Id), token).GetAwaiter().GetResult().Succeeded, "Owner unlock succeeds through fake cm");
            string[] calls = File.ReadAllLines(Path.Combine(metadata, "calls"));
            Assert(calls.Length == 3 && !calls[0].Contains("--onlycurrentuser") &&
                calls[1].Contains("--onlycurrentuser") && calls[1].Contains("--onlycurrentworkspace"), "Both filters requery ownership immediately before unlock");
            Assert(calls[0].Contains("--repository=TestSCM@fake-server:8087") &&
                calls[2] == "lock\tunlock\tfake-server:8087\t" + Id, "Unlock specifies exact server and GUID");
            foreach (string scenario in new [] { "other-owner", "other-workspace", "vanished", "changed-owner", "changed-revision" })
            {
                SetScenario(metadata, scenario);
                Reject<InvalidOperationException>(() => client.UnlockOwnAsync(temporary, Guid.Parse(Id), token).GetAwaiter().GetResult(), scenario + " refused");
                Assert(!File.ReadAllText(Path.Combine(metadata, "calls")).Contains("unlock"), scenario + " never sends unlock");
            }
            SetScenario(metadata, "malformed");
            Reject<InvalidDataException>(() => client.UnlockOwnAsync(temporary, Guid.Parse(Id), token).GetAwaiter().GetResult(), "Malformed ownership response refused");
            Assert(!File.ReadAllText(Path.Combine(metadata, "calls")).Contains("unlock"), "Malformed ownership never sends unlock");
            SetScenario(metadata, "query-error");
            Reject<PlasticCommandException>(() => client.UnlockOwnAsync(temporary, Guid.Parse(Id), token).GetAwaiter().GetResult(), "Ownership query error preserved");
            Assert(!File.ReadAllText(Path.Combine(metadata, "calls")).Contains("unlock"), "Failed ownership query never sends unlock");
            SetScenario(metadata, "permission-error");
            Reject<PlasticCommandException>(() => client.UnlockOwnAsync(temporary, Guid.Parse(Id), token).GetAwaiter().GetResult(), "Server unlock permission error preserved");
            SetScenario(metadata, "timeout");
            var shortClient = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromMilliseconds(300) });
            Reject<PlasticCommandException>(() => shortClient.UnlockOwnAsync(temporary, Guid.Parse(Id), token).GetAwaiter().GetResult(), "Timed out query prevents unlock");
            Assert(!File.ReadAllText(Path.Combine(metadata, "calls")).Contains("unlock"), "Timed out ownership never sends unlock");
            SetScenario(metadata, "own");
            Reject<InvalidOperationException>(() => client.UnlockOwnAsync(temporary, Guid.NewGuid(), token).GetAwaiter().GetResult(), "Unlisted GUID refused");
            Assert(!File.ReadAllText(Path.Combine(metadata, "calls")).Contains("unlock"), "Unlisted GUID never sends unlock");
            SetScenario(metadata, "own");
            Reject<ArgumentException>(() => client.UnlockOwnAsync(temporary, Guid.Empty, token).GetAwaiter().GetResult(), "Empty GUID refused before process");
            Assert(!File.Exists(Path.Combine(metadata, "calls")), "Invalid GUID starts no process");
            string child = Path.Combine(temporary, "child"); Directory.CreateDirectory(child);
            Reject<ArgumentException>(() => client.UnlockOwnAsync(child, Guid.Parse(Id), token).GetAwaiter().GetResult(), "Nonroot request refused");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                Reject<OperationCanceledException>(() => client.UnlockOwnAsync(temporary, Guid.Parse(Id), cancel.Token).GetAwaiter().GetResult(), "Cancellation prevents unlock");
                Assert(!File.Exists(Path.Combine(metadata, "calls")), "Canceled operation starts no process");
            }
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static void SetScenario(string metadata, string scenario)
    {
        File.WriteAllText(Path.Combine(metadata, "scenario"), scenario);
        string calls = Path.Combine(metadata, "calls"); if (File.Exists(calls)) File.Delete(calls);
    }

    private static int FakeCm(string[] args)
    {
        string metadata = Path.Combine(Environment.CurrentDirectory, ".plastic");
        if (!File.Exists(Path.Combine(metadata, "scenario"))) return 91;
        File.AppendAllText(Path.Combine(metadata, "calls"), String.Join("\t", args) + Environment.NewLine);
        string scenario = File.ReadAllText(Path.Combine(metadata, "scenario"));
        if (args.Length >= 2 && args[1] == "list")
        {
            bool filtered = args.Contains("--onlycurrentuser") && args.Contains("--onlycurrentworkspace");
            if (!filtered) { Console.WriteLine(Row); return 0; }
            if (scenario == "other-owner" || scenario == "vanished") return 0;
            if (scenario == "query-error") { Console.Error.WriteLine("query denied"); return 5; }
            if (scenario == "timeout") { Thread.Sleep(1500); return 0; }
            if (scenario == "malformed") { Console.WriteLine("unrecognized response"); return 0; }
            string result = Row;
            if (scenario == "other-workspace") result = result.Replace("test-workspace", "another-workspace");
            if (scenario == "changed-owner") result = result.Replace("test-owner", "another-owner");
            if (scenario == "changed-revision") result = result.Replace("|101|", "|102|");
            Console.WriteLine(result); return 0;
        }
        if (args.Length == 4 && args[1] == "unlock")
        {
            if (scenario == "permission-error") { Console.Error.WriteLine("Only administrators may unlock"); return 6; }
            Console.WriteLine("Fixture lock removed"); return 0;
        }
        return 92;
    }
}
