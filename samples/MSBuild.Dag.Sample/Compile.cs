using MSBuild.Dag.Core;

namespace MSBuild.Dag.Sample;

internal sealed class Compile(Value<IReadOnlyList<string>> sources) : Operation
{
    public Value<IReadOnlyList<string>> Sources { get; } = sources;

    public Value<string> Assembly { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Sources];

    public override IReadOnlyList<Value> Outputs => [Assembly];
}
