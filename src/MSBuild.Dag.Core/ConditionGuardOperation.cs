namespace MSBuild.Dag.Core;

public sealed class ConditionGuardOperation(Value<bool> condition) : Operation
{
    public Value<bool> Condition { get; } = condition;

    public Value<GuardToken> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Condition];

    public override IReadOnlyList<Value> Outputs => [Result];
}
