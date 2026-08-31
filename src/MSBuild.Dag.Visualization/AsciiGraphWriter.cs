using System.Globalization;
using System.Text;
using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization;

public static class AsciiGraphWriter
{
    private const int HorizontalGap = 8;
    private const int VerticalGap = 2;

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

        foreach (var node in layout.Nodes)
        {
            DrawNode(canvas, node);
        }

        DrawEdges(canvas, layout);
        canvas.WriteTo(writer);
    }

    private static void DrawEdges(Canvas canvas, Layout layout)
    {
        var longEdgeLane = layout.NodeHeight + 1;

        foreach (var edge in layout.Edges)
        {
            var sourceX = edge.Source.Right + 1;
            var sourceY = edge.SourceY;
            var targetX = edge.Target.Left - 1;
            var targetY = edge.TargetY;

            if (sourceY == targetY)
            {
                canvas.DrawHorizontal(sourceY, sourceX, targetX);
                DrawEndpointJunctions(canvas, edge);
                canvas.Overwrite(targetX, targetY, '▶');
                DrawPortLabels(canvas, edge);
                continue;
            }

            if (edge.Target.Rank == edge.Source.Rank + 1)
            {
                var routeX = sourceX + ((targetX - sourceX) / 2);
                canvas.DrawHorizontal(sourceY, sourceX, routeX);
                canvas.DrawVertical(routeX, sourceY, targetY);
                canvas.DrawHorizontal(targetY, routeX, targetX);
                canvas.Overwrite(routeX, sourceY, targetY > sourceY ? '┐' : '┘');
                canvas.Overwrite(routeX, targetY, targetY > sourceY ? '└' : '┌');
                DrawEndpointJunctions(canvas, edge);
                canvas.Overwrite(targetX, targetY, '▶');
                DrawPortLabels(canvas, edge);
                continue;
            }

            var laneY = longEdgeLane;
            longEdgeLane += 2;

            canvas.DrawHorizontal(sourceY, sourceX, sourceX + 1);
            canvas.DrawVertical(sourceX + 1, sourceY, laneY);
            canvas.DrawHorizontal(laneY, sourceX + 1, targetX - 1);
            canvas.DrawVertical(targetX - 1, laneY, targetY);
            canvas.DrawHorizontal(targetY, targetX - 1, targetX);
            canvas.Overwrite(sourceX + 1, sourceY, '┐');
            canvas.Overwrite(sourceX + 1, laneY, '└');
            canvas.Overwrite(targetX - 1, laneY, '┘');
            canvas.Overwrite(targetX - 1, targetY, '┌');
            DrawEndpointJunctions(canvas, edge);
            canvas.Overwrite(targetX, targetY, '▶');
            DrawPortLabels(canvas, edge);
        }
    }

    private static void DrawEndpointJunctions(Canvas canvas, Edge edge)
    {
        canvas.Overwrite(edge.Source.Right, edge.SourceY, '├');
        canvas.Overwrite(edge.Target.Left, edge.TargetY, '┤');
    }

    private static void DrawPortLabels(Canvas canvas, Edge edge)
    {
        if (edge.SourcePort is int sourcePort)
        {
            canvas.Write(edge.Source.Right + 1, edge.SourceY, $"o{sourcePort}");
        }

        if (edge.TargetPort is int targetPort)
        {
            var label = $"i{targetPort}";
            canvas.Write(
                edge.Target.Left - label.Length - 1,
                edge.TargetY,
                label);
        }
    }

    private static void DrawNode(Canvas canvas, Node node)
    {
        canvas.DrawHorizontal(node.Top, node.Left, node.Right);
        canvas.DrawHorizontal(node.Bottom, node.Left, node.Right);
        canvas.Set(node.Left, node.Top, '┌');
        canvas.Set(node.Right, node.Top, '┐');
        canvas.Set(node.Left, node.Bottom, '└');
        canvas.Set(node.Right, node.Bottom, '┘');
        canvas.DrawVertical(node.Left, node.Top + 1, node.Bottom - 1);
        canvas.DrawVertical(node.Right, node.Top + 1, node.Bottom - 1);
        canvas.Write(node.Left + 2, node.CenterY, node.Label);
    }

    private sealed class Layout
    {
        private Layout(
            IReadOnlyList<Node> nodes,
            IReadOnlyList<Edge> edges,
            int width,
            int height,
            int nodeHeight)
        {
            Nodes = nodes;
            Edges = edges;
            Width = width;
            Height = height;
            NodeHeight = nodeHeight;
        }

        public IReadOnlyList<Node> Nodes { get; }

        public IReadOnlyList<Edge> Edges { get; }

        public int Width { get; }

        public int Height { get; }

        public int NodeHeight { get; }

        public static Layout Create(BuildGraph graph)
        {
            var operationNodes =
                new Dictionary<Operation, Node>(ReferenceEqualityComparer.Instance);

            for (var index = 0; index < graph.Operations.Count; index++)
            {
                var operation = graph.Operations[index];
                operationNodes.Add(
                    operation,
                    new Node(
                        $"[{index}] {GetTypeDisplayName(operation.GetType())}",
                        index,
                        operation.Inputs.Count,
                        operation.Outputs.Count));
            }

            AssignOperationRanks(graph, operationNodes);

            var nodes = operationNodes.Values.OrderBy(node => node.Order).ToList();
            var edges = new List<Edge>();
            var externalNodes = new Dictionary<Value, Node>(ReferenceEqualityComparer.Instance);
            var consumedValues = new HashSet<Value>(ReferenceEqualityComparer.Instance);
            var nextOrder = graph.Operations.Count;

            foreach (var consumer in graph.Operations)
            {
                var consumerNode = operationNodes[consumer];

                for (var inputIndex = 0; inputIndex < consumer.Inputs.Count; inputIndex++)
                {
                    var input = consumer.Inputs[inputIndex];
                    consumedValues.Add(input);
                    var producer = graph.GetProducer(input);

                    if (producer is not null)
                    {
                        edges.Add(
                            new Edge(
                                operationNodes[producer],
                                consumerNode,
                                IndexOfReference(producer.Outputs, input),
                                inputIndex));
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

                    edges.Add(new Edge(externalNode, consumerNode, null, inputIndex));
                }
            }

            foreach (var operation in graph.Operations)
            {
                var producerNode = operationNodes[operation];

                for (var outputIndex = 0; outputIndex < operation.Outputs.Count; outputIndex++)
                {
                    if (consumedValues.Contains(operation.Outputs[outputIndex]))
                    {
                        continue;
                    }

                    var outputNode = new Node(
                        $"output[{operationNodes[operation].Order}:{outputIndex}]",
                        nextOrder++,
                        inputCount: 1,
                        outputCount: 0)
                    {
                        Rank = producerNode.Rank + 1,
                    };

                    nodes.Add(outputNode);
                    edges.Add(new Edge(producerNode, outputNode, outputIndex, null));
                }
            }

            EnsureEdgesAdvanceRanks(edges);
            MoveNodesTowardConsumers(nodes, edges);
            PositionNodes(nodes, edges);

            var width = nodes.Max(node => node.Right) + 1;
            var nodeHeight = nodes.Max(node => node.Bottom) + 1;
            var longEdgeCount = edges.Count(
                edge => edge.Target.Rank > edge.Source.Rank + 1);
            var height = nodeHeight + (longEdgeCount * 2) + 1;

            return new Layout(nodes, edges, width, height, nodeHeight);
        }

        private static void MoveNodesTowardConsumers(
            IReadOnlyList<Node> nodes,
            IReadOnlyList<Edge> edges)
        {
            foreach (var node in nodes.OrderByDescending(node => node.Rank))
            {
                var outgoingEdges = edges
                    .Where(edge => ReferenceEquals(edge.Source, node))
                    .ToArray();

                if (outgoingEdges.Length == 0)
                {
                    continue;
                }

                var latestRank = outgoingEdges.Min(edge => edge.Target.Rank - 1);
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

        private static string GetTypeDisplayName(Type type)
        {
            var name = type.Name;
            var genericMarker = name.IndexOf('`');
            return genericMarker < 0 ? name : name[..genericMarker];
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

        private static void AssignOperationRanks(
            BuildGraph graph,
            IReadOnlyDictionary<Operation, Node> operationNodes)
        {
            var assigned = new HashSet<Operation>(ReferenceEqualityComparer.Instance);

            foreach (var operation in graph.Operations)
            {
                AssignOperationRank(graph, operation, operationNodes, assigned);
            }
        }

        private static int AssignOperationRank(
            BuildGraph graph,
            Operation operation,
            IReadOnlyDictionary<Operation, Node> operationNodes,
            HashSet<Operation> assigned)
        {
            var node = operationNodes[operation];

            if (assigned.Contains(operation))
            {
                return node.Rank;
            }

            var rank = operation.Inputs.Count == 0 ? 0 : 1;

            foreach (var dependency in graph.GetDependencies(operation))
            {
                rank = Math.Max(
                    rank,
                    AssignOperationRank(graph, dependency, operationNodes, assigned) + 1);
            }

            node.Rank = rank;
            assigned.Add(operation);
            return rank;
        }

        private static void EnsureEdgesAdvanceRanks(IReadOnlyList<Edge> edges)
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

        private static void PositionNodes(
            IReadOnlyList<Node> nodes,
            IReadOnlyList<Edge> edges)
        {
            var ranks = nodes
                .GroupBy(node => node.Rank)
                .OrderBy(group => group.Key)
                .ToArray();

            var rankLeft = 0;

            foreach (var rank in ranks)
            {
                var rankNodes = rank.ToList();
                var rankWidth = rankNodes.Max(node => node.Width);

                foreach (var node in rankNodes)
                {
                    node.Left = rankLeft;
                }

                rankLeft += rankWidth + HorizontalGap;
            }

            foreach (var rank in ranks)
            {
                var rankNodes = rank
                    .OrderBy(node => GetOrderingHint(node, edges))
                    .ThenBy(node => node.Order)
                    .ToArray();

                var nextTop = 0;

                foreach (var node in rankNodes)
                {
                    var desiredTop = GetDesiredTop(node, edges);
                    node.Top = Math.Max(nextTop, desiredTop);
                    nextTop = node.Bottom + VerticalGap + 1;
                }
            }
        }

        private static double GetOrderingHint(Node node, IReadOnlyList<Edge> edges)
        {
            var incomingEdges = edges
                .Where(edge => ReferenceEquals(edge.Target, node) && edge.Source.IsPositioned)
                .ToArray();

            if (incomingEdges.Length > 0)
            {
                return incomingEdges.Average(
                    edge => edge.SourceY - GetTargetPortOffset(edge));
            }

            var outgoingEdges = edges
                .Where(edge => ReferenceEquals(edge.Source, node))
                .ToArray();

            if (outgoingEdges.Length > 0)
            {
                return outgoingEdges.Min(
                    edge => (edge.Target.Order * 1_000) + (edge.TargetPort ?? 0));
            }

            return node.Order * 1_000;
        }

        private static int GetDesiredTop(Node node, IReadOnlyList<Edge> edges)
        {
            var predecessors = edges
                .Where(edge => ReferenceEquals(edge.Target, node))
                .Where(edge => edge.Source.IsPositioned)
                .ToArray();

            if (predecessors.Length == 0)
            {
                return 0;
            }

            return Math.Max(
                0,
                (int)Math.Round(
                    predecessors.Average(
                        edge => edge.SourceY - GetTargetPortOffset(edge))));
        }

        private static int GetTargetPortOffset(Edge edge) =>
            edge.TargetPort is int targetPort
                ? targetPort + 1
                : edge.Target.Height / 2;
    }

    private sealed class Node(
        string label,
        int order,
        int inputCount,
        int outputCount)
    {
        public string Label { get; } = label;

        public int Order { get; } = order;

        public int Rank { get; set; }

        public int Left { get; set; }

        public int Top { get; set; } = -1;

        public int Width => Label.Length + 4;

        public int Height => Math.Max(1, Math.Max(inputCount, outputCount)) + 2;

        public int Right => Left + Width - 1;

        public int CenterY => Top + (Height / 2);

        public int Bottom => Top + Height - 1;

        public bool IsPositioned => Top >= 0;

        public int GetInputY(int index) => Top + index + 1;

        public int GetOutputY(int index) => Top + index + 1;
    }

    private sealed record Edge(
        Node Source,
        Node Target,
        int? SourcePort,
        int? TargetPort)
    {
        public int SourceY =>
            SourcePort is int sourcePort
                ? Source.GetOutputY(sourcePort)
                : Source.CenterY;

        public int TargetY =>
            TargetPort is int targetPort
                ? Target.GetInputY(targetPort)
                : Target.CenterY;
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
                Set(x + index, y, text[index]);
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
                (_, '▶') => '▶',
                ('─', '│') or ('│', '─') => '┼',
                ('┼', _) or (_, '┼') => '┼',
                ('+', _) or (_, '+') => '┼',
                _ when existing == character => existing,
                _ => character,
            };
        }

        public void Overwrite(int x, int y, char character)
        {
            _characters[y, x] = character;
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
                    builder.Append(_characters[y, x] is '\0' ? ' ' : _characters[y, x]);
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
}
