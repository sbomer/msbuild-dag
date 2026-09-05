using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class UnsupportedTaskOperation(
    string taskName,
    string reason,
    OperationControl control)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public string TaskName { get; } = taskName;

    public string Reason { get; } = reason;

    public Value<GuardToken>? Guard => control.Guard;

    public Value<OrderToken>? OrderInput => control.OrderInput;

    public Value<OrderToken> OrderOutput => control.OrderOutput;

    public override IReadOnlyList<Value> Inputs =>
        (Guard, OrderInput) switch
        {
            (not null, not null) => [OrderInput, Guard],
            (not null, null) => [Guard],
            (null, not null) => [OrderInput],
            _ => [],
        };

    public override IReadOnlyList<Value> Outputs => [OrderOutput];
}
