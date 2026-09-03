namespace MSBuild.Dag.Core;

public class Value
{
    internal virtual Value CreateSibling() => new();
}

public sealed class Value<T> : Value
{
    internal override Value CreateSibling() => new Value<T>();
}
