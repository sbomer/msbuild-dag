namespace MSBuild.Dag.Core;

public interface IStateBindingOperation
{
    Value Source { get; }

    Value Result { get; }
}

internal sealed class StateBindingOperation<T>(
    Value<T> source,
    Value<T> result) : Operation, IStateBindingOperation
{
    public Value<T> TypedSource { get; } = source;

    public Value<T> TypedResult { get; } = result;

    Value IStateBindingOperation.Source => TypedSource;

    Value IStateBindingOperation.Result => TypedResult;

    public override IReadOnlyList<Value> Inputs => [TypedSource];

    public override IReadOnlyList<Value> Outputs => [TypedResult];
}
