using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class MissingTargetOperation(
    string declaringTargetName,
    string missingTargetName,
    string attributeName,
    OperationControl control)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public string DeclaringTargetName { get; } = declaringTargetName;

    public string MissingTargetName { get; } = missingTargetName;

    public string AttributeName { get; } = attributeName;

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
