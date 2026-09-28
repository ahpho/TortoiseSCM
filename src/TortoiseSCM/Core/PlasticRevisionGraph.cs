// Copyright (C) 2026 TortoiseSCM contributors. GPL-2.0-or-later.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TortoiseSCM
{
    public sealed class PlasticGraphNode
    {
        public long Id { get; set; }
        public long Changeset { get; set; }
        public long? ParentChangeset { get; set; }
        public string Guid { get; set; }
        public string Branch { get; set; }
        public string Owner { get; set; }
        public string Date { get; set; }
        public string Comment { get; set; }
        public string Repository { get; set; }
    }

    public sealed class PlasticGraphEdge
    {
        public long? Id { get; set; }
        public string Kind { get; set; }
        public long SourceChangeset { get; set; }
        public long DestinationChangeset { get; set; }
        public long? BaseChangeset { get; set; }
        public bool SourceLoaded { get; set; }
        public bool BaseLoaded { get; set; }
    }

    public sealed class PlasticRevisionGraphPage
    {
        public string Repository { get; set; }
        public IList<PlasticGraphNode> Nodes { get; set; }
        public IList<PlasticGraphEdge> Edges { get; set; }
        public bool HasMore { get; set; }
        public long? NextBeforeChangeset { get; set; }
    }

    public sealed partial class PlasticClient
    {
        public Task<PlasticChangesetDetails> GetGraphChangesetAsync(string path, long changeset, string expectedRepository, CancellationToken token)
        { return GetLabelChangesetAsync(path, changeset, expectedRepository, token); }

        public async Task<PlasticRevisionGraphPage> GetRevisionGraphAsync(string path, long? beforeChangeset, int limit,
            string expectedRepository, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (limit < 1 || limit > 100) throw new ArgumentOutOfRangeException("limit", "Load between 1 and 100 graph nodes per page.");
            if (beforeChangeset.HasValue && beforeChangeset.Value < 0) throw new ArgumentOutOfRangeException("beforeChangeset");
            // Capture synchronously before the first await; queries remain pinned even if
            // another client switches this workspace while native discovery is queued.
            var context = DiscoverWorkspace(path);
            if (context == null) throw new InvalidOperationException("The selected path is not in a Plastic SCM workspace.");
            ValidateBranchRepository(context.Repository);
            if (context.Repository.Contains("'")) throw new InvalidDataException("The repository cannot safely be represented in a graph query.");
            if (expectedRepository != null && context.Repository != expectedRepository)
                throw new InvalidOperationException("The graph repository changed. Refresh before continuing.");
            var command = await BuildReadCommandAsync(path, token).ConfigureAwait(false);
            ValidateGraphContext(context);
            if (!SamePath(command.WorkingDirectory, context.RootPath)) throw new InvalidOperationException("The graph workspace root changed.");
            var page = new PlasticRevisionGraphPage { Repository = context.Repository, Nodes = new List<PlasticGraphNode>(), Edges = new List<PlasticGraphEdge>() };
            if (beforeChangeset == 0) { token.ThrowIfCancellationRequested(); return page; }
            string repositoryClause = " on repository '" + context.Repository + "'";
            string query = (beforeChangeset.HasValue ? "where changesetid < " + beforeChangeset.Value.ToString(CultureInfo.InvariantCulture) + " " : "") +
                "order by changesetid desc limit " + (limit + 1).ToString(CultureInfo.InvariantCulture) + repositoryClause;
            var result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "find", "changeset", query, "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(result); ValidateGraphContext(context); token.ThrowIfCancellationRequested();
            var candidates = ParseGraphNodes(result.Output, context.Repository);
            if (candidates.Count > limit + 1 || (beforeChangeset.HasValue && candidates.Any(n => n.Changeset >= beforeChangeset.Value)))
                throw new InvalidDataException("The server returned an invalid graph page or ignored its limit.");
            page.Nodes = candidates.Take(limit).ToList();
            page.HasMore = candidates.Count > limit;
            if (page.HasMore) page.NextBeforeChangeset = page.Nodes.Last().Changeset;
            if (page.Nodes.Count == 0) { ValidateGraphContext(context); token.ThrowIfCancellationRequested(); return page; }
            // Only selected destinations enter the query. A sentinel row detects an
            // over-budget response; never publish a falsely complete truncated graph.
            query = "where (" + String.Join(" or ", page.Nodes.Select(n => "dstchangeset = " + n.Changeset.ToString(CultureInfo.InvariantCulture))) + ") limit 1001" + repositoryClause;
            ValidateGraphContext(context);
            result = await ExecuteAsync(RevisionCommand(context.RootPath, new[] { "find", "merge", query, "--xml", "--encoding=utf-8", "--nototal" }), token).ConfigureAwait(false);
            RequireSuccess(result); ValidateGraphContext(context); token.ThrowIfCancellationRequested();
            page.Edges = ParseGraphEdges(result.Output, page.Nodes);
            ValidateGraphContext(context); token.ThrowIfCancellationRequested();
            return page;
        }

        private void ValidateGraphContext(PlasticWorkspace expected)
        {
            var current = DiscoverWorkspace(expected.RootPath);
            if (current == null || !SamePath(current.RootPath, expected.RootPath) || current.Repository != expected.Repository ||
                current.IsPartial != expected.IsPartial || current.Name != expected.Name ||
                NormalizeMergeSelector(current.Selector) != NormalizeMergeSelector(expected.Selector))
                throw new InvalidOperationException("The workspace selector changed while loading the revision graph. Refresh before continuing.");
        }

        internal static IList<PlasticGraphNode> ParseGraphNodes(string xml, string repository)
        {
            var rows = GraphRows(xml, "CHANGESET");
            var result = new List<PlasticGraphNode>();
            var numbers = new HashSet<long>(); var ids = new HashSet<long>(); var guids = new HashSet<Guid>();
            foreach (var row in rows)
            {
                long id = GraphNumber(row, "ID", 1), number = GraphNumber(row, "CHANGESETID", 0), parent = GraphNumber(row, "PARENT", -1);
                Guid guid;
                string guidText = GraphField(row, "GUID"), branch = GraphField(row, "BRANCH");
                if (!Guid.TryParseExact(guidText, "D", out guid) || guid == Guid.Empty || !guids.Add(guid) || !ids.Add(id) || !numbers.Add(number) || parent == number)
                    throw new InvalidDataException("Invalid or duplicate changeset identity in revision graph.");
                string repositoryName = GraphField(row, "REPNAME");
                if (repositoryName + "@" + GraphField(row, "REPSERVER") != repository || GraphField(row, "REPOSITORY") != repositoryName)
                    throw new InvalidDataException("Graph changeset belongs to another repository.");
                try { ValidateBranchName(branch); } catch (ArgumentException error) { throw new InvalidDataException("Invalid graph branch.", error); }
                string date = GraphField(row, "DATE"); DateTimeOffset parsedDate;
                if (!DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedDate)) throw new InvalidDataException("Invalid graph changeset date.");
                result.Add(new PlasticGraphNode { Id = id, Changeset = number, ParentChangeset = parent == -1 ? (long?)null : parent,
                    Guid = guidText, Branch = branch, Repository = repository, Owner = GraphField(row, "OWNER"), Date = date, Comment = GraphField(row, "COMMENT") });
            }
            // Reject unexpected ordering rather than manufacture a cursor that could skip rows.
            for (int i = 1; i < result.Count; ++i) if (result[i - 1].Changeset <= result[i].Changeset) throw new InvalidDataException("Graph changesets are not in descending order.");
            return result;
        }

        internal static IList<PlasticGraphEdge> ParseGraphEdges(string xml, IList<PlasticGraphNode> nodes)
        {
            var rows = GraphRows(xml, "MERGE");
            if (rows.Count > 1000) throw new InvalidDataException("This graph page exceeds 1000 merge links. Load fewer changesets; no incomplete graph was returned.");
            var byNumber = nodes.ToDictionary(n => n.Changeset);
            var byId = nodes.ToDictionary(n => n.Id);
            var edges = nodes.Where(n => n.ParentChangeset.HasValue).Select(n => new PlasticGraphEdge { Kind = "parent", SourceChangeset = n.ParentChangeset.Value,
                DestinationChangeset = n.Changeset, SourceLoaded = byNumber.ContainsKey(n.ParentChangeset.Value) }).ToList();
            var ids = new HashSet<long>();
            foreach (var row in rows)
            {
                long id = GraphNumber(row, "ID", 1), source = GraphNumber(row, "SRCCHANGESET", 0), destination = GraphNumber(row, "DSTCHANGESET", 0);
                long sourceId = GraphNumber(row, "SRCID", 1), destinationId = GraphNumber(row, "DSTID", 1);
                string kind = GraphField(row, "TYPE"), baseText = GraphField(row, "BASECHANGESET");
                long? baseNumber = baseText.Length == 0 ? (long?)null : GraphNumber(row, "BASECHANGESET", 0);
                PlasticGraphNode sourceNode = null, destinationNode, idNode;
                if (!ids.Add(id) || source == destination || sourceId == destinationId || !byNumber.TryGetValue(destination, out destinationNode) ||
                    destinationNode.Id != destinationId || (byNumber.TryGetValue(source, out sourceNode) && sourceNode.Id != sourceId) ||
                    (byId.TryGetValue(sourceId, out idNode) && idNode.Changeset != source))
                    throw new InvalidDataException("Invalid merge identity or destination in revision graph.");
                if (String.IsNullOrWhiteSpace(kind) || kind.Any(c => !Char.IsLetterOrDigit(c) && c != '_' && c != '-') || kind == "parent")
                    throw new InvalidDataException("Invalid native merge type.");
                // Keep unknown native types visible as their literal type; they are never
                // silently represented as normal merges or primary ancestry.
                if (kind.StartsWith("interval", StringComparison.Ordinal) && !baseNumber.HasValue)
                    throw new InvalidDataException("An interval merge is missing its base changeset.");
                if (GraphField(row, "DSTBRANCH") != "br:" + destinationNode.Branch ||
                    (sourceNode != null && GraphField(row, "SRCBRANCH") != "br:" + sourceNode.Branch))
                    throw new InvalidDataException("Merge branch and changeset identities disagree.");
                edges.Add(new PlasticGraphEdge { Id = id, Kind = kind, SourceChangeset = source, DestinationChangeset = destination,
                    BaseChangeset = baseNumber, SourceLoaded = byNumber.ContainsKey(source), BaseLoaded = baseNumber.HasValue && byNumber.ContainsKey(baseNumber.Value) });
            }
            // Only ordinary merge and primary parent links express ancestry. Cherry-pick
            // and subtractive intervals must not create invented ancestor relationships.
            var incoming = edges.Where(e => e.SourceLoaded && (e.Kind == "parent" || e.Kind == "merge"))
                .GroupBy(e => e.DestinationChangeset).ToDictionary(g => g.Key, g => g.Select(e => e.SourceChangeset).ToList());
            var marks = new Dictionary<long, int>();
            foreach (var node in nodes) VisitGraphNode(node.Changeset, incoming, marks);
            return edges;
        }

        private static void VisitGraphNode(long number, IDictionary<long, List<long>> incoming, IDictionary<long, int> marks)
        {
            int mark;
            if (marks.TryGetValue(number, out mark)) { if (mark == 1) throw new InvalidDataException("Cycle in revision graph ancestry."); return; }
            marks[number] = 1; List<long> parents;
            if (incoming.TryGetValue(number, out parents)) foreach (long parent in parents) VisitGraphNode(parent, incoming, marks);
            marks[number] = 2;
        }

        private static List<XElement> GraphRows(string xml, string name)
        {
            var document = SafeXml.Load(xml);
            if (document.Root == null || document.Root.Name != "PLASTICQUERY" || document.Root.Elements().Any(e => e.Name != name))
                throw new InvalidDataException("Unexpected revision graph XML.");
            return document.Root.Elements().ToList();
        }
        private static string GraphField(XElement row, string name)
        {
            var fields = row.Elements(name).ToList();
            if (fields.Count != 1 || fields[0].HasElements || fields[0].HasAttributes) throw new InvalidDataException("Missing or ambiguous graph field: " + name);
            return fields[0].Value;
        }
        private static long GraphNumber(XElement row, string name, long minimum)
        {
            long value;
            if (!Int64.TryParse(GraphField(row, name), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value) || value < minimum)
                throw new InvalidDataException("Invalid graph number: " + name);
            return value;
        }
    }
}
