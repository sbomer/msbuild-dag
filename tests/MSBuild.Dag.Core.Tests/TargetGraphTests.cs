namespace MSBuild.Dag.Core.Tests;

public sealed class TargetGraphTests
{
    [Fact]
    public void DerivesTargetDependencyThroughExportedValue()
    {
        var value = new Value<string>();
        var producer = new TestOperation([], [value]);
        var consumer = new TestOperation([value], []);
        var producingTarget = new Target([], [value], [producer]);
        var consumingTarget = new Target([value], [], [consumer]);

        var graph = new BuildGraph([producingTarget, consumingTarget]);

        Assert.Same(producingTarget, graph.GetProducer(value));
        Assert.Equal([producingTarget], graph.GetDependencies(consumingTarget));
    }

    [Fact]
    public void IncludesExplicitOrderDependencies()
    {
        var dependency = new Target([], [], []);
        var target = new Target([], [], []);

        var graph = new BuildGraph(
            [dependency, target],
            [new TargetDependency(dependency, target)]);

        Assert.Equal([dependency], graph.GetDependencies(target));
    }

    [Fact]
    public void RejectsExternalOperationInputMissingFromTargetInputs()
    {
        var external = new Value<string>();
        var operation = new TestOperation([external], []);

        var exception = Assert.Throws<ArgumentException>(
            () => new Target([], [], [operation]));

        Assert.Contains("target input", exception.Message);
    }

    [Fact]
    public void RejectsTargetOutputNotProducedByTarget()
    {
        var output = new Value<string>();

        var exception = Assert.Throws<ArgumentException>(
            () => new Target([], [output], []));

        Assert.Contains("produced inside", exception.Message);
    }

    [Fact]
    public void RejectsCrossTargetValueNotExportedByProducer()
    {
        var value = new Value<string>();
        var producer = new TestOperation([], [value]);
        var consumer = new TestOperation([value], []);
        var producingTarget = new Target([], [], [producer]);
        var consumingTarget = new Target([value], [], [consumer]);

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildGraph([producingTarget, consumingTarget]));

        Assert.Contains("exported", exception.Message);
    }

    [Fact]
    public void RejectsCyclesAcrossDataAndOrderDependencies()
    {
        var value = new Value<string>();
        var producer = new TestOperation([], [value]);
        var consumer = new TestOperation([value], []);
        var first = new Target([], [value], [producer]);
        var second = new Target([value], [], [consumer]);

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildGraph(
                [first, second],
                [new TargetDependency(second, first)]));

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
