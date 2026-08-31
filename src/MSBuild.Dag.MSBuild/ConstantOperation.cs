using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ConstantOperation<T>(T content) : Operation
{
    public T Content { get; } = content;

    public Value<T> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [];

    public override IReadOnlyList<Value> Outputs => [Result];
}
