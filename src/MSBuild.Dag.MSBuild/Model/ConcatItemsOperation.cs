using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ConcatItemsOperation(
    Value<IReadOnlyList<MSBuildItem>> existingItems,
    Value<IReadOnlyList<MSBuildItem>> appendedItems,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<MSBuildItem>> ExistingItems { get; } =
        existingItems;

    public Value<IReadOnlyList<MSBuildItem>> AppendedItems { get; } =
        appendedItems;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<MSBuildItem>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => Guard is null
        ? [ExistingItems, AppendedItems]
        : [Guard, ExistingItems, AppendedItems];

    public override IReadOnlyList<Value> Outputs => [Result];
}
