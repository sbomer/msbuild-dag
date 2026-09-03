namespace MSBuild.Dag.Core.Tests;

public sealed class OperationGraphTests
{
    [Fact]
    public void DerivesDependencyThroughPropertyValue()
    {
        var task = new TestOperation([], [new Value<string>()]);
        var consumer = new TestOperation([task.Outputs[0]], [new Value()]);

        var graph = new OperationGraph([task, consumer]);

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

        var graph = new OperationGraph([generatedItems, concatenatedItems, compile]);

        Assert.Equal([generatedItems], graph.GetDependencies(concatenatedItems));
        Assert.Equal([concatenatedItems], graph.GetDependencies(compile));
        Assert.Null(graph.GetProducer(initialItems));
    }

    [Fact]
    public void CollapsesMultipleValuesFromSameProducerToOneDependency()
    {
        var producer = new TestOperation([], [new Value(), new Value()]);
        var consumer = new TestOperation(producer.Outputs, [new Value()]);

        var graph = new OperationGraph([producer, consumer]);

        Assert.Equal([producer], graph.GetDependencies(consumer));
    }

    [Fact]
    public void SignedGraphAllowsInputToPassThroughAsOutput()
    {
        var value = new Value<string>();

        var graph = new OperationGraph([value], [], [value]);

        Assert.Equal([value], graph.Inputs);
        Assert.Equal([value], graph.Outputs);
    }

    [Fact]
    public void ConditionalRegionRequiresMatchingBranchSignatures()
    {
        var condition = new Value<bool>();
        var argument = new Value<string>();
        var result = new Value<string>();
        var whenTrueInput = new Value<string>();
        var whenFalseInput = new Value<int>();

        var exception = Assert.Throws<ArgumentException>(
            () => new ConditionalRegionOperation(
                condition,
                [argument],
                new OperationGraph(
                    [whenTrueInput],
                    [],
                    [whenTrueInput]),
                new OperationGraph(
                    [whenFalseInput],
                    [],
                    [whenFalseInput]),
                [result]));

        Assert.Contains("input types", exception.Message);
    }

    [Fact]
    public void ConditionalRegionRejectsInferredBranchBoundaries()
    {
        var condition = new Value<bool>();
        var argument = new Value<string>();
        var result = new Value<string>();
        var whenTrueInput = new Value<string>();
        var whenFalseInput = new Value<string>();

        var exception = Assert.Throws<ArgumentException>(
            () => new ConditionalRegionOperation(
                condition,
                [argument],
                new OperationGraph([]),
                new OperationGraph(
                    [whenFalseInput],
                    [],
                    [whenFalseInput]),
                [result]));

        Assert.Contains("explicit", exception.Message);
    }

    [Fact]
    public void OperationGraphRejectsConditionalBranchValueAliasing()
    {
        var condition = new Value<bool>();
        var argument = new Value<string>();
        var branchInput = new Value<string>();
        var falseInput = new Value<string>();
        var result = new Value<string>();
        var conditional = new ConditionalRegionOperation(
            condition,
            [argument],
            new OperationGraph(
                [branchInput],
                [],
                [branchInput]),
            new OperationGraph(
                [falseInput],
                [],
                [falseInput]),
            [result]);

        var exception = Assert.Throws<ArgumentException>(
            () => new OperationGraph(
                [condition, argument, branchInput],
                [conditional],
                [result]));

        Assert.Contains("local to one branch", exception.Message);
    }

    [Fact]
    public void ConditionalRegionRejectsBranchValueAsResult()
    {
        var condition = new Value<bool>();
        var argument = new Value<string>();
        var whenTrueInput = new Value<string>();
        var whenFalseInput = new Value<string>();

        var exception = Assert.Throws<ArgumentException>(
            () => new ConditionalRegionOperation(
                condition,
                [argument],
                new OperationGraph(
                    [whenTrueInput],
                    [],
                    [whenTrueInput]),
                new OperationGraph(
                    [whenFalseInput],
                    [],
                    [whenFalseInput]),
                [whenTrueInput]));

        Assert.Contains("must not be local", exception.Message);
    }

    [Fact]
    public void RejectsMultipleProducersForOneValue()
    {
        var sharedOutput = new Value();
        var first = new TestOperation([], [sharedOutput]);
        var second = new TestOperation([], [sharedOutput]);

        var exception = Assert.Throws<ArgumentException>(
            () => new OperationGraph([first, second]));

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
            () => new OperationGraph([first, second]));

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
