using MSBuild.Dag.Core;
using MSBuild.Dag.Execution;
using MSBuild.Dag.MSBuild;
using System.Runtime.ExceptionServices;

namespace MSBuild.Dag.Cli;

internal sealed class TranslatedOperationEvaluator
{
    private readonly Dictionary<BuildProgram, ChildExecutionContext>
        _childExecutions = new(ReferenceEqualityComparer.Instance);
    private readonly OperationEvaluator _evaluator;

    public TranslatedOperationEvaluator()
    {
        _evaluator = new OperationEvaluator()
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
            .Add<ConstantOperation<OrderToken>>(
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
            .Add<JoinItemValuesOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        string.Join(
                            values.Get(operation.Separator),
                            values.Get(operation.Values)));
                    return ValueTask.CompletedTask;
                })
            .Add<ConcatStringsOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Left) +
                        values.Get(operation.Right));
                    return ValueTask.CompletedTask;
                })
            .Add<ValueOrDefaultOperation>(
                static (operation, values, _) =>
                {
                    var value = values.Get(operation.Value);
                    values.Set(
                        operation.Result,
                        value.Length == 0
                            ? values.Get(operation.DefaultValue)
                            : value);
                    return ValueTask.CompletedTask;
                })
            .Add<UnsupportedPropertyFunctionOperation>(
                static (operation, _, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            $"Unsupported property function " +
                            $"'{operation.FunctionName}' was evaluated.")))
            .Add<UnsupportedTargetOperation>(
                static (operation, _, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            $"Target '{operation.TargetName}' cannot execute: " +
                            operation.Reason)))
            .Add<FileExistsOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Contents) is not null);
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
            .Add<ErrorOperation>(
                static (operation, values, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            values.Get(operation.Text))))
            .Add<MSBuildInvocationOperation>(
                ExecuteMSBuildInvocationAsync)
            .Add<UnsupportedTaskOperation>(
                static (operation, _, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            $"Task '{operation.TaskName}' cannot execute: " +
                            operation.Reason)))
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
            .Add<AndOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Left) &&
                        values.Get(operation.Right));
                    return ValueTask.CompletedTask;
                })
            .Add<OrOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        values.Get(operation.Left) ||
                        values.Get(operation.Right));
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
    }

    private async ValueTask ExecuteMSBuildInvocationAsync(
        MSBuildInvocationOperation operation,
        ValueStore parentValues,
        CancellationToken cancellationToken)
    {
        if (!_childExecutions.TryGetValue(
            operation.Program,
            out var execution))
        {
            execution = new ChildExecutionContext(
                operation.Program,
                EvaluateAsync);
            _childExecutions.Add(operation.Program, execution);
        }

        await execution.Gate.WaitAsync(cancellationToken);

        try
        {
            execution.Failure?.Throw();

            if (!execution.InputsInitialized)
            {
                foreach (var binding in operation.FileInputBindings)
                {
                    execution.Values.Set(
                        binding.Destination,
                        parentValues.Get(binding.Source));
                }

                foreach (var binding in operation.StringInputBindings)
                {
                    execution.Values.Set(
                        binding.Destination,
                        parentValues.Get(binding.Source));
                }

                execution.InputsInitialized = true;
            }

            var targetNames = parentValues.Get(operation.RequestedTargets)
                .Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries);

            if (targetNames.Length == 0)
            {
                throw new InvalidOperationException(
                    $"MSBuild invocation of '{operation.ProjectPath}' " +
                    "requested no targets.");
            }

            try
            {
                foreach (var targetName in targetNames)
                {
                    if (!operation.Targets.TryGetValue(
                        targetName,
                        out var target))
                    {
                        throw new InvalidOperationException(
                            $"Target '{targetName}' does not exist in invoked " +
                            $"project '{operation.ProjectPath}'.");
                    }

                    await execution.Executor.ExecuteAsync(
                        target,
                        cancellationToken);
                }
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException)
            {
                execution.Failure = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }
        finally
        {
            execution.Gate.Release();
        }
    }

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

    public ValueTask EvaluateAsync(
        Operation operation,
        ValueStore values,
        CancellationToken cancellationToken) =>
        _evaluator.EvaluateAsync(operation, values, cancellationToken);

    private sealed class ChildExecutionContext
    {
        public ChildExecutionContext(
            BuildProgram program,
            Func<Operation, ValueStore, CancellationToken, ValueTask>
                evaluateOperation)
        {
            Executor = new BuildProgramExecutor(
                program,
                Values,
                evaluateOperation);
        }

        public ValueStore Values { get; } = new();

        public BuildProgramExecutor Executor { get; }

        public SemaphoreSlim Gate { get; } = new(1, 1);

        public bool InputsInitialized { get; set; }

        public ExceptionDispatchInfo? Failure { get; set; }
    }
}
