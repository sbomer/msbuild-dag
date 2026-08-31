namespace MSBuild.Dag.Core.Tests;

public sealed class BuildGraphTests
{
    [Fact]
    public void DerivesDependencyThroughPropertyValue()
    {
        var task = new TestOperation([], [new Value<string>()]);
        var consumer = new TestOperation([task.Outputs[0]], [new Value()]);

        var graph = new BuildGraph([task, consumer]);

        Assert.Same(task, graph.GetProducer(task.Outputs[0]));
        Assert.Equal([task], graph.GetDependencies(consumer));
    }

    [Fact]
    public void DerivesDependencyThroughItemCollection()
    {
        var initialItems = new Value<IReadOnlyList<string>>();
        var generatedItems = new TestOperation(
            [],
            [new Value<IReadOnlyList<string>>()]);
        var concatenatedItems = new TestOperation(
            [initialItems, generatedItems.Outputs[0]],
            [new Value<IReadOnlyList<string>>()]);
        var compile = new TestOperation(
            [concatenatedItems.Outputs[0]],
            [new Value()]);

        var graph = new BuildGraph([generatedItems, concatenatedItems, compile]);

        Assert.Equal([generatedItems], graph.GetDependencies(concatenatedItems));
        Assert.Equal([concatenatedItems], graph.GetDependencies(compile));
        Assert.Null(graph.GetProducer(initialItems));
    }

    [Fact]
    public void CollapsesMultipleValuesFromSameProducerToOneDependency()
    {
        var producer = new TestOperation([], [new Value(), new Value()]);
        var consumer = new TestOperation(producer.Outputs, [new Value()]);

        var graph = new BuildGraph([producer, consumer]);

        Assert.Equal([producer], graph.GetDependencies(consumer));
    }

    [Fact]
    public void RejectsMultipleProducersForOneValue()
    {
        var sharedOutput = new Value();
        var first = new TestOperation([], [sharedOutput]);
        var second = new TestOperation([], [sharedOutput]);

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildGraph([first, second]));

        Assert.Contains("multiple producers", exception.Message);
    }

    [Fact]
    public void RejectsCycles()
    {
        var firstOutput = new Value();
        var secondOutput = new Value();
        var first = new TestOperation([secondOutput], [firstOutput]);
        var second = new TestOperation([firstOutput], [secondOutput]);

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildGraph([first, second]));

        Assert.Contains("acyclic", exception.Message);
    }

    private sealed class TestOperation(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs) : Operation
    {
        public override IReadOnlyList<Value> Inputs { get; } = inputs;

        public override IReadOnlyList<Value> Outputs { get; } = outputs;
    }
}
