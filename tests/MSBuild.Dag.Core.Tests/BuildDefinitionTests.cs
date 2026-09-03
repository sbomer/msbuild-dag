namespace MSBuild.Dag.Core.Tests;

public sealed class BuildDefinitionTests
{
    [Fact]
    public void LinksStateReadToLatestOrderedWrite()
    {
        var location = new StateLocation<string>();
        var writtenValue = new Value<string>();
        var writeOperation = new TestOperation([], [writtenValue]);
        var writer = new TargetDefinition(
            [],
            [],
            [new StateWrite<string>(location, writtenValue)],
            [],
            new OperationGraph([writeOperation]),
            []);
        var read = new StateRead<string>(location);
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
    public void LinksStateReadToEvaluationValueWithoutWriter()
    {
        var location = new StateLocation<string>();
        var initialization = new StateInitialization<string>(
            location,
            "initial");
        var read = new StateRead<string>(location);
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
        var location = new StateLocation<string>();
        var writtenValue = new Value<string>();
        var writer = new TargetDefinition(
            [],
            [],
            [new StateWrite<string>(location, writtenValue)],
            [],
            new OperationGraph(
            [
                new TestOperation([], [writtenValue]),
            ]),
            []);
        var read = new StateRead<string>(location);
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
    public void RejectsConditionalStateWrite()
    {
        var location = new StateLocation<string>();
        var value = new Value<string>();
        var target = new TargetDefinition(
            [],
            [],
            [
                new StateWrite<string>(location, value)
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

        var exception = Assert.Throws<ConditionalStateWriteException>(
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
