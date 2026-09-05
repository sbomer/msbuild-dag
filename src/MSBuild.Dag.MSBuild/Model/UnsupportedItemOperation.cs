using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class UnsupportedItemOperation(
    string description,
    Value<IReadOnlyList<MSBuildItem>> items,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public string Description { get; } = description;

    public Value<IReadOnlyList<MSBuildItem>> Items { get; } = items;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<MSBuildItem>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Items] : [Guard, Items];

    public override IReadOnlyList<Value> Outputs => [Result];
}
