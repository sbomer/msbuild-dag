namespace MSBuild.Dag.Core;

public sealed class NotOperation(Value<bool> operand) : Operation
{
    public Value<bool> Operand { get; } = operand;

    public Value<bool> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Operand];

    public override IReadOnlyList<Value> Outputs => [Result];
}
