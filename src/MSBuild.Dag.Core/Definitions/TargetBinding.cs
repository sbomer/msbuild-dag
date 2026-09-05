namespace MSBuild.Dag.Core;

public abstract partial class TargetInput
{
    public abstract StateLocation Location { get; }

    public abstract Value Value { get; }
}

public sealed partial class TargetInput<T>(
    StateLocation<T> location) : TargetInput
{
    public override StateLocation<T> Location { get; } = location;

    public override Value<T> Value { get; } = new();
}

public abstract class TargetOutput
{
    public abstract StateLocation Location { get; }

    public abstract Value Value { get; }

    public bool IsConditional { get; init; }
}

public sealed class TargetOutput<T>(
    StateLocation<T> location,
    Value<T> value) : TargetOutput
{
    public override StateLocation<T> Location { get; } = location;

    public override Value<T> Value { get; } = value;
}
