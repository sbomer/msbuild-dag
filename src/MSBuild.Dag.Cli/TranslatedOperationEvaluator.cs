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
            .Add<ReplaceOperation<string>>(
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
            .Add<ExcludeItemsOperation>(
                static (operation, values, _) =>
                {
                    var excludedItems = values.Get(operation.ExcludedItems)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    values.Set(
                        operation.Result,
                        values.Get(operation.IncludedItems)
                            .Where(item => !excludedItems.Contains(item))
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<ExpandItemsExpressionOperation>(
                static (operation, values, _) =>
                {
                    var expanded = operation.Replacements.Aggregate(
                        values.Get(operation.Source),
                        static (current, replacement) =>
                            current.Replace(
                                replacement.OldValue,
                                replacement.NewValue,
                                StringComparison.Ordinal));
                    IReadOnlyList<string> items = Microsoft.Build.Evaluation
                        .ProjectCollection
                        .Unescape(expanded)
                        .Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries);
                    values.Set(operation.Result, items);
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
            .Add<MessageOperation>(
                static (operation, values, _) =>
                {
                    Console.WriteLine(values.Get(operation.Text));
                    return ValueTask.CompletedTask;
                })
            .Add<MissingTargetOperation>(
                static (operation, _, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            $"Target '{operation.DeclaringTargetName}' " +
                            $"references missing target " +
                            $"'{operation.MissingTargetName}' through " +
                            $"{operation.AttributeName}.")))
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
            .Add<ConditionGuardOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        new GuardToken(values.Get(operation.Condition)));
                    return ValueTask.CompletedTask;
                });

    public static ValueTask EvaluateAsync(
        Operation operation,
        ValueStore values,
        CancellationToken cancellationToken) =>
        s_evaluator.EvaluateAsync(operation, values, cancellationToken);
}
