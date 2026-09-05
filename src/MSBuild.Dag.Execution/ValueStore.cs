using MSBuild.Dag.Core;

namespace MSBuild.Dag.Execution;

public sealed class ValueStore
{
    private static readonly object s_unavailable = new();

    private readonly Dictionary<Value, object?> _values =
        new(ReferenceEqualityComparer.Instance);

    public void Set<T>(Value<T> value, T result)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!_values.TryAdd(value, result))
        {
            throw new InvalidOperationException("A value can only be assigned once.");
        }
    }

    public T Get<T>(Value<T> value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!_values.TryGetValue(value, out var result))
        {
            throw new InvalidOperationException("The value has not been assigned.");
        }

        if (ReferenceEquals(result, s_unavailable))
        {
            throw new InvalidOperationException("The value is unavailable.");
        }

        if (result is T typedResult)
        {
            return typedResult;
        }

        if (result is null && default(T) is null)
        {
            return default!;
        }

        throw new InvalidOperationException(
            $"The stored value is not assignable to {typeof(T)}.");
    }

    public bool Contains(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _values.ContainsKey(value);
    }

    public bool IsAvailable(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return _values.TryGetValue(value, out var result) &&
               !ReferenceEquals(result, s_unavailable);
    }

    internal void SetUnavailable(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!_values.TryAdd(value, s_unavailable))
        {
            throw new InvalidOperationException("A value can only be assigned once.");
        }
    }

    internal void SetInitial(Value value, object? content)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!_values.TryAdd(value, content))
        {
            throw new InvalidOperationException(
                "An initial value can only be assigned once.");
        }
    }

    internal void Copy(Value source, Value result)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(result);

        if (!_values.TryGetValue(source, out var content))
        {
            throw new InvalidOperationException(
                "The source value has not been assigned.");
        }

        if (!_values.TryAdd(result, content))
        {
            throw new InvalidOperationException(
                "A value can only be assigned once.");
        }
    }
}
