namespace MSBuild.Dag.Core;

public sealed class StateConflictException(
    TargetDefinition firstTarget,
    TargetDefinition secondTarget,
    StateLocation location)
    : InvalidOperationException(
        "Two unordered target definitions have conflicting state access.")
{
    public TargetDefinition FirstTarget { get; } = firstTarget;

    public TargetDefinition SecondTarget { get; } = secondTarget;

    public StateLocation Location { get; } = location;
}

public sealed class TargetOrderCycleException(
    IReadOnlyList<TargetDefinition> cycle)
    : InvalidOperationException(
        "Target-definition ordering is contradictory.")
{
    public IReadOnlyList<TargetDefinition> Cycle { get; } = cycle.ToArray();
}

public sealed class ConditionalStateWriteException(
    TargetDefinition target)
    : NotSupportedException(
        "Conditional target state writes require state merging.")
{
    public TargetDefinition Target { get; } = target;
}

public sealed class MissingInitialStateException(
    TargetDefinition target,
    StateLocation location)
    : InvalidOperationException(
        "A state read has no preceding write or evaluation-time value.")
{
    public TargetDefinition Target { get; } = target;

    public StateLocation Location { get; } = location;
}
