using MSBuild.Dag.Core;
using MSBuild.Dag.Execution;
using MSBuild.Dag.MSBuild;

namespace MSBuild.Dag.Cli;

internal static class TranslatedOperationEvaluator
{
    private static readonly OperationEvaluator s_evaluator =
        new OperationEvaluator()
            .Add<ConstantOperation<string>>(
                static (operation, values, _) =>
                {
                    values.Set(operation.Result, operation.Content);
                    return ValueTask.CompletedTask;
                })
            .Add<ConstantOperation<IReadOnlyList<string>>>(
                static (operation, values, _) =>
                {
                    values.Set(operation.Result, operation.Content);
                    return ValueTask.CompletedTask;
                })
            .Add<ConcatItemsOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.ExistingItems)
                            .Concat(values.Get(operation.AppendedItems))
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<ToyCompileOperation>(
                static (operation, values, _) =>
                {
                    var configuration = values.Get(operation.Configuration);
                    values.Set(
                        operation.Assembly,
                        $"bin/{configuration}/App.dll");
                    return ValueTask.CompletedTask;
                })
            .Add<EqualOperation<string>>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        StringComparer.OrdinalIgnoreCase.Equals(
                            values.Get(operation.Left),
                            values.Get(operation.Right)));
                    return ValueTask.CompletedTask;
                })
            .Add<NotEqualOperation<string>>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        !StringComparer.OrdinalIgnoreCase.Equals(
                            values.Get(operation.Left),
                            values.Get(operation.Right)));
                    return ValueTask.CompletedTask;
                })
            .Add<NotOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        !values.Get(operation.Operand));
                    return ValueTask.CompletedTask;
                })
            .Add<ConditionGateOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        new OrderToken(values.Get(operation.Condition)));
                    return ValueTask.CompletedTask;
                });

    public static ValueTask EvaluateAsync(
        Operation operation,
        ValueStore values,
        CancellationToken cancellationToken) =>
        s_evaluator.EvaluateAsync(operation, values, cancellationToken);
}
