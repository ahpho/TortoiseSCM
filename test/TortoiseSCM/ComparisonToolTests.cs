// GPL-2.0-or-later. Native profiles, migration and editor lifetime use an owned child process.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class ComparisonToolTests
{
    private static int count;
    private static void Check(bool value, string message) { count++; if (!value) throw new Exception(message); }
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0].StartsWith("/base:"))
        {
            File.WriteAllLines(Path.Combine(Environment.CurrentDirectory, "native-args.txt"), args);
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "native-command-line.txt"), Environment.CommandLine);
            Thread.Sleep(350);
            return Int32.Parse(File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "exit.txt")));
        }
        string root = Path.Combine(Path.GetTempPath(), "tscm-native-" + Guid.NewGuid().ToString("N") + " 中文 & space");
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "settings.xml");
            var config = PlasticClientConfig.Load(settings);
            Check(config.UseTortoiseMerge && config.UseBeyondCompare, "Fresh application settings select bundled native profile");
            config.Save(); Check(PlasticClientConfig.Load(settings).UseTortoiseMerge, "Native default round trips");
            File.WriteAllText(settings, "<TortoiseSCM><BeyondComparePath /></TortoiseSCM>");
            Check(PlasticClientConfig.Load(settings).UseTortoiseMerge, "Legacy automatic BC setting adopts new default");
            new XDocument(new XElement("TortoiseSCM", new XElement("BeyondComparePath", Path.Combine(root, "BComp.exe")))).Save(settings);
            Check(!PlasticClientConfig.Load(settings).UseTortoiseMerge, "Explicit legacy BC path remains selected even when unavailable");
            File.WriteAllText(settings, "<TortoiseSCM><ToolProvider>BeyondCompare</ToolProvider><BeyondComparePath /></TortoiseSCM>");
            Check(!PlasticClientConfig.Load(settings).UseTortoiseMerge, "Explicit automatic BC choice survives reload");
            File.WriteAllText(settings, "<TortoiseSCM><ToolProvider>Unknown</ToolProvider></TortoiseSCM>");
            bool rejected = false; try { PlasticClientConfig.Load(settings); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Unknown provider is rejected");
            string before = Path.Combine(root, "base {local}.txt"), local = Path.Combine(root, "local.txt"), remote = Path.Combine(root, "remote.txt"), output = Path.Combine(root, "output.txt");
            var diff = new[] { "/solo", "/readonly", before, local, "/lefttitle=Old 中文", "/righttitle=New & value" };
            var translated = ComparisonTool.NativeArguments(diff, false);
            Check(translated.SequenceEqual(new[] { "/base:" + before, "/mine:" + local, "/readonly", "/basename:Old 中文", "/minename:New & value" }), "Native diff preserves literal paths titles and read-only mode");
            var merge = PlasticToolArguments.Expand(BeyondCompareTool.MergeArguments, new Dictionary<string, string> { { "base", before }, { "local", local }, { "remote", remote }, { "merged", output } }, true);
            translated = ComparisonTool.NativeArguments(merge, true);
            Check(translated.Contains("/base:" + before) && translated.Contains("/mine:" + local) && translated.Contains("/theirs:" + remote) && translated.Contains("/merged:" + output), "Three-way roles and independent result map exactly");
            Check(translated.Contains("/saverequired") && !translated.Contains("/readonly"), "Merge result is editable and asks to save");
            Check(ComparisonTool.QuoteNativeArgument("/base:" + before) == "/base:\"" + before + "\"", "Raw native syntax quotes the value after the colon");
            Check(ComparisonTool.QuoteNativeArgument("/basename:Old \"quoted\" title") == "/basename:\"Old \"\"quoted\"\" title\"", "Native title escaping follows CCmdLineParser doubled quotes");
            rejected = false; try { ComparisonTool.QuoteNativeArgument("/base:bad\0path"); } catch (ArgumentException) { rejected = true; }
            Check(rejected, "Native quoting rejects NUL instead of truncating a path");
            string exe = Path.Combine(root, "TortoiseGitMerge.exe");
            File.Copy(Assembly.GetExecutingAssembly().Location, exe);
            File.WriteAllText(Path.Combine(root, "exit.txt"), "0");
            using (var cancel = new CancellationTokenSource())
            {
                var task = ComparisonTool.RunDiffAsync(exe, diff, root, cancel.Token);
                Check(!task.IsCompleted, "Tool session retains its inputs while child is alive");
                Check(task.GetAwaiter().GetResult().Succeeded, "Native zero exit succeeds");
                Check(File.ReadAllLines(Path.Combine(root, "native-args.txt")).SequenceEqual(ComparisonTool.NativeArguments(diff, false)), "CreateProcess preserves Unicode and spaces without shell expansion");
                Check(File.ReadAllText(Path.Combine(root, "native-command-line.txt")).Contains(" /base:\"" + before + "\""), "Actual child receives raw /base:value quoting supported by TortoiseGitMerge");
            }
            File.WriteAllText(Path.Combine(root, "exit.txt"), "11");
            Check(!ComparisonTool.RunDiffAsync(exe, diff, root, CancellationToken.None).GetAwaiter().GetResult().Succeeded, "Native failure 11 is not mistaken for BC differences");
            File.WriteAllText(Path.Combine(root, "exit.txt"), "0");
            Check(ComparisonTool.RunMergeAsync(exe, merge, root, CancellationToken.None).GetAwaiter().GetResult().Succeeded, "Native merge waits for independent child");
            Check(File.ReadAllLines(Path.Combine(root, "native-args.txt")).SequenceEqual(translated), "Actual merge child receives reviewed roles");
            using (var cancel = new CancellationTokenSource())
            {
                var task = ComparisonTool.RunDiffAsync(exe, diff, root, cancel.Token);
                cancel.Cancel(); Check(!task.IsCompleted, "Cancellation does not kill the editor or release its inputs");
                bool cancelled = false; try { task.GetAwaiter().GetResult(); } catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled, "Cancellation is observed after editor exits");
            }
            Console.WriteLine("PASS: " + count + " native comparison profile assertions"); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
