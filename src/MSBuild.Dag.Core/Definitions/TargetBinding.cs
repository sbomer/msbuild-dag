namespace MSBuild.Dag.Core;

public abstract partial class TargetInput
{
    public abstract Location Location { get; }

    public abstract Value Value { get; }
}

public sealed partial class TargetInput<T>(
    Location<T> location) : TargetInput
{
    public override Location<T> Location { get; } = location;

    public override Value<T> Value { get; } = new();
}

public class TargetOutput(Value value)
{
    public virtual Location? Location => null;

    public Value Value { get; } = value;

    public bool IsConditional { get; init; }
}

public sealed class TargetOutput<T>(
    Location<T> location,
    Value<T> value) : TargetOutput(value)
{
    public override Location<T> Location { get; } = location;
}
