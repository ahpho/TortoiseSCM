// GPL-2.0-or-later. Isolated durable history/template and interprocess update tests.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class CommitMessageStoreTests
{
    private const string Repository = "仓库@Server:8087";
    private static string root;
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length != 0) return Child(args);
        root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-commit-messages-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TestRoundTrip(); TestLimitsAndUpdates(); TestExpectedTemplate(); TestCorruption(); TestConcurrentUpdates(); TestMutexTimeoutAndRecovery(); TestAtomicFailure();
            Console.WriteLine("PASS: " + assertions + " commit message store assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { try { action(); } catch (T) { assertions++; return; } throw new Exception(message); }
    private static string DirectoryFor(string name) { return Path.Combine(root, name); }
    private static string FileFor(string directory) { return Directory.GetFiles(directory, "*.xml").Single(); }

    private static void TestRoundTrip()
    {
        string directory = DirectoryFor("roundtrip"); var store = new CommitMessageStore(directory);
        Check(store.Load(Repository).Recent.Count == 0 && store.Load(Repository).Templates.Count == 0, "Missing library is empty");
        Check(!Directory.Exists(directory), "Reading a missing library creates no storage directory");
        string message = "  修复 <A> & \"B\" 😀\r\n第二行\r第三行\n\t末尾  ";
        string templateName = "版本\t计划\r\n一";
        store.RecordSuccess(Repository, message); store.SaveTemplate(Repository, templateName, message);
        var loaded = new CommitMessageStore(directory).Load(Repository);
        Check(loaded.Recent.Single() == message && loaded.Templates.Single().Text == message && loaded.Templates.Single().Name == templateName,
            "Unicode, XML characters, whitespace and mixed line endings round-trip exactly");
        Check(File.ReadAllText(FileFor(directory)).Contains("&#xD;"), "Carriage returns stored as entities instead of normalized");
        loaded.Recent.Clear(); loaded.Templates[0].Text = "caller mutation"; loaded.Templates.Clear();
        Check(store.Load(Repository).Recent.Single() == message && store.Load(Repository).Templates.Single().Text == message, "Returned mutable copies cannot alter persistent state");
        store.RecordSuccess(Repository.ToLowerInvariant(), "lowercase"); store.RecordSuccess("other@Server:8087", "other");
        Check(store.Load(Repository).Recent.Single() == message && store.Load(Repository.ToLowerInvariant()).Recent.Single() == "lowercase" &&
            store.Load("other@Server:8087").Recent.Single() == "other", "Repositories remain exact-case isolated");
        Check(Directory.GetFiles(directory, "*.xml").All(file => Path.GetFileNameWithoutExtension(file).Length == 64), "Repository identity uses SHA256 filename");
    }

    private static void TestLimitsAndUpdates()
    {
        string directory = DirectoryFor("limits"); var store = new CommitMessageStore(directory);
        for (int index = 0; index < CommitMessageStore.MaxRecent + 3; index++) store.RecordSuccess(Repository, "message-" + index);
        var recent = store.Load(Repository).Recent;
        Check(recent.Count == 20 && recent[0] == "message-22" && recent[19] == "message-3", "History is bounded newest-first");
        store.RecordSuccess(Repository, "message-7"); recent = store.Load(Repository).Recent;
        Check(recent.Count == 20 && recent[0] == "message-7" && recent.Count(message => message == "message-7") == 1, "Repeated exact message moves to MRU without duplication");
        store.RecordSuccess(Repository, "MESSAGE-7");
        Check(store.Load(Repository).Recent.Contains("message-7") && store.Load(Repository).Recent.Contains("MESSAGE-7"), "History deduplication is ordinal");
        for (int index = 0; index < CommitMessageStore.MaxTemplates; index++) store.SaveTemplate(Repository, "template-" + index, "text-" + index);
        Reject<InvalidOperationException>(() => store.SaveTemplate(Repository, "overflow", "text"), "New template above capacity rejected");
        store.SaveTemplate(Repository, "template-0", "updated");
        Check(store.Load(Repository).Templates.Count == 20 && store.Load(Repository).Templates[0].Text == "updated", "Explicit upsert at capacity retains template order");
        store.DeleteTemplate(Repository, "template-0"); store.DeleteTemplate(Repository, "missing");
        store.SaveTemplate(Repository, "TEMPLATE-1", "distinct case");
        Check(store.Load(Repository).Templates.Count == 20 && store.Load(Repository).Templates.Any(item => item.Name == "template-1") &&
            store.Load(Repository).Templates.Any(item => item.Name == "TEMPLATE-1"), "Template removal and names are exact ordinal");
        store.ClearRecent(Repository);
        Check(store.Load(Repository).Recent.Count == 0 && store.Load(Repository).Templates.Count == 20, "Clear recent preserves named templates");
        string original = File.ReadAllText(FileFor(directory));
        foreach (string invalid in new[] { null, "", " \r\n", "bad\0", new string('a', CommitMessageStore.MaxMessageLength + 1) })
        { Reject<ArgumentException>(() => store.RecordSuccess(Repository, invalid), "Invalid message rejected"); Reject<ArgumentException>(() => store.SaveTemplate(Repository, "name", invalid), "Invalid template body rejected"); }
        foreach (string invalid in new[] { null, "", " \t", "bad\0", new string('a', CommitMessageStore.MaxTemplateNameLength + 1) })
        { Reject<ArgumentException>(() => store.SaveTemplate(Repository, invalid, "text"), "Invalid template name rejected"); Reject<ArgumentException>(() => store.DeleteTemplate(Repository, invalid), "Invalid delete name rejected"); }
        Reject<ArgumentException>(() => store.Load(""), "Empty repository rejected");
        Reject<ArgumentException>(() => store.ClearRecent("bad\0"), "Invalid repository rejected");
        Check(File.ReadAllText(FileFor(directory)) == original, "Rejected values leave library unchanged");
        store.DeleteTemplate(Repository, "template-1");
        store.SaveTemplate(Repository, new string('n', 80), new string('文', 32768));
        store.RecordSuccess(Repository, new string('文', 32768));
        Check(store.Load(Repository).Recent[0].Length == 32768 && store.Load(Repository).Templates.Last().Name.Length == 80, "Inclusive text and name limits accepted");
    }

    private static void TestExpectedTemplate()
    {
        string directory = DirectoryFor("expected"); var first = new CommitMessageStore(directory); var second = new CommitMessageStore(directory);
        first.SaveTemplate(Repository, "name", "first", null);
        Reject<InvalidOperationException>(() => second.SaveTemplate(Repository, "name", "second", null), "Stale create cannot overwrite another window's template");
        first.SaveTemplate(Repository, "name", "updated", "first");
        Reject<InvalidOperationException>(() => second.SaveTemplate(Repository, "name", "stale", "first"), "Stale edit cannot overwrite a changed template");
        Reject<InvalidOperationException>(() => second.DeleteTemplate(Repository, "name", "first"), "Stale delete cannot remove a changed template");
        Check(first.Load(Repository).Templates.Single().Text == "updated", "Rejected stale operations preserve the current value");
        first.DeleteTemplate(Repository, "name", "updated");
        Reject<InvalidOperationException>(() => second.SaveTemplate(Repository, "name", "resurrected", "updated"), "Stale update cannot resurrect a deleted template");
        Reject<InvalidOperationException>(() => second.DeleteTemplate(Repository, "name", "updated"), "Conditional deletion requires an existing reviewed template");
        first.SaveTemplate(Repository, "name", "recreated", null);
        Check(first.Load(Repository).Templates.Single().Text == "recreated", "Fresh reviewed creation succeeds after deletion");
    }

    private static void TestCorruption()
    {
        string directory = DirectoryFor("corrupt"); var store = new CommitMessageStore(directory);
        store.RecordSuccess(Repository, "original"); string file = FileFor(directory), valid = File.ReadAllText(file);
        var documents = new List<string> { "<broken>", valid.Replace("version=\"1\"", "version=\"2\""),
            valid.Replace("仓库@Server:8087", "other@Server:8087"),
            "<!DOCTYPE commitMessages [<!ENTITY x 'bad'>]><commitMessages>&x;</commitMessages>" };
        var duplicate = XDocument.Parse(valid); duplicate.Root.Element("recent").Add(new XElement("message", "original")); documents.Add(duplicate.ToString());
        var excessive = XDocument.Parse(valid); excessive.Root.Element("recent").RemoveAll();
        for (int index = 0; index < 21; index++) excessive.Root.Element("recent").Add(new XElement("message", "message-" + index));
        documents.Add(excessive.ToString());
        var invalid = XDocument.Parse(valid); invalid.Root.Element("recent").Element("message").Value = " "; documents.Add(invalid.ToString());
        invalid = XDocument.Parse(valid); invalid.Root.Element("recent").Element("message").Add(new XElement("nested", "bad")); documents.Add(invalid.ToString());
        invalid = XDocument.Parse(valid); invalid.Root.Element("templates").Add(new XElement("template", new XAttribute("name", ""), "text")); documents.Add(invalid.ToString());
        invalid = XDocument.Parse(valid); invalid.Root.Element("templates").Add(new XElement("template", new XAttribute("name", "same"), "one"), new XElement("template", new XAttribute("name", "same"), "two")); documents.Add(invalid.ToString());
        invalid = XDocument.Parse(valid); invalid.Root.Element("recent").Element("message").Value = new string('a', 32769); documents.Add(invalid.ToString());
        invalid = XDocument.Parse(valid);
        for (int index = 0; index < 21; index++) invalid.Root.Element("templates").Add(new XElement("template", new XAttribute("name", "name-" + index), "text"));
        documents.Add(invalid.ToString());
        invalid = XDocument.Parse(valid); invalid.Root.Add(new XElement("unexpected")); documents.Add(invalid.ToString());
        documents.Add(new string('x', 8 * 1024 * 1024 + 1));
        foreach (string corrupt in documents)
        {
            File.WriteAllText(file, corrupt, new UTF8Encoding(false)); byte[] original = File.ReadAllBytes(file);
            Reject<InvalidDataException>(() => store.Load(Repository), "Corrupt library cannot load");
            Reject<InvalidDataException>(() => store.RecordSuccess(Repository, "new"), "Corrupt library cannot be overwritten by history");
            Reject<InvalidDataException>(() => store.SaveTemplate(Repository, "name", "text"), "Corrupt library cannot be overwritten by template");
            Reject<InvalidDataException>(() => store.DeleteTemplate(Repository, "name"), "Corrupt library cannot be overwritten by deletion");
            Reject<InvalidDataException>(() => store.ClearRecent(Repository), "Corrupt library cannot be overwritten by clear");
            Check(File.ReadAllBytes(file).SequenceEqual(original), "Rejected corrupted bytes are preserved exactly");
            Check(Directory.GetFiles(directory).Length == 1, "Corruption failure creates no temporary file");
        }
    }

    private static void TestConcurrentUpdates()
    {
        string directory = DirectoryFor("concurrent"); Directory.CreateDirectory(directory);
        var children = new List<Process>();
        try
        {
            for (int index = 0; index < 8; index++) children.Add(StartChild("write", directory, index.ToString()));
            File.WriteAllText(Path.Combine(directory, "go"), "go");
            foreach (var child in children) { Check(child.WaitForExit(20000), "Concurrent writer terminates"); Check(child.ExitCode == 0, "Concurrent writer succeeds: " + child.StandardError.ReadToEnd()); }
            var library = new CommitMessageStore(directory).Load(Repository);
            Check(library.Recent.Count == 8 && library.Templates.Count == 8, "Separate-process read-modify-write retains every writer");
            for (int index = 0; index < 8; index++) Check(library.Recent.Contains("message-" + index) && library.Templates.Any(item => item.Name == "template-" + index), "No concurrent update lost");
            Check(Directory.GetFiles(directory, "*.tmp").Length == 0, "Successful replacements leave no temporary files");
        }
        finally { foreach (var child in children) { if (!child.HasExited) child.Kill(); child.Dispose(); } }
    }

    private static void TestMutexTimeoutAndRecovery()
    {
        string directory = DirectoryFor("mutex"); Directory.CreateDirectory(directory); var store = new CommitMessageStore(directory);
        store.RecordSuccess(Repository, "initial"); byte[] original = File.ReadAllBytes(FileFor(directory));
        using (var holder = StartChild("hold", directory, "unused"))
        {
            try
            {
                WaitForFile(Path.Combine(directory, "locked"));
                Reject<TimeoutException>(() => store.RecordSuccess(Repository, "timeout"), "Named mutex has bounded wait");
                Check(File.ReadAllBytes(FileFor(directory)).SequenceEqual(original), "Lock timeout preserves bytes");
                // Kill without releasing the mutex to exercise abandoned-owner recovery.
                string name = (string)typeof(CommitMessageStore).GetMethod("MutexName", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(store, new object[] { Repository });
                using (var observer = new Mutex(false, name))
                {
                    // Keep the named object alive so WaitOne observes abandonment,
                    // rather than acquiring a freshly created mutex after owner exit.
                    holder.Kill(); holder.WaitForExit();
                    store.RecordSuccess(Repository, "after-abandon");
                }
                Check(store.Load(Repository).Recent.SequenceEqual(new[] { "after-abandon", "initial" }), "Abandoned mutex is acquired, validated and released");
            }
            finally { if (!holder.HasExited) holder.Kill(); }
        }
    }

    private static void TestAtomicFailure()
    {
        string directory = DirectoryFor("atomic"); var store = new CommitMessageStore(directory);
        store.RecordSuccess(Repository, "initial"); string file = FileFor(directory); byte[] original = File.ReadAllBytes(file);
        using (var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        { Reject<IOException>(() => store.RecordSuccess(Repository, "blocked replacement"), "Sharing conflict aborts atomic replace"); }
        Check(File.ReadAllBytes(file).SequenceEqual(original), "Failed replacement retains original bytes");
        Check(Directory.GetFiles(directory).Length == 1, "Failed replacement removes only owned temporary file");
        store.SaveTemplate(Repository, "after failure", "works");
        Check(store.Load(Repository).Recent.Single() == "initial" && store.Load(Repository).Templates.Count == 1, "Failed replacement releases mutex and permits later changes");
    }

    private static Process StartChild(string mode, string directory, string value)
    {
        return Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, mode + " \"" + directory + "\" " + value)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true });
    }
    private static void WaitForFile(string file)
    {
        var elapsed = Stopwatch.StartNew();
        while (!File.Exists(file)) { if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("Child synchronization timed out."); Thread.Sleep(10); }
    }
    private static int Child(string[] args)
    {
        try
        {
            var store = new CommitMessageStore(args[1]);
            if (args[0] == "write")
            {
                WaitForFile(Path.Combine(args[1], "go"));
                store.SaveTemplate(Repository, "template-" + args[2], "text-" + args[2]); store.RecordSuccess(Repository, "message-" + args[2]);
            }
            else
            {
                string name = (string)typeof(CommitMessageStore).GetMethod("MutexName", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(store, new object[] { Repository });
                using (var mutex = new Mutex(false, name))
                { mutex.WaitOne(); File.WriteAllText(Path.Combine(args[1], "locked"), "ready"); Thread.Sleep(30000); mutex.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
