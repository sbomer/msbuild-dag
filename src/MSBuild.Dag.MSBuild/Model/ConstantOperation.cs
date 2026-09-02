using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public interface IConstantOperation
{
    object? Content { get; }
}

public sealed class ConstantOperation<T>(
    T content,
    OperationControl? control = null)
    : Operation, IConstantOperation, IGuardedOperation, IOrderedOperation
{
    public T Content { get; } = content;

    object? IConstantOperation.Content => Content;

    public Value<GuardToken>? Guard => control?.Guard;

    public Value<OrderToken>? OrderInput => control?.OrderInput;

    public Value<OrderToken>? OrderOutput => control?.OrderOutput;

    public Value<T> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        CreateInputs(Guard, OrderInput);

    private static IReadOnlyList<Value> CreateInputs(
        Value<GuardToken>? guard,
        Value<OrderToken>? orderInput) =>
        (guard, orderInput) switch
        {
            (not null, not null) => [orderInput, guard],
            (not null, null) => [guard],
            (null, not null) => [orderInput],
            _ => [],
        };

    public override IReadOnlyList<Value> Outputs =>
        OrderOutput is null ? [Result] : [OrderOutput, Result];
}
