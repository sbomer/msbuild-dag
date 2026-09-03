using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ExcludeItemsOperation(
    Value<IReadOnlyList<string>> includedItems,
    Value<IReadOnlyList<string>> excludedItems,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<string>> IncludedItems { get; } = includedItems;

    public Value<IReadOnlyList<string>> ExcludedItems { get; } = excludedItems;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => Guard is null
        ? [IncludedItems, ExcludedItems]
        : [Guard, IncludedItems, ExcludedItems];

    public override IReadOnlyList<Value> Outputs => [Result];
}
