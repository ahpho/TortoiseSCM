// GPL-2.0-or-later. Beyond Compare profile and configuration migration tests.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TortoiseSCM;

internal static class BeyondCompareTests
{
    private static int assertions;
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-BC-" + Guid.NewGuid().ToString("N") + " 中文 & spaces");
        Directory.CreateDirectory(root);
        try
        {
            string installed = Path.Combine(root, "Beyond Compare 5");
            Directory.CreateDirectory(installed);
            string bcomp = Path.Combine(installed, "BComp.exe"), bcompare = Path.Combine(installed, "BCompare.exe");
            // Deliberately not executable: discovery must only inspect the path, never run it.
            File.WriteAllText(bcomp, "detection fixture"); File.WriteAllText(bcompare, "GUI fixture");
            Check(BeyondCompareTool.NormalizeExecutable(bcomp) == bcomp, "BComp is preserved with Unicode and spaces");
            Check(BeyondCompareTool.NormalizeExecutable(bcompare) == bcomp, "BCompare selection resolves to waiting BComp sibling");
            Check(BeyondCompareTool.NormalizeExecutable("  " + bcomp + "  ") == bcomp, "Surrounding whitespace is normalized");
            Check(BeyondCompareTool.ResolveExecutable(bcompare) == bcomp, "Explicit configuration uses normalized waiting executable");
            Reject<ArgumentException>(() => BeyondCompareTool.NormalizeExecutable("BComp.exe"), "Relative executable rejected");
            Reject<ArgumentException>(() => BeyondCompareTool.NormalizeExecutable("C:BComp.exe"), "Drive-relative executable rejected");
            Reject<ArgumentException>(() => BeyondCompareTool.NormalizeExecutable(@"\BComp.exe"), "Root-relative executable rejected");
            Reject<ArgumentException>(() => BeyondCompareTool.NormalizeExecutable(null), "Empty explicit selection rejected");
            Reject<ArgumentException>(() => BeyondCompareTool.NormalizeExecutable(Path.Combine(installed, "other.exe")), "Other executable rejected");
            Reject<ArgumentException>(() => BeyondCompareTool.NormalizeExecutable(bcomp + " /solo"), "Executable argument suffix rejected");
            string absent = Path.Combine(root, "removed", "BComp.exe");
            Reject<FileNotFoundException>(() => BeyondCompareTool.ResolveExecutable(absent), "Stale configured path does not fall back to a detected installation");
            File.Delete(bcomp);
            Reject<FileNotFoundException>(() => BeyondCompareTool.NormalizeExecutable(bcompare), "GUI executable without waiting sibling rejected");
            File.WriteAllText(bcomp, "detection fixture");
            Check(BeyondCompareTool.FindExecutable(new[] { absent, "invalid.exe", bcompare }) == bcomp, "Detection skips stale or invalid entries and normalizes registry-style GUI candidate");
            string older = Path.Combine(root, "Beyond Compare 4", "BComp.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(older)); File.WriteAllText(older, "older");
            Check(BeyondCompareTool.FindExecutable(new[] { bcomp, older }) == bcomp, "Candidate priority is deterministic");
            Check(BeyondCompareTool.FindExecutable(new[] { absent, older }) == older, "Older installation remains usable when newer one is absent");
            Check(BeyondCompareTool.FindExecutable(new[] { absent, "invalid" }) == "", "Unavailable discovery returns empty without preventing settings use");

            var paths = new Dictionary<string, string> {
                { "base", Path.Combine(root, "base & % ! 中文.txt") },
                { "local", Path.Combine(root, "local {remote}.txt") },
                { "remote", Path.Combine(root, "remote.txt") },
                { "merged", Path.Combine(root, "output result.txt") }
            };
            var diff = PlasticToolArguments.Expand(BeyondCompareTool.DiffArguments, paths, false);
            Check(diff.SequenceEqual(new[] { "/solo", "/readonly", paths["base"], paths["local"], "/lefttitle=Base", "/righttitle=Local" }), "Diff profile preserves each path as one literal argument with named sides");
            var merge = PlasticToolArguments.Expand(BeyondCompareTool.MergeArguments, paths, true);
            Check(merge.SequenceEqual(new[] { "/solo", "/readonly", paths["local"], paths["remote"], paths["base"], "/mergeoutput=" + paths["merged"], "/lefttitle=Local", "/righttitle=Remote", "/centertitle=Base", "/outputtitle=Merged" }), "Merge uses BC local/remote/base order, read-only inputs, separate output and named sides");
            Check(!merge.Any(a => a.IndexOf("automerge", StringComparison.OrdinalIgnoreCase) >= 0), "Profile never requests unattended automatic merge");

            string settings = Path.Combine(root, "settings.xml");
            Check(PlasticClientConfig.Load(settings).UseBeyondCompare, "Fresh settings always activate supported profile");
            File.WriteAllText(settings, "<TortoiseSCM><UseBuiltInDiff>true</UseBuiltInDiff><UseBuiltInMerge>true</UseBuiltInMerge><DiffToolPath>removed.exe</DiffToolPath><DiffToolArguments>old diff</DiffToolArguments><MergeToolPath>removed-merge.exe</MergeToolPath><MergeToolArguments>old merge</MergeToolArguments><UseBeyondCompare>false</UseBeyondCompare></TortoiseSCM>");
            var config = PlasticClientConfig.Load(settings);
            Check(config.UseBeyondCompare, "Legacy XML cannot opt out of BC profile");
            Check(config.UseBuiltInDiff && config.UseBuiltInMerge && config.DiffToolPath == "removed.exe" && config.MergeToolArguments == "old merge", "Migration preserves dormant legacy fields");
            config.CmPath = "cm-configured.exe"; config.GluonPath = "gluon-configured.exe";
            config.Timeout = TimeSpan.FromSeconds(93); config.Save();
            var loaded = PlasticClientConfig.Load(settings);
            Check(loaded.UseBeyondCompare && loaded.BeyondComparePath == "", "Auto-detection setting round trips without requiring BC installed");
            Check(loaded.CmPath == config.CmPath && loaded.GluonPath == config.GluonPath && loaded.Timeout == config.Timeout, "Unrelated SCM configuration remains usable");
            Check(loaded.UseBuiltInDiff && loaded.UseBuiltInMerge && loaded.DiffToolPath == "removed.exe" && loaded.MergeToolPath == "removed-merge.exe" && loaded.DiffToolArguments == "old diff" && loaded.MergeToolArguments == "old merge", "Saving migrated configuration retains all dormant choices");
            Check(XDocument.Load(settings).Root.Element("UseBeyondCompare") == null, "No unsupported profile opt-out is emitted");
            loaded.BeyondComparePath = bcompare; loaded.Save();
            Check(PlasticClientConfig.Load(settings).BeyondComparePath == bcomp, "Selected BCompare persists normalized BComp path");
            string before = File.ReadAllText(settings);
            loaded.BeyondComparePath = absent;
            Reject<FileNotFoundException>(() => loaded.Save(), "Explicit missing installation fails settings validation");
            Check(File.ReadAllText(settings) == before, "Failed validation preserves previous configuration bytes");
            var stale = XDocument.Load(settings); stale.Root.SetElementValue("BeyondComparePath", absent); stale.Save(settings);
            loaded = PlasticClientConfig.Load(settings);
            Check(loaded.UseBeyondCompare && loaded.BeyondComparePath == absent && loaded.CmPath == config.CmPath, "Stale tool path loads successfully for unrelated SCM operations");
            Reject<FileNotFoundException>(() => BeyondCompareTool.ResolveExecutable(loaded.BeyondComparePath), "Actual tool resolution exposes stale installation");
            new XDocument(new XElement("TortoiseSCM", new XElement("MergeToolPath", bcompare), new XElement("DiffToolPath", absent))).Save(settings);
            loaded = PlasticClientConfig.Load(settings);
            Check(loaded.BeyondComparePath == bcompare && BeyondCompareTool.ResolveExecutable(loaded.BeyondComparePath) == bcomp, "Legacy explicit BC installation migrates with merge path priority");
            var migration = XDocument.Load(settings); migration.Root.SetElementValue("BeyondComparePath", ""); migration.Save(settings);
            Check(PlasticClientConfig.Load(settings).BeyondComparePath == "", "Explicit automatic detection is not replaced by dormant paths");
            migration.Root.Element("BeyondComparePath").Remove(); migration.Root.Element("MergeToolPath").Remove(); migration.Save(settings);
            Check(PlasticClientConfig.Load(settings).BeyondComparePath == absent, "Stale legacy BC path is retained for diagnosis");
            Console.WriteLine("PASS: " + assertions + " Beyond Compare profile assertions");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }

    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject<T>(Action action, string message) where T : Exception
    { bool rejected = false; try { action(); } catch (T) { rejected = true; } Check(rejected, message); }
}
