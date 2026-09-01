using MSBuild.Dag.Core;

namespace MSBuild.Dag.Execution.Tests;

public sealed class OperationGraphExecutorTests
{
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
}
