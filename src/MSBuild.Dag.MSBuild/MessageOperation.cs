using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class MessageOperation(
    Value<string> text,
    Value<string> importance,
    OperationControl control)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public Value<string> Text { get; } = text;

    public Value<string> Importance { get; } = importance;

    public Value<GuardToken>? Guard => control.Guard;

    public Value<OrderToken>? OrderInput => control.OrderInput;

    public Value<OrderToken> OrderOutput => control.OrderOutput;

    public override IReadOnlyList<Value> Inputs =>
        (Guard, OrderInput) switch
        {
            (not null, not null) => [OrderInput, Guard, Text, Importance],
            (not null, null) => [Guard, Text, Importance],
            (null, not null) => [OrderInput, Text, Importance],
            _ => [Text, Importance],
        };

    public override IReadOnlyList<Value> Outputs => [OrderOutput];
}
