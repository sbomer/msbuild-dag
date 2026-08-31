using MSBuild.Dag.Core;

namespace MSBuild.Dag.Sample;

internal sealed class ReportConfiguration(Value<string> configuration) : Operation
{
    public Value<string> Configuration { get; } = configuration;

    public override IReadOnlyList<Value> Inputs => [Configuration];

    public override IReadOnlyList<Value> Outputs => [];
}
