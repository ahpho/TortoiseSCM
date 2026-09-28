// GPL-2.0-or-later. Synthetic large-tree and deterministic cancellation regression.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using TortoiseSCM;

internal static class PartialBranchSwitchScaleTests
{
    private const string Repository = "scale@local", Namespace = "7bc5ef28-aaf2-4380-98b5-5a0a891f6461";
    private const BindingFlags Flags = BindingFlags.Static | BindingFlags.NonPublic;
    private static readonly PlasticWorkspace Workspace = new PlasticWorkspace { RootPath = Path.GetTempPath(), Repository = Repository };
    private static readonly PlasticBranch Branch = new PlasticBranch { Name = "/main/scale", HeadChangeset = 17 };
    private static int assertions;

    private static int Main()
    {
        try
        {
            RunScale(1000);
            RunScale(10000);
            TestCaseAndScopeBoundaries();
            TestCancellation();
            Console.WriteLine("PASS: " + assertions + " Partial switch scale/cancellation assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static object Invoke(string name, params object[] args)
    {
        try { return typeof(PlasticClient).GetMethod(name, Flags).Invoke(null, args); }
        catch (TargetInvocationException error) { throw error.InnerException; }
    }
    private static object Parse(string xml, CancellationToken token)
    { return Invoke("ParsePartialSwitchTree", xml, Workspace, false, token); }
    private static PlasticPartialBranchSwitchPreview Build(object loaded, object target, string[] rules, bool full, CancellationToken token)
    { return (PlasticPartialBranchSwitchPreview)Invoke("BuildPartialSwitchPreview", Workspace, Branch, loaded, target, rules, full, token); }
    private static void Check(bool value, string message)
    { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
    private static string Item(string path, long id, bool directory)
    {
        return "<LsItem><CurrentPath>" + path + "</CurrentPath><ItemId>" + id.ToString(CultureInfo.InvariantCulture) +
            "</ItemId><Type>" + (directory ? "dir" : "txt") + "</Type><Repository>rep:" + Repository +
            "</Repository><SymlinkTarget /></LsItem>";
    }
    private static string Xml(params string[] items)
    { return "<LsResults><LsItems>" + String.Join("", items) + "</LsItems></LsResults>"; }

    private static string LargeXml(int groups, bool reverse, bool additions)
    {
        var xml = new StringBuilder("<LsResults><LsItems>");
        if (!reverse) xml.Append(Item("/", 1, true));
        for (int index = 0; index < groups; index++)
        {
            int group = reverse ? groups - index - 1 : index;
            long id = 2 + group * 11;
            string path = "/d" + group.ToString("D5", CultureInfo.InvariantCulture);
            // Parent order intentionally differs between the trees.
            if (!reverse) xml.Append(Item(path, id, true));
            for (int file = 0; file < 10; file++) xml.Append(Item(path + "/f" + file + ".txt", id + file + 1, false));
            if (reverse) xml.Append(Item(path, id, true));
        }
        if (reverse) xml.Append(Item("/", 1, true));
        if (additions)
        {
            xml.Append(Item("/d" + (groups - 1).ToString("D5", CultureInfo.InvariantCulture) + "/new", 2000001, true));
            xml.Append(Item("/unselected", 2000002, true));
        }
        return xml.Append("</LsItems></LsResults>").ToString();
    }

    private static void RunScale(int groups)
    {
        string loadedXml = LargeXml(groups, false, false), targetXml = LargeXml(groups, true, false);
        var rules = new string[groups];
        for (int group = 0; group < groups; group++) rules[group] = Namespace + ":" + (2 + group * 11);
        var elapsed = Stopwatch.StartNew();
        object loaded = Parse(loadedXml, CancellationToken.None), target = Parse(targetXml, CancellationToken.None);
        var preview = Build(loaded, target, rules, false, CancellationToken.None);
        elapsed.Stop();
        Check(preview.CanSwitch && preview.LoadedDirectoryCount == groups + 1 && preview.LoadingRuleCount == groups,
            "Large unchanged tree retains its scope and directory count");
        Check(preview.Directories.Count == groups + 1 && preview.Directories[0].Path == "/" &&
            preview.Directories[groups].Path == "/d" + (groups - 1).ToString("D5", CultureInfo.InvariantCulture),
            "Large preview has stable path order despite reverse target input");
        // Generous guard catches quadratic regressions; timings are reported, not a server SLA.
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(30), "Synthetic parse/match exceeded 30 seconds");
        Console.WriteLine("SCALE: " + (groups * 11 + 1) + " items/tree; " + groups + " rules; parse+match " + elapsed.ElapsedMilliseconds + " ms");
        target = Parse(LargeXml(groups, true, true), CancellationToken.None);
        elapsed.Restart(); preview = Build(loaded, target, rules, false, CancellationToken.None); elapsed.Stop();
        int added = 0;
        foreach (var row in preview.Directories) if (row.Change == "Added") { added++; Check(row.Path != "/unselected", "Unselected directory stays outside loaded scope"); }
        Check(!preview.CanSwitch && added == 1, "New directory inside the last of many scopes is blocked");
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(10), "Large multi-scope matching exceeded 10 seconds");
        Console.WriteLine("SCALE: additions match " + elapsed.ElapsedMilliseconds + " ms");
    }

    private static void TestCaseAndScopeBoundaries()
    {
        object loaded = Parse(Xml(Item("/", 1, true), Item("/a", 2, true)), CancellationToken.None);
        object target = Parse(Xml(Item("/", 1, true), Item("/a", 2, true), Item("/ab", 3, true)), CancellationToken.None);
        Check(Build(loaded, target, new[] { Namespace + ":2" }, false, CancellationToken.None).CanSwitch,
            "Scope /a does not accidentally include sibling /ab");
        Check(!Build(loaded, target, new string[0], true, CancellationToken.None).CanSwitch, "Root full scope includes siblings");
        target = Parse(Xml(Item("/", 1, true), Item("/A", 2, true)), CancellationToken.None);
        var preview = Build(loaded, target, new string[0], false, CancellationToken.None);
        Check(!preview.CanSwitch && preview.Directories[1].Change == "Moved", "Case-only directory moves remain blocked");
        Reject<InvalidDataException>(() => Parse(Xml(Item("/", 1, true), Item("/a", 2, true), Item("/A", 3, true)), CancellationToken.None), "Case-insensitive path duplicate rejected");
        Reject<InvalidDataException>(() => Parse(Xml(Item("/", 1, true), Item("/a", 2, true), Item("/A/file", 3, false)), CancellationToken.None), "Parent chain retains exact-case validation");
    }

    private static void TestCancellation()
    {
        string xml = LargeXml(1000, false, false);
        using (var cancellation = new CancellationTokenSource())
        using (var reader = new CancelReader(xml, cancellation))
        {
            Reject<OperationCanceledException>(() => Invoke("ParsePartialSwitchTreeFromReader", reader, Workspace, false, cancellation.Token), "XML parsing observes cancellation while reading");
            Check(reader.CharactersRead >= 8192 && reader.CharactersRead < xml.Length / 2, "Cancelled parsing stops before consuming full XML");
        }
        object tree = Parse(Xml(Item("/", 1, true)), CancellationToken.None);
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Reject<OperationCanceledException>(() => Parse(xml, cancellation.Token), "Precancelled parser does no work");
            Reject<OperationCanceledException>(() => Build(tree, tree, new string[0], false, cancellation.Token), "Precancelled matching does no work");
        }
        Reject<System.Xml.XmlException>(() => Parse("<!DOCTYPE LsResults [<!ENTITY x 'unsafe'>]><LsResults><LsItems>&x;</LsItems></LsResults>", CancellationToken.None), "Cancellable parser continues to prohibit DTDs");
    }

    private sealed class CancelReader : StringReader
    {
        private readonly CancellationTokenSource cancellation;
        internal int CharactersRead;
        internal CancelReader(string xml, CancellationTokenSource cancellation) : base(xml) { this.cancellation = cancellation; }
        public override int Read(char[] buffer, int index, int count)
        {
            int read = base.Read(buffer, index, Math.Min(count, 1024));
            CharactersRead += read;
            if (CharactersRead >= 8192) cancellation.Cancel();
            return read;
        }
    }
}
