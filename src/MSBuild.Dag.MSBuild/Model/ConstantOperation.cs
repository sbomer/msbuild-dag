using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public interface IConstantOperation
{
    object? Content { get; }
}

public sealed class ConstantOperation<T>(
    T content,
    Value<GuardToken>? guard = null)
    : Operation, IConstantOperation, IGuardedOperation
{
    public T Content { get; } = content;

    object? IConstantOperation.Content => Content;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<T> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [] : [Guard];

    public override IReadOnlyList<Value> Outputs => [Result];
}
