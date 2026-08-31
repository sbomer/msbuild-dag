using MSBuild.Dag.Core;

namespace MSBuild.Dag.Sample;

internal sealed class ComputeConfiguration : Operation
{
    public Value<string> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [];

    public override IReadOnlyList<Value> Outputs => [Result];
}
