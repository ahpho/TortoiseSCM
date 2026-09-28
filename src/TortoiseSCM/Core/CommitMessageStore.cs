// GPL-2.0-or-later. Explicit templates and successful commit messages; never drafts.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed class CommitMessageTemplate
    {
        public string Name { get; set; }
        public string Text { get; set; }
    }

    public sealed class CommitMessageLibrary
    {
        public IList<string> Recent { get; private set; }
        public IList<CommitMessageTemplate> Templates { get; private set; }
        public CommitMessageLibrary()
        { Recent = new List<string>(); Templates = new List<CommitMessageTemplate>(); }
    }

    public sealed class CommitMessageStore
    {
        public const int MaxRecent = 20, MaxTemplates = 20, MaxMessageLength = 32768, MaxTemplateNameLength = 80;
        private const int MaxRepositoryLength = 4096, MaxFileBytes = 8 * 1024 * 1024;
        private readonly string directory;

        public CommitMessageStore(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory)) throw new ArgumentException("A commit message directory is required.", "directory");
            this.directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }

        public static CommitMessageStore CreateDefault()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrWhiteSpace(profile)) throw new InvalidOperationException("The current user's local application data directory is unavailable.");
            return new CommitMessageStore(Path.Combine(profile, "TortoiseSCM", "commit-messages"));
        }

        public CommitMessageLibrary Load(string repository)
        { return WithLock(repository, () => Read(repository)); }

        public void RecordSuccess(string repository, string message)
        {
            ValidateText(message, MaxMessageLength, "message");
            Mutate(repository, library =>
            {
                for (int index = library.Recent.Count - 1; index >= 0; index--)
                    if (String.Equals(library.Recent[index], message, StringComparison.Ordinal)) library.Recent.RemoveAt(index);
                library.Recent.Insert(0, message);
                while (library.Recent.Count > MaxRecent) library.Recent.RemoveAt(library.Recent.Count - 1);
            });
        }

        public void SaveTemplate(string repository, string name, string text)
        { SaveTemplate(repository, name, text, false, null); }

        public void SaveTemplate(string repository, string name, string text, string expectedText)
        { SaveTemplate(repository, name, text, true, expectedText); }

        private void SaveTemplate(string repository, string name, string text, bool checkExpected, string expectedText)
        {
            ValidateText(name, MaxTemplateNameLength, "name"); ValidateText(text, MaxMessageLength, "text");
            Mutate(repository, library =>
            {
                var existing = library.Templates.SingleOrDefault(item => String.Equals(item.Name, name, StringComparison.Ordinal));
                if (checkExpected && (expectedText == null ? existing != null : existing == null || !String.Equals(existing.Text, expectedText, StringComparison.Ordinal)))
                    throw new InvalidOperationException("The named template changed in another window. Reload it before saving.");
                if (existing != null) { existing.Text = text; return; }
                if (library.Templates.Count >= MaxTemplates) throw new InvalidOperationException("The commit message template limit has been reached.");
                library.Templates.Add(new CommitMessageTemplate { Name = name, Text = text });
            });
        }

        public void DeleteTemplate(string repository, string name)
        { DeleteTemplate(repository, name, false, null); }

        public void DeleteTemplate(string repository, string name, string expectedText)
        { DeleteTemplate(repository, name, true, expectedText); }

        private void DeleteTemplate(string repository, string name, bool checkExpected, string expectedText)
        {
            ValidateText(name, MaxTemplateNameLength, "name");
            Mutate(repository, library =>
            {
                var existing = library.Templates.SingleOrDefault(item => String.Equals(item.Name, name, StringComparison.Ordinal));
                if (checkExpected && (existing == null || !String.Equals(existing.Text, expectedText, StringComparison.Ordinal)))
                    throw new InvalidOperationException("The named template changed in another window. Reload it before deleting.");
                for (int index = library.Templates.Count - 1; index >= 0; index--)
                    if (String.Equals(library.Templates[index].Name, name, StringComparison.Ordinal)) library.Templates.RemoveAt(index);
            });
        }

        public void ClearRecent(string repository)
        { Mutate(repository, library => library.Recent.Clear()); }

        private void Mutate(string repository, Action<CommitMessageLibrary> change)
        { WithLock(repository, () => { var library = Read(repository); change(library); Write(repository, library); return true; }); }

        private T WithLock<T>(string repository, Func<T> operation)
        {
            ValidateText(repository, MaxRepositoryLength, "repository");
            using (var mutex = new Mutex(false, MutexName(repository)))
            {
                bool held = false;
                try
                {
                    try { held = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
                    catch (AbandonedMutexException) { held = true; }
                    if (!held) throw new TimeoutException("Another process is updating the commit message library. Try again.");
                    return operation();
                }
                finally { if (held) mutex.ReleaseMutex(); }
            }
        }

        private string MutexName(string repository)
        {
            // The directory is a Windows filesystem identity; repository spelling
            // is intentionally exact because arbitrary server names are not folded.
            return "Local\\TortoiseSCM.CommitMessages." + Hash(directory.ToUpperInvariant() + "\n" + repository);
        }

        private string FileName(string repository) { return Path.Combine(directory, Hash(repository) + ".xml"); }
        private static string Hash(string value)
        {
            using (var hash = SHA256.Create())
                return String.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(value)).Select(item => item.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static void ValidateText(string value, int limit, string name)
        {
            if (String.IsNullOrWhiteSpace(value) || value.Length > limit) throw new ArgumentException("A nonempty " + name + " of at most " + limit + " characters is required.", name);
            try { XmlConvert.VerifyXmlChars(value); }
            catch (XmlException error) { throw new ArgumentException("The " + name + " contains invalid XML characters.", name, error); }
        }

        private CommitMessageLibrary Read(string repository)
        {
            string path = FileName(repository);
            var library = new CommitMessageLibrary();
            XDocument document;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > MaxFileBytes) throw new InvalidDataException("The commit message library is too large.");
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFileBytes };
                    using (var reader = XmlReader.Create(stream, settings)) document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
                }
                var root = document.Root;
                if (root == null || root.Name != "commitMessages" || root.Attributes().Count() != 2 || (string)root.Attribute("version") != "1" ||
                    !String.Equals((string)root.Attribute("repository"), repository, StringComparison.Ordinal) || root.Elements().Count() != 2 ||
                    root.Elements("recent").Count() != 1 || root.Elements("templates").Count() != 1 || HasUnexpectedText(root))
                    throw new InvalidDataException("Unsupported or mismatched commit message library.");
                var recent = root.Element("recent"); var templates = root.Element("templates");
                if (recent.HasAttributes || templates.HasAttributes || HasUnexpectedText(recent) || HasUnexpectedText(templates) ||
                    recent.Elements().Count() > MaxRecent || templates.Elements().Count() > MaxTemplates)
                    throw new InvalidDataException("Invalid commit message library limits or structure.");
                var messages = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in recent.Elements())
                {
                    if (element.Name != "message" || element.HasAttributes || element.HasElements || !messages.Add(element.Value))
                        throw new InvalidDataException("Invalid or duplicate recent commit message.");
                    ValidateText(element.Value, MaxMessageLength, "message"); library.Recent.Add(element.Value);
                }
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in templates.Elements())
                {
                    string name = (string)element.Attribute("name");
                    if (element.Name != "template" || element.Attributes().Count() != 1 || element.HasElements || !names.Add(name))
                        throw new InvalidDataException("Invalid or duplicate commit message template.");
                    ValidateText(name, MaxTemplateNameLength, "name"); ValidateText(element.Value, MaxMessageLength, "text");
                    library.Templates.Add(new CommitMessageTemplate { Name = name, Text = element.Value });
                }
            }
            catch (FileNotFoundException) { return library; }
            catch (DirectoryNotFoundException) { return library; }
            catch (XmlException error) { throw new InvalidDataException("The commit message library XML is invalid; the original file was preserved.", error); }
            catch (ArgumentException error) { throw new InvalidDataException("The commit message library contains invalid values; the original file was preserved.", error); }
            return library;
        }

        private static bool HasUnexpectedText(XElement element)
        { return element.Nodes().OfType<XText>().Any(text => !String.IsNullOrWhiteSpace(text.Value)); }

        private void Write(string repository, CommitMessageLibrary library)
        {
            var document = new XDocument(new XElement("commitMessages", new XAttribute("version", "1"), new XAttribute("repository", repository),
                new XElement("recent", library.Recent.Select(message => new XElement("message", message))),
                new XElement("templates", library.Templates.Select(template => new XElement("template", new XAttribute("name", template.Name), template.Text)))));
            Directory.CreateDirectory(directory);
            string path = FileName(repository), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), NewLineHandling = NewLineHandling.Entitize, Indent = true };
                    using (var writer = XmlWriter.Create(stream, settings)) document.Save(writer);
                    if (stream.Length > MaxFileBytes) throw new InvalidDataException("The commit message library is too large.");
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
