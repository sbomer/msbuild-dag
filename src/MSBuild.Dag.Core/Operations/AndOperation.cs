namespace MSBuild.Dag.Core;

public sealed class AndOperation(
    Value<bool> left,
    Value<bool> right) : Operation
{
    public Value<bool> Left { get; } = left;

    public Value<bool> Right { get; } = right;

    public Value<bool> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Left, Right];

    public override IReadOnlyList<Value> Outputs => [Result];
}
