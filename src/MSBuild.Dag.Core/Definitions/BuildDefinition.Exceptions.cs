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

public sealed class ConditionalTargetOutputException(
    TargetDefinition target)
    : NotSupportedException(
        "Conditional target outputs require state merging.")
{
    public TargetDefinition Target { get; } = target;
}

public sealed class MissingTargetInputException(
    TargetDefinition target,
    StateLocation location)
    : InvalidOperationException(
        "A target input has no preceding output or evaluation-time value.")
{
    public TargetDefinition Target { get; } = target;

    public StateLocation Location { get; } = location;
}
