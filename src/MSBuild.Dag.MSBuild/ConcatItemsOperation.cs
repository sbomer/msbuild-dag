using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ConcatItemsOperation(
    Value<IReadOnlyList<string>> existingItems,
    Value<IReadOnlyList<string>> appendedItems) : Operation
{
    public Value<IReadOnlyList<string>> ExistingItems { get; } = existingItems;

    public Value<IReadOnlyList<string>> AppendedItems { get; } = appendedItems;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [ExistingItems, AppendedItems];

    public override IReadOnlyList<Value> Outputs => [Result];
}
