using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ConstantOperation<T>(
    T content,
    Value<OrderToken>? orderToken = null) : Operation, IOrderedOperation
{
    public T Content { get; } = content;

    public Value<OrderToken>? OrderInput { get; } = orderToken;

    public Value<OrderToken>? OrderOutput { get; } =
        orderToken is null ? null : new();

    public Value<T> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs { get; } =
        orderToken is null ? [] : [orderToken];

    public override IReadOnlyList<Value> Outputs =>
        OrderOutput is null ? [Result] : [Result, OrderOutput];
}
