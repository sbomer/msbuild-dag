using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ToyCompileOperation(
    Value<IReadOnlyList<string>> sources,
    Value<string> configuration,
    Value<OrderToken>? orderToken = null) : Operation, IOrderedOperation
{
    public Value<IReadOnlyList<string>> Sources { get; } = sources;

    public Value<string> Configuration { get; } = configuration;

    public Value<OrderToken>? OrderInput { get; } = orderToken;

    public Value<OrderToken>? OrderOutput { get; } =
        orderToken is null ? null : new();

    public Value<string> Assembly { get; } = new();

    public override IReadOnlyList<Value> Inputs { get; } =
        orderToken is null
            ? [sources, configuration]
            : [sources, configuration, orderToken];

    public override IReadOnlyList<Value> Outputs =>
        OrderOutput is null ? [Assembly] : [Assembly, OrderOutput];
}
