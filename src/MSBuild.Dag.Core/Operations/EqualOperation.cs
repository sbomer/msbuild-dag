namespace MSBuild.Dag.Core;

public sealed class EqualOperation<T>(
    Value<T> left,
    Value<T> right) : Operation
{
    public Value<T> Left { get; } = left;

    public Value<T> Right { get; } = right;

    public Value<bool> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Left, Right];

    public override IReadOnlyList<Value> Outputs => [Result];
}
