using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed record StringReplacement(
    string OldValue,
    string NewValue);

public sealed class ExpandItemsExpressionOperation(
    Value<string> source,
    IReadOnlyList<StringReplacement> replacements,
    OperationControl? control = null)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public Value<string> Source { get; } = source;

    public IReadOnlyList<StringReplacement> Replacements { get; } =
        replacements.ToArray();

    public Value<GuardToken>? Guard => control?.Guard;

    public Value<OrderToken>? OrderInput => control?.OrderInput;

    public Value<OrderToken>? OrderOutput => control?.OrderOutput;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        (Guard, OrderInput) switch
        {
            (not null, not null) => [OrderInput, Guard, Source],
            (not null, null) => [Guard, Source],
            (null, not null) => [OrderInput, Source],
            _ => [Source],
        };

    public override IReadOnlyList<Value> Outputs =>
        OrderOutput is null ? [Result] : [OrderOutput, Result];
}
