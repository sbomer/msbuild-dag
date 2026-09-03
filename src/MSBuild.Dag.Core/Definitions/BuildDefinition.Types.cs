namespace MSBuild.Dag.Core;

internal sealed record LinkedTargetBody(
    IReadOnlyList<Value> Inputs,
    OperationGraph Graph,
    IReadOnlyList<Value> Outputs);
