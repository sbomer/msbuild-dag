using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization.Tests;

public sealed class AsciiGraphWriterTests
{
    [Fact]
    public void RendersInputsOutputsAndDependencies()
    {
        var external = new Value();
        var producer = new TestOperation([], [new Value(), new Value()]);
        var consumer = new TestOperation(
            [external, producer.Outputs[0]],
            [new Value()]);
        var graph = new OperationGraph([producer, consumer]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Equal(1, CountOccurrences(result, "[0] TestOperation"));
        Assert.Equal(1, CountOccurrences(result, "[1] TestOperation"));
        Assert.Equal(1, CountOccurrences(result, "external[0]"));
        Assert.Equal(1, CountOccurrences(result, "output[0:1]"));
        Assert.Equal(1, CountOccurrences(result, "output[1:0]"));
        Assert.Contains('▼', result);
        Assert.Contains('┌', result);
        Assert.Contains('┘', result);
        Assert.Contains("o0", result);
        Assert.Contains("i1", result);
    }

    [Fact]
    public void RendersEmptyGraph()
    {
        var graph = new OperationGraph([]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Equal($"OperationGraph{Environment.NewLine}(empty){Environment.NewLine}", result);
    }

    [Fact]
    public void RendersOrderEdgeWhenDataAlreadyConnectsOperations()
    {
        var initialOrder = new Value<OrderToken>();
        var producer = new OrderedTestOperation(initialOrder);
        var consumer = new OrderedTestOperation(
            producer.OrderOutput!,
            producer.Result);
        var graph = new OperationGraph([producer, consumer]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Contains('▼', result);
        Assert.Contains("order", result);
        Assert.Contains('╎', result);
    }

    [Fact]
    public void LabelsOrderEdgesSeparatelyFromDataEdges()
    {
        var initialOrder = new Value<OrderToken>();
        var first = new OrderedTestOperation(initialOrder);
        var second = new OrderedTestOperation(first.OrderOutput!);
        var consumer = new TestOperation(
            [first.Result, second.Result],
            [new Value()]);
        var graph = new OperationGraph([first, second, consumer]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Contains("order", result);
        Assert.Contains('╎', result);
        Assert.Contains("i0", result);
        Assert.Contains("i1", result);
        Assert.Equal(1, CountOccurrences(result, "[2] TestOperation"));
    }

    [Fact]
    public void RendersTargetsWithDataAndExplicitOrderEdges()
    {
        var data = new Value<string>();
        var producer = new TestOperation([], [data]);
        var consumer = new TestOperation([data], []);
        var first = new Target([], [data], [producer]);
        var second = new Target([data], [], [consumer]);
        var third = new Target([], [], []);
        var graph = new BuildGraph(
            [first, second, third],
            [new TargetDependency(second, third)]);
        var names = new Dictionary<Target, string>(
            ReferenceEqualityComparer.Instance)
        {
            [first] = "Prepare",
            [second] = "Compile",
            [third] = "Report",
        };

        var result = AsciiGraphWriter.RenderCompact(graph, names);

        Assert.StartsWith($"BuildGraph{Environment.NewLine}", result);
        Assert.Contains("[0] Prepare", result);
        Assert.Contains("[1] Compile", result);
        Assert.Contains("[2] Report", result);
        Assert.Contains('─', result);
        Assert.Contains("order", result);
        Assert.Contains('╎', result);
    }

    [Fact]
    public void ExpandedBuildGraphIncludesTargetBodies()
    {
        var data = new Value<string>();
        var producer = new TestOperation([], [data]);
        var consumer = new TestOperation([data], []);
        var first = new Target([], [data], [producer]);
        var second = new Target([data], [], [consumer]);
        var graph = new BuildGraph([first, second]);
        var names = new Dictionary<Target, string>(
            ReferenceEqualityComparer.Instance)
        {
            [first] = "Prepare",
            [second] = "Compile",
        };

        var result = AsciiGraphWriter.Render(graph, names);

        Assert.Contains("BuildGraph", result);
        Assert.Contains("[0] Prepare", result);
        Assert.Contains("[1] Compile", result);
        Assert.Equal(2, CountOccurrences(result, "TestOperation"));
        Assert.DoesNotContain("Target bodies", result);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;

        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private sealed class TestOperation(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs) : Operation
    {
        public override IReadOnlyList<Value> Inputs { get; } = inputs;

        public override IReadOnlyList<Value> Outputs { get; } = outputs;
    }

    private sealed class OrderedTestOperation : Operation, IOrderedOperation
    {
        private readonly IReadOnlyList<Value> _inputs;

        public OrderedTestOperation(
            Value<OrderToken> orderInput,
            Value<int>? input = null)
        {
            OrderInput = orderInput;
            _inputs = input is null
                ? [orderInput]
                : [orderInput, input];
        }

        public Value<OrderToken>? OrderInput { get; }

        public Value<OrderToken>? OrderOutput { get; } = new();

        public Value<int> Result { get; } = new();

        public override IReadOnlyList<Value> Inputs => _inputs;

        public override IReadOnlyList<Value> Outputs =>
            [OrderOutput!, Result];
    }
}
