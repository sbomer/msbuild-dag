namespace MSBuild.Dag.Core;

public abstract class InitialValue
{
    public abstract Value Value { get; }

    public abstract object? Content { get; }
}

public sealed class InitialValue<T>(
    Value<T> value,
    T content) : InitialValue
{
    public override Value<T> Value { get; } = value;

    public T TypedContent { get; } = content;

    public override object? Content => TypedContent;
}
