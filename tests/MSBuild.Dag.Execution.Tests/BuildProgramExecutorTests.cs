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
