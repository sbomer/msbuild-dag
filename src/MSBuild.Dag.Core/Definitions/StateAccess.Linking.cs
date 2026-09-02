namespace MSBuild.Dag.Core;

public abstract partial class StateRead
{
    internal abstract Operation CreateBinding(Value source);
}

public sealed partial class StateRead<T>
{
    internal override Operation CreateBinding(Value source)
    {
        if (source is not Value<T> typedSource)
        {
            throw new ArgumentException(
                "The reaching value type must match the state location.",
                nameof(source));
        }

        return new StateBindingOperation<T>(typedSource, Value);
    }
}
