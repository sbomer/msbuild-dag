namespace MSBuild.Dag.Core.Tests;

public sealed class BuildProgramTests
{
    [Fact]
    public void DerivesTargetPredecessorThroughExportedValue()
    {
        var value = new Value<string>();
        var producer = new TestOperation([], [value]);
        var consumer = new TestOperation([value], []);
        var producingTarget = new Target(
            [],
            [value],
            new OperationGraph([producer]));
        var consumingTarget = new Target(
            [value],
            [],
            new OperationGraph([consumer]));

        var program = new BuildProgram([producingTarget, consumingTarget]);

        Assert.Same(producingTarget, program.GetProducer(value));
        Assert.Equal([producingTarget], program.GetPredecessors(consumingTarget));
    }

    [Fact]
    public void TargetUsesItsBodySignature()
    {
        var input = new Value<string>();
        var output = new Value<string>();
        var operation = new TestOperation([input], [output]);
        var body = new OperationGraph(
            [input],
            [operation],
            [output]);

        var target = new Target(body);

        Assert.Same(body.Inputs, target.Inputs);
        Assert.Same(body.Outputs, target.Outputs);
    }

    [Fact]
    public void IncludesExplicitOrderDependencies()
    {
        var dependency = EmptyTarget();
        var target = new Target(
            [dependency],
            [],
            [],
            new OperationGraph([]),
            []);

        var program = new BuildProgram([dependency, target]);

        Assert.Equal([dependency], program.GetPredecessors(target));
    }

    [Fact]
    public void PreservesPreludeAndEpilogueSequenceOrder()
    {
        var first = EmptyTarget();
        var second = EmptyTarget();
        var afterFirst = EmptyTarget();
        var afterSecond = EmptyTarget();
        var target = new Target(
            [first, second],
            [],
            [],
            new OperationGraph([]),
            [afterFirst, afterSecond]);

        var program = new BuildProgram(
            [first, second, target, afterFirst, afterSecond]);

        Assert.Contains(first, program.GetOrderPredecessors(second));
        Assert.Contains(second, program.GetOrderPredecessors(target));
        Assert.Contains(target, program.GetOrderPredecessors(afterFirst));
        Assert.Contains(afterFirst, program.GetOrderPredecessors(afterSecond));
    }

    [Fact]
    public void RejectsTargetReferenceOutsideGraph()
    {
        var missing = EmptyTarget();
        var target = new Target(
            [missing],
            [],
            [],
            new OperationGraph([]),
            []);

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildProgram([target]));

        Assert.Contains("target reference", exception.Message);
    }

    [Fact]
    public void RejectsExternalOperationInputMissingFromTargetInputs()
    {
        var external = new Value<string>();
        var operation = new TestOperation([external], []);

        var exception = Assert.Throws<ArgumentException>(
            () => new Target([], [], new OperationGraph([operation])));

        Assert.Contains("operation-graph input", exception.Message);
    }

    [Fact]
    public void RejectsTargetOutputNotProducedByTarget()
    {
        var output = new Value<string>();

        var exception = Assert.Throws<ArgumentException>(
            () => new Target([], [output], new OperationGraph([])));

        Assert.Contains("produced inside", exception.Message);
    }

    [Fact]
    public void RejectsTargetPassingInputThroughAsOutput()
    {
        var value = new Value<string>();
        var body = new OperationGraph([value], [], [value]);

        var exception = Assert.Throws<ArgumentException>(
            () => new Target(body));

        Assert.Contains("produced inside", exception.Message);
    }

    [Fact]
    public void RejectsCrossTargetValueNotExportedByProducer()
    {
        var value = new Value<string>();
        var producer = new TestOperation([], [value]);
        var consumer = new TestOperation([value], []);
        var producingTarget = new Target(
            [],
            [],
            new OperationGraph([producer]));
        var consumingTarget = new Target(
            [value],
            [],
            new OperationGraph([consumer]));

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildProgram([producingTarget, consumingTarget]));

        Assert.Contains("exported", exception.Message);
    }

    [Fact]
    public void RejectsCyclesAcrossDataAndOrderDependencies()
    {
        var value = new Value<string>();
        var producer = new TestOperation([], [value]);
        var consumer = new TestOperation([value], []);
        var first = new Target(
            [],
            [value],
            new OperationGraph([producer]));
        var second = new Target(
            [value],
            [],
            new OperationGraph([consumer]));

        var orderedFirst = new Target(
            [second],
            [],
            [value],
            new OperationGraph([producer]),
            []);

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildProgram([orderedFirst, second]));

        Assert.Contains("acyclic", exception.Message);
    }

    [Fact]
    public void RejectsContradictoryGlobalTargetOrder()
    {
        var first = EmptyTarget();
        var second = EmptyTarget();
        var firstThenSecond = new Target(
            [first, second],
            [],
            [],
            new OperationGraph([]),
            []);
        var secondThenFirst = new Target(
            [second, first],
            [],
            [],
            new OperationGraph([]),
            []);

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildProgram(
                [first, second, firstThenSecond, secondThenFirst]));

        Assert.Contains("program order", exception.Message);
        Assert.Contains("acyclic", exception.Message);
    }

    private static Target EmptyTarget() =>
        new([], [], new OperationGraph([]));

    private sealed class TestOperation(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs) : Operation
    {
        public override IReadOnlyList<Value> Inputs { get; } = inputs;

        public override IReadOnlyList<Value> Outputs { get; } = outputs;
    }
}
