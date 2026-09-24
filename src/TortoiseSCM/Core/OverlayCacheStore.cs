// TortoiseSCM - GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace TortoiseSCM
{
    public sealed class OverlaySnapshot
    {
        public DateTime GeneratedUtc { get; set; }
        public IDictionary<string, PlasticOverlayState> Entries { get; set; }
        public OverlaySnapshot() { GeneratedUtc = DateTime.UtcNow; Entries = new Dictionary<string, PlasticOverlayState>(StringComparer.OrdinalIgnoreCase); }

        public byte[] Encode()
        {
            if (Entries.Count > 200000) throw new InvalidDataException("Overlay snapshot exceeds 200000 entries.");
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory, Encoding.Unicode))
            {
                writer.Write(Encoding.ASCII.GetBytes("TSCMOVL1")); writer.Write((uint)1); writer.Write((uint)Entries.Count); writer.Write(GeneratedUtc.ToFileTimeUtc());
                var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in Entries.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ValidateEntry(item.Key, item.Value);
                    if (!unique.Add(item.Key)) throw new InvalidDataException("Duplicate overlay path.");
                    writer.Write((uint)item.Value); writer.Write((uint)item.Key.Length); writer.Write(new UnicodeEncoding(false, false, true).GetBytes(item.Key));
                    if (memory.Length > 32 * 1024 * 1024) throw new InvalidDataException("Overlay snapshot exceeds 32 MiB.");
                }
                return memory.ToArray();
            }
        }

        public static OverlaySnapshot Decode(byte[] bytes, DateTime now)
        {
            if (bytes.Length > 32 * 1024 * 1024 || bytes.Length < 24) throw new InvalidDataException("Invalid overlay snapshot size.");
            using (var memory = new MemoryStream(bytes, false))
            using (var reader = new BinaryReader(memory, Encoding.Unicode))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "TSCMOVL1" || reader.ReadUInt32() != 1) throw new InvalidDataException("Unknown overlay snapshot.");
                uint count = reader.ReadUInt32(); if (count > 200000) throw new InvalidDataException("Too many overlay entries.");
                var result = new OverlaySnapshot { GeneratedUtc = DateTime.FromFileTimeUtc(reader.ReadInt64()) };
                if (result.GeneratedUtc > now.AddSeconds(5) || result.GeneratedUtc < now.AddSeconds(-120)) throw new InvalidDataException("Overlay snapshot expired.");
                for (uint i = 0; i < count; ++i)
                {
                    var state = (PlasticOverlayState)reader.ReadUInt32(); uint chars = reader.ReadUInt32();
                    if (chars == 0 || chars > 32767 || chars * 2 > memory.Length - memory.Position) throw new InvalidDataException("Invalid overlay path length.");
                    string path = new UnicodeEncoding(false, false, true).GetString(reader.ReadBytes((int)chars * 2)); ValidateEntry(path, state);
                    if (result.Entries.ContainsKey(path)) throw new InvalidDataException("Duplicate overlay path.");
                    result.Entries.Add(path, state);
                }
                if (memory.Position != memory.Length) throw new InvalidDataException("Trailing overlay data.");
                return result;
            }
        }

        private static void ValidateEntry(string path, PlasticOverlayState state)
        {
            if (state < PlasticOverlayState.Normal || state > PlasticOverlayState.Conflict || String.IsNullOrEmpty(path) || path.Length > 32767 ||
                path.Length < 3 || !((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) || path[1] != ':' || path[2] != '\\' ||
                path.Any(ch => ch < 32) || path.IndexOfAny(new[] { '/', '"', '<', '>', '|', '*', '?' }) >= 0 || path.IndexOf(':', 2) >= 0)
                throw new InvalidDataException("Invalid overlay state or local path.");
            foreach (string part in path.Substring(3).Split('\\'))
                if (path.Length > 3 && (part.Length == 0 || part == "." || part == ".." || part.Equals(".plastic", StringComparison.OrdinalIgnoreCase) || part.EndsWith(".") || part.EndsWith(" ")))
                    throw new InvalidDataException("Invalid overlay path component.");
            try { new UnicodeEncoding(false, false, true).GetByteCount(path); }
            catch (EncoderFallbackException error) { throw new InvalidDataException("Invalid UTF-16 overlay path.", error); }
        }
    }

    // Each workspace publishes its own dated fragment. Aggregate publishing never renews an old fragment's age.
    public sealed class OverlayCacheStore
    {
        public static readonly string DefaultDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TortoiseSCM");
        private readonly string directory;
        private string Workspaces { get { return Path.Combine(directory, "overlay-workspaces"); } }
        public string SnapshotPath { get { return Path.Combine(directory, "overlay-cache-v1.bin"); } }
        public OverlayCacheStore(string directory) { this.directory = Path.GetFullPath(directory); }

        public void Track(string root)
        {
            root = NormalizeRoot(root); EnsureDirectory();
            if (!File.Exists(Path.Combine(Workspaces, Key(root) + ".root")) && Directory.GetFiles(Workspaces, "*.root").Length >= 64)
                throw new InvalidOperationException("The overlay cache already tracks 64 workspaces.");
            AtomicWrite(Path.Combine(Workspaces, Key(root) + ".root"), Encoding.UTF8.GetBytes(root));
        }

        public IList<string> TrackedRoots()
        {
            EnsureDirectory(); var roots = new List<string>();
            foreach (string file in Directory.GetFiles(Workspaces, "*.root").Take(64))
            {
                try
                {
                    if (new FileInfo(file).Length > 65536) continue;
                    string root = NormalizeRoot(File.ReadAllText(file, Encoding.UTF8));
                    if (Path.GetFileNameWithoutExtension(file) == Key(root) && !roots.Contains(root, StringComparer.OrdinalIgnoreCase)) roots.Add(root);
                }
                catch (IOException) { } catch (InvalidDataException) { } catch (ArgumentException) { }
            }
            return roots;
        }

        public void Publish(string root, IDictionary<string, PlasticOverlayState> entries)
        {
            root = NormalizeRoot(root); EnsureDirectory();
            if (entries.Keys.Any(path => !Within(path, root))) throw new InvalidDataException("Overlay fragment escapes its workspace.");
            using (var gate = Gate())
            {
                AtomicWrite(Path.Combine(Workspaces, Key(root) + ".bin"), new OverlaySnapshot { Entries = entries }.Encode());
                PublishAggregate();
            }
        }

        public void Invalidate(string root)
        {
            root = NormalizeRoot(root); EnsureDirectory();
            using (var gate = Gate())
            {
                string fragment = Path.Combine(Workspaces, Key(root) + ".bin");
                if (File.Exists(fragment)) File.Delete(fragment);
                PublishAggregate();
            }
        }

        private void PublishAggregate()
        {
            var combined = new OverlaySnapshot();
            foreach (string root in TrackedRoots().OrderBy(path => path.Length))
            {
                // Nested workspace boundaries override an outer workspace, including when the nested fragment expired.
                foreach (string path in combined.Entries.Keys.Where(path => Within(path, root)).ToArray()) combined.Entries.Remove(path);
                try
                {
                    string file = Path.Combine(Workspaces, Key(root) + ".bin");
                    if (!File.Exists(file) || new FileInfo(file).Length > 32 * 1024 * 1024) continue;
                    var part = OverlaySnapshot.Decode(File.ReadAllBytes(file), DateTime.UtcNow);
                    if (part.GeneratedUtc < DateTime.UtcNow.AddSeconds(-90)) continue;
                    if (part.GeneratedUtc < combined.GeneratedUtc) combined.GeneratedUtc = part.GeneratedUtc;
                    foreach (var item in part.Entries) if (Within(item.Key, root)) combined.Entries[item.Key] = item.Value;
                }
                catch (IOException) { } catch (InvalidDataException) { } catch (ArgumentException) { }
            }
            AtomicWrite(SnapshotPath, combined.Encode());
        }

        private FileStream Gate()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try { return new FileStream(Path.Combine(directory, "overlay-store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { if (DateTime.UtcNow >= deadline) throw; Thread.Sleep(50); }
            }
        }

        private void EnsureDirectory()
        {
            // Refuse redirecting persistent cache writes through a junction/symlink.
            for (string path = Workspaces; !String.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
                if ((Directory.Exists(path) || File.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Overlay storage may not use reparse points.");
            Directory.CreateDirectory(Workspaces);
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Overlay file may not be a reparse point.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static string NormalizeRoot(string root)
        {
            string path = Path.GetFullPath(root); if (path.Length > 3) path = path.TrimEnd('\\');
            if (path.Length < 3 || path[1] != ':' || path[2] != '\\' || path.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) throw new ArgumentException("Overlay caching requires a local workspace directory.");
            return path;
        }
        private static bool Within(string path, string root)
        { return path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase); }
        private static string Key(string root)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.Unicode.GetBytes(root.ToUpperInvariant()))).Replace("-", "").ToLowerInvariant(); }
    }
}
