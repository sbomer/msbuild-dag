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

        Assert.Same(concat.Result, compile.Sources);
        Assert.Same(compile.Assembly, result.Properties["AssemblyPath"]);
        Assert.Same(concat.Result, result.Items["Compile"]);
        Assert.Same(condition.Result, result.TargetConditions["Build"]);

        Assert.Contains(
            result.Graph.GetDependencies(compile),
            operation => ReferenceEquals(operation, concat));

        var configuration = Assert.Single(
            result.Graph.Operations
                .OfType<ConstantOperation<string>>(),
            operation => operation.Content == "Debug");
        Assert.Contains(
            result.Graph.GetDependencies(configuration),
            operation => operation is ConditionGateOperation);

        var itemConstants = result.Graph.Operations
            .OfType<ConstantOperation<IReadOnlyList<string>>>()
            .ToArray();

        Assert.Contains(
            itemConstants,
            operation => operation.Content.SequenceEqual(["Program.cs"]));
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
        var evaluator = new OperationEvaluator()
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
            .Add<ConditionGateOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        new OrderToken(values.Get(operation.Condition)));
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
                    compileExecuted = true;
                    values.Set(operation.Assembly, "App.dll");
                    return ValueTask.CompletedTask;
                });
        var values = new ValueStore();

        await new BuildGraphExecutor().ExecuteAsync(
            result.Graph,
            values,
            evaluator.EvaluateAsync);

        Assert.False(values.Get(result.TargetConditions["Build"]));
        Assert.False(compileExecuted);
    }
}
