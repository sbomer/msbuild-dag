namespace MSBuild.Dag.Core;

public sealed class OperationControl(
    Value<GuardToken>? guard,
    Value<OrderToken>? orderInput)
{
    public Value<GuardToken>? Guard { get; } = guard;

    public Value<OrderToken>? OrderInput { get; } = orderInput;

    public Value<OrderToken> OrderOutput { get; } = new();
}
