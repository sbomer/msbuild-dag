namespace MSBuild.Dag.Core.Tests;

public sealed class BuildDefinitionTests
{
    [Fact]
    public void LinksTargetInputToLatestOrderedOutput()
    {
        var location = new Location<string>();
        var writtenValue = new Value<string>();
        var writeOperation = new TestOperation([], [writtenValue]);
        var writer = new TargetDefinition(
            [],
            [],
            [new TargetOutput<string>(location, writtenValue)],
            [],
            new OperationGraph([writeOperation]),
            []);
        var read = new TargetInput<string>(location);
        var result = new Value<string>();
        var readOperation = new TestOperation([read.Value], [result]);
        var reader = new TargetDefinition(
            [writer],
            [read],
            [],
            [result],
            new OperationGraph([readOperation]),
            []);

        var linked = new BuildDefinition(
            new EvaluationSnapshot(
            [
                new StateInitialization<string>(location, "initial"),
            ]),
            [reader, writer])
            .Link();

        var linkedWriter = linked.Targets[writer];
        var linkedReader = linked.Targets[reader];
        var exportedValue = Assert.Single(linkedWriter.Outputs);

        Assert.Null(linkedReader.Body.GetProducer(read.Value));
        Assert.Equal([read.Value], linkedReader.Body.Inputs);
        Assert.Equal([exportedValue], linkedReader.Inputs);
        Assert.Contains(writtenValue, linkedWriter.Body.Outputs);
        Assert.NotSame(writtenValue, exportedValue);
        Assert.Same(
            exportedValue,
            linked.GetStateAfter(reader)[location]);
    }

    [Fact]
    public void LinksTargetInputToEvaluationValueWithoutOutput()
    {
        var location = new Location<string>();
        var initialization = new StateInitialization<string>(
            location,
            "initial");
        var read = new TargetInput<string>(location);
        var result = new Value<string>();
        var target = new TargetDefinition(
            [],
            [read],
            [],
            [result],
            new OperationGraph(
            [
                new TestOperation([read.Value], [result]),
            ]),
            []);

        var linked = new BuildDefinition(
            new EvaluationSnapshot([initialization]),
            [target])
            .Link();

        var linkedTarget = linked.Targets[target];

        Assert.Null(linkedTarget.Body.GetProducer(read.Value));
        Assert.Equal([read.Value], linkedTarget.Body.Inputs);
        Assert.Equal(
            [initialization.InitialValue.Value],
            linkedTarget.Inputs);
        Assert.Same(
            initialization.InitialValue.Value,
            linked.GetStateAfter(target)[location]);
    }

    [Fact]
    public void RejectsUnorderedStateConflict()
    {
        var location = new Location<string>();
        var writtenValue = new Value<string>();
        var writer = new TargetDefinition(
            [],
            [],
            [new TargetOutput<string>(location, writtenValue)],
            [],
            new OperationGraph(
            [
                new TestOperation([], [writtenValue]),
            ]),
            []);
        var read = new TargetInput<string>(location);
        var reader = new TargetDefinition(
            [],
            [read],
            [],
            [],
            new OperationGraph(
            [
                new TestOperation([read.Value], []),
            ]),
            []);

        var exception = Assert.Throws<StateConflictException>(
            () => new BuildDefinition(
                new EvaluationSnapshot(
                [
                    new StateInitialization<string>(location, "initial"),
                ]),
                [writer, reader])
                .Link());

        Assert.Same(writer, exception.FirstTarget);
        Assert.Same(reader, exception.SecondTarget);
        Assert.Same(location, exception.Location);
    }

    [Fact]
    public void RejectsConditionalTargetOutput()
    {
        var location = new Location<string>();
        var value = new Value<string>();
        var target = new TargetDefinition(
            [],
            [],
            [
                new TargetOutput<string>(location, value)
                {
                    IsConditional = true,
                },
            ],
            [],
            new OperationGraph(
            [
                new TestOperation([], [value]),
            ]),
            []);

        var exception = Assert.Throws<ConditionalTargetOutputException>(
            () => new BuildDefinition(
                new EvaluationSnapshot(
                [
                    new StateInitialization<string>(location, "initial"),
                ]),
                [target])
                .Link());

        Assert.Same(target, exception.Target);
    }

    private sealed class TestOperation(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs) : Operation
    {
        public override IReadOnlyList<Value> Inputs { get; } = inputs;

        public override IReadOnlyList<Value> Outputs { get; } = outputs;
    }
}
