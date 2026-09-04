using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ValueOrDefaultOperation(
    Value<string> value,
    Value<string> defaultValue,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<string> Value { get; } = value;

    public Value<string> DefaultValue { get; } = defaultValue;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<string> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null
            ? [Value, DefaultValue]
            : [Guard, Value, DefaultValue];

    public override IReadOnlyList<Value> Outputs => [Result];
}
