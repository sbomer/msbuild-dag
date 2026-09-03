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

        Assert.Equal(4, result.Definition.Targets.Count);
        Assert.Equal(4, result.Program.Targets.Count);
        Assert.Equal(4, result.TargetDefinitions.Count);
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
        var sourcesBinding = Assert.IsAssignableFrom<IStateBindingOperation>(
            graph.GetProducer(compile.Sources));
        var configurationBinding =
            Assert.IsAssignableFrom<IStateBindingOperation>(
                graph.GetProducer(compile.Configuration));

        Assert.Same(concat.Result, sourcesBinding.Source);
        Assert.Same(compile.Assembly, result.Properties["AssemblyPath"]);
        Assert.Same(concat.Result, result.Items["Compile"]);
        Assert.Same(configuration.Result, result.Properties["Configuration"]);
        Assert.Contains(
            afterBuild.Body.Operations.OfType<ConstantOperation<string>>(),
            operation => operation.Content == "true" &&
                ReferenceEquals(
                    operation.Result,
                    result.Properties["AfterBuildRan"]));
        Assert.Same(configuration.Result, configurationBinding.Source);
        Assert.Contains(configuration.Result, prepare.Outputs);
        Assert.Contains(configuration.Result, build.Inputs);
        Assert.Contains(concat.Result, collectSources.Outputs);
        Assert.Contains(concat.Result, build.Inputs);

        Assert.Equal(2, graph.GetDependencies(compile).Count);
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
    public void RejectsSdkStyleProjectWithGlobalTargetOrderCycle()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "SdkStyle.csproj");

        var exception = Assert.Throws<InvalidOperationException>(
            () => new MSBuildProjectTranslator().Translate(projectPath, "Build"));

        Assert.Contains("Target orchestration must be acyclic", exception.Message);
        Assert.Contains(
            "'Build' -> '_PackAsBuildAfterTarget' -> 'Pack' -> " +
            "'GenerateNuspec' -> 'Build'",
            exception.Message);
    }

    [Fact]
    public void ExpandsTargetDependenciesFromEvaluatedProperties()
    {
        var result = TranslateAsset("DynamicDepends.proj", "Build");
        var build = result.Targets["Build"];

        Assert.Equal(
            [
                result.Targets["Prepare"],
                result.Targets["CollectSources"],
            ],
            build.Prelude);
    }

    [Fact]
    public void LinksPropertyStateIndependentlyOfTargetDeclarationOrder()
    {
        var result = TranslateAsset("OutOfOrderState.proj", "Build");
        var prepare = result.Targets["Prepare"];
        var build = result.Targets["Build"];
        var configuration = Assert.Single(
            prepare.Body.Operations.OfType<ConstantOperation<string>>(),
            operation => operation.Content == "Debug");
        var compile = Assert.Single(
            build.Body.Operations.OfType<ToyCompileOperation>());
        var binding = Assert.IsAssignableFrom<IStateBindingOperation>(
            build.Body.GetProducer(compile.Configuration));

        Assert.Same(configuration.Result, binding.Source);
        Assert.Contains(configuration.Result, prepare.Outputs);
        Assert.Contains(configuration.Result, build.Inputs);
    }

    [Fact]
    public void LinksReadToLatestOrderedPropertyWrite()
    {
        var result = TranslateAsset("OrderedStateVersions.proj", "Build");
        var first = result.Targets["First"];
        var second = result.Targets["Second"];
        var build = result.Targets["Build"];
        var firstConfiguration = Assert.Single(
            first.Body.Operations.OfType<ConstantOperation<string>>());
        var secondConfiguration = Assert.Single(
            second.Body.Operations.OfType<ConstantOperation<string>>());
        var compile = Assert.Single(
            build.Body.Operations.OfType<ToyCompileOperation>());
        var binding = Assert.IsAssignableFrom<IStateBindingOperation>(
            build.Body.GetProducer(compile.Configuration));

        Assert.Equal("First", firstConfiguration.Content);
        Assert.Equal("Second", secondConfiguration.Content);
        Assert.Same(secondConfiguration.Result, binding.Source);
        Assert.Same(
            secondConfiguration.Result,
            result.Properties["Configuration"]);
    }

    [Fact]
    public void LinksPropertyCopyThroughStateRead()
    {
        var result = TranslateAsset("PropertyCopy.proj", "Build");
        var prepare = result.Targets["Prepare"];
        var binding = Assert.Single(
            prepare.Body.Operations.OfType<IStateBindingOperation>());

        Assert.Same(
            binding.Result,
            result.Properties["Configuration"]);
        Assert.Contains(binding.Result, prepare.Outputs);
    }

    [Fact]
    public async Task TranslatesUnescapedPropertyFunctionItemExpression()
    {
        var result = TranslateAsset("PropertyFunctionItems.proj", "Build");
        var build = result.Targets["Build"];
        var expansion = Assert.Single(
            build.Body.Operations.OfType<ExpandItemsExpressionOperation>());
        var exclusion = Assert.Single(
            build.Body.Operations.OfType<ExcludeItemsOperation>());
        var binding = Assert.IsAssignableFrom<IStateBindingOperation>(
            build.Body.GetProducer(expansion.Source));
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(
            [
                new StringReplacement("+", ";"),
                new StringReplacement("-", ";"),
            ],
            expansion.Replacements);
        Assert.Same(
            result.Definition.Evaluation.Initializations.Single(
                initialization => ReferenceEquals(
                    initialization.Location,
                    result.TargetDefinitions["Build"].Reads
                        .OfType<StateRead<string>>()
                        .Single().Location)).InitialValue.Value,
            binding.Source);
        Assert.Equal(
            ["clr", "libs", "native"],
            values.Get(result.Items["SpecifiedSubsetName"]));
        var specifiedItems = Assert.IsType<ConcatItemsOperation>(
            build.Body.GetProducer(exclusion.IncludedItems));

        Assert.Same(expansion.Result, specifiedItems.AppendedItems);
        Assert.Equal(
            ["libs"],
            values.Get(result.Items["InvalidSpecifiedSubsetName"]));
    }

    [Fact]
    public void RejectsUnorderedPropertyStateConflict()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TranslateAsset("UnorderedStateConflict.proj", "Build"));

        Assert.Contains(
            "Targets 'UnrelatedWriter' and 'Build' have conflicting access " +
            "to property 'Configuration'",
            exception.Message);
        Assert.Contains("not ordered", exception.Message);
    }

    [Fact]
    public void ReportsReadOnlyStateFromRequestedTargetExecution()
    {
        var result = TranslateAsset("ReadOnlyState.proj", "Build");
        var buildCompile = Assert.Single(
            result.Targets["Build"].Body.Operations
                .OfType<ToyCompileOperation>());
        var configurationBinding =
            Assert.IsAssignableFrom<IStateBindingOperation>(
                result.Targets["Build"].Body.GetProducer(
                    buildCompile.Configuration));
        var sourcesBinding = Assert.IsAssignableFrom<IStateBindingOperation>(
            result.Targets["Build"].Body.GetProducer(buildCompile.Sources));

        Assert.Same(
            configurationBinding.Source,
            result.Properties["Configuration"]);
        Assert.Same(
            sourcesBinding.Source,
            result.Items["Compile"]);
    }

    [Fact]
    public void RejectsConditionalStateWritesUntilMergesAreModeled()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => TranslateAsset("ConditionalStateWrite.proj", "Build"));

        Assert.Contains(
            "state writes in conditional target 'MaybePrepare'",
            exception.Message);
    }

    [Theory]
    [InlineData("ConditionalProperty.proj", "Debug")]
    [InlineData("ConditionalPropertyFalse.proj", "Release")]
    public async Task ExecutesConditionalPropertyAssignment(
        string assetName,
        string expectedConfiguration)
    {
        var result = TranslateAsset(assetName, "Build");
        var build = result.Targets["Build"];
        var conditional = Assert.Single(
            build.Body.Operations.OfType<ConditionalRegionOperation>());
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Same(
            conditional.Outputs[0],
            result.Properties["Configuration"]);
        Assert.Equal(
            expectedConfiguration,
            values.Get(result.Properties["Configuration"]));
    }

    [Theory]
    [InlineData("ConditionalPropertyGroup.proj", "A", "B")]
    [InlineData("ConditionalPropertyGroupFalse.proj", "OldX", "OldY")]
    public async Task ExecutesConditionalPropertyGroup(
        string assetName,
        string expectedX,
        string expectedY)
    {
        var result = TranslateAsset(assetName, "Build");
        var build = result.Targets["Build"];
        var conditional = Assert.Single(
            build.Body.Operations.OfType<ConditionalRegionOperation>());
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(2, conditional.Outputs.Count);
        Assert.Equal(expectedX, values.Get(result.Properties["X"]));
        Assert.Equal(expectedY, values.Get(result.Properties["Y"]));
    }

    [Theory]
    [InlineData("RuntimeDepends.proj", "ChooseDependencies")]
    [InlineData("RuntimeTaskOutputDepends.proj", "ChooseDependencies")]
    [InlineData("LateRuntimeDepends.proj", "RewriteDependencies")]
    public void RejectsTargetDependenciesComputedDuringTargetExecution(
        string assetName,
        string assigningTarget)
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => TranslateAsset(assetName, "Entry"));

        Assert.Contains(
            "DependsOnTargets on target 'Build' references property " +
            "'BuildDependsOn'",
            exception.Message);
        Assert.Contains(
            $"assigned by target '{assigningTarget}'",
            exception.Message);
        Assert.Contains(
            "must be fixed after project evaluation",
            exception.Message);
    }

    [Theory]
    [InlineData("RuntimeBefore.proj", "BeforeTargets")]
    [InlineData("RuntimeAfter.proj", "AfterTargets")]
    public void RejectsTargetRegistrationsComputedDuringTargetExecution(
        string assetName,
        string attributeName)
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => TranslateAsset(assetName, "Build"));

        Assert.Contains(
            $"{attributeName} on target 'Injected' references property 'Anchor'",
            exception.Message);
        Assert.Contains("assigned by target 'RewriteAnchor'", exception.Message);
        Assert.Contains("must be fixed after project evaluation", exception.Message);
    }

    [Fact]
    public void ExpandsBeforeTargetsFromEvaluatedProperty()
    {
        var result = TranslateAsset("DynamicBefore.proj", "Build");

        Assert.Equal(
            [result.Targets["Before"]],
            result.Targets["Build"].Prelude);
    }

    [Fact]
    public void ExpandsAfterTargetsFromEvaluatedProperty()
    {
        var result = TranslateAsset("DynamicAfter.proj", "Build");

        Assert.Equal(
            [result.Targets["After"]],
            result.Targets["Build"].Epilogue);
    }

    [Fact]
    public async Task MissingRequestedDependencyFailsDuringExecution()
    {
        var result = TranslateAsset("MissingTarget.proj", "Build");
        var warning = Assert.Single(result.Warnings);
        var evaluator = CreateEvaluator();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new BuildProgramExecutor(
                result.Program,
                new ValueStore(),
                evaluator.EvaluateAsync)
                .ExecuteAsync(result.Targets["Build"]));

        Assert.Contains("missing target 'Missing'", warning);
        Assert.Contains("missing target 'Missing'", exception.Message);
    }

    [Theory]
    [InlineData("MissingBeforeTarget.proj")]
    [InlineData("MissingAfterTarget.proj")]
    public void WarnsAboutMissingTargetRegistrationAnchor(string assetName)
    {
        var result = TranslateAsset(assetName, "Build");
        var warning = Assert.Single(result.Warnings);

        Assert.Contains("missing target 'Missing'", warning);
    }

    [Fact]
    public async Task WarnsWithoutFailingForMissingDependencyOnUnreachableTarget()
    {
        var result = TranslateAsset(
            "UnreachableMissingTarget.proj",
            "Build");

        var warning = Assert.Single(result.Warnings);

        Assert.Contains("Target 'Dormant'", warning);
        Assert.Contains("missing target 'Missing'", warning);
        Assert.DoesNotContain("will fail", warning);
        Assert.Empty(result.Targets["Dormant"].Prelude);

        await new BuildProgramExecutor(
            result.Program,
            new ValueStore(),
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(result.Targets["Build"]);
    }

    [Fact]
    public void ReportsWarningBeforeLaterTranslationFailure()
    {
        var warnings = new List<string>();
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "WarningBeforeFailure.proj");

        var exception = Assert.Throws<NotSupportedException>(
            () => new MSBuildProjectTranslator().Translate(
                projectPath,
                "Build",
                warnings.Add));

        Assert.Single(warnings);
        Assert.Contains("missing target 'Missing'", warnings[0]);
        Assert.Contains("task 'Exec'", exception.Message);
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

        Assert.Contains("ordering is contradictory", exception.Message);
        Assert.Contains("'A' -> 'B' -> 'A'", exception.Message);
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
    public async Task TranslatesAndExecutesMessageTasksInOrder()
    {
        var result = TranslateAsset("Message.proj", "Build");
        var build = result.Targets["Build"];
        var messages = build.Body.Operations
            .OfType<MessageOperation>()
            .ToArray();
        var first = messages[0];
        var second = messages[1];
        var textBinding = Assert.IsAssignableFrom<IStateBindingOperation>(
            build.Body.GetProducer(first.Text));
        var importance = Assert.IsType<ConstantOperation<string>>(
            build.Body.GetProducer(first.Importance));
        var defaultImportance = Assert.IsType<ConstantOperation<string>>(
            build.Body.GetProducer(second.Importance));
        var secondText = Assert.IsType<ConstantOperation<string>>(
            build.Body.GetProducer(second.Text));
        var observed = new List<(string Text, string Importance)>();
        var evaluator = CreateEvaluator(
            onMessage: (operation, values) =>
                observed.Add(
                    (values.Get(operation.Text),
                     values.Get(operation.Importance))));

        await new BuildProgramExecutor(
            result.Program,
            new ValueStore(),
            evaluator.EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(2, messages.Length);
        Assert.Same(first.OrderOutput, secondText.OrderInput);
        Assert.Same(
            result.Definition.Evaluation.Initializations.Single(
                initialization => ReferenceEquals(
                    initialization.Location,
                    result.TargetDefinitions["Build"].Reads
                        .Single().Location)).InitialValue.Value,
            textBinding.Source);
        Assert.Equal("High", importance.Content);
        Assert.Equal("normal", defaultImportance.Content);
        Assert.Equal(
            [
                ("Hello from evaluation", "High"),
                ("Done", "normal"),
            ],
            observed);
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

        await new BuildProgramExecutor(
            result.Program,
            values,
            evaluator.EvaluateAsync)
            .ExecuteAsync(result.Targets["Build"]);

        Assert.False(values.Get(result.TargetConditions["Build"]));
        Assert.False(compileExecuted);
    }

    private static OperationEvaluator CreateEvaluator(
        Action<ToyCompileOperation>? onCompile = null,
        Action<MessageOperation, ValueStore>? onMessage = null) =>
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
                        .ProjectCollection.Unescape(expanded)
                        .Split(
                            ';',
                            StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries);
                    values.Set(operation.Result, items);
                    return ValueTask.CompletedTask;
                })
            .Add<ToyCompileOperation>(
                (operation, values, _) =>
                {
                    onCompile?.Invoke(operation);
                    values.Set(operation.Assembly, "App.dll");
                    return ValueTask.CompletedTask;
                })
            .Add<MessageOperation>(
                (operation, values, _) =>
                {
                    onMessage?.Invoke(operation, values);
                    return ValueTask.CompletedTask;
                })
            .Add<MissingTargetOperation>(
                static (operation, _, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            $"Target '{operation.DeclaringTargetName}' " +
                            $"references missing target " +
                            $"'{operation.MissingTargetName}' through " +
                            $"{operation.AttributeName}.")));

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
