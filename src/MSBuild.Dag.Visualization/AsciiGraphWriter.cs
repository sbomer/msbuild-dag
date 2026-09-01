using System.Globalization;
using System.Text;
using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization;

public static class AsciiGraphWriter
{
    private const int HorizontalGap = 10;

    public static string Render(OperationGraph graph)
    {
        return Render(
            graph,
            "OperationGraph",
            labelProvider: null,
            contentProvider: null);
    }

    public static string Render(
        BuildGraph graph,
        IReadOnlyDictionary<Target, string>? targetNames = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var adapter = BuildGraphRenderingAdapter.Create(graph, targetNames);
        return Render(
            adapter.Graph,
            "BuildGraph",
            adapter.GetLabel,
            contentProvider: null);
    }

    public static string RenderExpanded(
        BuildGraph graph,
        IReadOnlyDictionary<Target, string>? targetNames = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var adapter = BuildGraphRenderingAdapter.Create(
            graph,
            targetNames,
            includeTargetBodies: true);
        return Render(
            adapter.Graph,
            "BuildGraph",
            adapter.GetLabel,
            adapter.GetContent);
    }

    public static void Write(OperationGraph graph, TextWriter writer)
    {
        Write(
            graph,
            writer,
            "OperationGraph",
            labelProvider: null,
            contentProvider: null);
    }

    public static void Write(
        BuildGraph graph,
        TextWriter writer,
        IReadOnlyDictionary<Target, string>? targetNames = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var adapter = BuildGraphRenderingAdapter.Create(graph, targetNames);
        Write(
            adapter.Graph,
            writer,
            "BuildGraph",
            adapter.GetLabel,
            contentProvider: null);
    }

    public static void WriteExpanded(
        BuildGraph graph,
        TextWriter writer,
        IReadOnlyDictionary<Target, string>? targetNames = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(writer);

        var adapter = BuildGraphRenderingAdapter.Create(
            graph,
            targetNames,
            includeTargetBodies: true);
        Write(
            adapter.Graph,
            writer,
            "BuildGraph",
            adapter.GetLabel,
            adapter.GetContent);
    }

