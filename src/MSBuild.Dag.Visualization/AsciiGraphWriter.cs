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
            contentProvider: null,
            valueLabelProvider: null,
            externalLabelProvider: null);
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
        IReadOnlyDictionary<Target, string>? targetNames = null,
        Func<Operation, string?>? operationLabelProvider = null,
        Func<Value, string?>? valueLabelProvider = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        var adapter = BuildProgramRenderingAdapter.Create(
            program,
            targetNames,
            includeTargetBodies: true,
            operationLabelProvider,
            valueLabelProvider);
        return Render(
            adapter.Graph,
            "BuildProgram",
            adapter.GetLabel,
            adapter.GetContent,
            valueLabelProvider,
            CreateInitialValueLabelProvider(program));
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
            contentProvider: null,
            valueLabelProvider: null);
    }

    public static void Write(OperationGraph graph, TextWriter writer)
    {
        Write(
            graph,
            writer,
            "OperationGraph",
            labelProvider: null,
            contentProvider: null,
            valueLabelProvider: null,
            externalLabelProvider: null);
    }

    public static void Write(
        BuildProgram program,
        TextWriter writer,
        IReadOnlyDictionary<Target, string>? targetNames = null,
        Func<Operation, string?>? operationLabelProvider = null,
        Func<Value, string?>? valueLabelProvider = null)
    {
        ArgumentNullException.ThrowIfNull(program);

        var adapter = BuildProgramRenderingAdapter.Create(
            program,
            targetNames,
            includeTargetBodies: true,
            operationLabelProvider,
            valueLabelProvider);
        Write(
            adapter.Graph,
            writer,
            "BuildProgram",
            adapter.GetLabel,
            adapter.GetContent,
            valueLabelProvider,
            CreateInitialValueLabelProvider(program));
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
            contentProvider: null,
            valueLabelProvider: null,
            externalLabelProvider: null);
    }

    internal static string Render(
        OperationGraph graph,
        string heading,
        Func<Operation, string>? labelProvider,
        Func<Operation, GraphNodeContent?>? contentProvider,
        Func<Value, string?>? valueLabelProvider = null,
        Func<Value, string?>? externalLabelProvider = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(
            graph,
            writer,
            heading,
            labelProvider,
            contentProvider,
            valueLabelProvider,
            externalLabelProvider);
        return writer.ToString();
    }

    private static void Write(
        OperationGraph graph,
        TextWriter writer,
        string heading,
        Func<Operation, string>? labelProvider,
        Func<Operation, GraphNodeContent?>? contentProvider,
        Func<Value, string?>? valueLabelProvider,
        Func<Value, string?>? externalLabelProvider)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine(heading);

        if (graph.Operations.Count == 0)
        {
            writer.WriteLine("(empty)");
            return;
        }

        var layout = Layout.Create(
            graph,
            labelProvider,
            contentProvider,
            valueLabelProvider: valueLabelProvider,
            externalLabelProvider: externalLabelProvider);
        WriteLayout(layout, writer);
    }

    internal static GraphNodeContent RenderTargetBody(
        Target target,
        Func<Operation, string?>? operationLabelProvider,
        Func<Value, string?>? valueLabelProvider) =>
        RenderSignedGraph(
            target.Body,
            operationLabelProvider,
            valueLabelProvider,
            labelBoundaries: false);

    private static GraphNodeContent RenderSignedGraph(
        OperationGraph graph,
        Func<Operation, string?>? operationLabelProvider,
        Func<Value, string?>? valueLabelProvider,
        bool labelBoundaries,
        int inputLabelOffset = 0)
    {
        if (graph.Inputs.Count == 0 &&
            graph.Operations.Count == 0 &&
            graph.Outputs.Count == 0)
        {
            return new GraphNodeContent(
                ["(empty)"],
                [],
                []);
        }

        var adapter = TargetBodyRenderingAdapter.Create(
            graph,
            operationLabelProvider,
            valueLabelProvider,
            labelBoundaries,
            inputLabelOffset);
        var layout = Layout.Create(
            adapter.Graph,
            adapter.GetLabel,
            operation => RenderOperationContent(
                operation,
                operationLabelProvider,
                valueLabelProvider),
            valueLabelProvider,
            externalLabelProvider: null,
            renderDanglingOutputs: false,
            preserveBoundaryOrder: labelBoundaries,
            placeSourceOperationsAfterInputs: labelBoundaries);
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

    private static GraphNodeContent? RenderOperationContent(
        Operation operation,
        Func<Operation, string?>? operationLabelProvider,
        Func<Value, string?>? valueLabelProvider)
    {
        if (operation is not ConditionalRegionOperation conditional)
        {
            return null;
        }

        var whenTrue = ReserveTitleColumns(
            RenderSignedGraph(
                conditional.WhenTrue,
                operationLabelProvider,
                valueLabelProvider,
                labelBoundaries: true,
                inputLabelOffset: 1),
            "true");
        var whenFalse = ReserveTitleColumns(
            RenderSignedGraph(
                conditional.WhenFalse,
                operationLabelProvider,
                valueLabelProvider,
                labelBoundaries: true,
                inputLabelOffset: 1),
            "false");
        var whenTrueWidth = Math.Max(
            " true ".Length,
            whenTrue.Lines.Select(static line => line.Length).DefaultIfEmpty().Max());
        var whenFalseWidth = Math.Max(
            " false ".Length,
            whenFalse.Lines.Select(static line => line.Length).DefaultIfEmpty().Max());
        var branchHeight = Math.Max(
            Math.Max(0, whenTrue.Lines.Count - 1),
            Math.Max(0, whenFalse.Lines.Count - 1));
        var frameWidth = whenTrueWidth + whenFalseWidth + 7;
        var dividerX = whenTrueWidth + 3;
        var trueOrigin = 2;
        var falseOrigin = whenTrueWidth + 5;
        var trueBundleX = whenTrue.InputOffsets.Count == 1
            ? trueOrigin + whenTrue.InputOffsets[0]
            : SelectBundleX(
                preferred: "true".Length + 4,
                minimum: "true".Length + 3,
                maximum: dividerX - 2,
                whenTrue.InputOffsets
                    .Concat(whenTrue.OutputOffsets)
                    .Select(offset => trueOrigin + offset));
        var falseBundleX = whenFalse.InputOffsets.Count == 1
            ? falseOrigin + whenFalse.InputOffsets[0]
            : SelectBundleX(
                preferred: dividerX + "false".Length + 4,
                minimum: dividerX + "false".Length + 3,
                maximum: frameWidth - 3,
                whenFalse.InputOffsets
                    .Concat(whenFalse.OutputOffsets)
                    .Select(offset => falseOrigin + offset));
        var conditionX = frameWidth - 2;
        var frameLines = new List<string>(branchHeight + 4)
        {
            $"┌{new string('─', whenTrueWidth + 2)}" +
            $"┬{new string('─', whenFalseWidth + 2)}┐",
            CreateTitleLine(),
        };

        for (var index = 0; index < branchHeight; index++)
        {
            var childLineIndex = index + 1;
            var trueLine = childLineIndex < whenTrue.Lines.Count
                ? whenTrue.Lines[childLineIndex]
                : string.Empty;
            var falseLine = childLineIndex < whenFalse.Lines.Count
                ? whenFalse.Lines[childLineIndex]
                : string.Empty;
            frameLines.Add(
                $"│ {trueLine.PadRight(whenTrueWidth)} " +
                $"│ {falseLine.PadRight(whenFalseWidth)} │");
        }

        frameLines.Add(CreateEmptyFrameLine());
        frameLines.Add(
            $"└{new string('─', whenTrueWidth + 2)}" +
            $"┴{new string('─', whenFalseWidth + 2)}┘");

        var dataInputCount = conditional.Inputs.Count - 1;
        var inputRoutingHeight = dataInputCount > 0 ? 3 : 0;
        var outputRoutingHeight = conditional.Outputs.Count > 0 ? 3 : 0;
        var frameTop = inputRoutingHeight;
        var localInputBusY = frameTop + 1;
        var childTop = localInputBusY;
        var localOutputBusY = childTop + branchHeight + 1;
        var frameBottom = frameTop + frameLines.Count - 1;
        var height =
            inputRoutingHeight +
            frameLines.Count +
            outputRoutingHeight;
        var canvas = new Canvas(frameLines[0].Length, height);

        for (var index = 0; index < frameLines.Count; index++)
        {
            canvas.Write(0, frameTop + index, frameLines[index]);
        }

        var inputOffsets = CreateBusPortOffsets(
            dataInputCount,
            [trueBundleX, falseBundleX, conditionX]);

        canvas.Overwrite(conditionX, 0, '│');

        if (inputOffsets.Length > 0)
        {
            const int globalInputBusY = 1;
            var inputBusStart = inputOffsets
                .Append(trueBundleX)
                .Append(falseBundleX)
                .Min();
            var inputBusEnd = inputOffsets
                .Append(trueBundleX)
                .Append(falseBundleX)
                .Max();
            DrawDoubleHorizontal(
                globalInputBusY,
                inputBusStart,
                inputBusEnd);

            foreach (var inputX in inputOffsets)
            {
                DrawDoubleVertical(inputX, 0, globalInputBusY - 1);
            }

            DrawInputBundle(
                trueBundleX,
                whenTrue.InputOffsets.Select(offset => trueOrigin + offset));
            DrawInputBundle(
                falseBundleX,
                whenFalse.InputOffsets.Select(offset => falseOrigin + offset));

            foreach (var x in inputOffsets
                .Append(trueBundleX)
                .Append(falseBundleX)
                .Distinct())
            {
                canvas.Overwrite(
                    x,
                    globalInputBusY,
                    GetJunction(
                        doubleStroke: true,
                        x,
                        inputBusStart,
                        inputBusEnd,
                        up: inputOffsets.Contains(x),
                        down: x == trueBundleX || x == falseBundleX));
            }

            void DrawInputBundle(
                int bundleX,
                IEnumerable<int> branchInputs)
            {
                var ports = branchInputs.ToArray();
                DrawDoubleVertical(
                    bundleX,
                    globalInputBusY + 1,
                    frameTop);
                canvas.Overwrite(bundleX, frameTop, '╨');
                canvas.DrawVertical(
                    bundleX,
                    frameTop + 1,
                    localInputBusY);
                var busStart = ports.Append(bundleX).Min();
                var busEnd = ports.Append(bundleX).Max();
                canvas.DrawHorizontal(
                    localInputBusY,
                    busStart,
                    busEnd);

                foreach (var portX in ports)
                {
                    if (localInputBusY < childTop)
                    {
                        canvas.DrawVertical(
                            portX,
                            localInputBusY + 1,
                            childTop);
                    }
                }

                canvas.Overwrite(conditionX, 1, '◆');

                foreach (var x in ports.Append(bundleX).Distinct())
                {
                    canvas.Overwrite(
                        x,
                        localInputBusY,
                        GetJunction(
                            doubleStroke: false,
                            x,
                            busStart,
                            busEnd,
                            up: x == bundleX,
                            down: ports.Contains(x)));
                }
            }
        }

        var outputOffsets = CreateBusPortOffsets(
            conditional.Outputs.Count,
            [trueBundleX, falseBundleX]);
        var contentBottom = height - 1;

        if (outputOffsets.Length > 0)
        {
            var globalOutputBusY = frameBottom + 2;
            DrawOutputBundle(
                trueBundleX,
                whenTrue.OutputOffsets.Select(offset => trueOrigin + offset),
                childTop + whenTrue.Lines.Count - 1);
            DrawOutputBundle(
                falseBundleX,
                whenFalse.OutputOffsets.Select(offset => falseOrigin + offset),
                childTop + whenFalse.Lines.Count - 1);
            var outputBusStart = outputOffsets
                .Append(trueBundleX)
                .Append(falseBundleX)
                .Min();
            var outputBusEnd = outputOffsets
                .Append(trueBundleX)
                .Append(falseBundleX)
                .Max();
            DrawDoubleHorizontal(
                globalOutputBusY,
                outputBusStart,
                outputBusEnd);

            foreach (var outputX in outputOffsets)
            {
                DrawDoubleVertical(
                    outputX,
                    globalOutputBusY + 1,
                    contentBottom);
            }

            foreach (var x in outputOffsets
                .Append(trueBundleX)
                .Append(falseBundleX)
                .Distinct())
            {
                canvas.Overwrite(
                    x,
                    globalOutputBusY,
                    GetJunction(
                        doubleStroke: true,
                        x,
                        outputBusStart,
                        outputBusEnd,
                        up: x == trueBundleX || x == falseBundleX,
                        down: outputOffsets.Contains(x)));
            }

            void DrawOutputBundle(
                int bundleX,
                IEnumerable<int> branchOutputs,
                int branchOutputY)
            {
                var ports = branchOutputs.ToArray();

                foreach (var portX in ports)
                {
                    canvas.DrawVertical(
                        portX,
                        branchOutputY,
                        localOutputBusY - 1);
                }

                var busStart = ports.Append(bundleX).Min();
                var busEnd = ports.Append(bundleX).Max();
                canvas.DrawHorizontal(
                    localOutputBusY,
                    busStart,
                    busEnd);

                foreach (var x in ports.Append(bundleX).Distinct())
                {
                    canvas.Overwrite(
                        x,
                        localOutputBusY,
                        GetJunction(
                            doubleStroke: false,
                            x,
                            busStart,
                            busEnd,
                            up: ports.Contains(x),
                            down: x == bundleX));
                }

                if (localOutputBusY + 1 < frameBottom)
                {
                    canvas.DrawVertical(
                        bundleX,
                        localOutputBusY + 1,
                        frameBottom - 1);
                }

                canvas.Overwrite(bundleX, frameBottom, '╥');
                DrawDoubleVertical(
                    bundleX,
                    frameBottom + 1,
                    globalOutputBusY - 1);
            }
        }

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        canvas.WriteTo(writer);

        return new GraphNodeContent(
            writer.ToString()
                .Split(Environment.NewLine)
                .Where(static line => line.Length > 0)
                .ToArray(),
            [conditionX, .. inputOffsets],
            outputOffsets);

        string CreateEmptyFrameLine() =>
            $"│ {string.Empty.PadRight(whenTrueWidth)} " +
            $"│ {string.Empty.PadRight(whenFalseWidth)} │";

        static GraphNodeContent ReserveTitleColumns(
            GraphNodeContent content,
            string title)
        {
            if (content.InputOffsets.Count == 0)
            {
                return content;
            }

            var minimumInputOffset = content.InputOffsets.Min();
            var requiredInputOffset = title.Length + 2;
            var padding = Math.Max(
                0,
                requiredInputOffset - minimumInputOffset);

            if (padding == 0)
            {
                return content;
            }

            return new GraphNodeContent(
                content.Lines
                    .Select(line => new string(' ', padding) + line)
                    .ToArray(),
                content.InputOffsets
                    .Select(offset => offset + padding)
                    .ToArray(),
                content.OutputOffsets
                    .Select(offset => offset + padding)
                    .ToArray());
        }

        void DrawDoubleHorizontal(int y, int startX, int endX)
        {
            for (var x = Math.Min(startX, endX);
                x <= Math.Max(startX, endX);
                x++)
            {
                canvas.Overwrite(x, y, '═');
            }
        }

        void DrawDoubleVertical(int x, int startY, int endY)
        {
            for (var y = Math.Min(startY, endY);
                y <= Math.Max(startY, endY);
                y++)
            {
                canvas.Overwrite(x, y, '║');
            }
        }

        static char GetJunction(
            bool doubleStroke,
            int x,
            int start,
            int end,
            bool up,
            bool down)
        {
            var left = x > start;
            var right = x < end;
            var connections =
                (up ? 0b0001 : 0) |
                (right ? 0b0010 : 0) |
                (down ? 0b0100 : 0) |
                (left ? 0b1000 : 0);

            return (doubleStroke, connections) switch
            {
                (false, 0b0011) => '└',
                (false, 0b0110) => '┌',
                (false, 0b1001) => '┘',
                (false, 0b1100) => '┐',
                (false, 0b1011) => '┴',
                (false, 0b1110) => '┬',
                (false, 0b0111) => '├',
                (false, 0b1101) => '┤',
                (false, 0b0101) => '│',
                (false, 0b1111) => '┼',
                (true, 0b0011) => '╚',
                (true, 0b0110) => '╔',
                (true, 0b1001) => '╝',
                (true, 0b1100) => '╗',
                (true, 0b1011) => '╩',
                (true, 0b1110) => '╦',
                (true, 0b0111) => '╠',
                (true, 0b1101) => '╣',
                (true, 0b0101) => '║',
                (true, 0b1111) => '╬',
                _ => doubleStroke ? '═' : '─',
            };
        }

        static int SelectBundleX(
            int preferred,
            int minimum,
            int maximum,
            IEnumerable<int> branchPorts)
        {
            var ports = branchPorts.ToHashSet();
            var candidate = Enumerable.Range(
                    minimum,
                    Math.Max(1, maximum - minimum + 1))
                .Where(candidate => !ports.Contains(candidate))
                .OrderBy(candidate => Math.Abs(candidate - preferred))
                .FirstOrDefault(-1);

            return candidate >= 0
                ? candidate
                : Math.Clamp(preferred, minimum, maximum);
        }

        int[] CreateBusPortOffsets(
            int count,
            IReadOnlyCollection<int> excluded)
        {
            var used = new HashSet<int>(excluded);
            var result = new int[count];

            for (var index = 0; index < count; index++)
            {
                var desired = ((index + 1) * frameWidth) / (count + 1);
                var candidate = Enumerable.Range(1, frameWidth - 2)
                    .Where(position => !used.Contains(position))
                    .OrderBy(position => Math.Abs(position - desired))
                    .First();
                result[index] = candidate;
                used.Add(candidate);
            }

            return result;
        }

        string CreateTitleLine()
        {
            var trueLine = whenTrue.Lines.Count > 0
                ? whenTrue.Lines[0]
                : string.Empty;
            var falseLine = whenFalse.Lines.Count > 0
                ? whenFalse.Lines[0]
                : string.Empty;
            var characters = (
                $"│ {trueLine.PadRight(whenTrueWidth)} " +
                $"│ {falseLine.PadRight(whenFalseWidth)} │")
                .ToCharArray();
            WriteTitle(
                characters,
                "true",
                left: 0,
                right: dividerX,
                [trueBundleX]);
            WriteTitle(
                characters,
                "false",
                left: dividerX,
                right: frameWidth - 1,
                [falseBundleX]);
            return new string(characters);
        }

        static void WriteTitle(
            char[] characters,
            string title,
            int left,
            int right,
            IEnumerable<int> inputPorts)
        {
            var ports = inputPorts.ToArray();
            var start = Enumerable.Range(
                    left + 2,
                    Math.Max(1, right - left - title.Length - 2))
                .FirstOrDefault(
                    candidate => ports.All(
                        port =>
                            port < candidate - 1 ||
                            port > candidate + title.Length),
                    left + 2);

            title.CopyTo(
                sourceIndex: 0,
                characters,
                destinationIndex: start,
                count: title.Length);
        }
    }

    private static void WriteLayout(Layout layout, TextWriter writer)
    {
        var canvas = new Canvas(layout.Width, layout.Height);

        foreach (var edge in layout.Edges)
        {
            DrawEdge(canvas, edge);
        }

        DrawSharedSourceJunctions(canvas, layout.Edges);
        DrawSharedValueCrossings(canvas, layout.Edges);

        foreach (var node in layout.Nodes)
        {
            DrawNode(canvas, node);
        }

        var labeledSources = new HashSet<(Node Source, int Slot)>();

        foreach (var edge in layout.Edges)
        {
            DrawEdgeEndpoints(
                canvas,
                edge,
                labeledSources.Add((edge.Source, edge.SourceSlot)));
        }

        canvas.WriteTo(writer);
    }

    private static void DrawSharedSourceJunctions(
        Canvas canvas,
        IReadOnlyList<Edge> edges)
    {
        foreach (var group in edges
            .Where(edge =>
                edge.Source.GetOutputX(edge.SourceSlot) !=
                edge.Target.GetInputX(edge.TargetSlot))
            .Select(edge => (
                Edge: edge,
                RouteY: edge.DepartureY ??
                    Math.Min(
                        edge.Target.Top - 2,
                        Math.Max(
                            edge.Source.Bottom + 2,
                            edge.SourceRankBottom + 2) +
                        ((edge.RouteLane ?? 0) * 2))))
            .GroupBy(entry => (
                entry.Edge.Source,
                entry.Edge.SourceSlot,
                entry.RouteY))
            .Where(group => group.Count() > 1))
        {
            var sourceX = group.Key.Source.GetOutputX(group.Key.SourceSlot);
            var routesLeft = group.Any(
                entry =>
                    (entry.Edge.LaneX ??
                     entry.Edge.Target.GetInputX(entry.Edge.TargetSlot)) <
                    sourceX);
            var routesRight = group.Any(
                entry =>
                    (entry.Edge.LaneX ??
                     entry.Edge.Target.GetInputX(entry.Edge.TargetSlot)) >
                    sourceX);
            var junction = (routesLeft, routesRight) switch
            {
                (true, true) => '┴',
                (true, false) => '┘',
                (false, true) => '└',
                _ => '│',
            };

            canvas.Overwrite(sourceX, group.Key.RouteY, junction);

            var routes = group
                .Select(entry => (
                    Entry: entry,
                    DestinationX:
                        entry.Edge.LaneX ??
                        entry.Edge.Target.GetInputX(entry.Edge.TargetSlot)))
                .ToArray();

            foreach (var route in routes)
            {
                var x = route.DestinationX;

                if (!routes.Any(
                    other =>
                        !ReferenceEquals(other.Entry.Edge, route.Entry.Edge) &&
                        Math.Min(sourceX, other.DestinationX) < x &&
                        x < Math.Max(sourceX, other.DestinationX)))
                {
                    continue;
                }

                var continuesLeft = routes.Any(
                    other => Math.Min(sourceX, other.DestinationX) < x);
                var continuesRight = routes.Any(
                    other => Math.Max(sourceX, other.DestinationX) > x);
                canvas.Overwrite(
                    x,
                    group.Key.RouteY,
                    (continuesLeft, continuesRight) switch
                    {
                        (true, true) => '┬',
                        (true, false) => '┐',
                        (false, true) => '┌',
                        _ => '│',
                    });
            }
        }
    }

    private static void DrawSharedValueCrossings(
        Canvas canvas,
        IReadOnlyList<Edge> edges)
    {
        foreach (var group in edges.GroupBy(
            edge => (edge.Source, edge.SourceSlot)))
        {
            var groupedEdges = group.ToArray();

            for (var firstIndex = 0;
                firstIndex < groupedEdges.Length;
                firstIndex++)
            {
                for (var secondIndex = firstIndex + 1;
                    secondIndex < groupedEdges.Length;
                    secondIndex++)
                {
                    ConnectCrossings(
                        groupedEdges[firstIndex],
                        groupedEdges[secondIndex]);
                    ConnectCrossings(
                        groupedEdges[secondIndex],
                        groupedEdges[firstIndex]);
                }
            }
        }

        void ConnectCrossings(Edge horizontalEdge, Edge verticalEdge)
        {
            foreach (var horizontal in GetHorizontalSegments(horizontalEdge))
            {
                foreach (var vertical in GetVerticalSegments(verticalEdge))
                {
                    if (Math.Min(horizontal.StartX, horizontal.EndX) <=
                            vertical.X &&
                        vertical.X <=
                            Math.Max(horizontal.StartX, horizontal.EndX) &&
                        Math.Min(vertical.StartY, vertical.EndY) <
                            horizontal.Y &&
                        horizontal.Y <
                            Math.Max(vertical.StartY, vertical.EndY))
                    {
                        canvas.Overwrite(
                            vertical.X,
                            horizontal.Y,
                            vertical.X == horizontal.StartX
                                ? horizontal.EndX > horizontal.StartX
                                    ? '├'
                                    : '┤'
                                : vertical.X == horizontal.EndX
                                    ? horizontal.StartX > horizontal.EndX
                                        ? '├'
                                        : '┤'
                                    : '┼');
                    }
                }
            }
        }
    }

    private static IEnumerable<(int Y, int StartX, int EndX)>
        GetHorizontalSegments(Edge edge)
    {
        var sourceX = edge.Source.GetOutputX(edge.SourceSlot);
        var targetX = edge.Target.GetInputX(edge.TargetSlot);

        if (edge.LaneX is int laneX)
        {
            yield return (edge.DepartureY!.Value, sourceX, laneX);
            yield return (edge.ArrivalY!.Value, laneX, targetX);
        }
        else if (sourceX != targetX)
        {
            yield return (GetMiddleRouteY(edge), sourceX, targetX);
        }
    }

    private static IEnumerable<(int X, int StartY, int EndY)>
        GetVerticalSegments(Edge edge)
    {
        var sourceX = edge.Source.GetOutputX(edge.SourceSlot);
        var sourceY = edge.Source.Bottom;
        var targetX = edge.Target.GetInputX(edge.TargetSlot);
        var targetY = edge.Target.Top;

        if (edge.LaneX is int laneX)
        {
            if (laneX == sourceX)
            {
                yield return (
                    sourceX,
                    sourceY,
                    edge.ArrivalY!.Value);
                yield return (targetX, edge.ArrivalY.Value, targetY);
                yield break;
            }

            yield return (sourceX, sourceY, edge.DepartureY!.Value);
            yield return (
                laneX,
                edge.DepartureY.Value,
                edge.ArrivalY!.Value);
            yield return (targetX, edge.ArrivalY.Value, targetY);
        }
        else if (sourceX == targetX)
        {
            yield return (sourceX, sourceY, targetY);
        }
        else
        {
            var middleY = GetMiddleRouteY(edge);
            yield return (sourceX, sourceY, middleY);
            yield return (targetX, middleY, targetY);
        }
    }

    private static int GetMiddleRouteY(Edge edge) =>
        Math.Min(
            edge.Target.Top - 2,
            Math.Max(
                edge.Source.Bottom + 2,
                edge.SourceRankBottom + 2) +
            ((edge.RouteLane ?? 0) * 2));

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

            if (laneX == sourceX)
            {
                if (sourceX == targetX)
                {
                    canvas.DrawEdgeVertical(
                        sourceX,
                        sourceY,
                        targetY,
                        verticalStroke);
                    return;
                }

                var laneEndSourceExisting = canvas.Get(sourceX, laneEndY);
                var laneEndTargetExisting = canvas.Get(targetX, laneEndY);
                canvas.DrawEdgeVertical(
                    sourceX,
                    sourceY,
                    laneEndY,
                    verticalStroke);
                canvas.DrawEdgeHorizontal(
                    laneEndY,
                    sourceX,
                    targetX,
                    horizontalStroke);
                canvas.DrawEdgeVertical(
                    targetX,
                    laneEndY,
                    targetY,
                    verticalStroke);
                canvas.OverwriteCorner(
                    sourceX,
                    laneEndY,
                    targetX > sourceX ? '└' : '┘',
                    laneEndSourceExisting);
                canvas.OverwriteCorner(
                    targetX,
                    laneEndY,
                    targetX > sourceX ? '┐' : '┌',
                    laneEndTargetExisting);
                return;
            }

            var laneSourceCornerExisting = canvas.Get(sourceX, laneStartY);
            var departureCornerExisting = canvas.Get(laneX, laneStartY);
            var arrivalCornerExisting = canvas.Get(laneX, laneEndY);
            var laneTargetCornerExisting = canvas.Get(targetX, laneEndY);
            canvas.DrawEdgeVertical(sourceX, sourceY, laneStartY, verticalStroke);
            canvas.DrawEdgeVertical(targetX, laneEndY, targetY, verticalStroke);
            canvas.DrawEdgeHorizontal(
                laneStartY,
                sourceX,
                laneX,
                horizontalStroke);
            canvas.DrawEdgeVertical(
                laneX,
                laneStartY,
                laneEndY,
                verticalStroke);
            canvas.DrawEdgeHorizontal(
                laneEndY,
                laneX,
                targetX,
                horizontalStroke);
            canvas.DrawEdgeVertical(
                targetX,
                laneEndY,
                lastRouteY,
                verticalStroke);
            canvas.OverwriteCorner(
                sourceX,
                laneStartY,
                laneX > sourceX ? '└' : '┘',
                laneSourceCornerExisting);
            canvas.OverwriteCorner(
                laneX,
                laneStartY,
                laneX > sourceX ? '┐' : '┌',
                departureCornerExisting);
            canvas.OverwriteCorner(
                laneX,
                laneEndY,
                targetX > laneX ? '└' : '┘',
                arrivalCornerExisting);
            canvas.OverwriteCorner(
                targetX,
                laneEndY,
                targetX > laneX ? '┐' : '┌',
                laneTargetCornerExisting);
            return;
        }

        if (sourceX == targetX)
        {
            canvas.DrawEdgeVertical(
                sourceX,
                sourceY,
                targetY,
                verticalStroke);
            return;
        }

        var middleY = Math.Min(
            lastRouteY,
            firstRouteY + ((edge.RouteLane ?? 0) * 2));
        var sourceCornerExisting = canvas.Get(sourceX, middleY);
        var targetCornerExisting = canvas.Get(targetX, middleY);
        canvas.DrawEdgeVertical(
            sourceX,
            sourceY,
            middleY,
            verticalStroke);
        canvas.DrawEdgeHorizontal(
            middleY,
            sourceX,
            targetX,
            horizontalStroke);
        canvas.DrawEdgeVertical(
            targetX,
            middleY,
            targetY,
            verticalStroke);
        canvas.OverwriteCorner(
            sourceX,
            middleY,
            targetX > sourceX ? '└' : '┘',
            sourceCornerExisting);
        canvas.OverwriteCorner(
            targetX,
            middleY,
            targetX > sourceX ? '┐' : '┌',
            targetCornerExisting);
    }

    private static void DrawEdgeEndpoints(
        Canvas canvas,
        Edge edge,
        bool drawSourceLabel)
    {
        var sourceX = edge.Source.GetOutputX(edge.SourceSlot);
        var targetX = edge.Target.GetInputX(edge.TargetSlot);

        if (edge.Source.Kind is not NodeKind.TargetInput)
        {
            canvas.Overwrite(
                sourceX,
                edge.Source.Bottom,
                edge.Source.UsesDoubleBoundaryLines &&
                    edge.Source.HasBodyOutput(edge.SourceSlot)
                    ? '╨'
                    : edge.Source.HasBodyOutput(edge.SourceSlot)
                        ? '┼'
                        : '┬');
        }

        if (edge.Target.Kind is not NodeKind.TargetOutput)
        {
            canvas.Overwrite(
                targetX,
                edge.Target.Top,
                edge.Target.UsesDoubleBoundaryLines &&
                    edge.TargetSlot > 0 &&
                    edge.Target.HasBodyInput(edge.TargetSlot)
                    ? '╥'
                    : edge.Target.HasBodyInput(edge.TargetSlot)
                        ? '┼'
                        : '┴');
            canvas.Overwrite(targetX, edge.Target.Top - 1, '▼');
        }

        if (drawSourceLabel &&
            edge.Source.Kind is not NodeKind.TargetInput)
        {
            canvas.Write(
                sourceX + 1,
                edge.Source.Bottom + 1,
                edge.SourceLabel);
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
            var portX = node.Kind is NodeKind.TargetInput
                ? node.GetOutputX(0)
                : node.GetInputX(0);
            canvas.Set(portX, node.Top, '│');
            canvas.Write(portX + 1, node.Top, node.Label);
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
                    var inputX = node.GetInputX(slot);

                    if (node.UsesDoubleBoundaryLines && slot > 0)
                    {
                        canvas.Overwrite(inputX, node.Top, '╥');
                        canvas.Overwrite(inputX, node.Top + 1, '║');
                        canvas.Overwrite(inputX, node.Top + 2, '║');
                    }
                    else
                    {
                        canvas.DrawVertical(
                            inputX,
                            node.Top,
                            node.Top + 2);
                    }
                }
            }

            for (var slot = 0; slot < (node.OutputPorts?.Count ?? 0); slot++)
            {
                if (node.OutputPorts?[slot] is not null)
                {
                    var outputX = node.GetOutputX(slot);

                    if (node.UsesDoubleBoundaryLines)
                    {
                        canvas.Overwrite(outputX, node.Bottom - 1, '║');
                        canvas.Overwrite(outputX, node.Bottom, '╨');
                    }
                    else
                    {
                        canvas.DrawVertical(
                            outputX,
                            node.Bottom - 1,
                            node.Bottom);
                    }
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
            Func<Value, string?>? valueLabelProvider = null,
            Func<Value, string?>? externalLabelProvider = null,
            bool renderDanglingOutputs = true,
            bool preserveBoundaryOrder = false,
            bool placeSourceOperationsAfterInputs = false)
        {
            var operationNodes =
                new Dictionary<Operation, Node>(ReferenceEqualityComparer.Instance);
            var displayIndex = 0;

            for (var index = 0; index < graph.Operations.Count; index++)
            {
                var operation = graph.Operations[index];
                var boundary = operation as ITargetBoundaryOperation;
                var target = operation as ITargetRenderingOperation;
                var conditional = operation as ConditionalRegionOperation;
                var content = contentProvider?.Invoke(operation);
                var operationLabel =
                    labelProvider?.Invoke(operation) ??
                    GetTypeDisplayName(operation.GetType());
                var inputPorts = target?.InputPorts ??
                    (conditional is null
                        ? null
                        : Enumerable.Range(
                                0,
                                conditional.Inputs.Count)
                            .Select(static port => (int?)port)
                            .ToArray());
                var outputPorts = target?.OutputPorts ??
                    (conditional is null
                        ? null
                        : Enumerable.Range(
                                0,
                                conditional.Outputs.Count)
                            .Select(static port => (int?)port)
                            .ToArray());
                var outputLabels = operation.Outputs
                    .Select((value, outputIndex) =>
                        GetSourceLabel(
                            value,
                            operation,
                            outputIndex,
                            valueLabelProvider))
                    .ToArray();
                operationNodes.Add(
                    operation,
                    new Node(
                        boundary is null
                            ? $"[{displayIndex++}] {operationLabel}"
                            : operationLabel,
                        index,
                        operation.Inputs.Count,
                        operation.Outputs.Count,
                        content,
                        boundary is not null
                            ? boundary.BoundaryKind is TargetBoundaryKind.Input
                                ? NodeKind.TargetInput
                                : NodeKind.TargetOutput
                            : NodeKind.Box,
                        target?.BodyInputCount ?? 0,
                        target?.BodyOutputCount ?? 0,
                        inputPorts,
                        outputPorts,
                        outputLabels,
                        usesDoubleBoundaryLines:
                            conditional is not null));
            }

            AssignOperationRanks(graph, operationNodes);

            if (placeSourceOperationsAfterInputs &&
                operationNodes.Values.Any(
                    node => node.Kind is NodeKind.TargetInput))
            {
                foreach (var node in operationNodes.Values.Where(
                    node => node.Kind is NodeKind.Box && node.Rank == 0))
                {
                    node.Rank = 1;
                }
            }

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
                                isGuardEdge,
                                GetSourceLabel(
                                    input,
                                    producer,
                                    IndexOfReference(producer.Outputs, input),
                                    valueLabelProvider)));
                        continue;
                    }

                    if (!externalNodes.TryGetValue(input, out var externalNode))
                    {
                        externalNode = new Node(
                            externalLabelProvider?.Invoke(input) ??
                                valueLabelProvider?.Invoke(input) ??
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
                            isGuardEdge,
                            GetSourceLabel(
                                input,
                                producer: null,
                                outputIndex: 0,
                                valueLabelProvider)));
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
                    var valueLabel = valueLabelProvider?.Invoke(output);
                    var outputNode = new Node(
                        valueLabel is not null
                            ? "output"
                            : $"output[{producerNode.Order}:{outputIndex}]",
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
                            isGuardEdge,
                            GetSourceLabel(
                                output,
                                operation,
                                outputIndex,
                                valueLabelProvider)));
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

            var ranks = nodes
                .GroupBy(node => node.Rank)
                .OrderBy(group => group.Key)
                .ToArray();

            foreach (var rank in ranks)
            {
                var rankNodes =
                    preserveBoundaryOrder &&
                    rank.All(
                        node => node.Kind is
                            NodeKind.TargetInput or NodeKind.TargetOutput)
                        ? rank.OrderBy(node => node.Order).ToArray()
                        : rank
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
                contentWidth,
                preserveBoundaryOrder);
            AssignLongEdgeLanes(
                longEdges,
                nodes,
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
                        : (routeLaneCount * 2) + 1;

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
                    edge.SourceRankBottom +
                    2 +
                    (edge.DepartureLane!.Value * 2);
                var precedingTargetRank =
                    ranks[rankIndexes[edge.Target.Rank] - 1];
                edge.ArrivalY =
                    precedingTargetRank.Max(node => node.Bottom) +
                    2 +
                    (edge.ArrivalLane!.Value * 2);
            }

            var width = contentWidth + 2 + (longEdges.Length * 2);
            var height = nodes.Max(node => node.Bottom) + 1;

            return new Layout(nodes, edges, operationNodes, width, height);
        }

        private static void AssignLongEdgeLanes(
            IReadOnlyList<Edge> longEdges,
            IReadOnlyList<Node> nodes,
            int contentWidth)
        {
            var reserved = new HashSet<int>();
            var fallbackIndex = 0;

            foreach (var edge in longEdges)
            {
                var sourceX = edge.Source.GetOutputX(edge.SourceSlot);
                var targetX = edge.Target.GetInputX(edge.TargetSlot);
                var blocked = nodes
                    .Where(node =>
                        node.Rank > edge.Source.Rank &&
                        node.Rank < edge.Target.Rank)
                    .SelectMany(node => Enumerable.Range(
                        Math.Max(0, node.Left - 1),
                        Math.Min(contentWidth - 1, node.Right + 1) -
                            Math.Max(0, node.Left - 1) +
                            1))
                    .ToHashSet();
                var laneX = Enumerable.Range(0, contentWidth)
                    .Where(candidate =>
                        !blocked.Contains(candidate) &&
                        !reserved.Contains(candidate))
                    .OrderBy(candidate =>
                        Math.Abs(candidate - sourceX) +
                        Math.Abs(candidate - targetX))
                    .FirstOrDefault(-1);

                if (laneX < 0)
                {
                    laneX = contentWidth + 2 + (fallbackIndex++ * 2);
                }

                edge.LaneX = laneX;
                reserved.Add(laneX);
            }
        }

        private static void ReorderRanksTowardConsumers(
            IReadOnlyList<IGrouping<int, Node>> ranks,
            IReadOnlyList<Edge> edges,
            IReadOnlyDictionary<int, int> rowWidths,
            int contentWidth,
            bool preserveBoundaryOrder)
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
                    if (preserveBoundaryOrder)
                    {
                        PositionRank(
                            rank.OrderBy(node => node.Order),
                            rowWidths[rank.Key],
                            contentWidth);
                        continue;
                    }

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

                PositionRank(
                    rankNodes,
                    rowWidths[rank.Key],
                    contentWidth);
            }
        }

        private static void PositionRank(
            IEnumerable<Node> nodes,
            int rowWidth,
            int contentWidth)
        {
            var left = (contentWidth - rowWidth) / 2;

            foreach (var node in nodes)
            {
                node.Left = left;
                left += node.Width + HorizontalGap;
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
                var lanes = new List<List<(
                    int Start,
                    int End,
                    (Node Source, int Slot)? SourceGroup)>>();
                var segments = new List<(
                    int Start,
                    int End,
                    int SourceX,
                    bool IsOrderEdge,
                    (Node Source, int Slot)? SourceGroup,
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
                            (edge.Source, edge.SourceSlot),
                            lane => edge.RouteLane = lane);
                    }
                    else if (edge.Source.Rank == sourceRank &&
                        edge.Target.Rank > targetRank)
                    {
                        AddSegment(
                            edge.Source.GetOutputX(edge.SourceSlot),
                            edge.LaneX!.Value,
                            edge.IsOrderEdge,
                            (edge.Source, edge.SourceSlot),
                            lane => edge.DepartureLane = lane);
                    }
                    else if (edge.Source.Rank < sourceRank &&
                        edge.Target.Rank == targetRank)
                    {
                        AddSegment(
                            edge.LaneX!.Value,
                            edge.Target.GetInputX(edge.TargetSlot),
                            edge.IsOrderEdge,
                            sourceGroup: null,
                            lane => edge.ArrivalLane = lane);
                    }
                }

                var orderLaneStart = -1;

                foreach (var segment in segments
                    .OrderBy(segment => segment.IsOrderEdge)
                    .ThenByDescending(
                        segment => segments.Count(
                            other =>
                                other.SourceGroup != segment.SourceGroup &&
                                other.Start < segment.SourceX &&
                                segment.SourceX < other.End))
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
                                (segment.SourceGroup is not null &&
                                 segment.SourceGroup == existing.SourceGroup) ||
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

                    lanes[laneIndex].Add(
                        (segment.Start, segment.End, segment.SourceGroup));
                    segment.AssignLane(laneIndex);
                }

                result[sourceRank] = lanes.Count;

                void AddSegment(
                    int firstX,
                    int secondX,
                    bool isOrderEdge,
                    (Node Source, int Slot)? sourceGroup,
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
                         firstX,
                         isOrderEdge,
                         sourceGroup,
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
            var targetOffset = node.Kind is NodeKind.TargetOutput
                ? 1
                : (((edge.TargetSlot + 1) * node.Width) /
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

        private static string GetSourceLabel(
            Value value,
            Operation? producer,
            int outputIndex,
            Func<Value, string?>? valueLabelProvider)
        {
            if (IsOrderValue(value))
            {
                return "order";
            }

            if (IsGuardValue(value))
            {
                return "guard";
            }

            var valueLabel = valueLabelProvider?.Invoke(value);

            if (valueLabel is not null)
            {
                return valueLabel;
            }

            return producer is null
                ? "o0"
                : $"o{GetOutputPort(producer, outputIndex)}";
        }

        private static string GetTypeDisplayName(Type type)
        {
            var name = type.Name;
            var genericMarker = name.IndexOf('`');
            return genericMarker < 0 ? name : name[..genericMarker];
        }
    }

    private static Func<Value, string?> CreateInitialValueLabelProvider(
        BuildProgram program)
    {
        var labels = new Dictionary<Value, string>(
            ReferenceEqualityComparer.Instance);

        foreach (var (value, content) in program.InitialValues)
        {
            labels.Add(
                value,
                FormatInitialValue(content));
        }

        return value => labels.GetValueOrDefault(value);
    }

    private static string FormatInitialValue(object? content) =>
        content switch
        {
            null => "null",
            string value => value.Length == 0 ? "''" : value,
            IReadOnlyList<string> items =>
                $"@({string.Join("; ", items)})",
            System.Collections.IEnumerable items =>
                $"@({string.Join(
                    "; ",
                    items.Cast<object>().Select(
                        static item => item.ToString()))})",
            bool value => value ? "true" : "false",
            IFormattable value =>
                value.ToString(format: null, CultureInfo.InvariantCulture),
            _ => content.ToString() ?? string.Empty,
        };

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
        IReadOnlyList<int?>? outputPorts = null,
        IReadOnlyList<string>? outputLabels = null,
        bool usesDoubleBoundaryLines = false)
    {
        public string Label { get; } = label;

        public int Order { get; } = order;

        public NodeKind Kind { get; } = kind;

        public int BodyInputCount { get; } = bodyInputCount;

        public int BodyOutputCount { get; } = bodyOutputCount;

        public IReadOnlyList<int?>? InputPorts { get; } = inputPorts;

        public IReadOnlyList<int?>? OutputPorts { get; } = outputPorts;

        public bool UsesDoubleBoundaryLines { get; } =
            usesDoubleBoundaryLines;

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
                outputPorts,
                outputLabels)
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
                ? Left + 1
                : content is not null &&
                    InputPorts is not null &&
                    InputPorts[slot] is int inputPort
                    ? Left + 2 + ContentOffset + content.InputOffsets[inputPort]
                    : Left + (((slot + 1) * Width) / (InputCount + 1));

        public int GetOutputX(int slot) =>
            Kind is NodeKind.TargetInput
                ? Left + 1
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
            IReadOnlyList<int?>? outputPorts,
            IReadOnlyList<string>? outputLabels)
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

            while (!PortLabelsFit(
                    width,
                    inputCount,
                    outputCount,
                    content,
                    contentOffset,
                    inputPorts,
                    outputPorts,
                    outputLabels))
            {
                width++;
            }

            return width;
        }

        private static bool PortLabelsFit(
            int width,
            int inputCount,
            int outputCount,
            GraphNodeContent? content,
            int contentOffset,
            IReadOnlyList<int?>? inputPorts,
            IReadOnlyList<int?>? outputPorts,
            IReadOnlyList<string>? outputLabels) =>
            LabelsFit(
                Enumerable.Range(0, inputPorts?.Count ?? 0)
                    .Select(slot =>
                    {
                        var port = inputPorts![slot];
                        var x = content is not null &&
                            port is int inputPort
                            ? 2 + contentOffset + content.InputOffsets[inputPort]
                            : ((slot + 1) * width) / (Math.Max(1, inputCount) + 1);
                        return (Start: x, End: x + (port is int index ? $"i{index}".Length : 0));
                    }),
                width) &&
            LabelsFit(
                Enumerable.Range(
                    0,
                    Math.Max(
                        outputPorts?.Count ?? 0,
                        outputLabels?.Count ?? 0))
                    .Select(slot =>
                    {
                        var port = outputPorts?[slot];
                        var x = content is not null &&
                            port is int outputPort
                            ? 2 + contentOffset + content.OutputOffsets[outputPort]
                            : content is not null && outputPorts is not null
                                ? GetOrderOutputPortX(width, slot, outputPorts)
                                : ((slot + 1) * width) /
                                    (Math.Max(1, outputCount) + 1);
                        var labelLength = outputLabels?[slot].Length ??
                            (port is int index ? $"o{index}".Length : "order".Length);
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
        bool IsGuardEdge,
        string SourceLabel)
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

        public void DrawEdgeHorizontal(
            int y,
            int startX,
            int endX,
            char character)
        {
            for (var x = Math.Min(startX, endX); x <= Math.Max(startX, endX); x++)
            {
                var existing = _characters[y, x];
                if (existing is '│' or '╎' or '╳')
                {
                    _characters[y, x] = '╳';
                    continue;
                }

                Set(x, y, character);
            }
        }

        public void DrawEdgeVertical(
            int x,
            int startY,
            int endY,
            char character)
        {
            for (var y = Math.Min(startY, endY); y <= Math.Max(startY, endY); y++)
            {
                var existing = _characters[y, x];
                if (existing is '─' or '╌' or '╳')
                {
                    _characters[y, x] = '╳';
                    continue;
                }

                Set(x, y, character);
            }
        }

        public void Set(int x, int y, char character)
        {
            var existing = _characters[y, x];
            var existingConnections = GetConnections(existing);
            var newConnections = GetConnections(character);

            _characters[y, x] = (existing, character) switch
            {
                ('\0', _) => character,
                _ when existing == character => existing,
                _ when existingConnections != 0 && newConnections != 0 =>
                    GetConnectionCharacter(
                        existingConnections | newConnections,
                        existing,
                        character),
                _ => character,
            };
        }

        private static int GetConnections(char character) =>
            character switch
            {
                '─' or '╌' => 0b1010,
                '│' or '╎' => 0b0101,
                '┌' => 0b0110,
                '┐' => 0b1100,
                '└' => 0b0011,
                '┘' => 0b1001,
                '├' => 0b0111,
                '┤' => 0b1101,
                '┬' => 0b1110,
                '┴' => 0b1011,
                '┼' => 0b1111,
                _ => 0,
            };

        private static char GetConnectionCharacter(
            int connections,
            char existing,
            char character) =>
            connections switch
            {
                0b1010 when existing == '╌' || character == '╌' => '╌',
                0b0101 when existing == '╎' || character == '╎' => '╎',
                0b1010 => '─',
                0b0101 => '│',
                0b0110 => '┌',
                0b1100 => '┐',
                0b0011 => '└',
                0b1001 => '┘',
                0b0111 => '├',
                0b1101 => '┤',
                0b1110 => '┬',
                0b1011 => '┴',
                _ => '┼',
            };

        public void Overwrite(int x, int y, char character)
        {
            _characters[y, x] = character;
        }

        public char Get(int x, int y) => _characters[y, x];

        public void OverwriteCorner(
            int x,
            int y,
            char character,
            char existing)
        {
            var existingConnections = GetConnections(existing);
            var cornerConnections = GetConnections(character);

            _characters[y, x] = existing switch
            {
                '\0' => character,
                _ when existingConnections != 0 && cornerConnections != 0 =>
                    GetConnectionCharacter(
                        existingConnections | cornerConnections,
                        existing,
                        character),
                _ => '┼',
            };
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
