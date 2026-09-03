namespace MSBuild.Dag.Core;

public sealed class ContainsOperation<T>(
    Value<IReadOnlyList<T>> values,
    Value<T> candidate)
    : Operation
{
    public Value<IReadOnlyList<T>> Values { get; } = values;

    public Value<T> Candidate { get; } = candidate;

    public Value<bool> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Values, Candidate];

    public override IReadOnlyList<Value> Outputs => [Result];
}
