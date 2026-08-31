using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ToyCompileOperation(
    Value<IReadOnlyList<string>> sources,
    Value<string> configuration) : Operation
{
    public Value<IReadOnlyList<string>> Sources { get; } = sources;

    public Value<string> Configuration { get; } = configuration;

    public Value<string> Assembly { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Sources, Configuration];

    public override IReadOnlyList<Value> Outputs => [Assembly];
}
