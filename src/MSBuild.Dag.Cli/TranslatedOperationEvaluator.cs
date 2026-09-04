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
            .Add<ConstantOperation<IReadOnlyList<MSBuildItem>>>(
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
                        .Select(static item => item.Identity)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    values.Set(
                        operation.Result,
                        values.Get(operation.IncludedItems)
                            .Where(item =>
                                !excludedItems.Contains(item.Identity))
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<UpdateItemMetadataOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Items)
                            .Select(item => item.WithMetadata(
                                operation.Metadata.ToDictionary(
                                    static pair => pair.Key,
                                    pair => pair.Value.Replace(
                                        "%(Identity)",
                                        item.Identity,
                                        StringComparison.OrdinalIgnoreCase),
                                    StringComparer.OrdinalIgnoreCase)))
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<GetItemMetadataOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Items)
                            .Select(item =>
                                item.GetMetadataValue(operation.MetadataName))
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<SetItemMetadataOperation>(
                static (operation, values, _) =>
                {
                    var items = values.Get(operation.Items);
                    var metadataValues = values.Get(operation.MetadataValues);
                    var mask = operation.Mask is null
                        ? null
                        : values.Get(operation.Mask);
                    EnsureMatchingItemCounts(
                        items.Count,
                        metadataValues.Count,
                        mask?.Count);
                    values.Set(
                        operation.Result,
                        items.Select(
                                (item, index) =>
                                    mask is null || mask[index]
                                        ? item.WithMetadata(
                                            new Dictionary<string, string>(
                                                StringComparer.OrdinalIgnoreCase)
                                            {
                                                [operation.MetadataName] =
                                                    metadataValues[index],
                                            })
                                        : item)
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<BroadcastItemValueOperation<string>>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        Enumerable.Repeat(
                                values.Get(operation.Value),
                                values.Get(operation.Items).Count)
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<ConcatItemValuesOperation>(
                static (operation, values, _) =>
                {
                    var left = values.Get(operation.Left);
                    var right = values.Get(operation.Right);
                    EnsureMatchingItemCounts(left.Count, right.Count);
                    values.Set(
                        operation.Result,
                        left.Zip(
                                right,
                                static (leftValue, rightValue) =>
                                    leftValue + rightValue)
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<EqualItemValuesOperation<string>>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Values)
                            .Select(value =>
                                StringComparer.OrdinalIgnoreCase.Equals(
                                    value,
                                    values.Get(operation.Candidate)))
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<NotItemValuesOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Values)
                            .Select(static value => !value)
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<ProjectItemIdentitiesOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Items)
                            .Select(static item => item.Identity)
                            .ToArray());
                    return ValueTask.CompletedTask;
                })
            .Add<ContainsOperation<string>>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Values).Contains(
                            values.Get(operation.Candidate),
                            StringComparer.OrdinalIgnoreCase));
                    return ValueTask.CompletedTask;
                })
            .Add<IsEmptyOperation<MSBuildItem>>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Values).Count == 0);
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
                    IReadOnlyList<MSBuildItem> items = Microsoft.Build.Evaluation
                        .ProjectCollection
                        .Unescape(expanded)
                        .Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                        .Select(static identity => new MSBuildItem(identity))
                        .ToArray();
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

    private static void EnsureMatchingItemCounts(
        int expected,
        params int?[] actualCounts)
    {
        if (actualCounts.Any(count => count is not null && count != expected))
        {
            throw new InvalidOperationException(
                "Item-aligned values must have matching element counts.");
        }
    }

    public static ValueTask EvaluateAsync(
        Operation operation,
        ValueStore values,
        CancellationToken cancellationToken) =>
        s_evaluator.EvaluateAsync(operation, values, cancellationToken);
}
