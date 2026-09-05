namespace MSBuild.Dag.Core.Tests;

public sealed class BuildProgramTests
{
    [Fact]
    public void DerivesTargetPredecessorThroughExportedValue()
    {
        var bodyOutput = new Value<string>();
        var producer = new TestOperation([], [bodyOutput]);
        var producingTarget = new Target(
            new OperationGraph([producer]));
        var exportedValue = Assert.Single(producingTarget.Outputs);
        var bodyInput = new Value<string>();
        var consumer = new TestOperation([bodyInput], []);
        var consumingTarget = new Target(
            [],
            [exportedValue],
            new OperationGraph([bodyInput], [consumer], []),
            []);

        var program = new BuildProgram([producingTarget, consumingTarget]);

        Assert.Same(producingTarget, program.GetProducer(exportedValue));
        Assert.Equal([producingTarget], program.GetPredecessors(consumingTarget));
    }

    [Fact]
    public void TargetUsesDistinctSymmetricBoundaryValues()
    {
        var input = new Value<string>();
        var output = new Value<string>();
        var operation = new TestOperation([input], [output]);
        var body = new OperationGraph(
            [input],
            [operation],
            [output]);

        var target = new Target(body);

        Assert.Single(target.Inputs);
        Assert.Single(target.Outputs);
        Assert.NotSame(body.Inputs[0], target.Inputs[0]);
        Assert.NotSame(body.Outputs[0], target.Outputs[0]);
        Assert.Equal(body.Inputs[0].GetType(), target.Inputs[0].GetType());
        Assert.Equal(body.Outputs[0].GetType(), target.Outputs[0].GetType());
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
    public void RejectsInitialContentWithDifferentType()
    {
        var value = new Value<string>();

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildProgram(
                [],
                new Dictionary<Value, object?>
                {
                    [value] = 42,
                }));

        Assert.Contains("System.String", exception.Message);
    }

    [Fact]
    public void AllowsTargetPassingInputThroughAsDistinctOutput()
    {
        var value = new Value<string>();
        var body = new OperationGraph([value], [], [value]);

        var target = new Target(body);

        Assert.NotSame(target.Inputs[0], value);
        Assert.NotSame(value, target.Outputs[0]);
        Assert.NotSame(target.Inputs[0], target.Outputs[0]);
    }

    [Fact]
    public void AllowsTargetToExportBodyParameterBoundFromExternalInput()
    {
        var external = new Value<string>();
        var parameter = new Value<string>();
        var body = new OperationGraph([parameter], [], [parameter]);

        var target = new Target([], [external], body, []);

        Assert.Equal([external], target.Inputs);
        Assert.Equal([parameter], target.Body.Inputs);
        Assert.Equal([parameter], target.Body.Outputs);
        Assert.NotSame(parameter, Assert.Single(target.Outputs));
    }

    [Fact]
    public void RejectsTargetInputBindingWithDifferentType()
    {
        var external = new Value<int>();
        var parameter = new Value<string>();
        var body = new OperationGraph([parameter], [], []);

        var exception = Assert.Throws<ArgumentException>(
            () => new Target(
                [],
                [external],
                body,
                [new Value<string>()],
                []));

        Assert.Contains("same type", exception.Message);
    }

    [Fact]
    public void RejectsTargetOutputBindingWithDifferentType()
    {
        var bodyOutput = new Value<string>();
        var body = new OperationGraph(
            [],
            [new TestOperation([], [bodyOutput])],
            [bodyOutput]);

        var exception = Assert.Throws<ArgumentException>(
            () => new Target(
                [],
                [],
                body,
                [new Value<int>()],
                []));

        Assert.Contains("same type", exception.Message);
    }

    [Fact]
    public void RejectsSharedBodyAndExternalBoundaryValue()
    {
        var bodyOutput = new Value<string>();
        var body = new OperationGraph(
            [],
            [new TestOperation([], [bodyOutput])],
            [bodyOutput]);

        var exception = Assert.Throws<ArgumentException>(
            () => new Target(
                [],
                [],
                body,
                [bodyOutput],
                []));

        Assert.Contains("distinct", exception.Message);
    }

    [Fact]
    public void RejectsCrossTargetValueNotExportedByProducer()
    {
        var value = new Value<string>();
        var producer = new TestOperation([], [value]);
        var consumer = new TestOperation([value], []);
        var producingTarget = new Target(
            new OperationGraph([producer]));
        var unexportedValue = value;
        var bodyInput = new Value<string>();
        var consumingOperation = new TestOperation([bodyInput], []);
        var consumingTarget = new Target(
            [],
            [unexportedValue],
            new OperationGraph([bodyInput], [consumingOperation], []),
            []);

        var exception = Assert.Throws<ArgumentException>(
            () => new BuildProgram([producingTarget, consumingTarget]));

        Assert.Contains("exported", exception.Message);
    }

    [Fact]
    public void RejectsCyclesAcrossDataAndOrderDependencies()
    {
        var value = new Value<string>();
        var producer = new TestOperation([], [value]);
        var first = new Target(new OperationGraph([producer]));
        var exportedValue = Assert.Single(first.Outputs);
        var bodyInput = new Value<string>();
        var consumer = new TestOperation([bodyInput], []);
        var second = new Target(
            [],
            [exportedValue],
            new OperationGraph([bodyInput], [consumer], []),
            []);

        var orderedFirst = new Target(
            [second],
            first.Inputs,
            first.Body,
            first.Outputs,
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
