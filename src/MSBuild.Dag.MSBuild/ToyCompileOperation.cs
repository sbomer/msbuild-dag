using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class ToyCompileOperation(
    Value<IReadOnlyList<string>> sources,
    Value<string> configuration,
    OperationControl? control = null)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public Value<IReadOnlyList<string>> Sources { get; } = sources;

    public Value<string> Configuration { get; } = configuration;

    public Value<GuardToken>? Guard => control?.Guard;

    public Value<OrderToken>? OrderInput => control?.OrderInput;

    public Value<OrderToken>? OrderOutput => control?.OrderOutput;

    public Value<string> Assembly { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        (Guard, OrderInput) switch
        {
            (not null, not null) =>
                [OrderInput, Guard, Sources, Configuration],
            (not null, null) => [Guard, Sources, Configuration],
            (null, not null) => [OrderInput, Sources, Configuration],
            _ => [Sources, Configuration],
        };

    public override IReadOnlyList<Value> Outputs =>
        OrderOutput is null ? [Assembly] : [OrderOutput, Assembly];
}
