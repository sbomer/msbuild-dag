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

    private static int GetInputPort(Operation operation, int inputIndex) =>
        operation is ITargetRenderingOperation target
            ? target.InputPorts[inputIndex] ??
                throw new InvalidOperationException(
                    "A data edge must map to a target input port.")
            : inputIndex;

    private static int GetOutputPort(Operation operation, int outputIndex) =>
        operation is ITargetRenderingOperation target
            ? target.OutputPorts[outputIndex] ??
                throw new InvalidOperationException(
                    "A data edge must map to a target output port.")
            : outputIndex;

    public static string Render(
        BuildProgram program,
        IReadOnlyDictionary<Target, string>? targetNames = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        var adapter = BuildProgramRenderingAdapter.Create(
            program,
            targetNames,
            includeTargetBodies: true);
        return Render(
            adapter.Graph,
            "BuildProgram",
            adapter.GetLabel,
            adapter.GetContent);
    }

    public static string RenderCompact(
        BuildProgram program,
        IReadOnlyDictionary<Target, string>? targetNames = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        var adapter = BuildProgramRenderingAdapter.Create(program, targetNames);
        return Render(
            adapter.Graph,
            "BuildProgram",
            adapter.GetLabel,
            contentProvider: null);
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
        BuildProgram program,
        TextWriter writer,
        IReadOnlyDictionary<Target, string>? targetNames = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        var adapter = BuildProgramRenderingAdapter.Create(
            program,
            targetNames,
            includeTargetBodies: true);
        Write(
            adapter.Graph,
            writer,
            "BuildProgram",
            adapter.GetLabel,
            adapter.GetContent);
    }

    public static void WriteCompact(
        BuildProgram program,
        TextWriter writer,
        IReadOnlyDictionary<Target, string>? targetNames = null)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(writer);

        var adapter = BuildProgramRenderingAdapter.Create(program, targetNames);
        Write(
            adapter.Graph,
            writer,
            "BuildProgram",
            adapter.GetLabel,
            contentProvider: null);
    }

    internal static string Render(
        OperationGraph graph,
        string heading,
        Func<Operation, string>? labelProvider,
        Func<Operation, GraphNodeContent?>? contentProvider)
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
        Func<Operation, GraphNodeContent?>? contentProvider)
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
        WriteLayout(layout, writer);
    }

    internal static GraphNodeContent RenderTargetBody(Target target)
    {
        if (target.Body.Operations.Count == 0)
        {
            return new GraphNodeContent(
                ["(empty)"],
                [],
                []);
        }

        var adapter = TargetBodyRenderingAdapter.Create(target);
        var layout = Layout.Create(
            adapter.Graph,
            adapter.GetLabel,
            contentProvider: null,
            renderDanglingOutputs: false);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);

        WriteLayout(layout, writer);

        return new GraphNodeContent(
            writer.ToString()
                .Split(Environment.NewLine)
                .Where(static line => line.Length > 0)
                .ToArray(),
            adapter.Inputs
                .Select(operation => layout.GetNode(operation).GetOutputX(0))
                .ToArray(),
            adapter.Outputs
                .Select(operation => layout.GetNode(operation).GetInputX(0))
                .ToArray());
    }

    private static void WriteLayout(Layout layout, TextWriter writer)
    {
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
        var firstRouteY = Math.Max(
            sourceY + 2,
            edge.SourceRankBottom + 2);
        var lastRouteY = targetY - 2;
        var horizontalStroke = edge.IsOrderEdge ? '╌' : '─';
        var verticalStroke = edge.IsOrderEdge ? '╎' : '│';

        if (edge.LaneX is int laneX)
        {
            var laneStartY = edge.DepartureY!.Value;
            var laneEndY = edge.ArrivalY!.Value;
            var laneSourceCornerOccupied = canvas.IsPopulated(sourceX, laneStartY);
            var departureCornerOccupied = canvas.IsPopulated(laneX, laneStartY);
            var arrivalCornerOccupied = canvas.IsPopulated(laneX, laneEndY);
            var laneTargetCornerOccupied = canvas.IsPopulated(targetX, laneEndY);
            canvas.DrawVertical(sourceX, sourceY, laneStartY, verticalStroke);
            canvas.DrawVertical(targetX, laneEndY, targetY, verticalStroke);
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
            canvas.OverwriteCorner(
                sourceX,
                laneStartY,
                laneX > sourceX ? '└' : '┘',
                laneSourceCornerOccupied);
            canvas.OverwriteCorner(
                laneX,
                laneStartY,
                laneX > sourceX ? '┐' : '┌',
                departureCornerOccupied);
            canvas.OverwriteCorner(
                laneX,
                laneEndY,
                targetX > laneX ? '└' : '┘',
                arrivalCornerOccupied);
            canvas.OverwriteCorner(
                targetX,
                laneEndY,
                targetX > laneX ? '┐' : '┌',
                laneTargetCornerOccupied);
            return;
        }

        if (sourceX == targetX)
        {
            canvas.DrawVertical(
                sourceX,
                sourceY,
                targetY,
                verticalStroke);
            return;
        }

        var middleY = Math.Min(
            lastRouteY,
            firstRouteY + (edge.RouteLane ?? 0));
        var sourceCornerOccupied = canvas.IsPopulated(sourceX, middleY);
        var targetCornerOccupied = canvas.IsPopulated(targetX, middleY);
        canvas.DrawVertical(
            sourceX,
            sourceY,
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
            targetY,
            verticalStroke);
        canvas.OverwriteCorner(
            sourceX,
            middleY,
            targetX > sourceX ? '└' : '┘',
            sourceCornerOccupied);
        canvas.OverwriteCorner(
            targetX,
            middleY,
            targetX > sourceX ? '┐' : '┌',
            targetCornerOccupied);
    }

    private static void DrawEdgeEndpoints(Canvas canvas, Edge edge)
    {
        var sourceX = edge.Source.GetOutputX(edge.SourceSlot);
        var targetX = edge.Target.GetInputX(edge.TargetSlot);

        if (edge.Source.Kind is not NodeKind.TargetInput)
        {
            canvas.Overwrite(
                sourceX,
                edge.Source.Bottom,
                edge.Source.HasBodyOutput(edge.SourceSlot) ? '┼' : '┬');
        }

        if (edge.Target.Kind is not NodeKind.TargetOutput)
        {
            canvas.Overwrite(
                targetX,
                edge.Target.Top,
                edge.Target.HasBodyInput(edge.TargetSlot) ? '┼' : '┴');
            canvas.Overwrite(targetX, edge.Target.Top - 1, '▼');
        }

        if (edge.Source.Kind is not NodeKind.TargetInput)
        {
            var edgeLabel = edge.IsOrderEdge
                ? "order"
                : edge.IsGuardEdge
                    ? "guard"
                : $"o{edge.SourcePort!.Value}";
            canvas.Write(sourceX + 1, edge.Source.Bottom + 1, edgeLabel);
        }

        if (edge.Target.Kind is not NodeKind.TargetOutput &&
            !edge.IsOrderEdge &&
            edge.TargetPort is int targetPort)
        {
            canvas.Write(
                targetX + 1,
                edge.Target.Top - 1,
                $"i{targetPort}");
        }
    }

    private static void DrawNode(Canvas canvas, Node node)
    {
        if (node.Kind is not NodeKind.Box)
        {
            canvas.Write(node.Left + (node.Width / 2) + 1, node.Top, node.Label);
            return;
        }

        canvas.Clear(node.Left, node.Top, node.Right, node.Bottom);
        canvas.DrawHorizontal(node.Top, node.Left, node.Right);
        canvas.DrawHorizontal(node.Bottom, node.Left, node.Right);
        canvas.Overwrite(node.Left, node.Top, '┌');
        canvas.Overwrite(node.Right, node.Top, '┐');
        canvas.Overwrite(node.Left, node.Bottom, '└');
        canvas.Overwrite(node.Right, node.Bottom, '┘');
        canvas.DrawVertical(node.Left, node.Top + 1, node.Bottom - 1);
        canvas.DrawVertical(node.Right, node.Top + 1, node.Bottom - 1);

        if (node.Content.Count == 0)
        {
            canvas.Write(node.Left + 2, node.Top + 1, node.Label);
        }
        else
        {
            canvas.Write(node.GetTitleX(), node.Top + 1, node.Label);

            for (var slot = 0; slot < (node.InputPorts?.Count ?? 0); slot++)
            {
                if (node.InputPorts?[slot] is not null)
                {
                    canvas.DrawVertical(
                        node.GetInputX(slot),
                        node.Top,
                        node.Top + 2);
                }
            }

            for (var slot = 0; slot < (node.OutputPorts?.Count ?? 0); slot++)
            {
                if (node.OutputPorts?[slot] is not null)
                {
                    canvas.DrawVertical(
                        node.GetOutputX(slot),
                        node.Bottom - 1,
                        node.Bottom);
                }
            }
        }

        for (var index = 0; index < node.Content.Count; index++)
        {
            canvas.Write(
                node.Left + 2 + node.ContentOffset,
                node.Top + 2 + index,
                node.Content[index]);
        }
    }

    private sealed class Layout(
        IReadOnlyList<Node> nodes,
        IReadOnlyList<Edge> edges,
        IReadOnlyDictionary<Operation, Node> operationNodes,
        int width,
        int height)
    {
        public IReadOnlyList<Node> Nodes { get; } = nodes;

        public IReadOnlyList<Edge> Edges { get; } = edges;

        public Node GetNode(Operation operation) => operationNodes[operation];

        public int Width { get; } = width;

        public int Height { get; } = height;

        public static Layout Create(
            OperationGraph graph,
            Func<Operation, string>? labelProvider,
            Func<Operation, GraphNodeContent?>? contentProvider,
            bool renderDanglingOutputs = true)
        {
            var operationNodes =
                new Dictionary<Operation, Node>(ReferenceEqualityComparer.Instance);

            for (var index = 0; index < graph.Operations.Count; index++)
            {
                var operation = graph.Operations[index];
                var boundary = operation as ITargetBoundaryOperation;
                var target = operation as ITargetRenderingOperation;
                var operationLabel =
                    labelProvider?.Invoke(operation) ??
                    GetTypeDisplayName(operation.GetType());
                operationNodes.Add(
                    operation,
                    new Node(
                            boundary is null
                                ? $"[{index}] {operationLabel}"
                                : operationLabel,
                            index,
                            operation.Inputs.Count,
                            operation.Outputs.Count,
                            contentProvider?.Invoke(operation),
                            boundary is not null
                                ? boundary.BoundaryKind is TargetBoundaryKind.Input
                                    ? NodeKind.TargetInput
                                    : NodeKind.TargetOutput
                                : NodeKind.Box,
                            target?.BodyInputCount ?? 0,
                            target?.BodyOutputCount ?? 0,
                            target?.InputPorts,
                            target?.OutputPorts));
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
                                    : GetOutputPort(
                                        producer,
                                        IndexOfReference(producer.Outputs, input)),
                                isOrderEdge || isGuardEdge
                                    ? null
                                    : GetInputPort(consumer, inputIndex),
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
                                : GetInputPort(consumer, inputIndex),
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

                    if (!renderDanglingOutputs)
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
            MoveTargetOutputsToFinalRank(nodes);
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

            ReorderRanksTowardConsumers(
                ranks,
                edges,
                rowWidths,
                contentWidth);

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
                    var nextRank = ranks[rankIndex + 1];
                    var boundaryOnlyGap =
                        rank.All(node => node.Kind is NodeKind.TargetInput) ||
                        nextRank.All(node => node.Kind is NodeKind.TargetOutput);
                    var routeLaneCount = routeLaneCounts[rank.Key];
                    var routingGap = routeLaneCount == 0
                        ? boundaryOnlyGap ? 1 : 3
                        : routeLaneCount + (boundaryOnlyGap ? 1 : 2);

                    top += rank.Max(node => node.Height) + routingGap;
                }
            }

            var rankIndexes = ranks
                .Select((rank, index) => (rank.Key, index))
                .ToDictionary(pair => pair.Key, pair => pair.index);
            var rankBottoms = ranks.ToDictionary(
                rank => rank.Key,
                rank => rank.Max(node => node.Bottom));

            foreach (var edge in edges)
            {
                edge.SourceRankBottom = rankBottoms[edge.Source.Rank];
            }

            foreach (var edge in longEdges)
            {
                edge.DepartureY =
                    edge.SourceRankBottom + 2 + edge.DepartureLane!.Value;
                var precedingTargetRank =
                    ranks[rankIndexes[edge.Target.Rank] - 1];
                edge.ArrivalY =
                    precedingTargetRank.Max(node => node.Bottom) +
                    2 +
                    edge.ArrivalLane!.Value;
            }

            var width = contentWidth + 2 + (longEdges.Length * 2);
            var height = nodes.Max(node => node.Bottom) + 1;

            return new Layout(nodes, edges, operationNodes, width, height);
        }

        private static void ReorderRanksTowardConsumers(
            IReadOnlyList<IGrouping<int, Node>> ranks,
            IReadOnlyList<Edge> edges,
            IReadOnlyDictionary<int, int> rowWidths,
            int contentWidth)
        {
            for (var rankIndex = ranks.Count - 2; rankIndex >= 0; rankIndex--)
            {
                var rank = ranks[rankIndex];
                var rankNodes = rank
                    .OrderBy(node => GetConsumerPositionHint(node, edges))
                    .ThenBy(node => node.Order)
                    .ToArray();

                if (rankNodes.All(node => node.Kind is NodeKind.TargetInput))
                {
                    PositionTargetInputsTowardConsumers(
                        rank
                            .OrderBy(node => HasLongOutgoingEdge(node, edges))
                            .ThenBy(node => GetConsumerPositionHint(node, edges))
                            .ThenBy(node => node.Order)
                            .ToArray(),
                        edges,
                        contentWidth);
                    continue;
                }

                var left =
                    (contentWidth - rowWidths[rank.Key]) / 2;

                foreach (var node in rankNodes)
                {
                    node.Left = left;
                    left += node.Width + HorizontalGap;
                }
            }
        }

        private static bool HasLongOutgoingEdge(
            Node node,
            IReadOnlyList<Edge> edges) =>
            edges.Any(edge =>
                ReferenceEquals(edge.Source, node) &&
                edge.Target.Rank > node.Rank + 1);

        private static double GetConsumerPositionHint(
            Node node,
            IReadOnlyList<Edge> edges)
        {
            var outgoing = edges
                .Where(edge => ReferenceEquals(edge.Source, node))
                .ToArray();

            return outgoing.Length == 0
                ? node.Left
                : outgoing.Average(
                    edge => edge.Target.GetInputX(edge.TargetSlot));
        }

        private static void PositionTargetInputsTowardConsumers(
            IReadOnlyList<Node> inputs,
            IReadOnlyList<Edge> edges,
            int contentWidth)
        {
            var minimumLeft = 0;

            foreach (var input in inputs)
            {
                var desiredLeft =
                    (int)Math.Round(GetConsumerPositionHint(input, edges)) -
                    (input.Width / 2);
                input.Left = Math.Clamp(
                    Math.Max(desiredLeft, minimumLeft),
                    0,
                    contentWidth - input.Width);
                minimumLeft = input.Right + 2;
            }
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
                    bool IsOrderEdge,
                    Action<int> AssignLane)>();

                foreach (var edge in edges)
                {
                    if (edge.Source.Rank == sourceRank &&
                        edge.Target.Rank == targetRank)
                    {
                        if (edge.Source.GetOutputX(edge.SourceSlot) ==
                            edge.Target.GetInputX(edge.TargetSlot))
                        {
                            continue;
                        }

                        AddSegment(
                            edge.Source.GetOutputX(edge.SourceSlot),
                            edge.Target.GetInputX(edge.TargetSlot),
                            edge.IsOrderEdge,
                            lane => edge.RouteLane = lane);
                    }
                    else if (edge.Source.Rank == sourceRank &&
                        edge.Target.Rank > targetRank)
                    {
                        AddSegment(
                            edge.Source.GetOutputX(edge.SourceSlot),
                            edge.LaneX!.Value,
                            edge.IsOrderEdge,
                            lane => edge.DepartureLane = lane);
                    }
                    else if (edge.Source.Rank < sourceRank &&
                        edge.Target.Rank == targetRank)
                    {
                        AddSegment(
                            edge.LaneX!.Value,
                            edge.Target.GetInputX(edge.TargetSlot),
                            edge.IsOrderEdge,
                            lane => edge.ArrivalLane = lane);
                    }
                }

                var orderLaneStart = -1;

                foreach (var segment in segments
                    .OrderBy(segment => segment.IsOrderEdge)
                    .ThenByDescending(
                        segment => segment.End - segment.Start)
                    .ThenBy(segment => segment.Start))
                {
                    if (segment.IsOrderEdge && orderLaneStart < 0)
                    {
                        orderLaneStart = lanes.Count;
                    }

                    var minimumLane = segment.IsOrderEdge
                        ? orderLaneStart
                        : 0;
                    var laneIndex = -1;

                    for (var index = minimumLane;
                        index < lanes.Count;
                        index++)
                    {
                        if (lanes[index].All(
                            existing =>
                                segment.End < existing.Start ||
                                segment.Start > existing.End))
                        {
                            laneIndex = index;
                            break;
                        }
                    }

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
                    bool isOrderEdge,
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
                         isOrderEdge,
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
                        edge.Target.GetInputX(edge.TargetSlot));
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
                if (node.Kind is NodeKind.TargetInput)
                {
                    continue;
                }

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

        private static void MoveTargetOutputsToFinalRank(
            IReadOnlyList<Node> nodes)
        {
            var outputRank = nodes
                .Where(node => node.Kind is not NodeKind.TargetOutput)
                .Select(node => node.Rank)
                .DefaultIfEmpty(0)
                .Max() + 1;

            foreach (var output in nodes.Where(
                node => node.Kind is NodeKind.TargetOutput))
            {
                output.Rank = outputRank;
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
        GraphNodeContent? content = null,
        NodeKind kind = NodeKind.Box,
        int bodyInputCount = 0,
        int bodyOutputCount = 0,
        IReadOnlyList<int?>? inputPorts = null,
        IReadOnlyList<int?>? outputPorts = null)
    {
        public string Label { get; } = label;

        public int Order { get; } = order;

        public NodeKind Kind { get; } = kind;

        public int BodyInputCount { get; } = bodyInputCount;

        public int BodyOutputCount { get; } = bodyOutputCount;

        public IReadOnlyList<int?>? InputPorts { get; } = inputPorts;

        public IReadOnlyList<int?>? OutputPorts { get; } = outputPorts;

        public IReadOnlyList<string> Content { get; } = content?.Lines ?? [];

        public int ContentOffset { get; } =
            GetContentOffset(label, content);

        public int InputCount { get; } = Math.Max(1, inputCount);

        public int OutputCount { get; } = Math.Max(1, outputCount);

        public int Rank { get; set; }

        public int Left { get; set; } = -1;

        public int Top { get; set; }

        public int Width { get; } = kind is NodeKind.Box
            ? GetBoxWidth(
                label,
                inputCount,
                outputCount,
                content,
                inputPorts,
                outputPorts)
            : label.Length + 3;

        public int Height =>
            Kind is NodeKind.Box
                ? Content.Count + 3
                : 1;

        public int Right => Left + Width - 1;

        public int Bottom => Top + Height - 1;

        public bool IsPositioned => Left >= 0;

        public int GetInputX(int slot) =>
            Kind is NodeKind.TargetOutput
                ? Left + (Width / 2)
                : content is not null &&
                    InputPorts is not null &&
                    InputPorts[slot] is int inputPort
                    ? Left + 2 + ContentOffset + content.InputOffsets[inputPort]
                    : Left + (((slot + 1) * Width) / (InputCount + 1));

        public int GetOutputX(int slot) =>
            Kind is NodeKind.TargetInput
                ? Left + (Width / 2)
                : content is not null &&
                    OutputPorts is not null &&
                    OutputPorts[slot] is int outputPort
                    ? Left + 2 + ContentOffset + content.OutputOffsets[outputPort]
                    : content is not null && OutputPorts is not null
                        ? GetOrderOutputPortX(slot, OutputPorts)
                    : Left + (((slot + 1) * Width) / (OutputCount + 1));

        public bool HasBodyInput(int slot) =>
            Content.Count > 0 &&
            InputPorts is not null &&
            InputPorts[slot] is not null;

        public bool HasBodyOutput(int slot) =>
            Content.Count > 0 &&
            OutputPorts is not null &&
            OutputPorts[slot] is not null;

        public int GetTitleX()
        {
            var minimum = Left + 2;
            var maximum = Right - Label.Length - 1;

            for (var candidate = minimum; candidate <= maximum; candidate++)
            {
                var overlapsInput = Enumerable.Range(0, InputPorts?.Count ?? 0)
                    .Where(slot => InputPorts![slot] is not null)
                    .Select(GetInputX)
                    .Any(inputX =>
                        inputX >= candidate - 1 &&
                        inputX <= candidate + Label.Length);

                if (!overlapsInput)
                {
                    return candidate;
                }
            }

            return minimum;
        }

        private int GetOrderOutputPortX(
            int slot,
            IReadOnlyList<int?> ports)
        {
            var orderIndex = 0;

            for (var index = 0; index < slot; index++)
            {
                if (ports[index] is null)
                {
                    orderIndex++;
                }
            }

            return Right -
                2 -
                ((ports.Count(static port => port is null) - orderIndex) * 7);
        }

        private static int GetOrderOutputReservation(
            GraphNodeContent? content,
            IReadOnlyList<int?>? outputPorts)
        {
            if (content is null)
            {
                return 0;
            }

            var count = outputPorts?.Count(static port => port is null) ?? 0;
            return count * 7;
        }

        private static int GetBoxWidth(
            string label,
            int inputCount,
            int outputCount,
            GraphNodeContent? content,
            IReadOnlyList<int?>? inputPorts,
            IReadOnlyList<int?>? outputPorts)
        {
            var contentOffset = GetContentOffset(label, content);
            var width = Math.Max(
                Math.Max(
                    label.Length + 4,
                    (Math.Max(inputCount, outputCount) * 7) + 3),
                (content?.Lines.Select(static line => line.Length).DefaultIfEmpty(0).Max() ?? 0) +
                    contentOffset +
                    GetOrderOutputReservation(content, outputPorts) +
                    4);

            while (content is not null &&
                !PortLabelsFit(
                    width,
                    inputCount,
                    outputCount,
                    content,
                    contentOffset,
                    inputPorts,
                    outputPorts))
            {
                width++;
            }

            return width;
        }

        private static bool PortLabelsFit(
            int width,
            int inputCount,
            int outputCount,
            GraphNodeContent content,
            int contentOffset,
            IReadOnlyList<int?>? inputPorts,
            IReadOnlyList<int?>? outputPorts) =>
            LabelsFit(
                Enumerable.Range(0, inputPorts?.Count ?? 0)
                    .Select(slot =>
                    {
                        var port = inputPorts![slot];
                        var x = port is int inputPort
                            ? 2 + contentOffset + content.InputOffsets[inputPort]
                            : ((slot + 1) * width) / (Math.Max(1, inputCount) + 1);
                        return (Start: x, End: x + (port is int index ? $"i{index}".Length : 0));
                    }),
                width) &&
            LabelsFit(
                Enumerable.Range(0, outputPorts?.Count ?? 0)
                    .Select(slot =>
                    {
                        var port = outputPorts![slot];
                        var x = port is int outputPort
                            ? 2 + contentOffset + content.OutputOffsets[outputPort]
                            : GetOrderOutputPortX(width, slot, outputPorts);
                        var labelLength = port is int index ? $"o{index}".Length : "order".Length;
                        return (Start: x, End: x + labelLength);
                    }),
                width);

        private static bool LabelsFit(
            IEnumerable<(int Start, int End)> labels,
            int width)
        {
            var ordered = labels.OrderBy(label => label.Start).ToArray();

            return ordered.All(label => label.Start > 0 && label.End < width - 1) &&
                ordered.Zip(ordered.Skip(1))
                    .All(pair => pair.First.End + 1 < pair.Second.Start);
        }

        private static int GetOrderOutputPortX(
            int width,
            int slot,
            IReadOnlyList<int?> ports)
        {
            var orderIndex = 0;

            for (var index = 0; index < slot; index++)
            {
                if (ports[index] is null)
                {
                    orderIndex++;
                }
            }

            return width -
                3 -
                ((ports.Count(static port => port is null) - orderIndex) * 7);
        }

        private static int GetContentOffset(
            string label,
            GraphNodeContent? content)
        {
            if (content is null)
            {
                return 0;
            }

            var titleRight = label.Length + 2;
            var firstContentColumn = content.Lines.Count == 0
                ? int.MaxValue
                : content.Lines[0].TakeWhile(static character => character == ' ').Count() + 2;
            return firstContentColumn <= titleRight ||
                content.InputOffsets.Any(offset => offset + 2 <= titleRight)
                ? label.Length + 3
                : 0;
        }
    }

    private enum NodeKind
    {
        Box,
        TargetInput,
        TargetOutput,
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

        public int SourceRankBottom { get; set; }
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

        public bool IsPopulated(int x, int y) =>
            _characters[y, x] is not '\0';

        public void OverwriteCorner(
            int x,
            int y,
            char character,
            bool preserveCrossing)
        {
            _characters[y, x] = preserveCrossing ? '┼' : character;
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

internal sealed record GraphNodeContent(
    IReadOnlyList<string> Lines,
    IReadOnlyList<int> InputOffsets,
    IReadOnlyList<int> OutputOffsets);
