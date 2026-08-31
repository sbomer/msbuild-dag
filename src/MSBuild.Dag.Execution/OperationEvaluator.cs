using MSBuild.Dag.Core;

namespace MSBuild.Dag.Execution;

public sealed class OperationEvaluator
{
    private readonly Dictionary<
        Type,
        Func<Operation, ValueStore, CancellationToken, ValueTask>> _evaluators = [];

    public OperationEvaluator Add<TOperation>(
        Func<TOperation, ValueStore, CancellationToken, ValueTask> evaluate)
        where TOperation : Operation
    {
        ArgumentNullException.ThrowIfNull(evaluate);

        if (!_evaluators.TryAdd(
                typeof(TOperation),
                (operation, values, cancellationToken) =>
                    evaluate((TOperation)operation, values, cancellationToken)))
        {
            throw new InvalidOperationException(
                $"An evaluator for {typeof(TOperation).Name} is already registered.");
        }

        return this;
    }

    public ValueTask EvaluateAsync(
        Operation operation,
        ValueStore values,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(values);

        if (!_evaluators.TryGetValue(operation.GetType(), out var evaluate))
        {
            throw new NotSupportedException(
                $"No evaluator exists for {operation.GetType().Name}.");
        }

        return evaluate(operation, values, cancellationToken);
    }
}
