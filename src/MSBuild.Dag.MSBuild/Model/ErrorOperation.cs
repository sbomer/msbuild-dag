using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ErrorOperation(
    Value<string> text,
    OperationControl control)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public Value<string> Text { get; } = text;

    public Value<GuardToken>? Guard => control.Guard;

    public Value<OrderToken>? OrderInput => control.OrderInput;

    public Value<OrderToken> OrderOutput => control.OrderOutput;

    public override IReadOnlyList<Value> Inputs =>
        (Guard, OrderInput) switch
        {
            (not null, not null) => [OrderInput, Guard, Text],
            (not null, null) => [Guard, Text],
            (null, not null) => [OrderInput, Text],
            _ => [Text],
        };

    public override IReadOnlyList<Value> Outputs => [OrderOutput];
}
