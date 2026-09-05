namespace MSBuild.Dag.Core;

public abstract class Location
{
    internal abstract void ValidateContent(object? content);

    internal abstract Value CreateValue();
}

public sealed class Location<T> : Location
{
    internal override void ValidateContent(object? content) => Cast(content);

    internal override Value<T> CreateValue() => new();

    private static T Cast(object? content)
    {
        if (content is T typedContent)
        {
            return typedContent;
        }

        if (content is null && default(T) is null)
        {
            return default!;
        }

        throw new ArgumentException(
            $"The value is not assignable to {typeof(T)}.",
            nameof(content));
    }
}
