using MSBuild.Dag.Core;

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

        Assert.Same(concat.Result, compile.Sources);
        Assert.Same(compile.Assembly, result.Properties["AssemblyPath"]);
        Assert.Same(concat.Result, result.Items["Compile"]);

        Assert.Contains(
            result.Graph.GetDependencies(compile),
            operation => ReferenceEquals(operation, concat));
        Assert.Equal(2, result.Graph.GetDependencies(compile).Count);

        var itemConstants = result.Graph.Operations
            .OfType<ConstantOperation<IReadOnlyList<string>>>()
            .ToArray();

        Assert.Contains(
            itemConstants,
            operation => operation.Content.SequenceEqual(["Program.cs"]));
    }

    [Fact]
    public void LoadsSdkStyleProjectBeforeReportingUnsupportedSemantics()
    {
        var projectPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestAssets",
            "SdkStyle.csproj");

        var exception = Assert.Throws<NotSupportedException>(
            () => new MSBuildProjectTranslator().Translate(projectPath, "Build"));

        Assert.Contains("Target conditions", exception.Message);
    }
}
