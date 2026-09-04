using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ConcatStringsOperation(
    Value<string> left,
    Value<string> right,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<string> Left { get; } = left;

    public Value<string> Right { get; } = right;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<string> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Left, Right] : [Guard, Left, Right];

    public override IReadOnlyList<Value> Outputs => [Result];
}
