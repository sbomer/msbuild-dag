using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ConcatItemsOperation(
    Value<IReadOnlyList<string>> existingItems,
    Value<IReadOnlyList<string>> appendedItems,
    Value<OrderToken>? orderToken = null) : Operation, IOrderedOperation
{
    public Value<IReadOnlyList<string>> ExistingItems { get; } = existingItems;

    public Value<IReadOnlyList<string>> AppendedItems { get; } = appendedItems;

    public Value<OrderToken>? OrderInput { get; } = orderToken;

    public Value<OrderToken>? OrderOutput { get; } =
        orderToken is null ? null : new();

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs { get; } =
        orderToken is null
            ? [existingItems, appendedItems]
            : [existingItems, appendedItems, orderToken];

    public override IReadOnlyList<Value> Outputs =>
        OrderOutput is null ? [Result] : [Result, OrderOutput];
}
