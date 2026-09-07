namespace MSBuild.Dag.Core;

public sealed class TargetStateConflict
{
    public TargetStateConflict(
        Target firstTarget,
        Target secondTarget,
        Location location)
    {
        ArgumentNullException.ThrowIfNull(firstTarget);
        ArgumentNullException.ThrowIfNull(secondTarget);
        ArgumentNullException.ThrowIfNull(location);

        if (ReferenceEquals(firstTarget, secondTarget))
        {
            throw new ArgumentException(
                "A state conflict must relate two distinct targets.");
        }

        FirstTarget = firstTarget;
        SecondTarget = secondTarget;
        Location = location;
    }

    public Target FirstTarget { get; }

    public Target SecondTarget { get; }

    public Location Location { get; }
}

public sealed class RequestStateConflictException(
    TargetStateConflict conflict)
    : InvalidOperationException(
        "The target request activates unordered targets with conflicting " +
        "state access.")
{
    public TargetStateConflict Conflict { get; } =
        conflict ?? throw new ArgumentNullException(nameof(conflict));
}
