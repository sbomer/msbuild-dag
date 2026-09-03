namespace MSBuild.Dag.Core;

public sealed class IsEmptyOperation<T>(
    Value<IReadOnlyList<T>> values)
    : Operation
{
    public Value<IReadOnlyList<T>> Values { get; } = values;

    public Value<bool> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Values];

    public override IReadOnlyList<Value> Outputs => [Result];
}
