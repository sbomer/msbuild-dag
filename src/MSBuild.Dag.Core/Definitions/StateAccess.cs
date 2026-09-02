namespace MSBuild.Dag.Core;

public abstract partial class StateRead
{
    public abstract StateLocation Location { get; }

    public abstract Value Value { get; }
}

public sealed partial class StateRead<T>(
    StateLocation<T> location) : StateRead
{
    public override StateLocation<T> Location { get; } = location;

    public override Value<T> Value { get; } = new();
}

public abstract class StateWrite
{
    public abstract StateLocation Location { get; }

    public abstract Value Value { get; }

    public bool IsConditional { get; init; }
}

public sealed class StateWrite<T>(
    StateLocation<T> location,
    Value<T> value) : StateWrite
{
    public override StateLocation<T> Location { get; } = location;

    public override Value<T> Value { get; } = value;
}
