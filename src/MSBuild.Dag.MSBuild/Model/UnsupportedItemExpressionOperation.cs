using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class UnsupportedItemExpressionOperation(
    string expression,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public string Expression { get; } = expression;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<MSBuildItem>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [] : [Guard];

    public override IReadOnlyList<Value> Outputs => [Result];
}
