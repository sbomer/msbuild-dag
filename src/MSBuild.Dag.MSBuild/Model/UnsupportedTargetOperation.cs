using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class UnsupportedTargetOperation(
    string targetName,
    string reason) : Operation
{
    public string TargetName { get; } = targetName;

    public string Reason { get; } = reason;

    public override IReadOnlyList<Value> Inputs => [];

    public override IReadOnlyList<Value> Outputs => [];
}
