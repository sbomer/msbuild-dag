using MSBuild.Dag.Core;
using MSBuild.Dag.Execution;

namespace MSBuild.Dag.MSBuild.Tests;

public sealed class MSBuildProjectTranslatorTests
{
    [Fact]
    public void TranslatesTargetPropertiesItemsAndTaskOutput()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ToyBuild.proj");

        var result = new MSBuildProjectTranslator().Translate(projectPath, "Build");

        var compile = Assert.Single(result.Graph.Operations.OfType<ToyCompileOperation>());
        var concat = Assert.Single(result.Graph.Operations.OfType<ConcatItemsOperation>());
        var condition = Assert.Single(
            result.Graph.Operations.OfType<EqualOperation<string>>());
        var guard = Assert.Single(
            result.Graph.Operations.OfType<ConditionGuardOperation>());

        Assert.Same(concat.Result, compile.Sources);
        Assert.Same(compile.Assembly, result.Properties["AssemblyPath"]);
        Assert.Same(concat.Result, result.Items["Compile"]);
        Assert.Same(condition.Result, result.TargetConditions["Build"]);
        Assert.Same(condition.Result, guard.Condition);

        Assert.Contains(
            result.Graph.GetDependencies(compile),
            operation => ReferenceEquals(operation, concat));

        var configuration = Assert.Single(
            result.Graph.Operations
                .OfType<ConstantOperation<string>>(),
            operation => operation.Content == "Debug");
        Assert.Contains(
            result.Graph.GetDependencies(configuration),
            operation => operation is ConditionGuardOperation);
        Assert.Same(guard.Result, configuration.Guard);
        Assert.Null(configuration.OrderInput);
        Assert.Same(configuration.OrderOutput, configuration.Outputs[0]);
        Assert.Same(configuration.Result, configuration.Outputs[1]);

        var appendedItems = Assert.Single(
            result.Graph.Operations
                .OfType<ConstantOperation<IReadOnlyList<string>>>(),
            operation => operation.Content.SequenceEqual(["Program.cs"]));

        Assert.Null(appendedItems.Guard);
        Assert.Same(configuration.OrderOutput, appendedItems.OrderInput);
        Assert.Same(
            appendedItems.OrderInput,
            appendedItems.Inputs[0]);
        Assert.Same(appendedItems.OrderOutput, appendedItems.Outputs[0]);
        Assert.Same(appendedItems.Result, appendedItems.Outputs[1]);

        Assert.NotNull(compile.OrderInput);
        Assert.Same(compile.OrderInput, compile.Inputs[0]);
        Assert.Same(compile.OrderOutput, compile.Outputs[0]);
        Assert.Same(compile.Assembly, compile.Outputs[1]);
    }

    [Fact]
    public void LoadsSdkStyleProjectBeforeReportingUnsupportedTargetDependencies()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "SdkStyle.csproj");

        var exception = Assert.Throws<NotSupportedException>(
            () => new MSBuildProjectTranslator().Translate(projectPath, "Build"));

        Assert.Contains("DependsOnTargets", exception.Message);
    }

    [Fact]
    public void TranslatesTargetConditionToBooleanDataflow()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "Condition.proj");

        var result = new MSBuildProjectTranslator().Translate(projectPath, "Build");

        var comparison = Assert.Single(
            result.Graph.Operations.OfType<NotEqualOperation<string>>());

        Assert.Same(comparison.Result, result.TargetConditions["Build"]);
        Assert.Equal(2, result.Graph.GetDependencies(comparison).Count);
    }

    [Fact]
    public async Task FalseTargetConditionPreventsTargetBodyExecution()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ConditionFalse.proj");
        var result = new MSBuildProjectTranslator().Translate(projectPath, "Build");
        var compileExecuted = false;
        var evaluator = CreateEvaluator(
            _ => compileExecuted = true);
        var values = new ValueStore();

        await new OperationGraphExecutor().ExecuteAsync(
            result.Graph,
            values,
            evaluator.EvaluateAsync);

        Assert.False(values.Get(result.TargetConditions["Build"]));
        Assert.False(compileExecuted);
    }

    [Fact(
        Skip = "Multi-target lowering and post-condition state merges are not yet implemented.")]
    public async Task SkippedTargetPreservesPriorPropertyForFollowingTarget()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ToyBuild.proj");
        var result = new MSBuildProjectTranslator()
            .Translate(projectPath, "AfterSkippedBuild");
        var values = new ValueStore();
        var evaluator = CreateEvaluator();

        await new OperationGraphExecutor().ExecuteAsync(
            result.Graph,
            values,
            evaluator.EvaluateAsync);

        Assert.Equal(
            "Release",
            values.Get(result.Properties["ObservedConfiguration"]));
    }

    private static OperationEvaluator CreateEvaluator(
        Action<ToyCompileOperation>? onCompile = null) =>
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
            .Add<ConditionGuardOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        new GuardToken(values.Get(operation.Condition)));
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
                (operation, values, _) =>
                {
                    onCompile?.Invoke(operation);
                    values.Set(operation.Assembly, "App.dll");
                    return ValueTask.CompletedTask;
                });
}
