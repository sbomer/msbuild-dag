using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ConcatItemsOperation(
    Value<IReadOnlyList<string>> existingItems,
    Value<IReadOnlyList<string>> appendedItems,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<string>> ExistingItems { get; } = existingItems;

    public Value<IReadOnlyList<string>> AppendedItems { get; } = appendedItems;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => Guard is null
        ? [ExistingItems, AppendedItems]
        : [Guard, ExistingItems, AppendedItems];

    public override IReadOnlyList<Value> Outputs => [Result];
}