    private static string Render(
        OperationGraph graph,
        string heading,
        Func<Operation, string>? labelProvider,
        Func<Operation, IReadOnlyList<string>?>? contentProvider)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(graph, writer, heading, labelProvider, contentProvider);
        return writer.ToString();
    }

    private static void Write(
        OperationGraph graph,
        TextWriter writer,
        string heading,
        Func<Operation, string>? labelProvider,
        Func<Operation, IReadOnlyList<string>?>? contentProvider)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine(heading);

        if (graph.Operations.Count == 0)
        {
            writer.WriteLine("(empty)");
            return;
        }

        var layout = Layout.Create(graph, labelProvider, contentProvider);
        var canvas = new Canvas(layout.Width, layout.Height);

        foreach (var edge in layout.Edges)
        {
            DrawEdge(canvas, edge);
        }

        foreach (var node in layout.Nodes)
        {
            DrawNode(canvas, node);
        }

        foreach (var edge in layout.Edges)
        {
            DrawEdgeEndpoints(canvas, edge);
        }

        canvas.WriteTo(writer);
    }

    private static void DrawEdge(Canvas canvas, Edge edge)
    {
        var sourceX = edge.Source.GetOutputX(edge.SourceSlot);
        var sourceY = edge.Source.Bottom;
        var targetX = edge.Target.GetInputX(edge.TargetSlot);
        var targetY = edge.Target.Top;
        var firstRouteY = sourceY + 2;
        var lastRouteY = targetY - 2;
        var horizontalStroke = edge.IsOrderEdge ? '╌' : '─';
        var verticalStroke = edge.IsOrderEdge ? '╎' : '│';

        canvas.DrawVertical(sourceX, sourceY, firstRouteY, verticalStroke);
        canvas.DrawVertical(targetX, lastRouteY, targetY, verticalStroke);

        if (edge.LaneX is int laneX)
        {
            var laneStartY = edge.DepartureY!.Value;
            var laneEndY = edge.ArrivalY!.Value;
            canvas.DrawHorizontal(
                laneStartY,
                sourceX,
                laneX,
                horizontalStroke);
            canvas.DrawVertical(
                laneX,
                laneStartY,
                laneEndY,
                verticalStroke);
            canvas.DrawHorizontal(
                laneEndY,
                laneX,
                targetX,
                horizontalStroke);
            canvas.DrawVertical(
                targetX,
                laneEndY,
                lastRouteY,
                verticalStroke);
            canvas.Overwrite(
                sourceX,
                laneStartY,
                laneX > sourceX ? '└' : '┘');
            canvas.Overwrite(
                laneX,
                laneStartY,
                laneX > sourceX ? '┐' : '┌');
            canvas.Overwrite(
                laneX,
                laneEndY,
                targetX > laneX ? '└' : '┘');
            canvas.Overwrite(
                targetX,
                laneEndY,
                targetX > laneX ? '┐' : '┌');
            return;
        }

        if (sourceX == targetX)
        {
            canvas.DrawVertical(
                sourceX,
                firstRouteY,
                lastRouteY,
                verticalStroke);
            return;
        }

        var middleY = Math.Min(
            lastRouteY,
            firstRouteY + (edge.RouteLane ?? 0));
        canvas.DrawVertical(
            sourceX,
            firstRouteY,
            middleY,
            verticalStroke);
        canvas.DrawHorizontal(
            middleY,
            sourceX,
            targetX,
            horizontalStroke);
        canvas.DrawVertical(
            targetX,
            middleY,
            lastRouteY,
            verticalStroke);
        canvas.Overwrite(
            sourceX,
            middleY,
            targetX > sourceX ? '└' : '┘');
        canvas.Overwrite(
            targetX,
            middleY,
            targetX > sourceX ? '┐' : '┌');
    }

    private static void DrawEdgeEndpoints(Canvas canvas, Edge edge)
    {
        var sourceX = edge.Source.GetOutputX(edge.SourceSlot);
        var targetX = edge.Target.GetInputX(edge.TargetSlot);

        canvas.Overwrite(sourceX, edge.Source.Bottom, '┬');
        canvas.Overwrite(targetX, edge.Target.Top, '┴');
        canvas.Overwrite(targetX, edge.Target.Top - 1, '▼');

        var edgeLabel = edge.IsOrderEdge
            ? "order"
            : edge.IsGuardEdge
                ? "guard"
            : $"o{edge.SourcePort!.Value}";
        canvas.Write(sourceX + 1, edge.Source.Bottom + 1, edgeLabel);

        if (!edge.IsOrderEdge && edge.TargetPort is int targetPort)
        {
            canvas.Write(
                targetX + 1,
                edge.Target.Top - 1,
                $"i{targetPort}");
        }
    }

    private static void DrawNode(Canvas canvas, Node node)
    {
        canvas.Clear(node.Left, node.Top, node.Right, node.Bottom);
        canvas.DrawHorizontal(node.Top, node.Left, node.Right);
        canvas.DrawHorizontal(node.Bottom, node.Left, node.Right);
        canvas.Overwrite(node.Left, node.Top, '┌');
        canvas.Overwrite(node.Right, node.Top, '┐');
        canvas.Overwrite(node.Left, node.Bottom, '└');
        canvas.Overwrite(node.Right, node.Bottom, '┘');
        canvas.DrawVertical(node.Left, node.Top + 1, node.Bottom - 1);
        canvas.DrawVertical(node.Right, node.Top + 1, node.Bottom - 1);
        canvas.Write(node.Left + 2, node.Top + 1, node.Label);

        for (var index = 0; index < node.Content.Count; index++)
        {
            canvas.Write(
                node.Left + 2,
                node.Top + 2 + index,
                node.Content[index]);
        }
    }

    private sealed class Layout(
        IReadOnlyList<Node> nodes,
        IReadOnlyList<Edge> edges,
        int width,
        int height)
    {
        public IReadOnlyList<Node> Nodes { get; } = nodes;

        public IReadOnlyList<Edge> Edges { get; } = edges;

        public int Width { get; } = width;

        public int Height { get; } = height;

        public static Layout Create(
            OperationGraph graph,
            Func<Operation, string>? labelProvider,
            Func<Operation, IReadOnlyList<string>?>? contentProvider)
        {
            var operationNodes =
                new Dictionary<Operation, Node>(ReferenceEqualityComparer.Instance);

            for (var index = 0; index < graph.Operations.Count; index++)
            {
                var operation = graph.Operations[index];
                operationNodes.Add(
                    operation,
                    new Node(
                        $"[{index}] {labelProvider?.Invoke(operation) ?? GetTypeDisplayName(operation.GetType())}",
                        index,
                        operation.Inputs.Count,
                        operation.Outputs.Count,
                        contentProvider?.Invoke(operation)));
            }

            AssignOperationRanks(graph, operationNodes);

            var nodes = operationNodes.Values.OrderBy(node => node.Order).ToList();
            var edges = new List<Edge>();
            var externalNodes =
                new Dictionary<Value, Node>(ReferenceEqualityComparer.Instance);
            var consumedValues =
                new HashSet<Value>(ReferenceEqualityComparer.Instance);
            var nextOrder = graph.Operations.Count;

            foreach (var consumer in graph.Operations)
            {
                var consumerNode = operationNodes[consumer];

                for (var inputIndex = 0; inputIndex < consumer.Inputs.Count; inputIndex++)
                {
                    var input = consumer.Inputs[inputIndex];
                    consumedValues.Add(input);
                    var producer = graph.GetProducer(input);
                    var isOrderEdge = IsOrderValue(input);
                    var isGuardEdge = IsGuardValue(input);

                    if (producer is not null)
                    {
                        edges.Add(
                            new Edge(
                                operationNodes[producer],
                                consumerNode,
                                IndexOfReference(producer.Outputs, input),
                                inputIndex,
                                isOrderEdge || isGuardEdge
                                    ? null
                                    : IndexOfReference(producer.Outputs, input),
                                isOrderEdge || isGuardEdge
                                    ? null
                                    : inputIndex,
                                isOrderEdge,
                                isGuardEdge));
                        continue;
                    }

                    if (!externalNodes.TryGetValue(input, out var externalNode))
                    {
                        externalNode = new Node(
                            $"external[{externalNodes.Count}]",
                            nextOrder++,
                            inputCount: 0,
                            outputCount: 1)
                        {
                            Rank = 0,
                        };
                        externalNodes.Add(input, externalNode);
                        nodes.Add(externalNode);
                    }

                    edges.Add(
                        new Edge(
                            externalNode,
                            consumerNode,
                            SourceSlot: 0,
                            TargetSlot: inputIndex,
                            SourcePort: isOrderEdge || isGuardEdge ? null : 0,
                            TargetPort: isOrderEdge || isGuardEdge
                                ? null
                                : inputIndex,
                            isOrderEdge,
                            isGuardEdge));
                }
            }

            foreach (var operation in graph.Operations)
            {
                var producerNode = operationNodes[operation];

                for (var outputIndex = 0; outputIndex < operation.Outputs.Count; outputIndex++)
                {
                    var output = operation.Outputs[outputIndex];

                    if (consumedValues.Contains(output))
                    {
                        continue;
                    }

                    var isOrderEdge = IsOrderValue(output);
                    var isGuardEdge = IsGuardValue(output);
                    var outputNode = new Node(
                        $"output[{producerNode.Order}:{outputIndex}]",
                        nextOrder++,
                        inputCount: 1,
                        outputCount: 0)
                    {
                        Rank = producerNode.Rank + 1,
                    };

                    nodes.Add(outputNode);
                    edges.Add(
                        new Edge(
                            producerNode,
                            outputNode,
                            outputIndex,
                            TargetSlot: 0,
                            SourcePort: isOrderEdge || isGuardEdge
                                ? null
                                : outputIndex,
                            TargetPort: null,
                            isOrderEdge,
                            isGuardEdge));
                }
            }

            EnsureEdgesAdvanceRanks(edges);
            MoveNodesTowardConsumers(nodes, edges);
            EnsureEdgesAdvanceRanks(edges);

            var rowWidths = nodes
                .GroupBy(node => node.Rank)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(node => node.Width) +
                        ((group.Count() - 1) * HorizontalGap));
            var contentWidth = rowWidths.Values.Max();
            var longEdges = edges
                .Where(edge => edge.Target.Rank > edge.Source.Rank + 1)
                .ToArray();

            for (var index = 0; index < longEdges.Length; index++)
            {
                longEdges[index].LaneX =
                    contentWidth + 2 + (index * 2);
            }

            var ranks = nodes
                .GroupBy(node => node.Rank)
                .OrderBy(group => group.Key)
                .ToArray();

            foreach (var rank in ranks)
            {
                var rankNodes = rank
                    .OrderBy(node => GetOrderingHint(node, edges))
                    .ThenBy(node => node.Order)
                    .ToArray();
                var left =
                    (contentWidth - rowWidths[rank.Key]) / 2;

                foreach (var node in rankNodes)
                {
                    node.Left = left;
                    left += node.Width + HorizontalGap;
                }

                if (rankNodes.Length == 1)
                {
                    AlignWithSinglePredecessor(
                        rankNodes[0],
                        edges,
                        minimumLeft: 0,
                        contentWidth);
                }
            }

            var routeLaneCounts = AssignRouteLanes(ranks, edges);

            var top = 0;

            for (var rankIndex = 0; rankIndex < ranks.Length; rankIndex++)
            {
                var rank = ranks[rankIndex];

                foreach (var node in rank)
                {
                    node.Top = top;
                }

                if (rankIndex + 1 < ranks.Length)
                {
                    var routingGap = Math.Max(
                        3,
                        routeLaneCounts[rank.Key] + 2);

                    top += rank.Max(node => node.Height) + routingGap;
                }
            }

            var rankIndexes = ranks
                .Select((rank, index) => (rank.Key, index))
                .ToDictionary(pair => pair.Key, pair => pair.index);

            foreach (var edge in longEdges)
            {
                edge.DepartureY =
                    edge.Source.Bottom + 2 + edge.DepartureLane!.Value;
                var precedingTargetRank =
                    ranks[rankIndexes[edge.Target.Rank] - 1];
                edge.ArrivalY =
                    precedingTargetRank.Max(node => node.Bottom) +
                    2 +
                    edge.ArrivalLane!.Value;
            }

            var width = contentWidth + 2 + (longEdges.Length * 2);
            var height = nodes.Max(node => node.Bottom) + 1;

            return new Layout(nodes, edges, width, height);
        }

        private static Dictionary<int, int> AssignRouteLanes(
            IReadOnlyList<IGrouping<int, Node>> ranks,
            IReadOnlyList<Edge> edges)
        {
            var result = new Dictionary<int, int>();

            for (var rankIndex = 0; rankIndex + 1 < ranks.Count; rankIndex++)
            {
                var sourceRank = ranks[rankIndex].Key;
                var targetRank = ranks[rankIndex + 1].Key;
                var lanes = new List<List<(int Start, int End)>>();
                var segments = new List<(
                    int Start,
                    int End,
                    Action<int> AssignLane)>();

                foreach (var edge in edges)
                {
                    if (edge.Source.Rank == sourceRank &&
                        edge.Target.Rank == targetRank)
                    {
                        AddSegment(
                            edge.Source.GetOutputX(edge.SourceSlot),
                            edge.Target.GetInputX(edge.TargetSlot),
                            lane => edge.RouteLane = lane);
                    }
                    else if (edge.Source.Rank == sourceRank &&
                        edge.Target.Rank > targetRank)
                    {
                        AddSegment(
                            edge.Source.GetOutputX(edge.SourceSlot),
                            edge.LaneX!.Value,
                            lane => edge.DepartureLane = lane);
                    }
                    else if (edge.Source.Rank < sourceRank &&
                        edge.Target.Rank == targetRank)
                    {
                        AddSegment(
                            edge.LaneX!.Value,
                            edge.Target.GetInputX(edge.TargetSlot),
                            lane => edge.ArrivalLane = lane);
                    }
                }

                foreach (var segment in segments
                    .OrderByDescending(
                        segment => segment.End - segment.Start)
                    .ThenBy(segment => segment.Start))
                {
                    var laneIndex = lanes.FindIndex(
                        lane => lane.All(
                            existing =>
                                segment.End < existing.Start ||
                                segment.Start > existing.End));

                    if (laneIndex < 0)
                    {
                        laneIndex = lanes.Count;
                        lanes.Add([]);
                    }

                    lanes[laneIndex].Add((segment.Start, segment.End));
                    segment.AssignLane(laneIndex);
                }

                result[sourceRank] = lanes.Count;

                void AddSegment(
                    int firstX,
                    int secondX,
                    Action<int> assignLane)
                {
                    if (firstX == secondX)
                    {
                        assignLane(0);
                        return;
                    }

                    segments.Add(
                        (Math.Min(firstX, secondX),
                         Math.Max(firstX, secondX),
                         assignLane));
                }
            }

            return result;
        }

        private static void AlignWithSinglePredecessor(
            Node node,
            IReadOnlyList<Edge> edges,
            int minimumLeft,
            int contentWidth)
        {
            var incoming = edges
                .Where(edge => ReferenceEquals(edge.Target, node))
                .Where(edge => edge.Source.IsPositioned)
                .ToArray();

            if (incoming.Length != 1)
            {
                return;
            }

            var edge = incoming[0];
            var sourceX = edge.Source.GetOutputX(edge.SourceSlot);
            var targetOffset =
                (((edge.TargetSlot + 1) * node.Width) /
                    (node.InputCount + 1));

            node.Left = Math.Clamp(
                sourceX - targetOffset,
                minimumLeft,
                minimumLeft + contentWidth - node.Width);
        }

        private static double GetOrderingHint(
            Node node,
            IReadOnlyList<Edge> edges)
        {
            var outgoing = edges
                .Where(edge => ReferenceEquals(edge.Source, node))
                .ToArray();

            if (outgoing.Length > 0)
            {
                return outgoing.Min(
                    edge =>
                        (edge.Target.Order * 1_000) +
                        edge.TargetSlot);
            }

            var incoming = edges
                .Where(edge => ReferenceEquals(edge.Target, node))
                .Where(edge => edge.Source.IsPositioned)
                .ToArray();

            if (incoming.Length > 0)
            {
                return incoming.Average(
                    edge => edge.Source.GetOutputX(edge.SourceSlot));
            }

            return node.Order;
        }

        private static void MoveNodesTowardConsumers(
            IReadOnlyList<Node> nodes,
            IReadOnlyList<Edge> edges)
        {
            foreach (var node in nodes.OrderByDescending(node => node.Rank))
            {
                var outgoing = edges
                    .Where(edge => ReferenceEquals(edge.Source, node))
                    .ToArray();

                if (outgoing.Length == 0)
                {
                    continue;
                }

                var latestRank = outgoing.Min(edge => edge.Target.Rank - 1);
                var earliestRank = edges
                    .Where(edge => ReferenceEquals(edge.Target, node))
                    .Select(edge => edge.Source.Rank + 1)
                    .DefaultIfEmpty(0)
                    .Max();

                if (latestRank >= earliestRank)
                {
                    node.Rank = latestRank;
                }
            }
        }

        private static void AssignOperationRanks(
            OperationGraph graph,
            IReadOnlyDictionary<Operation, Node> nodes)
        {
            var assigned =
                new HashSet<Operation>(ReferenceEqualityComparer.Instance);

            foreach (var operation in graph.Operations)
            {
                AssignOperationRank(graph, operation, nodes, assigned);
            }
        }

        private static int AssignOperationRank(
            OperationGraph graph,
            Operation operation,
            IReadOnlyDictionary<Operation, Node> nodes,
            HashSet<Operation> assigned)
        {
            var node = nodes[operation];

            if (assigned.Contains(operation))
            {
                return node.Rank;
            }

            var rank = operation.Inputs.Count == 0 ? 0 : 1;

            foreach (var dependency in graph.GetDependencies(operation))
            {
                rank = Math.Max(
                    rank,
                    AssignOperationRank(graph, dependency, nodes, assigned) + 1);
            }

            node.Rank = rank;
            assigned.Add(operation);
            return rank;
        }

        private static void EnsureEdgesAdvanceRanks(
            IReadOnlyList<Edge> edges)
        {
            var changed = true;

            while (changed)
            {
                changed = false;

                foreach (var edge in edges)
                {
                    if (edge.Target.Rank > edge.Source.Rank)
                    {
                        continue;
                    }

                    edge.Target.Rank = edge.Source.Rank + 1;
                    changed = true;
                }
            }
        }

        private static int IndexOfReference(
            IReadOnlyList<Value> values,
            Value expected)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (ReferenceEquals(values[index], expected))
                {
                    return index;
                }
            }

            throw new InvalidOperationException(
                "The producer does not expose the value as an output.");
        }

        private static string GetTypeDisplayName(Type type)
        {
            var name = type.Name;
            var genericMarker = name.IndexOf('`');
            return genericMarker < 0 ? name : name[..genericMarker];
        }
    }

    private sealed class Node(
        string label,
        int order,
        int inputCount,
        int outputCount,
        IReadOnlyList<string>? content = null)
    {
        public string Label { get; } = label;

        public int Order { get; } = order;

        public IReadOnlyList<string> Content { get; } = content ?? [];

        public int InputCount { get; } = Math.Max(1, inputCount);

        public int OutputCount { get; } = Math.Max(1, outputCount);

        public int Rank { get; set; }

        public int Left { get; set; } = -1;

        public int Top { get; set; }

        public int Width { get; } = Math.Max(
            Math.Max(
                label.Length + 4,
                (Math.Max(inputCount, outputCount) * 7) + 3),
            (content?.Select(static line => line.Length).DefaultIfEmpty(0).Max() ?? 0) + 4);

        public int Height => Content.Count + 3;

        public int Right => Left + Width - 1;

        public int Bottom => Top + Height - 1;

        public bool IsPositioned => Left >= 0;

        public int GetInputX(int slot) =>
            Left + (((slot + 1) * Width) / (InputCount + 1));

        public int GetOutputX(int slot) =>
            Left + (((slot + 1) * Width) / (OutputCount + 1));
    }

    private sealed record Edge(
        Node Source,
        Node Target,
        int SourceSlot,
        int TargetSlot,
        int? SourcePort,
        int? TargetPort,
        bool IsOrderEdge,
        bool IsGuardEdge)
    {
        public int? LaneX { get; set; }

        public int? RouteLane { get; set; }

        public int? DepartureLane { get; set; }

        public int? ArrivalLane { get; set; }

        public int? DepartureY { get; set; }

        public int? ArrivalY { get; set; }
    }

    private sealed class Canvas
    {
        private readonly char[,] _characters;

        public Canvas(int width, int height)
        {
            _characters = new char[height, width];
        }

        public void Write(int x, int y, string text)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (x + index < _characters.GetLength(1))
                {
                    Set(x + index, y, text[index]);
                }
            }
        }

        public void DrawHorizontal(
            int y,
            int startX,
            int endX,
            char character = '─')
        {
            for (var x = Math.Min(startX, endX); x <= Math.Max(startX, endX); x++)
            {
                Set(x, y, character);
            }
        }

        public void DrawVertical(
            int x,
            int startY,
            int endY,
            char character = '│')
        {
            for (var y = Math.Min(startY, endY); y <= Math.Max(startY, endY); y++)
            {
                Set(x, y, character);
            }
        }

        public void Set(int x, int y, char character)
        {
            var existing = _characters[y, x];

            _characters[y, x] = (existing, character) switch
            {
                ('\0', _) => character,
                _ when IsHorizontal(existing) && IsVertical(character) => '┼',
                _ when IsVertical(existing) && IsHorizontal(character) => '┼',
                ('┼', _) or (_, '┼') => '┼',
                _ when existing == character => existing,
                _ => character,
            };
        }

        private static bool IsHorizontal(char character) =>
            character is '─' or '╌';

        private static bool IsVertical(char character) =>
            character is '│' or '╎';

        public void Overwrite(int x, int y, char character)
        {
            _characters[y, x] = character;
        }

        public void Clear(int left, int top, int right, int bottom)
        {
            for (var y = top; y <= bottom; y++)
            {
                for (var x = left; x <= right; x++)
                {
                    _characters[y, x] = '\0';
                }
            }
        }

        public void WriteTo(TextWriter writer)
        {
            var builder = new StringBuilder(_characters.GetLength(1));
            var lastPopulatedRow = GetLastPopulatedRow();

            for (var y = 0; y <= lastPopulatedRow; y++)
            {
                builder.Clear();

                for (var x = 0; x < _characters.GetLength(1); x++)
                {
                    builder.Append(
                        _characters[y, x] is '\0'
                            ? ' '
                            : _characters[y, x]);
                }

                writer.WriteLine(builder.ToString().TrimEnd());
            }
        }

        private int GetLastPopulatedRow()
        {
            for (var y = _characters.GetLength(0) - 1; y >= 0; y--)
            {
                for (var x = 0; x < _characters.GetLength(1); x++)
                {
                    if (_characters[y, x] is not '\0')
                    {
                        return y;
                    }
                }
            }

            return 0;
        }
    }

    private static bool IsOrderValue(Value value) =>
        value is Value<OrderToken>;

    private static bool IsGuardValue(Value value) =>
        value is Value<GuardToken>;

}
