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
        var graph = new BuildGraph([producer, consumer]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Equal(1, CountOccurrences(result, "[0] TestOperation"));
        Assert.Equal(1, CountOccurrences(result, "[1] TestOperation"));
        Assert.Equal(1, CountOccurrences(result, "external[0]"));
        Assert.Equal(1, CountOccurrences(result, "output[0:1]"));
        Assert.Equal(1, CountOccurrences(result, "output[1:0]"));
        Assert.Contains('▶', result);
        Assert.Contains('┌', result);
        Assert.Contains('┘', result);
        Assert.Contains("o0", result);
        Assert.Contains("i1", result);
        Assert.DoesNotContain('┼', result);
    }

    [Fact]
    public void RendersEmptyGraph()
    {
        var graph = new BuildGraph([]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Equal($"BuildGraph{Environment.NewLine}(empty){Environment.NewLine}", result);
    }

    [Fact]
    public void OmitsOrderEdgeWhenDataAlreadyConnectsOperations()
    {
        var initialOrder = new Value<OrderToken>();
        var producer = new OrderedTestOperation(initialOrder);
        var consumer = new OrderedTestOperation(
            producer.OrderOutput!,
            producer.Result);
        var graph = new BuildGraph([producer, consumer]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Equal(3, CountOccurrences(result, "▶"));
        Assert.DoesNotContain('┼', result);
    }

    [Fact]
    public void VerticalGraphLabelsOrderEdgesSeparatelyFromDataEdges()
    {
        var initialOrder = new Value<OrderToken>();
        var first = new OrderedTestOperation(initialOrder);
        var second = new OrderedTestOperation(first.OrderOutput!);
        var consumer = new TestOperation(
            [first.Result, second.Result],
            [new Value()]);
        var graph = new BuildGraph([first, second, consumer]);

        var result = VerticalGraphWriter.Render(graph);

        Assert.Contains("order", result);
        Assert.Contains("i0", result);
        Assert.Contains("i1", result);
        Assert.Equal(1, CountOccurrences(result, "[2] TestOperation"));
        Assert.DoesNotContain('┼', result);
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
                : [input, orderInput];
        }

        public Value<OrderToken>? OrderInput { get; }

        public Value<OrderToken>? OrderOutput { get; } = new();

        public Value<int> Result { get; } = new();

        public override IReadOnlyList<Value> Inputs => _inputs;

        public override IReadOnlyList<Value> Outputs =>
            [Result, OrderOutput!];
    }
}
