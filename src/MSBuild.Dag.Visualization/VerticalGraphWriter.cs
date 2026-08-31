using System.Globalization;
using System.Text;
using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization;

public static class VerticalGraphWriter
{
    private const int HorizontalGap = 10;

    public static string Render(BuildGraph graph)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(graph, writer);
        return writer.ToString();
    }

    public static void Write(BuildGraph graph, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine("BuildGraph");

        if (graph.Operations.Count == 0)
        {
            writer.WriteLine("(empty)");
            return;
        }

        var layout = Layout.Create(graph);
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

        canvas.DrawVertical(sourceX, sourceY, firstRouteY);
        canvas.DrawVertical(targetX, lastRouteY, targetY);

        if (edge.LaneX is int laneX)
        {
            var laneStartY = firstRouteY;
            var laneEndY = lastRouteY - edge.TargetSlot;
            canvas.DrawHorizontal(laneStartY, sourceX, laneX);
            canvas.DrawVertical(laneX, laneStartY, laneEndY);
            canvas.DrawHorizontal(laneEndY, laneX, targetX);
            canvas.DrawVertical(targetX, laneEndY, lastRouteY);
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
            canvas.DrawVertical(sourceX, firstRouteY, lastRouteY);
            return;
        }

        var middleY = Math.Min(
            lastRouteY,
            firstRouteY + edge.TargetSlot + 1);
        canvas.DrawVertical(sourceX, firstRouteY, middleY);
        canvas.DrawHorizontal(middleY, sourceX, targetX);
        canvas.DrawVertical(targetX, middleY, lastRouteY);
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

        public static Layout Create(BuildGraph graph)
        {
            var operationNodes =
                new Dictionary<Operation, Node>(ReferenceEqualityComparer.Instance);

            for (var index = 0; index < graph.Operations.Count; index++)
            {
                var operation = graph.Operations[index];
                var visibleOrderInput =
                    HasVisibleOrderInput(graph, operation);
                var visibleOrderOutput =
                    HasVisibleOrderOutput(graph, operation);
                operationNodes.Add(
                    operation,
                    new Node(
                        $"[{index}] {GetTypeDisplayName(operation.GetType())}",
                        index,
                        operation.Inputs.Count(IsDataValue) +
                            (visibleOrderInput ? 1 : 0),
                        operation.Outputs.Count(IsDataValue) +
                            (visibleOrderOutput ? 1 : 0)));
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

                    if (producer is not null)
                    {
                        if (isOrderEdge &&
                            HasDataDependency(producer, consumer))
                        {
                            continue;
                        }

                        edges.Add(
                            new Edge(
                                operationNodes[producer],
                                consumerNode,
                                GetOutputSlot(graph, producer, input),
                                GetInputSlot(graph, consumer, input),
                                isOrderEdge
                                    ? null
                                    : IndexOfDataReference(producer.Outputs, input),
                                isOrderEdge
                                    ? null
                                    : IndexOfDataReference(consumer.Inputs, input),
                                isOrderEdge));
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
                            TargetSlot: GetInputSlot(
                                graph,
                                consumer,
                                input),
                            SourcePort: 0,
                            TargetPort: isOrderEdge
                                ? null
                                : IndexOfDataReference(consumer.Inputs, input),
                            isOrderEdge));
                }
            }

            foreach (var operation in graph.Operations)
            {
                var producerNode = operationNodes[operation];

                for (var outputIndex = 0; outputIndex < operation.Outputs.Count; outputIndex++)
                {
                    var output = operation.Outputs[outputIndex];

                    if (consumedValues.Contains(output) ||
                        IsOrderValue(output))
                    {
                        continue;
                    }

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
                            GetOutputSlot(graph, operation, output),
                            TargetSlot: 0,
                            SourcePort: IndexOfDataReference(
                                operation.Outputs,
                                output),
                            TargetPort: null,
                            IsOrderEdge: false));
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

            var ranks = nodes
                .GroupBy(node => node.Rank)
                .OrderBy(group => group.Key)
                .ToArray();
            var top = 0;

            for (var rankIndex = 0; rankIndex < ranks.Length; rankIndex++)
            {
                var rank = ranks[rankIndex];
                var rankNodes = rank
                    .OrderBy(node => GetOrderingHint(node, edges))
                    .ThenBy(node => node.Order)
                    .ToArray();
                var left = (contentWidth - rowWidths[rank.Key]) / 2;

                foreach (var node in rankNodes)
                {
                    node.Left = left;
                    node.Top = top;
                    left += node.Width + HorizontalGap;
                }

                if (rankNodes.Length == 1)
                {
                    AlignWithSinglePredecessor(
                        rankNodes[0],
                        edges,
                        contentWidth);
                }

                if (rankIndex + 1 < ranks.Length)
                {
                    var nextRank = ranks[rankIndex + 1].Key;
                    var targetSlots = edges
                        .Where(edge => edge.Target.Rank == nextRank)
                        .Select(edge => edge.TargetSlot)
                        .ToArray();
                    var maximumTargetSlot = targetSlots
                        .DefaultIfEmpty(0)
                        .Max();
                    var routingGap = maximumTargetSlot == 0
                        ? 3
                        : maximumTargetSlot + 4;
                    var maximumFanOut = edges
                        .Where(edge => edge.Source.Rank == rank.Key)
                        .GroupBy(edge => edge.Source)
                        .Select(group => group.Count())
                        .DefaultIfEmpty(0)
                        .Max();

                    if (maximumFanOut > 1)
                    {
                        routingGap = Math.Max(routingGap, 4);
                    }

                    top += Node.Height + routingGap;
                }
            }

            var longEdges = edges
                .Where(edge => edge.Target.Rank > edge.Source.Rank + 1)
                .ToArray();

            for (var index = 0; index < longEdges.Length; index++)
            {
                longEdges[index].LaneX = contentWidth + 2 + (index * 2);
            }

            var width = contentWidth + 2 + (longEdges.Length * 2);
            var height = nodes.Max(node => node.Bottom) + 1;

            return new Layout(nodes, edges, width, height);
        }

        private static void AlignWithSinglePredecessor(
            Node node,
            IReadOnlyList<Edge> edges,
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
                0,
                contentWidth - node.Width);
        }

        private static double GetOrderingHint(
            Node node,
            IReadOnlyList<Edge> edges)
        {
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
            BuildGraph graph,
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
            BuildGraph graph,
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

        private static bool HasVisibleOrderInput(
            BuildGraph graph,
            Operation consumer)
        {
            foreach (var input in consumer.Inputs.Where(IsOrderValue))
            {
                var producer = graph.GetProducer(input);

                if (producer is null ||
                    !HasDataDependency(producer, consumer))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasVisibleOrderOutput(
            BuildGraph graph,
            Operation producer)
        {
            foreach (var output in producer.Outputs.Where(IsOrderValue))
            {
                foreach (var consumer in graph.Operations)
                {
                    if (consumer.Inputs.Any(
                            input => ReferenceEquals(input, output)) &&
                        !HasDataDependency(producer, consumer))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasDataDependency(
            Operation producer,
            Operation consumer) =>
            producer.Outputs
                .Where(value => !IsOrderValue(value))
                .Any(
                    output => consumer.Inputs.Any(
                        input => ReferenceEquals(input, output)));

        private static int GetInputSlot(
            BuildGraph graph,
            Operation consumer,
            Value expected)
        {
            if (IsOrderValue(expected))
            {
                return consumer.Inputs.Count(IsDataValue);
            }

            return IndexOfDataReference(consumer.Inputs, expected);
        }

        private static int GetOutputSlot(
            BuildGraph graph,
            Operation producer,
            Value expected)
        {
            if (IsOrderValue(expected))
            {
                return 0;
            }

            var dataIndex =
                IndexOfDataReference(producer.Outputs, expected);
            return HasVisibleOrderOutput(graph, producer)
                ? dataIndex + 1
                : dataIndex;
        }

        private static int IndexOfDataReference(
            IReadOnlyList<Value> values,
            Value expected)
        {
            var dataIndex = 0;

            foreach (var value in values)
            {
                if (ReferenceEquals(value, expected))
                {
                    return dataIndex;
                }

                if (!IsOrderValue(value))
                {
                    dataIndex++;
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
        int outputCount)
    {
        public const int Height = 3;

        public string Label { get; } = label;

        public int Order { get; } = order;

        public int InputCount { get; } = Math.Max(1, inputCount);

        public int OutputCount { get; } = Math.Max(1, outputCount);

        public int Rank { get; set; }

        public int Left { get; set; } = -1;

        public int Top { get; set; }

        public int Width { get; } = Math.Max(
            label.Length + 4,
            (Math.Max(inputCount, outputCount) * 7) + 3);

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
        bool IsOrderEdge)
    {
        public int? LaneX { get; set; }
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

        public void DrawHorizontal(int y, int startX, int endX)
        {
            for (var x = Math.Min(startX, endX); x <= Math.Max(startX, endX); x++)
            {
                Set(x, y, '─');
            }
        }

        public void DrawVertical(int x, int startY, int endY)
        {
            for (var y = Math.Min(startY, endY); y <= Math.Max(startY, endY); y++)
            {
                Set(x, y, '│');
            }
        }

        public void Set(int x, int y, char character)
        {
            var existing = _characters[y, x];

            _characters[y, x] = (existing, character) switch
            {
                ('\0', _) => character,
                ('─', '│') or ('│', '─') => '┼',
                ('┼', _) or (_, '┼') => '┼',
                _ when existing == character => existing,
                _ => character,
            };
        }

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

    private static bool IsDataValue(Value value) =>
        !IsOrderValue(value);
}
