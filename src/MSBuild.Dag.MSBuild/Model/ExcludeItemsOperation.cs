using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ExcludeItemsOperation(
    Value<IReadOnlyList<string>> includedItems,
    Value<IReadOnlyList<string>> excludedItems,
    OperationControl? control = null)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public Value<IReadOnlyList<string>> IncludedItems { get; } = includedItems;

    public Value<IReadOnlyList<string>> ExcludedItems { get; } = excludedItems;

    public Value<GuardToken>? Guard => control?.Guard;

    public Value<OrderToken>? OrderInput => control?.OrderInput;

    public Value<OrderToken>? OrderOutput => control?.OrderOutput;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        (Guard, OrderInput) switch
        {
            (not null, not null) =>
                [OrderInput, Guard, IncludedItems, ExcludedItems],
            (not null, null) => [Guard, IncludedItems, ExcludedItems],
            (null, not null) => [OrderInput, IncludedItems, ExcludedItems],
            _ => [IncludedItems, ExcludedItems],
        };

    public override IReadOnlyList<Value> Outputs =>
        OrderOutput is null ? [Result] : [OrderOutput, Result];
}
