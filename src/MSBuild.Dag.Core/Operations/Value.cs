namespace MSBuild.Dag.Core;

public class Value
{
    internal virtual void ValidateContent(object? content)
    {
    }

    internal virtual Value CreateSibling() => new();
}

public sealed class Value<T> : Value
{
    internal override void ValidateContent(object? content)
    {
        if (content is T || content is null && default(T) is null)
        {
            return;
        }

        throw new ArgumentException(
            $"The value is not assignable to {typeof(T)}.",
            nameof(content));
    }

    internal override Value CreateSibling() => new Value<T>();
}
