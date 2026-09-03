using MSBuild.Dag.Core;

namespace MSBuild.Dag.Execution.Tests;

public sealed class BuildProgramExecutorTests
{
    [Fact]
    public async Task ExecutesTargetPreludeBodyAndEpilogueInOrderOnce()
    {
        var executionOrder = new List<string>();
        var shared = CreateTarget("Shared");
        var first = new Target(
            [shared],
            [],
            [],
            Body("First"),
            []);
        var second = new Target(
            [shared],
            [],
            [],
            Body("Second"),
            []);
        var after = CreateTarget("After");
        var requested = new Target(
            [first, second],
            [],
            [],
            Body("Requested"),
            [after]);
        var program = new BuildProgram(
            [shared, first, second, requested, after]);
        var executor = new BuildProgramExecutor(
            program,
            new ValueStore(),
            (operation, _, _) =>
            {
                executionOrder.Add(((RecordingOperation)operation).Name);
                return ValueTask.CompletedTask;
            });

        await executor.ExecuteAsync(requested);

        Assert.Equal(
            ["Shared", "First", "Second", "Requested", "After"],
            executionOrder);

        Target CreateTarget(string name) =>
            new([], [], Body(name));

        static OperationGraph Body(string name) =>
            new([new RecordingOperation(name)]);
    }

    [Fact]
    public async Task RejectsAfterTargetRequestedBeforeAnchor()
    {
        var after = new Target(
            [],
            [],
            new OperationGraph([new RecordingOperation("After")]));
        var anchor = new Target(
            [],
            [],
            [],
            new OperationGraph([new RecordingOperation("Anchor")]),
            [after]);
        var program = new BuildProgram([anchor, after]);
        var executor = new BuildProgramExecutor(
            program,
            new ValueStore(),
            static (_, _, _) => ValueTask.CompletedTask);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await executor.ExecuteAsync(after));

