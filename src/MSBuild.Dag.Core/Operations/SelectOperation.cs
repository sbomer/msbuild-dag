namespace MSBuild.Dag.Core;

public interface ISelectOperation
{
    Value<bool> Condition { get; }

    Value WhenTrue { get; }

    Value WhenFalse { get; }

    Value Result { get; }
}

public sealed class SelectOperation<T>(
    Value<bool> condition,
    Value<T> whenTrue,
    Value<T> whenFalse) : Operation, ISelectOperation
{
    public Value<bool> Condition { get; } = condition;

    public Value<T> WhenTrue { get; } = whenTrue;

    public Value<T> WhenFalse { get; } = whenFalse;

    public Value<T> Result { get; } = new();

    Value ISelectOperation.WhenTrue => WhenTrue;

    Value ISelectOperation.WhenFalse => WhenFalse;

    Value ISelectOperation.Result => Result;

    public override IReadOnlyList<Value> Inputs =>
        [Condition, WhenTrue, WhenFalse];

    public override IReadOnlyList<Value> Outputs => [Result];
}
