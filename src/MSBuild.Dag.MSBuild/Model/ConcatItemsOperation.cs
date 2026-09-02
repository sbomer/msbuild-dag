using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ConcatItemsOperation(
    Value<IReadOnlyList<string>> existingItems,
    Value<IReadOnlyList<string>> appendedItems,
    OperationControl? control = null)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public Value<IReadOnlyList<string>> ExistingItems { get; } = existingItems;

    public Value<IReadOnlyList<string>> AppendedItems { get; } = appendedItems;

    public Value<GuardToken>? Guard => control?.Guard;

    public Value<OrderToken>? OrderInput => control?.OrderInput;

    public Value<OrderToken>? OrderOutput => control?.OrderOutput;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        (Guard, OrderInput) switch
        {
            (not null, not null) =>
                [OrderInput, Guard, ExistingItems, AppendedItems],
            (not null, null) => [Guard, ExistingItems, AppendedItems],
            (null, not null) => [OrderInput, ExistingItems, AppendedItems],
            _ => [ExistingItems, AppendedItems],
        };

    public override IReadOnlyList<Value> Outputs =>
        OrderOutput is null ? [Result] : [OrderOutput, Result];
}
