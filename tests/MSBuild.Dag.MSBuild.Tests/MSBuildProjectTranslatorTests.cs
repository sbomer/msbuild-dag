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
        var sourceIdentities = Assert.Single(
            graph.Operations.OfType<ProjectItemIdentitiesOperation>());
        var configuration = Assert.Single(
            prepare.Body.Operations.OfType<ConstantOperation<string>>(),
            operation => operation.Content == "Debug");
        var concat = Assert.Single(
            collectSources.Body.Operations.OfType<ConcatItemsOperation>());

        Assert.Same(sourceIdentities, graph.GetProducer(compile.Sources));
        Assert.Null(graph.GetProducer(sourceIdentities.Items));
        Assert.Null(graph.GetProducer(compile.Configuration));
        Assert.Same(
            GetExternalOutput(collectSources, concat.Result),
            GetExternalInput(build, sourceIdentities.Items));
        Assert.Same(
            GetExternalOutput(build, compile.Assembly),
            result.Properties["AssemblyPath"]);
        Assert.Same(
            GetExternalOutput(collectSources, concat.Result),
            result.Items["Compile"]);
        Assert.Same(
            GetExternalOutput(prepare, configuration.Result),
            result.Properties["Configuration"]);
        Assert.Contains(
            afterBuild.Body.Operations.OfType<ConstantOperation<string>>(),
            operation => operation.Content == "true" &&
                ReferenceEquals(
                    GetExternalOutput(afterBuild, operation.Result),
                    result.Properties["AfterBuildRan"]));
        Assert.Same(
            GetExternalOutput(prepare, configuration.Result),
            GetExternalInput(build, compile.Configuration));
        Assert.Contains(configuration.Result, prepare.Body.Outputs);
        Assert.Contains(
            GetExternalOutput(prepare, configuration.Result),
            build.Inputs);
        Assert.Contains(concat.Result, collectSources.Body.Outputs);
        Assert.Contains(
            GetExternalOutput(collectSources, concat.Result),
            build.Inputs);

        Assert.Equal([sourceIdentities], graph.GetDependencies(compile));
        Assert.Null(configuration.Guard);
        Assert.Equal([configuration.Result], configuration.Outputs);

        var appendedItems = Assert.Single(
            collectSources.Body.Operations
                .OfType<ConstantOperation<IReadOnlyList<MSBuildItem>>>(),
            operation => operation.Content
                .Select(static item => item.Identity)
                .SequenceEqual(["Generated.cs"]));

        Assert.Null(appendedItems.Guard);
        Assert.Equal([appendedItems.Result], appendedItems.Outputs);

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
    public void SdkStyleReferenceCycleDoesNotMaskUnsupportedConstruct()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "SdkStyle.csproj");

        var exception = Assert.Throws<NotSupportedException>(
            () => new MSBuildProjectTranslator().Translate(projectPath, "Build"));

        Assert.Contains("file path", exception.Message);
        Assert.DoesNotContain("task conditions", exception.Message);
        Assert.DoesNotContain("target condition", exception.Message);
        Assert.DoesNotContain("orchestration", exception.Message);
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

        Assert.Null(build.Body.GetProducer(compile.Configuration));
        Assert.Same(
            GetExternalOutput(prepare, configuration.Result),
            GetExternalInput(build, compile.Configuration));
        Assert.Contains(configuration.Result, prepare.Body.Outputs);
        Assert.Contains(
            GetExternalOutput(prepare, configuration.Result),
            build.Inputs);
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

        Assert.Equal("First", firstConfiguration.Content);
        Assert.Equal("Second", secondConfiguration.Content);
        Assert.Null(build.Body.GetProducer(compile.Configuration));
        Assert.Same(
            GetExternalOutput(second, secondConfiguration.Result),
            GetExternalInput(build, compile.Configuration));
        Assert.Same(
            GetExternalOutput(second, secondConfiguration.Result),
            result.Properties["Configuration"]);
    }

    [Fact]
    public void LinksPropertyCopyThroughTargetInput()
    {
        var result = TranslateAsset("PropertyCopy.proj", "Build");
        var prepare = result.Targets["Prepare"];
        var parameter = Assert.Single(prepare.Body.Inputs);

        Assert.Empty(prepare.Body.Operations);
        Assert.Same(
            GetExternalOutput(prepare, parameter),
            result.Properties["Configuration"]);
        Assert.Contains(parameter, prepare.Body.Outputs);
        Assert.NotSame(Assert.Single(prepare.Inputs), parameter);
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
        var values = new ValueStore();

        Assert.Null(build.Body.GetProducer(expansion.Source));

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
        Assert.Contains(
            result.Program.InitialValues,
            initialValue => ReferenceEquals(
                initialValue.Key,
                GetExternalInput(build, expansion.Source)));
        Assert.Equal(
            ["clr", "libs", "native"],
            GetIdentities(values.Get(result.Items["SpecifiedSubsetName"])));
        var specifiedItems = Assert.IsType<ConcatItemsOperation>(
            build.Body.GetProducer(exclusion.IncludedItems));

        Assert.Same(expansion.Result, specifiedItems.AppendedItems);
        Assert.Equal(
            ["libs"],
            GetIdentities(values.Get(result.Items["InvalidSpecifiedSubsetName"])));
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
        var build = result.Targets["Build"];
        var sourceIdentities = Assert.IsType<ProjectItemIdentitiesOperation>(
            build.Body.GetProducer(buildCompile.Sources));

        Assert.Same(
            GetExternalInput(build, buildCompile.Configuration),
            result.Properties["Configuration"]);
        Assert.Same(
            GetExternalInput(build, sourceIdentities.Items),
            result.Items["Compile"]);
    }

    [Fact]
    public void RejectsConditionalTargetOutputsUntilMergesAreModeled()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => TranslateAsset("ConditionalTargetOutput.proj", "Build"));

        Assert.Contains(
            "outputs in conditional target 'MaybePrepare'",
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
        var assignedValue = Assert.Single(
            conditional.WhenTrue.Operations
                .OfType<ReplaceOperation<string>>());
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Same(
            GetExternalOutput(build, conditional.Outputs[0]),
            result.Properties["Configuration"]);
        Assert.NotSame(
            conditional.Outputs[0],
            result.Properties["Configuration"]);
        var bodySymbol = Assert.Single(
            result.ValueSymbols[conditional.Outputs[0]]);
        var exportedSymbol = Assert.Single(
            result.ValueSymbols[result.Properties["Configuration"]]);
        Assert.Equal(bodySymbol.Name, exportedSymbol.Name);
        Assert.NotEqual(bodySymbol.Version, exportedSymbol.Version);
        Assert.Equal("Debug", assignedValue.Content);
        Assert.Same(
            conditional.WhenTrue.Inputs[0],
            assignedValue.Previous);
        Assert.DoesNotContain(
            build.Body.Operations,
            operation => operation is ConstantOperation<string>
            {
                Content: "Debug",
            });
        Assert.Empty(conditional.WhenFalse.Operations);
        Assert.Single(conditional.WhenTrue.Inputs);
        Assert.Single(conditional.WhenFalse.Inputs);
        Assert.Equal(
            expectedConfiguration,
            values.Get(result.Properties["Configuration"]));
    }

    [Fact]
    public async Task ConditionalPropertyReferenceUsesBranchParameter()
    {
        var result = TranslateAsset(
            "ConditionalPropertyReference.proj",
            "Build");
        var build = result.Targets["Build"];
        var conditional = Assert.Single(
            build.Body.Operations.OfType<ConditionalRegionOperation>());
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(2, conditional.WhenTrue.Inputs.Count);
        Assert.Equal(2, conditional.WhenFalse.Inputs.Count);
        Assert.Empty(conditional.WhenTrue.Operations);
        Assert.Empty(conditional.WhenFalse.Operations);
        Assert.Same(
            conditional.WhenTrue.Inputs[1],
            conditional.WhenTrue.Outputs[0]);
        Assert.Same(
            conditional.WhenFalse.Inputs[0],
            conditional.WhenFalse.Outputs[0]);
        Assert.Equal("New", values.Get(result.Properties["X"]));
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
        var assignedValues = conditional.WhenTrue.Operations
            .OfType<ReplaceOperation<string>>()
            .Where(operation => operation.Content is "A" or "B")
            .ToArray();
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(2, build.Inputs.Count);
        Assert.Equal(2, build.Body.Inputs.Count);
        Assert.Equal(
            ["$(X)", "$(Y)"],
            build.Body.Inputs
                .Select(value => Assert.Single(result.ValueSymbols[value]).Name)
                .ToArray());
        Assert.Same(build.Body.Inputs[0], conditional.Inputs[1]);
        Assert.Same(build.Body.Inputs[1], conditional.Inputs[2]);

        Assert.Equal(2, conditional.Outputs.Count);
        Assert.Equal(2, assignedValues.Length);
        Assert.All(
            assignedValues,
            operation => Assert.IsNotAssignableFrom<IOrderedOperation>(operation));
        Assert.Equal(
            conditional.WhenTrue.Inputs,
            assignedValues.Select(operation => operation.Previous).ToArray());
        Assert.Equal(
            ["$(X)", "$(Y)"],
            conditional.WhenTrue.Inputs
                .Select(value => Assert.Single(result.ValueSymbols[value]).Name)
                .ToArray());
        Assert.Equal(
            ["$(X)", "$(Y)"],
            conditional.WhenFalse.Inputs
                .Select(value => Assert.Single(result.ValueSymbols[value]).Name)
                .ToArray());
        Assert.Equal(
            ["$(X)", "$(Y)"],
            conditional.Outputs
                .Select(value => Assert.Single(result.ValueSymbols[value]).Name)
                .ToArray());
        Assert.Equal(
            3,
            conditional.WhenTrue.Inputs
                .Concat(conditional.WhenFalse.Inputs)
                .Append(conditional.Outputs[0])
                .SelectMany(value => result.ValueSymbols[value])
                .Where(symbol => symbol.Name == "$(X)")
                .Select(symbol => symbol.Version)
                .Distinct()
                .Count());
        Assert.DoesNotContain(
            build.Body.Operations,
            operation => operation is ConstantOperation<string>
            {
                Content: "A" or "B",
            });
        Assert.Empty(conditional.WhenFalse.Operations);
        Assert.Equal(2, build.Body.Outputs.Count);
        Assert.Equal(2, build.Outputs.Count);
        Assert.Equal(conditional.Outputs, build.Body.Outputs);
        Assert.Same(
            GetExternalOutput(build, conditional.Outputs[0]),
            result.Properties["X"]);
        Assert.Same(
            GetExternalOutput(build, conditional.Outputs[1]),
            result.Properties["Y"]);
        Assert.NotSame(conditional.Outputs[0], result.Properties["X"]);
        Assert.NotSame(conditional.Outputs[1], result.Properties["Y"]);
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
    public async Task SeparatesActivationCyclesFromPrecedenceOrder()
    {
        var result = TranslateAsset("RunOnceAfterDependency.proj", "A");
        var a = result.Targets["A"];
        var b = result.Targets["B"];
        var c = result.Targets["C"];
        var messages = new List<string>();

        Assert.Equal([b], a.Epilogue);
        Assert.Equal([c], b.Prelude);
        Assert.Equal([a], c.Prelude);
        Assert.Contains(a, result.Program.GetOrderPredecessors(c));
        Assert.Contains(c, result.Program.GetOrderPredecessors(b));

        await new BuildProgramExecutor(
            result.Program,
            new ValueStore(),
            CreateEvaluator(
                onMessage: (operation, values) =>
                    messages.Add(values.Get(operation.Text)))
                .EvaluateAsync)
            .ExecuteAsync(a);

        Assert.Equal(["A", "C", "B"], messages);
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
        Assert.Single(graph.GetDependencies(comparison));
    }

    [Fact]
    public async Task TranslatesItemIdentityConditionToContainsDataflow()
    {
        var result = TranslateAsset("ItemIdentityCondition.proj", "Build");
        var build = result.Targets["Build"];
        var identities = Assert.Single(
            build.Body.Operations.OfType<ProjectItemIdentitiesOperation>());
        var contains = Assert.Single(
            build.Body.Operations.OfType<ContainsOperation<string>>());
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Same(
            identities.Result,
            contains.Values);
        Assert.Same(
            contains.Result,
            Assert.Single(
                build.Body.Operations
                    .OfType<ConditionalRegionOperation>())
                .Condition);
        Assert.Equal("true", values.Get(result.Properties["Found"]));
    }

    [Fact]
    public async Task TranslatesNonemptyItemListTargetCondition()
    {
        var result = TranslateAsset("ItemListCondition.proj", "Build");
        var build = result.Targets["Build"];
        var isEmpty = Assert.Single(
            build.Body.Operations.OfType<IsEmptyOperation<MSBuildItem>>());
        var not = Assert.Single(
            build.Body.Operations.OfType<NotOperation>());
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Same(isEmpty.Result, not.Operand);
        Assert.Same(not.Result, result.TargetConditions["Build"]);
        Assert.True(values.Get(result.TargetConditions["Build"]));
    }

    [Fact]
    public void UnsupportedItemOperationReportsActualOperation()
    {
        var exception = Assert.Throws<NotSupportedException>(
            () => TranslateAsset("UnsupportedItemMetadata.proj", "Build"));

        Assert.Contains(
            "<Candidate Include=\"value\" Text=\"- %(Identity)\" />",
            exception.Message);
        Assert.Contains(
            "only Include with optional Exclude and metadata-only updates",
            exception.Message);
    }

    [Fact]
    public async Task UpdatesMetadataOnEveryExistingItemUsingIdentity()
    {
        var result = TranslateAsset("ItemMetadataUpdate.proj", "Build");
        var build = result.Targets["Build"];
        var identity = Assert.Single(
            build.Body.Operations
                .OfType<GetItemMetadataOperation>());
        var broadcast = Assert.Single(
            build.Body.Operations
                .OfType<BroadcastItemValueOperation<string>>());
        var concat = Assert.Single(
            build.Body.Operations.OfType<ConcatItemValuesOperation>());
        var setMetadata = Assert.Single(
            build.Body.Operations.OfType<SetItemMetadataOperation>());
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal("Identity", identity.MetadataName);
        Assert.Same(broadcast.Result, concat.Left);
        Assert.Same(identity.Result, concat.Right);
        Assert.Equal("Text", setMetadata.MetadataName);
        Assert.Null(setMetadata.Mask);
        Assert.Same(concat.Result, setMetadata.MetadataValues);

        var items = values.Get(result.Items["SubsetName"]);
        Assert.Equal(["clr", "libs"], GetIdentities(items));
        Assert.Equal("runtime", items[0].Metadata["Kind"]);
        Assert.Equal("- clr", items[0].Metadata["Text"]);
        Assert.Equal("library", items[1].Metadata["Kind"]);
        Assert.Equal("- libs", items[1].Metadata["Text"]);
    }

    [Fact]
    public async Task ConditionallyUpdatesMetadataUsingCurrentMetadata()
    {
        var result = TranslateAsset(
            "ConditionalItemMetadataUpdate.proj",
            "Build");
        var build = result.Targets["Build"];
        var metadata = build.Body.Operations
            .OfType<GetItemMetadataOperation>()
            .ToArray();
        var comparison = Assert.Single(
            build.Body.Operations
                .OfType<EqualItemValuesOperation<string>>());
        var concat = Assert.Single(
            build.Body.Operations.OfType<ConcatItemValuesOperation>());
        var setMetadata = Assert.Single(
            build.Body.Operations.OfType<SetItemMetadataOperation>());
        var values = new ValueStore();
        var messages = new List<string>();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator(
                onMessage: (operation, store) =>
                    messages.Add(store.Get(operation.Text)))
                .EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(
            ["OnDemand", "Text", "Text"],
            metadata.Select(operation => operation.MetadataName).ToArray());
        Assert.Same(metadata[0].Result, comparison.Values);
        Assert.Same(comparison.Result, setMetadata.Mask);
        Assert.Same(concat.Result, setMetadata.MetadataValues);
        Assert.Equal("Text", setMetadata.MetadataName);
        Assert.Empty(
            build.Body.Operations.OfType<ConditionalRegionOperation>());

        var items = values.Get(result.Items["SubsetName"]);
        Assert.Equal("- clr", items[0].Metadata["Text"]);
        Assert.Equal(
            "- libs [only runs on demand]",
            items[1].Metadata["Text"]);
        Assert.Equal(
            ["- clr; - libs [only runs on demand]"],
            messages);
    }

    [Fact]
    public async Task ConditionallyIncludesItemsUsingPropertyValues()
    {
        var result = TranslateAsset("ConditionalItemInclude.proj", "Build");
        var build = result.Targets["Build"];
        var conditionals = build.Body.Operations
            .OfType<ConditionalRegionOperation>()
            .ToArray();
        var conjunctions = build.Body.Operations
            .OfType<AndOperation>()
            .ToArray();
        var disjunctions = build.Body.Operations
            .OfType<OrOperation>()
            .ToArray();
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(9, conditionals.Length);
        Assert.Equal(3, conjunctions.Length);
        Assert.Equal(3, disjunctions.Length);
        Assert.Equal(
            [
                "existing",
                "a",
                "build",
                "phrase",
                "or",
                "phrase-or",
                "precedence",
            ],
            GetIdentities(values.Get(result.Items["I"])));
    }

    [Fact]
    public async Task InterpolatesItemIdentitiesIntoPropertyValues()
    {
        var result = TranslateAsset("PropertyItemInterpolation.proj", "Build");
        var build = result.Targets["Build"];
        var joins = build.Body.Operations
            .OfType<JoinItemValuesOperation>()
            .ToArray();
        var concats = build.Body.Operations
            .OfType<ConcatStringsOperation>()
            .ToArray();
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(2, joins.Length);
        Assert.Equal(4, concats.Length);
        Assert.Equal(
            "Projects;;Restore;Build;Publish",
            values.Get(result.Properties["RemoveProps"]));
    }

    [Fact]
    public async Task InterpolatesPropertiesIntoItemIncludes()
    {
        var result = TranslateAsset(
            "PropertyInterpolatedItemInclude.proj",
            "Build");
        var build = result.Targets["Build"];
        var expansions = build.Body.Operations
            .OfType<ExpandItemsExpressionOperation>()
            .ToArray();
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(2, expansions.Length);
        Assert.Equal(
            ["Configuration=Debug"],
            GetIdentities(values.Get(result.Items["CommonProp"])));
        Assert.Equal(
            ["prefix", "one", "two", "suffix"],
            GetIdentities(values.Get(result.Items["Expanded"])));
    }

    [Fact]
    public async Task EvaluatesValueOrDefaultPropertyFunctions()
    {
        var result = TranslateAsset("ValueOrDefault.proj", "Build");
        var build = result.Targets["Build"];
        var operations = build.Body.Operations
            .OfType<ValueOrDefaultOperation>()
            .ToArray();
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(2, operations.Length);
        Assert.Equal(
            "prefix-fallback-suffix",
            values.Get(result.Properties["Fallback"]));
        Assert.Equal(
            "selected",
            values.Get(result.Properties["Existing"]));
    }

    [Fact]
    public async Task WarnsAndFailsWhenUnsupportedPropertyFunctionExecutes()
    {
        var warnings = new List<string>();
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "UnsupportedGetVsInstallRoot.proj");
        var result = new MSBuildProjectTranslator().Translate(
            projectPath,
            "Build",
            warnings.Add);
        var operation = Assert.Single(
            result.Targets["Build"].Body.Operations
                .OfType<UnsupportedPropertyFunctionOperation>());

        Assert.Single(warnings);
        Assert.Equal(warnings, result.Warnings);
        Assert.Contains(operation.FunctionName, warnings[0]);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new BuildProgramExecutor(
                result.Program,
                new ValueStore(),
                CreateEvaluator().EvaluateAsync)
                .ExecuteAsync(result.Targets["Build"]));

        Assert.Contains(operation.FunctionName, exception.Message);
    }

    [Fact]
    public async Task DoesNotEvaluateUnsupportedFunctionInFalseItemBranch()
    {
        var warnings = new List<string>();
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "GuardedUnsupportedPropertyFunction.proj");
        var result = new MSBuildProjectTranslator().Translate(
            projectPath,
            "Build",
            warnings.Add);
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator().EvaluateAsync)
            .ExecuteAsync(result.Targets["Build"]);

        Assert.Single(warnings);
        Assert.Equal(warnings, result.Warnings);
        Assert.Equal(
            ["existing"],
            GetIdentities(values.Get(result.Items["I"])));
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
        Assert.IsNotAssignableFrom<IOrderedOperation>(secondText);
        Assert.Same(first.OrderOutput, second.OrderInput);
        Assert.Contains(
            result.Program.InitialValues,
            initialValue => ReferenceEquals(
                initialValue.Key,
                GetExternalInput(build, first.Text)));
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
    public async Task FalseTaskConditionSkipsErrorAndPreservesOrder()
    {
        var result = TranslateAsset("ConditionalError.proj", "Build");
        var build = result.Targets["Build"];
        var conditional = Assert.Single(
            build.Body.Operations.OfType<ConditionalRegionOperation>());
        Assert.Single(
            conditional.WhenTrue.Operations.OfType<ErrorOperation>());
        var messages = new List<string>();
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator(
                onMessage: (operation, store) =>
                    messages.Add(store.Get(operation.Text))).EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(["continued"], messages);
    }

    [Fact]
    public async Task TrueTaskConditionExecutesError()
    {
        var result = TranslateAsset("ConditionalErrorTrue.proj", "Build");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new BuildProgramExecutor(
                result.Program,
                new ValueStore(),
                CreateEvaluator().EvaluateAsync)
                .ExecuteAsync(result.Targets["Build"]));

        Assert.Equal("Expected failure.", exception.Message);
    }

    [Fact]
    public async Task FalseTaskConditionPreservesPriorOutputProperty()
    {
        var result = TranslateAsset("ConditionalTaskOutput.proj", "Build");
        var build = result.Targets["Build"];
        var conditional = Assert.Single(
            build.Body.Operations.OfType<ConditionalRegionOperation>());
        var compile = Assert.Single(
            conditional.WhenTrue.Operations.OfType<ToyCompileOperation>());
        var message = Assert.Single(
            build.Body.Operations.OfType<MessageOperation>());
        var assemblyInputIndex = Enumerable.Range(
                1,
                conditional.Inputs.Count - 1)
            .Single(index =>
                result.ValueSymbols.TryGetValue(
                    conditional.Inputs[index],
                    out var symbols) &&
                symbols.Any(symbol => symbol.Name == "$(Assembly)"));
        var assemblyOutputIndex = Enumerable.Range(
                0,
                conditional.Outputs.Count)
            .Single(index =>
                result.ValueSymbols.TryGetValue(
                    conditional.Outputs[index],
                    out var symbols) &&
                symbols.Any(symbol => symbol.Name == "$(Assembly)"));
        var assemblyInput = conditional.Inputs[assemblyInputIndex];
        var assemblyOutput = conditional.Outputs[assemblyOutputIndex];

        Assert.Null(build.Body.GetProducer(assemblyInput));
        Assert.Same(
            compile.Assembly,
            conditional.WhenTrue.Outputs[assemblyOutputIndex]);
        Assert.Same(
            conditional.WhenFalse.Inputs[assemblyInputIndex - 1],
            conditional.WhenFalse.Outputs[assemblyOutputIndex]);
        Assert.Same(conditional, build.Body.GetProducer(assemblyOutput));
        Assert.Same(assemblyOutput, message.Text);
        Assert.Contains(conditional, build.Body.GetDependencies(message));

        var compileExecuted = false;
        var messages = new List<string>();
        var values = new ValueStore();

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator(
                _ => compileExecuted = true,
                (operation, store) =>
                    messages.Add(store.Get(operation.Text))).EvaluateAsync)
            .ExecuteAsync(build);

        Assert.False(compileExecuted);
        Assert.Equal("existing.dll", values.Get(result.Properties["Assembly"]));
        Assert.Equal(["existing.dll"], messages);
    }

    [Fact]
    public async Task EvaluatesExistsUsingStaticNormalizedPath()
    {
        var result = TranslateAsset("ExistsCondition.proj", "Build");
        var build = result.Targets["Build"];
        var exists = build.Body.Operations
            .OfType<FileExistsOperation>()
            .ToArray();
        var messages = new List<string>();
        var values = new ValueStore();

        foreach (var state in result.Files.Values)
        {
            values.Set(state, new FileContents());
        }

        await new BuildProgramExecutor(
            result.Program,
            values,
            CreateEvaluator(
                onMessage: (operation, values) =>
                    messages.Add(values.Get(operation.Text))).EvaluateAsync)
            .ExecuteAsync(build);

        Assert.Equal(2, exists.Length);
        var file = Assert.Single(result.Files);
        Assert.Contains(file.Value, result.Program.Inputs);
        Assert.All(
            exists,
            operation =>
            {
                Assert.Same(
                    file.Value,
                    GetExternalInput(build, operation.Contents));
            });
        Assert.Equal(["exists"], messages);
    }

    [Fact]
    public async Task DefersTargetAssignedFilePathFailureUntilExecution()
    {
        var result = TranslateAsset("DynamicExistsCondition.proj", "Build");
        var build = result.Targets["Build"];
        var operation = Assert.Single(
            build.Body.Operations.OfType<UnsupportedTargetOperation>());

        Assert.Contains(
            result.Warnings,
            warning =>
                warning.Contains(
                    "Target 'Build' cannot be fully translated",
                    StringComparison.Ordinal) &&
                warning.Contains(
                    "file path '$(CheckedPath)' depends on target-assigned " +
                    "property '$(CheckedPath)'",
                    StringComparison.Ordinal));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new BuildProgramExecutor(
                result.Program,
                new ValueStore(),
                CreateEvaluator().EvaluateAsync)
                .ExecuteAsync(build));

        Assert.Equal("Build", operation.TargetName);
        Assert.Contains(
            "file path '$(CheckedPath)' depends on target-assigned property",
            exception.Message);
    }

    [Fact]
    public async Task DefersItemDerivedFilePathFailureUntilTargetExecution()
    {
        var result = TranslateAsset(
            "ItemDerivedExistsCondition.proj",
            "Build");
        var build = result.Targets["Build"];
        var unsupported = result.Targets["Unsupported"];
        var operation = Assert.Single(
            unsupported.Body.Operations
                .OfType<UnsupportedTargetOperation>());
        var messages = new List<string>();

        Assert.Contains(
            result.Warnings,
            warning =>
                warning.Contains(
                    "Target 'Unsupported' cannot be fully translated",
                    StringComparison.Ordinal) &&
                warning.Contains(
                    "file path '%(Identity)' depends on item metadata",
                    StringComparison.Ordinal));

        await new BuildProgramExecutor(
            result.Program,
            new ValueStore(),
            CreateEvaluator(
                onMessage: (message, values) =>
                    messages.Add(values.Get(message.Text))).EvaluateAsync)
            .ExecuteAsync(build);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await new BuildProgramExecutor(
                result.Program,
                new ValueStore(),
                CreateEvaluator().EvaluateAsync)
                .ExecuteAsync(unsupported));

        Assert.Equal(["build"], messages);
        Assert.Equal("Unsupported", operation.TargetName);
        Assert.Contains(
            "file path '%(Identity)' depends on item metadata",
            exception.Message);
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
            .Add<NotOperation>(
                static (operation, values, _) =>
                {
                    values.Set(
                        operation.Result,
                        !values.Get(operation.Operand));
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
                    Assert.Equal(items.Count, metadataValues.Count);
                    Assert.True(mask is null || mask.Count == items.Count);
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
                    Assert.Equal(left.Count, right.Count);
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
                    var candidate = values.Get(operation.Candidate);
                    values.Set(
                        operation.Result,
                        values.Get(operation.Values)
                            .Select(value =>
                                StringComparer.OrdinalIgnoreCase.Equals(
                                    value,
                                    candidate))
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
                        .ProjectCollection.Unescape(expanded)
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
            .Add<ErrorOperation>(
                static (operation, values, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            values.Get(operation.Text))))
            .Add<MissingTargetOperation>(
                static (operation, _, _) =>
                    ValueTask.FromException(
                        new InvalidOperationException(
                            $"Target '{operation.DeclaringTargetName}' " +
                            $"references missing target " +
                            $"'{operation.MissingTargetName}' through " +
                            $"{operation.AttributeName}.")));

    private static string[] GetIdentities(
        IReadOnlyList<MSBuildItem> items) =>
        items.Select(static item => item.Identity).ToArray();

    private static Value GetExternalInput(
        Target target,
        Value bodyParameter)
    {
        for (var index = 0; index < target.Body.Inputs.Count; index++)
        {
            if (ReferenceEquals(target.Body.Inputs[index], bodyParameter))
            {
                return target.Inputs[index];
            }
        }

        throw new InvalidOperationException(
            "The value is not a target body parameter.");
    }

    private static Value GetExternalOutput(
        Target target,
        Value bodyResult)
    {
        for (var index = 0; index < target.Body.Outputs.Count; index++)
        {
            if (ReferenceEquals(target.Body.Outputs[index], bodyResult))
            {
                return target.Outputs[index];
            }
        }

        throw new InvalidOperationException(
            "The value is not a target body result.");
    }

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
