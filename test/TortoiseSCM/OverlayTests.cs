// GPL-2.0-or-later. Isolated cache protocol and state aggregation tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TortoiseSCM;

internal static class OverlayTests
{
    private static int count;
    private static void Assert(bool condition, string label) { count++; if (!condition) throw new Exception(label); }
    private static void Reject(Action action, string label)
    {
        bool rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
        Assert(rejected, label);
    }
    private static IDictionary<string, PlasticOverlayState> Entries(params object[] values)
    {
        var result = new Dictionary<string, PlasticOverlayState>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < values.Length; i += 2) result.Add((string)values[i], (PlasticOverlayState)values[i + 1]);
        return result;
    }

    public static int Main()
    {
        string temporary = Path.Combine(Path.GetTempPath(), "TortoiseSCM-overlay-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            Protocol(); States(temporary); Storage(temporary);
            Console.WriteLine("PASS: " + count + " overlay assertions"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(temporary, true); }
    }

    private static void Protocol()
    {
        DateTime now = DateTime.UtcNow;
        string path = "D:\\fixture\\中文 file.txt";
        var source = new OverlaySnapshot { GeneratedUtc = now, Entries = Entries(path, PlasticOverlayState.Modified, "D:\\fixture", PlasticOverlayState.Conflict) };
        byte[] bytes = source.Encode();
        Assert(Encoding.ASCII.GetString(bytes, 0, 8) == "TSCMOVL1" && BitConverter.ToUInt32(bytes, 8) == 1 &&
            BitConverter.ToUInt32(bytes, 12) == 2 && BitConverter.ToInt64(bytes, 16) == now.ToFileTimeUtc(), "Exact native wire header");
        var decoded = OverlaySnapshot.Decode(bytes, now);
        Assert(decoded.GeneratedUtc == now && decoded.Entries.Count == 2 && decoded.Entries[path] == PlasticOverlayState.Modified &&
            decoded.Entries["d:\\FIXTURE"] == PlasticOverlayState.Conflict, "Unicode roundtrip and Windows path case");
        byte[] malformed = (byte[])bytes.Clone(); malformed[0] = 0;
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Bad magic rejected");
        malformed = (byte[])bytes.Clone(); malformed[8] = 2;
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Unknown version rejected");
        malformed = bytes.Take(bytes.Length - 1).ToArray();
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Truncated path rejected");
        malformed = bytes.Concat(new byte[] { 0 }).ToArray();
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Trailing bytes rejected");
        malformed = new byte[32 * 1024 * 1024 + 1];
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Oversized file rejected");
        malformed = (byte[])bytes.Clone(); Array.Copy(BitConverter.GetBytes((uint)200001), 0, malformed, 12, 4);
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Excessive count rejected");
        malformed = (byte[])bytes.Clone(); Array.Copy(BitConverter.GetBytes((uint)32768), 0, malformed, 28, 4);
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Excessive path length rejected");
        malformed = (byte[])bytes.Clone(); Array.Copy(BitConverter.GetBytes((uint)9), 0, malformed, 24, 4);
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Unknown state rejected");
        Reject(() => OverlaySnapshot.Decode(bytes, now.AddSeconds(121)), "120 second native expiry enforced");
        Reject(() => OverlaySnapshot.Decode(bytes, now.AddSeconds(-6)), "Future timestamp rejected");
        Assert(OverlaySnapshot.Decode(bytes, now.AddSeconds(119)).Entries.Count == 2, "Unexpired snapshot remains valid");
        Assert(OverlaySnapshot.Decode(new OverlaySnapshot { GeneratedUtc = now }.Encode(), now).Entries.Count == 0, "Empty snapshot supported");
        Assert(OverlaySnapshot.Decode(new OverlaySnapshot { Entries = Entries("D:\\", PlasticOverlayState.Normal) }.Encode(), DateTime.UtcNow).Entries.ContainsKey("D:\\"), "Drive root canonical path preserved");
        byte[] single = new OverlaySnapshot { GeneratedUtc = now, Entries = Entries(path, PlasticOverlayState.Normal) }.Encode();
        malformed = single.Concat(single.Skip(24)).ToArray(); Array.Copy(BitConverter.GetBytes((uint)2), 0, malformed, 12, 4);
        Reject(() => OverlaySnapshot.Decode(malformed, now), "Duplicate wire paths rejected");
        foreach (string invalid in new[] { "relative.txt", "D:\\fixture\\..\\other", "D:\\fixture\\.plastic\\plastic.workspace", "D:\\fixture\\trailing\\", "D:\\fixture\\bad\tname", "D:\\fixture\\bad\ud800", "D:\\fixture\\bad.", "D:\\fixture\\bad " })
            Reject(() => new OverlaySnapshot { Entries = Entries(invalid, PlasticOverlayState.Normal) }.Encode(), "Native-invalid producer path rejected: " + invalid);
        var duplicates = new Dictionary<string, PlasticOverlayState>(StringComparer.Ordinal) { { "D:\\Fixture", PlasticOverlayState.Normal }, { "d:\\fixture", PlasticOverlayState.Modified } };
        Reject(() => new OverlaySnapshot { Entries = duplicates }.Encode(), "Case duplicate producer entries rejected");
    }

    private static void States(string directory)
    {
        string root = Path.Combine(directory, "workspace"), folder = Path.Combine(root, "folder"), file = Path.Combine(folder, "file.txt"),
            old = Path.Combine(root, "old", "moved.txt"), moved = Path.Combine(root, "new", "moved.txt"),
            ignored = Path.Combine(root, "ignored"), privatePath = Path.Combine(root, "private.txt"), unrelated = Path.Combine(directory, "outside.txt");
        var clean = PlasticClient.BuildOverlayStates(root, new[] { root, folder, file }, new PlasticStatusItem[0], new string[0]);
        Assert(clean.Count == 3 && clean.Values.All(value => value == PlasticOverlayState.Normal), "Explicit controlled inventory yields normal entries");
        var states = PlasticClient.BuildOverlayStates(root, new[] { root, folder, file, privatePath, ignored, Path.Combine(ignored, "secret") },
            new[] { new PlasticStatusItem { Path = file, StatusCode = "CH" }, new PlasticStatusItem { Path = privatePath, StatusCode = "PR" },
                new PlasticStatusItem { Path = ignored, StatusCode = "IG" }, new PlasticStatusItem { Path = moved, OldPath = old, StatusCode = "MV" } }, new[] { file });
        Assert(states[file] == PlasticOverlayState.Conflict && states[folder] == PlasticOverlayState.Conflict && states[root] == PlasticOverlayState.Conflict,
            "Conflict propagates to all ancestors and outranks modified");
        Assert(states[moved] == PlasticOverlayState.Modified && states[old] == PlasticOverlayState.Modified &&
            states[Path.GetDirectoryName(old)] == PlasticOverlayState.Modified && states[Path.GetDirectoryName(moved)] == PlasticOverlayState.Modified,
            "Move marks both source and destination ancestors");
        Assert(states[privatePath] == PlasticOverlayState.Unversioned && states[ignored] == PlasticOverlayState.Ignored,
            "Private and ignored replacements retain distinct overlay states");
        Assert(!states.ContainsKey(Path.Combine(ignored, "secret")), "Ignored replacement removes stale normal descendants");
        var outside = PlasticClient.BuildOverlayStates(root, new[] { unrelated }, new[] { new PlasticStatusItem { Path = unrelated, StatusCode = "CH" } }, new[] { unrelated });
        Assert(outside.Count == 0, "Paths outside root never enter snapshot");
        var deleted = PlasticClient.BuildOverlayStates(root, new string[0], new[] { new PlasticStatusItem { Path = file, StatusCode = "DE" } }, new string[0]);
        Assert(deleted[file] == PlasticOverlayState.Deleted && deleted[root] == PlasticOverlayState.Modified && deleted[folder] == PlasticOverlayState.Modified,
            "Deleted child marks ancestors modified even when file absent");
        var priority = PlasticClient.BuildOverlayStates(root, new[] { root, folder, file }, new[] { new PlasticStatusItem { Path = file, StatusCode = "CO" } }, new[] { root });
        Assert(priority[root] == PlasticOverlayState.Conflict && priority[file] == PlasticOverlayState.Modified,
            "Unknown merge marks root conflict and checkout never implies server lock");

        foreach (string code in new[] { "IG", "IGNORED", "PR", "PRIVATE" })
        {
            var replacement = PlasticClient.BuildOverlayStates(root, new[] { root, folder, file },
                new[] { new PlasticStatusItem { Path = folder, StatusCode = code, IsDirectory = true } }, new string[0]);
            Assert(replacement[root] == PlasticOverlayState.Normal && !replacement.ContainsKey(file),
                code + " subtree drops stale inventory without changing controlled parent");
            Assert(replacement[folder] == (code == "IG" || code == "IGNORED" ? PlasticOverlayState.Ignored : PlasticOverlayState.Unversioned),
                code + " directory retains its own overlay");
        }

        string addedFile = Path.Combine(folder, "added.txt"), deletedFile = Path.Combine(folder, "deleted.txt");
        foreach (string directoryCode in new[] { "AD", "DE", "CH" })
        {
            var changes = new[] {
                new PlasticStatusItem { Path = folder, StatusCode = directoryCode, IsDirectory = true },
                new PlasticStatusItem { Path = addedFile, StatusCode = "AD" },
                new PlasticStatusItem { Path = deletedFile, StatusCode = "DE" },
                new PlasticStatusItem { Path = file, StatusCode = "CH" } };
            foreach (var permutation in Permutations(changes))
            {
                var mixed = PlasticClient.BuildOverlayStates(root, new[] { root, folder, file }, permutation, new string[0]);
                Assert(mixed[root] == PlasticOverlayState.Modified &&
                    mixed[folder] == (directoryCode == "AD" ? PlasticOverlayState.Added : directoryCode == "DE" ? PlasticOverlayState.Deleted : PlasticOverlayState.Modified) &&
                    mixed[addedFile] == PlasticOverlayState.Added && mixed[deletedFile] == PlasticOverlayState.Deleted && mixed[file] == PlasticOverlayState.Modified,
                    directoryCode + " explicit directory state and child summaries are independent of enumeration order");
                var conflicted = PlasticClient.BuildOverlayStates(root, new[] { root, folder, file }, permutation, new[] { file });
                Assert(conflicted[root] == PlasticOverlayState.Conflict && conflicted[folder] == PlasticOverlayState.Conflict && conflicted[file] == PlasticOverlayState.Conflict,
                    "Conflict outranks added/deleted directories in every order");
            }
        }
        foreach (var permutation in Permutations(new[] {
            new PlasticStatusItem { Path = folder, StatusCode = "PR" },
            new PlasticStatusItem { Path = folder, StatusCode = "IG" },
            new PlasticStatusItem { Path = addedFile, StatusCode = "AD" },
            new PlasticStatusItem { Path = addedFile, StatusCode = "CH" } }))
        {
            var mixed = PlasticClient.BuildOverlayStates(root, new[] { root, folder, file }, permutation, new string[0]);
            Assert(!mixed.ContainsKey(file) && mixed[addedFile] == PlasticOverlayState.Added && mixed[root] == PlasticOverlayState.Modified,
                "Subtree replacement removes only stale inventory and preserves explicit changes in every order");
        }
    }

    private static IEnumerable<PlasticStatusItem[]> Permutations(PlasticStatusItem[] items)
    {
        if (items.Length == 0) { yield return items; yield break; }
        for (int i = 0; i < items.Length; i++)
            foreach (var tail in Permutations(items.Where((item, index) => index != i).ToArray()))
                yield return new[] { items[i] }.Concat(tail).ToArray();
    }

    private static void Storage(string temporary)
    {
        string cache = Path.Combine(temporary, "cache"), first = Path.Combine(temporary, "first"), second = Path.Combine(temporary, "second"), nested = Path.Combine(first, "nested");
        var store = new OverlayCacheStore(cache);
        store.Track(first); store.Track(second); store.Track(first.ToUpperInvariant());
        Assert(store.TrackedRoots().Count == 2, "Tracking deduplicates case-equivalent roots");
        store.Publish(first, Entries(first, PlasticOverlayState.Normal, Path.Combine(first, "file.txt"), PlasticOverlayState.Modified));
        store.Publish(second, Entries(second, PlasticOverlayState.Conflict));
        var current = Read(store);
        Assert(current.Entries.Count == 3 && current.Entries[first] == PlasticOverlayState.Normal && current.Entries[second] == PlasticOverlayState.Conflict,
            "Independent workspace fragments aggregate");
        DateTime oldFragmentTime = DateTime.UtcNow.AddSeconds(-80);
        RewriteFragment(cache, first, new OverlaySnapshot { GeneratedUtc = oldFragmentTime, Entries = Entries(first, PlasticOverlayState.Normal) }.Encode());
        store.Publish(second, Entries(second, PlasticOverlayState.Conflict)); current = Read(store);
        Assert(current.GeneratedUtc == oldFragmentTime && current.Entries.ContainsKey(first),
            "Aggregate preserves oldest included fragment timestamp when another root refreshes");
        Reject(() => store.Publish(first, Entries(second, PlasticOverlayState.Normal)), "Fragment cannot escape owning root");
        byte[] before = File.ReadAllBytes(store.SnapshotPath);
        Reject(() => store.Publish(first, Entries(Path.Combine(first, "bad\nname"), PlasticOverlayState.Normal)), "Invalid fragment rejected before publishing");
        Assert(before.SequenceEqual(File.ReadAllBytes(store.SnapshotPath)), "Rejected fragment preserves prior aggregate bytes");
        store.Invalidate(first); current = Read(store);
        Assert(!current.Entries.ContainsKey(first) && current.Entries.ContainsKey(second), "Invalidation removes only the requested workspace");
        store.Track(nested);
        string nestedFile = Path.Combine(nested, "asset.txt");
        store.Publish(first, Entries(first, PlasticOverlayState.Normal, nested, PlasticOverlayState.Normal, nestedFile, PlasticOverlayState.Normal));
        Assert(!Read(store).Entries.ContainsKey(nestedFile), "Unscanned nested workspace suppresses outer stale entries");
        store.Publish(nested, Entries(nested, PlasticOverlayState.Modified, nestedFile, PlasticOverlayState.Conflict));
        Assert(Read(store).Entries[nestedFile] == PlasticOverlayState.Conflict, "Nested fragment overrides outer inventory");
        RewriteFragment(cache, nested, new OverlaySnapshot { GeneratedUtc = DateTime.UtcNow.AddSeconds(-95), Entries = Entries(nestedFile, PlasticOverlayState.Conflict) }.Encode());
        store.Publish(second, Entries(second, PlasticOverlayState.Normal)); current = Read(store);
        Assert(!current.Entries.ContainsKey(nestedFile) && current.Entries.ContainsKey(first) && current.Entries.ContainsKey(second),
            "Refreshing another root never renews a stale nested fragment");
        RewriteFragment(cache, first, new OverlaySnapshot { GeneratedUtc = DateTime.UtcNow.AddSeconds(-125), Entries = Entries(first, PlasticOverlayState.Normal) }.Encode());
        store.Publish(second, Entries(second, PlasticOverlayState.Modified)); current = Read(store);
        Assert(!current.Entries.ContainsKey(first) && current.Entries[second] == PlasticOverlayState.Modified, "Expired independent fragment is omitted");
        RewriteFragment(cache, nested, new byte[] { 1, 2, 3 });
        store.Publish(second, Entries(second, PlasticOverlayState.Normal));
        Assert(!Read(store).Entries.ContainsKey(nestedFile), "Corrupt nested fragment cannot revive outer inventory");
        store.Publish(first, Entries(first, PlasticOverlayState.Normal));
        string fragment = Fragment(cache, first);
        byte[] truncated = new OverlaySnapshot { Entries = Entries(first, PlasticOverlayState.Normal) }.Encode().Take(25).ToArray();
        File.WriteAllBytes(fragment, truncated);
        store.Publish(second, Entries(second, PlasticOverlayState.Normal));
        Assert(Read(store).Entries.Count == 1, "Corrupt fragment isolated from healthy workspace");
        Assert(!Directory.GetFiles(cache, "*.tmp", SearchOption.AllDirectories).Any(), "Atomic publishing leaves no temporary files");
        var bounded = new OverlayCacheStore(Path.Combine(temporary, "bounded-cache"));
        for (int i = 0; i < 64; i++) bounded.Track(Path.Combine(temporary, "tracked-" + i));
        Assert(bounded.TrackedRoots().Count == 64, "Worker root tracking bound admits 64 distinct roots");
        Reject(() => bounded.Track(Path.Combine(temporary, "tracked-65")), "65th tracked root rejected explicitly");
        Assert(bounded.TrackedRoots().Count == 64, "Rejected root does not corrupt tracked set");
    }

    private static OverlaySnapshot Read(OverlayCacheStore store) { return OverlaySnapshot.Decode(File.ReadAllBytes(store.SnapshotPath), DateTime.UtcNow); }
    private static string Fragment(string cache, string root)
    {
        using (var hash = SHA256.Create())
            return Path.Combine(cache, "overlay-workspaces", BitConverter.ToString(hash.ComputeHash(Encoding.Unicode.GetBytes(root.ToUpperInvariant()))).Replace("-", "").ToLowerInvariant() + ".bin");
    }
    private static void RewriteFragment(string cache, string root, byte[] bytes) { File.WriteAllBytes(Fragment(cache, root), bytes); }
}
