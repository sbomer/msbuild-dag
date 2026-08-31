using MSBuild.Dag.Core;

namespace MSBuild.Dag.Sample;

internal sealed class GenerateSources : Operation
{
    public Value<IReadOnlyList<string>> GeneratedSources { get; } = new();

    public override IReadOnlyList<Value> Inputs => [];

    public override IReadOnlyList<Value> Outputs => [GeneratedSources];
}
