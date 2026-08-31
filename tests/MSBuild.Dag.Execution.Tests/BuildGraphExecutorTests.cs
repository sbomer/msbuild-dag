using MSBuild.Dag.Core;

namespace MSBuild.Dag.Execution.Tests;

public sealed class BuildGraphExecutorTests
{
    [Fact]
    public async Task ExecutesDependenciesBeforeConsumers()
    {
        var source = new Value<int>();
        var addOne = new UnaryOperation(source);
        var addAnother = new UnaryOperation(addOne.Result);
        var graph = new BuildGraph([addAnother, addOne]);
        var values = new ValueStore();
        values.Set(source, 40);

        await new BuildGraphExecutor().ExecuteAsync(
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
        var graph = new BuildGraph([operation]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new BuildGraphExecutor().ExecuteAsync(
                graph,
                new ValueStore(),
                static (_, _, _) => ValueTask.CompletedTask));

        Assert.Contains("input", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsUnassignedOperationOutput()
    {
        var operation = new SourceOperation();
        var graph = new BuildGraph([operation]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new BuildGraphExecutor().ExecuteAsync(
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
}