        Assert.Contains("program order", exception.Message);
    }

    [Fact]
    public async Task AcceptsRequestAfterGlobalPredecessorCompletes()
    {
        var executionOrder = new List<string>();
        var a = CreateTarget("A");
        var b = CreateTarget("B");
        var build = new Target(
            [a, b],
            [],
            [],
            CreateBody("Build"),
            []);
        var bFirst = new Target(
            [b],
            [],
            [],
            CreateBody("BFirst"),
            []);
        var program = new BuildProgram([a, b, build, bFirst]);
        var executor = new BuildProgramExecutor(
            program,
            new ValueStore(),
            (operation, _, _) =>
            {
                executionOrder.Add(((RecordingOperation)operation).Name);
                return ValueTask.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await executor.ExecuteAsync(bFirst));

        Assert.Contains("program order", exception.Message);

        await executor.ExecuteAsync(a);
        await executor.ExecuteAsync(bFirst);

        Assert.Equal(["A", "B", "BFirst"], executionOrder);

        static Target CreateTarget(string name) =>
            new([], [], CreateBody(name));

        static OperationGraph CreateBody(string name) =>
            new([new RecordingOperation(name)]);
    }

    [Fact]
    public async Task ExecutesDependenciesBeforeConsumers()
    {
        var source = new Value<int>();
        var addOne = new UnaryOperation(source);
        var addAnother = new UnaryOperation(addOne.Result);
        var graph = new OperationGraph([addAnother, addOne]);
        var values = new ValueStore();
        values.Set(source, 40);

        await new OperationGraphExecutor().ExecuteAsync(
            graph,
            values,
            static (operation, store, _) =>
            {
                var unary = (UnaryOperation)operation;
                store.Set(unary.Result, store.Get(unary.Input) + 1);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(42, values.Get(addAnother.Result));
    }

    [Fact]
    public async Task RejectsMissingExternalInput()
    {
        var operation = new UnaryOperation(new Value<int>());
        var graph = new OperationGraph([operation]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new OperationGraphExecutor().ExecuteAsync(
                graph,
                new ValueStore(),
                static (_, _, _) => ValueTask.CompletedTask));

        Assert.Contains("input", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsUnassignedOperationOutput()
    {
        var operation = new SourceOperation();
        var graph = new OperationGraph([operation]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new OperationGraphExecutor().ExecuteAsync(
                graph,
                new ValueStore(),
                static (_, _, _) => ValueTask.CompletedTask));

        Assert.Contains("output", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true, "new")]
    [InlineData(false, "previous")]
    public async Task ExecutesSelectOperation(
        bool conditionContent,
        string expected)
    {
        var condition = new Value<bool>();
        var whenTrue = new Value<string>();
        var whenFalse = new Value<string>();
        var select = new SelectOperation<string>(
            condition,
            whenTrue,
            whenFalse);
        var values = new ValueStore();
        values.Set(condition, conditionContent);
        values.Set(whenTrue, "new");
        values.Set(whenFalse, "previous");

        await new OperationGraphExecutor().ExecuteAsync(
            new OperationGraph([select]),
            values,
            static (_, _, _) =>
                ValueTask.FromException(
                    new InvalidOperationException(
                        "Select should execute intrinsically.")));

        Assert.Equal(expected, values.Get(select.Result));
    }

    [Theory]
    [InlineData(true, 11, 20, "then")]
    [InlineData(false, 10, 120, "else")]
    public async Task ConditionalRegionExecutesOnlySelectedBranch(
        bool conditionContent,
        int expectedX,
        int expectedY,
        string expectedBranch)
    {
        var condition = new Value<bool>();
        var x = new Value<int>();
        var y = new Value<int>();
        var thenX = new Value<int>();
        var thenY = new Value<int>();
        var elseX = new Value<int>();
        var elseY = new Value<int>();
        var updateX = new AddOperation(thenX, 1, "then");
        var updateY = new AddOperation(elseY, 100, "else");
        var xAfter = new Value<int>();
        var yAfter = new Value<int>();
        var conditional = new ConditionalRegionOperation(
            condition,
            [x, y],
            new OperationGraph(
                [thenX, thenY],
                [updateX],
                [updateX.Result, thenY]),
            new OperationGraph(
                [elseX, elseY],
                [updateY],
                [elseX, updateY.Result]),
            [xAfter, yAfter]);
        var values = new ValueStore();
        values.Set(condition, conditionContent);
        values.Set(x, 10);
        values.Set(y, 20);
        var executed = new List<string>();

        await new OperationGraphExecutor().ExecuteAsync(
            new OperationGraph(
                [condition, x, y],
                [conditional],
                [xAfter, yAfter]),
            values,
            (operation, store, _) =>
            {
                var add = (AddOperation)operation;
                executed.Add(add.Name);
                store.Set(
                    add.Result,
                    store.Get(add.Input) + add.Increment);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(expectedX, values.Get(xAfter));
        Assert.Equal(expectedY, values.Get(yAfter));
        Assert.Equal([expectedBranch], executed);
        Assert.False(
            values.Contains(
                conditionContent
                    ? elseY
                    : thenX));
    }

    [Fact]
    public async Task ConditionalRegionsCanNest()
    {
        var outerCondition = new Value<bool>();
        var innerCondition = new Value<bool>();
        var input = new Value<int>();
        var outerInnerCondition = new Value<bool>();
        var outerInput = new Value<int>();
        var innerTrueCondition = new Value<bool>();
        var innerTrueInput = new Value<int>();
        var innerFalseCondition = new Value<bool>();
        var innerFalseInput = new Value<int>();
        var innerTrue = new AddOperation(innerTrueInput, 1, "inner true");
        var innerFalse = new AddOperation(innerFalseInput, 10, "inner false");
        var innerResult = new Value<int>();
        var inner = new ConditionalRegionOperation(
            outerInnerCondition,
            [outerInput],
            new OperationGraph(
                [innerTrueInput],
                [innerTrue],
                [innerTrue.Result]),
            new OperationGraph(
                [innerFalseInput],
                [innerFalse],
                [innerFalse.Result]),
            [innerResult]);
        var outerFalseCondition = new Value<bool>();
        var outerFalseInput = new Value<int>();
        var outerFalse = new AddOperation(outerFalseInput, 100, "outer false");
        var result = new Value<int>();
        var outer = new ConditionalRegionOperation(
            outerCondition,
            [innerCondition, input],
            new OperationGraph(
                [outerInnerCondition, outerInput],
                [inner],
                [innerResult]),
            new OperationGraph(
                [outerFalseCondition, outerFalseInput],
                [outerFalse],
                [outerFalse.Result]),
            [result]);
        var values = new ValueStore();
        values.Set(outerCondition, true);
        values.Set(innerCondition, false);
        values.Set(input, 5);
        var executed = new List<string>();

        await new OperationGraphExecutor().ExecuteAsync(
            new OperationGraph(
                [outerCondition, innerCondition, input],
                [outer],
                [result]),
            values,
            (operation, store, _) =>
            {
                var add = (AddOperation)operation;
                executed.Add(add.Name);
                store.Set(
                    add.Result,
                    store.Get(add.Input) + add.Increment);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(15, values.Get(result));
        Assert.Equal(["inner false"], executed);
    }

    [Fact]
    public async Task EvaluatorDispatchesByOperationType()
    {
        var operation = new SourceOperation();
        var values = new ValueStore();
        var evaluator = new OperationEvaluator()
            .Add<SourceOperation>(
                static (source, store, _) =>
                {
                    store.Set(source.Result, 42);
                    return ValueTask.CompletedTask;
                });

        await evaluator.EvaluateAsync(operation, values, default);

        Assert.Equal(42, values.Get(operation.Result));
    }

    [Fact]
    public async Task EvaluatorRejectsUnknownOperationType()
    {
        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            async () => await new OperationEvaluator().EvaluateAsync(
                new SourceOperation(),
                new ValueStore(),
                default));

        Assert.Contains("No evaluator", exception.Message);
    }

    [Fact]
    public async Task ExecutesStateReadBoundToEvaluationSnapshot()
    {
        var location = new StateLocation<int>();
        var read = new StateRead<int>(location);
        var operation = new UnaryOperation(read.Value);
        var definition = new TargetDefinition(
            [],
            [read],
            [],
            [operation.Result],
            new OperationGraph([operation]),
            []);
        var linked = new BuildDefinition(
            new EvaluationSnapshot(
            [
                new StateInitialization<int>(location, 41),
            ]),
            [definition])
            .Link();
        var values = new ValueStore();
        var executor = new BuildProgramExecutor(
            linked.Program,
            values,
            static (candidate, store, _) =>
            {
                var unary = (UnaryOperation)candidate;
                store.Set(unary.Result, store.Get(unary.Input) + 1);
                return ValueTask.CompletedTask;
            });

        await executor.ExecuteAsync(linked.Targets[definition]);

        Assert.Equal(42, values.Get(operation.Result));
    }

    [Fact]
    public async Task BindsTargetInputsAndOutputsSymmetrically()
    {
        var externalInput = new Value<int>();
        var bodyInput = new Value<int>();
        var operation = new UnaryOperation(bodyInput);
        var externalOutput = new Value<int>();
        var target = new Target(
            [],
            [externalInput],
            new OperationGraph(
                [bodyInput],
                [operation],
                [operation.Result]),
            [externalOutput],
            []);
        var program = new BuildProgram(
            [target],
            [new InitialValue<int>(externalInput, 41)]);
        var values = new ValueStore();

        await new BuildProgramExecutor(
            program,
            values,
            static (candidate, store, _) =>
            {
                var unary = (UnaryOperation)candidate;
                store.Set(unary.Result, store.Get(unary.Input) + 1);
                return ValueTask.CompletedTask;
            })
            .ExecuteAsync(target);

        Assert.Equal(41, values.Get(bodyInput));
        Assert.Equal(42, values.Get(operation.Result));
        Assert.Equal(42, values.Get(externalOutput));
    }

    [Fact]
    public async Task InactiveGuardSkipsOperation()
    {
        var guard = new Value<GuardToken>();
        var operation = new GuardedSourceOperation(guard);
        var graph = new OperationGraph([operation]);
        var values = new ValueStore();
        values.Set(guard, new GuardToken(IsActive: false));
        var executed = false;

        await new OperationGraphExecutor().ExecuteAsync(
            graph,
            values,
            (_, _, _) =>
            {
                executed = true;
                return ValueTask.CompletedTask;
            });

        Assert.False(executed);
        Assert.True(values.Contains(operation.Result));
        Assert.False(values.IsAvailable(operation.Result));
    }

    [Fact]
    public async Task FirstOrderedOperationSynthesizesOrderToken()
    {
        var operation = new OrderedSourceOperation();
        var graph = new OperationGraph([operation]);
        var values = new ValueStore();

        await new OperationGraphExecutor().ExecuteAsync(
            graph,
            values,
            static (candidate, store, _) =>
            {
                var source = (OrderedSourceOperation)candidate;
                store.Set(source.Result, 42);
                return ValueTask.CompletedTask;
            });

        Assert.Equal(42, values.Get(operation.Result));
        Assert.Equal(new OrderToken(), values.Get(operation.OrderOutput!));
    }

    private sealed class SourceOperation : Operation
    {
        public Value<int> Result { get; } = new();

        public override IReadOnlyList<Value> Inputs => [];

        public override IReadOnlyList<Value> Outputs => [Result];
    }

    private sealed class UnaryOperation(Value<int> input) : Operation
    {
        public Value<int> Input { get; } = input;

        public Value<int> Result { get; } = new();

        public override IReadOnlyList<Value> Inputs => [Input];

        public override IReadOnlyList<Value> Outputs => [Result];
    }

    private sealed class AddOperation(
        Value<int> input,
        int increment,
        string name) : Operation
    {
        public Value<int> Input { get; } = input;

        public int Increment { get; } = increment;

        public string Name { get; } = name;

        public Value<int> Result { get; } = new();

        public override IReadOnlyList<Value> Inputs => [Input];

        public override IReadOnlyList<Value> Outputs => [Result];
    }

    private sealed class GuardedSourceOperation : Operation, IGuardedOperation
    {
        public GuardedSourceOperation(Value<GuardToken> guard)
        {
            Guard = guard;
        }

        public Value<int> Result { get; } = new();

        public Value<GuardToken>? Guard { get; }

        public override IReadOnlyList<Value> Inputs => [Guard!];

        public override IReadOnlyList<Value> Outputs => [Result];
    }

    private sealed class OrderedSourceOperation : Operation, IOrderedOperation
    {
        public Value<int> Result { get; } = new();

        public Value<OrderToken>? OrderInput => null;

        public Value<OrderToken>? OrderOutput { get; } = new();

        public override IReadOnlyList<Value> Inputs => [];

        public override IReadOnlyList<Value> Outputs =>
            [Result, OrderOutput!];
    }

    private sealed class RecordingOperation(string name) : Operation
    {
        public string Name { get; } = name;

        public override IReadOnlyList<Value> Inputs => [];

        public override IReadOnlyList<Value> Outputs => [];
    }
}
