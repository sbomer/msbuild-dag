namespace MSBuild.Dag.Core;

public sealed class ConditionGateOperation(Value<bool> condition) : Operation
{
    public Value<bool> Condition { get; } = condition;

    public Value<OrderToken> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Condition];

    public override IReadOnlyList<Value> Outputs => [Result];
}
