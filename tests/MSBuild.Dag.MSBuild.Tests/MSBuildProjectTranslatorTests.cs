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
        var afterBuild = result.Targets["AfterBuild"];
        var graph = build.Body;

        Assert.Equal(4, result.Program.Targets.Count);
        Assert.Equal([prepare, collectSources], build.Prelude);
        Assert.Equal([afterBuild], build.Epilogue);
        Assert.Empty(afterBuild.Prelude);
        Assert.Empty(afterBuild.Epilogue);
        Assert.Equal(2, result.Program.GetPredecessors(build).Count);
        Assert.Contains(prepare, result.Program.GetPredecessors(build));
        Assert.Contains(collectSources, result.Program.GetPredecessors(build));
        Assert.Empty(result.Program.GetPredecessors(prepare));
        Assert.Equal(
            [prepare],
            result.Program.GetPredecessors(collectSources));
        Assert.Equal(
            [build],
            result.Program.GetPredecessors(afterBuild));

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
        Assert.Contains(
            afterBuild.Body.Operations.OfType<ConstantOperation<string>>(),
            operation => operation.Content == "true" &&
                ReferenceEquals(
                    operation.Result,
                    result.Properties["AfterBuildRan"]));
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

        Assert.Equal(4, result.Program.Targets.Count);
        Assert.Equal(
            ["Prepare", "CollectSources", "Build", "AfterBuild"],
            result.Targets.Keys);
    }

    [Fact]
    public void LinksDependsBeforeAndAfterTargetsInStableOrder()
    {
        var result = TranslateAsset("Orchestration.proj", "Build");
        var build = result.Targets["Build"];

        Assert.Equal(
            [
                result.Targets["DependencyOne"],
                result.Targets["DependencyTwo"],
                result.Targets["BeforeOne"],
                result.Targets["BeforeTwo"],
            ],
            build.Prelude);
        Assert.Equal(
            [
                result.Targets["AfterOne"],
                result.Targets["AfterTwo"],
            ],
            build.Epilogue);
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

    [Theory]
    [InlineData("DynamicBefore.proj", "BeforeTargets")]
    [InlineData("DynamicAfter.proj", "AfterTargets")]
    public void RejectsNonLiteralTargetRegistrations(
        string assetName,
        string attributeName)
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => TranslateAsset(assetName, "Build"));

        Assert.Contains($"non-literal {attributeName}", exception.Message);
    }

    [Theory]
    [InlineData("MissingTarget.proj")]
    [InlineData("MissingBeforeTarget.proj")]
    [InlineData("MissingAfterTarget.proj")]
    public void RejectsMissingNamedTarget(string assetName)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TranslateAsset(assetName, "Build"));

        Assert.Contains("missing target 'Missing'", exception.Message);
    }

    [Fact]
    public void RejectsOrchestrationCycles()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TranslateAsset("OrchestrationCycle.proj", "First"));

        Assert.Contains("orchestration", exception.Message);
        Assert.Contains("acyclic", exception.Message);
    }

    [Fact]
    public void RejectsConflictingGlobalOrderFromAfterTargets()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TranslateAsset("ConflictingAfterOrder.proj", "Build"));

        Assert.Contains("acyclic", exception.Message);
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

    private static TranslationResult TranslateAsset(
        string assetName,
        string targetName)
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            assetName);

        return new MSBuildProjectTranslator().Translate(projectPath, targetName);
    }
}
