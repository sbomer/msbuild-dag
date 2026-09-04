using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public enum ItemMetadataComparison
{
    Equal,
    NotEqual,
}

public sealed class BroadcastItemValueOperation<T>(
    Value<IReadOnlyList<MSBuildItem>> items,
    Value<T> value,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<MSBuildItem>> Items { get; } = items;

    public Value<T> Value { get; } = value;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<T>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Items, Value] : [Guard, Items, Value];

    public override IReadOnlyList<Value> Outputs => [Result];
}

public sealed class ConcatItemValuesOperation(
    Value<IReadOnlyList<string>> left,
    Value<IReadOnlyList<string>> right,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<string>> Left { get; } = left;

    public Value<IReadOnlyList<string>> Right { get; } = right;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Left, Right] : [Guard, Left, Right];

    public override IReadOnlyList<Value> Outputs => [Result];
}

public sealed class EqualItemValuesOperation<T>(
    Value<IReadOnlyList<T>> values,
    Value<T> candidate,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<T>> Values { get; } = values;

    public Value<T> Candidate { get; } = candidate;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<bool>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Values, Candidate] : [Guard, Values, Candidate];

    public override IReadOnlyList<Value> Outputs => [Result];
}

public sealed class NotItemValuesOperation(
    Value<IReadOnlyList<bool>> values,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<bool>> Values { get; } = values;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<bool>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Values] : [Guard, Values];

    public override IReadOnlyList<Value> Outputs => [Result];
}

public sealed class JoinItemValuesOperation(
    Value<IReadOnlyList<string>> values,
    Value<string> separator,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<string>> Values { get; } = values;

    public Value<string> Separator { get; } = separator;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<string> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Values, Separator] : [Guard, Values, Separator];

    public override IReadOnlyList<Value> Outputs => [Result];
}
