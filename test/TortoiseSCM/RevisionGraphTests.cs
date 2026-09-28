// GPL-2.0-or-later. Deterministic revision-graph tests; native process is this executable.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using TortoiseSCM;

internal static class RevisionGraphTests
{
    private const string Repository = "graph@local:8087";
    private static int assertions;
    private static int Main(string[] args)
    {
        if (args.Length > 0) return Fake(args);
        string root = Path.Combine(Path.GetTempPath(), "TortoiseSCM-graph-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, ".plastic"));
        try
        {
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "graph\nguid\nStandard\n");
            Selector(root);
            var client = new PlasticClient(new PlasticClientConfig { CmPath = Assembly.GetExecutingAssembly().Location, Timeout = TimeSpan.FromSeconds(5) });
            var page = client.GetRevisionGraphAsync(root, null, 3, Repository, CancellationToken.None).GetAwaiter().GetResult();
            Check(page.Nodes.Select(n => n.Changeset).SequenceEqual(new long[] {139,138,137}) && page.HasMore && page.NextBeforeChangeset == 137, "Bounded page and exclusive continuation");
            Check(page.Edges.Count == 4 && page.Edges.Single(e => e.Kind == "merge").SourceChangeset == 137, "Actual merge joins the diamond");
            Check(page.Edges.Count(e => !e.SourceLoaded) == 2 && page.Edges.All(e => !e.BaseLoaded), "Off-page ancestry is explicit");
            string calls = File.ReadAllText(Path.Combine(root, ".plastic", "calls"));
            Check(calls.Contains("order by changesetid desc limit 4 on repository '" + Repository + "'") && calls.Contains("where (dstchangeset = 139 or dstchangeset = 138 or dstchangeset = 137) limit 1001"), "Queries pin repository and exact destinations");
            Check(!calls.Contains("dstchangeset = 136"), "Sentinel node is not fetched as a destination");
            File.WriteAllText(Path.Combine(root, ".plastic", "plastic.workspace"), "graph\nguid\nPartial\n");
            Check(client.GetRevisionGraphAsync(root, 140, 4, Repository, CancellationToken.None).GetAwaiter().GetResult().Nodes.Count == 4, "Partial graph is the same readonly repository query");
            int beforeCalls = File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Length;
            Check(client.GetRevisionGraphAsync(root, 0, 1, Repository, CancellationToken.None).GetAwaiter().GetResult().Nodes.Count == 0, "Zero cursor is exhausted");
            Check(File.ReadAllLines(Path.Combine(root, ".plastic", "calls")).Length == beforeCalls, "Exhausted cursor makes no native call");
            Reject<ArgumentOutOfRangeException>(() => client.GetRevisionGraphAsync(root, null, 101, Repository, CancellationToken.None).GetAwaiter().GetResult(), "Page cap required");
            Reject<ArgumentOutOfRangeException>(() => client.GetRevisionGraphAsync(root, -1, 1, Repository, CancellationToken.None).GetAwaiter().GetResult(), "Negative cursor refused");
            Reject<InvalidOperationException>(() => client.GetRevisionGraphAsync(root, null, 1, "other@server", CancellationToken.None).GetAwaiter().GetResult(), "Expected repository pinned");
            Reject<OperationCanceledException>(() => client.GetRevisionGraphAsync(root, null, 1, Repository, new CancellationToken(true)).GetAwaiter().GetResult(), "Cancellation before query");
            var nodes = PlasticClient.ParseGraphNodes(Wrap(Node(139,138), Node(138,136), Node(137,136), Node(136,0)), Repository);
            var edges = PlasticClient.ParseGraphEdges(Wrap(Merge(700,137,139,"intervalcherrypicksubtractive","136")), nodes);
            Check(edges.Last().Kind == "intervalcherrypicksubtractive" && edges.Last().BaseChangeset == 136 && edges.Last().BaseLoaded, "Subtractive interval retains literal type and base");
            Check(PlasticClient.ParseGraphEdges(Wrap(Merge(700,137,139,"future_kind","")), nodes).Last().Kind == "future_kind", "Unknown native type stays explicit");
            Check(PlasticClient.ParseGraphNodes(Wrap(Node(0,-1)), Repository).Single().ParentChangeset == null, "Native root -1 is no parent");
            var gaps=PlasticClient.ParseGraphNodes(Wrap(Node(139,136),Node(136,0)),Repository);
            Check(PlasticClient.ParseGraphEdges(Wrap(),gaps).First().SourceChangeset==136,"A numbering gap never invents adjacent parent links");
            Check(PlasticClient.ParseGraphEdges(Wrap(Merge(700,90,139,"intervalcherrypick","0")),nodes).Last().BaseChangeset==0,"Root base zero is distinct from absent base");
            BadNode(Node(139,139), "Self parent");
            var bad = Node(139,138); bad.Element("PARENT").Remove(); BadNode(bad,"Missing parent");
            bad = Node(139,138); bad.Add(new XElement("ID",1)); BadNode(bad,"Duplicate field");
            bad = Node(139,138); bad.Element("GUID").Value="invalid"; BadNode(bad,"Invalid GUID");
            bad = Node(139,138); bad.Element("DATE").Value="invalid"; BadNode(bad,"Invalid date");
            bad = Node(139,138); bad.Element("REPSERVER").Value="foreign"; BadNode(bad,"Foreign repository");
            bad = Node(139,138); bad.Element("REPOSITORY").Value="foreign"; BadNode(bad,"Inconsistent repository alias");
            bad = Node(139,138); bad.Element("PARENT").Value="-2"; BadNode(bad,"Invalid parent sentinel");
            bad = Node(139,138); bad.Element("CHANGESETID").Value="9223372036854775808"; BadNode(bad,"Overflowing changeset number");
            bad = Node(138,136); bad.Element("ID").Value="5139";
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphNodes(Wrap(Node(139,138),bad),Repository),"Shared internal ID refused");
            bad = Node(138,136); bad.Element("GUID").Value=Node(139,138).Element("GUID").Value;
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphNodes(Wrap(Node(139,138),bad),Repository),"Shared GUID refused");
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphNodes(Wrap(Node(138,136),Node(139,138)),Repository),"Unordered cursor response refused");
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphNodes(Wrap(Node(139,138),Node(139,138)),Repository),"Duplicate node refused");
            foreach (var invalid in new[] { Merge(700,137,999,"merge",""), Merge(700,137,139,"parent",""), Merge(700,137,139,"interval",""), Merge(700,139,139,"merge","") })
                Reject<InvalidDataException>(() => PlasticClient.ParseGraphEdges(Wrap(invalid),nodes),"Malformed merge refused");
            var wrongId = Merge(700,137,139,"merge",""); wrongId.Element("SRCID").Value="1";
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphEdges(Wrap(wrongId),nodes),"Source internal ID must match loaded node");
            wrongId = Merge(700,137,139,"merge",""); wrongId.Element("DSTID").Value="1";
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphEdges(Wrap(wrongId),nodes),"Destination internal ID must match loaded node");
            wrongId = Merge(700,90,139,"merge",""); wrongId.Element("SRCID").Value="5137";
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphEdges(Wrap(wrongId),nodes),"Off-page source cannot reuse another loaded identity");
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphEdges(Wrap(Merge(700,137,139,"merge",""),Merge(700,137,139,"merge","")),nodes),"Duplicate merge identity refused");
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphEdges(Wrap(Merge(700,139,138,"merge","")),nodes),"Ancestry cycle refused");
            Reject<InvalidDataException>(() => PlasticClient.ParseGraphEdges(Wrap(Enumerable.Range(0,1001).Select(i => Merge(700+i,137,139,"merge","")).ToArray()),nodes),"1001 merge sentinel refuses incomplete graph");
            foreach (string mode in new[] { "switch-nodes", "switch-edges", "repository", "cursor", "cap", "fail-nodes", "fail-edges" })
            {
                Selector(root); File.WriteAllText(Path.Combine(root,".plastic","mode"),mode);
                if (mode.StartsWith("fail")) Reject<PlasticCommandException>(() => client.GetRevisionGraphAsync(root,140,3,Repository,CancellationToken.None).GetAwaiter().GetResult(),"Native failure never becomes empty graph");
                else if (mode.StartsWith("switch") || mode=="repository") Reject<InvalidOperationException>(() => client.GetRevisionGraphAsync(root,140,3,Repository,CancellationToken.None).GetAwaiter().GetResult(),"Context change invalidates graph");
                else Reject<InvalidDataException>(() => client.GetRevisionGraphAsync(root,140,3,Repository,CancellationToken.None).GetAwaiter().GetResult(),"Invalid native page refused");
            }
            Selector(root); File.WriteAllText(Path.Combine(root,".plastic","mode"),"empty");
            page=client.GetRevisionGraphAsync(root,null,3,Repository,CancellationToken.None).GetAwaiter().GetResult();
            Check(page.Nodes.Count==0 && page.Edges.Count==0 && !page.HasMore && !page.NextBeforeChangeset.HasValue,"Empty native repository page is complete");
            File.WriteAllText(Path.Combine(root,".plastic","mode"),"slow");
            using(var cancellation=new CancellationTokenSource())
            {
                var pending=client.GetRevisionGraphAsync(root,null,3,Repository,cancellation.Token);
                cancellation.CancelAfter(150);
                Reject<OperationCanceledException>(()=>pending.GetAwaiter().GetResult(),"Cancellation during native query publishes no partial graph");
            }
            Console.WriteLine("PASS: " + assertions + " revision graph assertions"); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root,true); }
    }
    private static void Selector(string root) { File.WriteAllText(Path.Combine(root,".plastic","plastic.selector"),"repository \""+Repository+"\"\n path \"/\"\n smartbranch \"/main\"\n"); }
    private static XElement Node(long cs,long parent)
    {
        return new XElement("CHANGESET",new XElement("ID",cs+5000),new XElement("CHANGESETID",cs),new XElement("PARENT",parent),
            new XElement("GUID","00000000-0000-0000-0001-"+cs.ToString("D12",CultureInfo.InvariantCulture)),new XElement("BRANCH","/main"),
            new XElement("REPOSITORY","graph"),new XElement("REPNAME","graph"),new XElement("REPSERVER","local:8087"),new XElement("DATE","2026-09-28T01:00:00+08:00"),
            new XElement("OWNER","owner"),new XElement("COMMENT","graph fixture"));
    }
    private static XElement Merge(long id,long src,long dst,string type,string basis)
    { return new XElement("MERGE",new XElement("ID",id),new XElement("SRCID",src+5000),new XElement("DSTID",dst+5000),new XElement("SRCCHANGESET",src),new XElement("DSTCHANGESET",dst),new XElement("BASECHANGESET",basis),new XElement("TYPE",type),new XElement("SRCBRANCH","br:/main"),new XElement("DSTBRANCH","br:/main")); }
    private static string Wrap(params XElement[] rows) { return new XElement("PLASTICQUERY",rows).ToString(); }
    private static void BadNode(XElement row,string message) { Reject<InvalidDataException>(()=>PlasticClient.ParseGraphNodes(Wrap(row),Repository),message); }
    private static int Fake(string[] args)
    {
        Console.OutputEncoding=new UTF8Encoding(false); string metadata=Path.Combine(Environment.CurrentDirectory,".plastic");
        File.AppendAllText(Path.Combine(metadata,"calls"),String.Join("\t",args)+"\n");
        if(args[0]!="find" || args.Length!=6 || !args[2].Contains("on repository '"+Repository+"'"))return 90;
        string mode=File.Exists(Path.Combine(metadata,"mode"))?File.ReadAllText(Path.Combine(metadata,"mode")):"";
        bool nodes=args[1]=="changeset";
        if(mode==(nodes?"fail-nodes":"fail-edges"))return 7;
        if(mode==(nodes?"switch-nodes":"switch-edges"))File.AppendAllText(Path.Combine(metadata,"plastic.selector"),"changeset \"1\"\n");
        if(mode=="repository")File.WriteAllText(Path.Combine(metadata,"plastic.selector"),"repository \"foreign@local\"");
        if(mode=="slow")Thread.Sleep(3000);
        if(mode=="empty"){Console.Write(Wrap());return 0;}
        if(nodes)Console.Write(Wrap(mode=="cursor"?Node(140,138):Node(139,138),Node(138,136),Node(137,136),Node(136,0)));
        else Console.Write(mode=="cap"?Wrap(Enumerable.Range(0,1001).Select(i=>Merge(700+i,137,139,"merge","")).ToArray()):Wrap(Merge(700,137,139,"merge","")));
        return 0;
    }
    private static void Check(bool value,string message) { assertions++; if(!value)throw new Exception(message); }
    private static void Reject<T>(Action action,string message) where T:Exception { try{action();}catch(T){assertions++;return;}throw new Exception(message); }
}
