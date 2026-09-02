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
        var prepare = result.Targets["Prepare"];
        var collectSources = result.Targets["CollectSources"];
        var build = result.Targets["Build"];
        var graph = build.Body;

        Assert.Equal(3, result.Graph.Targets.Count);
        Assert.Equal(2, result.Graph.GetDependencies(build).Count);
        Assert.Contains(prepare, result.Graph.GetDependencies(build));
        Assert.Contains(collectSources, result.Graph.GetDependencies(build));
        Assert.Empty(result.Graph.GetDependencies(prepare));
        Assert.Empty(result.Graph.GetDependencies(collectSources));

        var compile = Assert.Single(graph.Operations.OfType<ToyCompileOperation>());
        var configuration = Assert.Single(
            prepare.Body.Operations.OfType<ConstantOperation<string>>(),
            operation => operation.Content == "Debug");
        var concat = Assert.Single(
            collectSources.Body.Operations.OfType<ConcatItemsOperation>());

        Assert.Same(concat.Result, compile.Sources);
        Assert.Same(compile.Assembly, result.Properties["AssemblyPath"]);
        Assert.Same(concat.Result, result.Items["Compile"]);
        Assert.Same(configuration.Result, result.Properties["Configuration"]);
        Assert.Same(configuration.Result, compile.Configuration);
        Assert.Contains(configuration.Result, prepare.Outputs);
        Assert.Contains(configuration.Result, build.Inputs);
        Assert.Contains(concat.Result, collectSources.Outputs);
        Assert.Contains(concat.Result, build.Inputs);

        Assert.Empty(graph.GetDependencies(compile));
        Assert.Null(configuration.Guard);
        Assert.Null(configuration.OrderInput);
        Assert.Same(configuration.OrderOutput, configuration.Outputs[0]);
        Assert.Same(configuration.Result, configuration.Outputs[1]);

        var appendedItems = Assert.Single(
            collectSources.Body.Operations
                .OfType<ConstantOperation<IReadOnlyList<string>>>(),
            operation => operation.Content.SequenceEqual(["Generated.cs"]));

        Assert.Null(appendedItems.Guard);
        Assert.Null(appendedItems.OrderInput);
        Assert.Same(appendedItems.OrderOutput, appendedItems.Outputs[0]);
        Assert.Same(appendedItems.Result, appendedItems.Outputs[1]);

        Assert.Null(compile.OrderInput);
        Assert.Same(compile.OrderOutput, compile.Outputs[0]);
        Assert.Same(compile.Assembly, compile.Outputs[1]);
    }

    [Fact]
    public void RequestedTargetDoesNotRestrictStaticGraphLowering()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "ToyBuild.proj");

        var result = new MSBuildProjectTranslator().Translate(
            projectPath,
            "Prepare");

        Assert.Equal(3, result.Graph.Targets.Count);
        Assert.Equal(
            ["Prepare", "CollectSources", "Build"],
            result.Targets.Keys);
    }

    [Fact]
    public void LoadsSdkStyleProjectBeforeReportingUnsupportedConstruct()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "SdkStyle.csproj");

        var exception = Assert.Throws<NotSupportedException>(
            () => new MSBuildProjectTranslator().Translate(projectPath, "Build"));

        Assert.Contains("restricted MSBuild translator", exception.Message);
    }

    [Fact]
    public void RejectsNonLiteralTargetDependencies()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "DynamicDepends.proj");

        var exception = Assert.Throws<NotSupportedException>(
            () => new MSBuildProjectTranslator().Translate(projectPath, "Build"));

        Assert.Contains("non-literal DependsOnTargets", exception.Message);
    }

    [Fact]
    public void TranslatesTargetConditionToBooleanDataflow()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "Condition.proj");

        var result = new MSBuildProjectTranslator().Translate(projectPath, "Build");
        var graph = result.Targets["Build"].Body;

        var comparison = Assert.Single(
            graph.Operations.OfType<NotEqualOperation<string>>());

        Assert.Same(comparison.Result, result.TargetConditions["Build"]);
        Assert.Equal(2, graph.GetDependencies(comparison).Count);
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
            result.Targets["Build"].Body,
            values,
            evaluator.EvaluateAsync);

        Assert.False(values.Get(result.TargetConditions["Build"]));
        Assert.False(compileExecuted);
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
