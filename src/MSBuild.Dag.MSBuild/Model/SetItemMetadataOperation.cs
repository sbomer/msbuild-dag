using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class SetItemMetadataOperation(
    Value<IReadOnlyList<MSBuildItem>> items,
    string metadataName,
    Value<IReadOnlyList<string>> metadataValues,
    Value<IReadOnlyList<bool>>? mask = null,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<MSBuildItem>> Items { get; } = items;

    public string MetadataName { get; } = metadataName;

    public Value<IReadOnlyList<string>> MetadataValues { get; } =
        metadataValues;

    public Value<IReadOnlyList<bool>>? Mask { get; } = mask;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<MSBuildItem>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        (Guard, Mask) switch
        {
            (not null, not null) =>
                [Guard, Items, Mask, MetadataValues],
            (not null, null) =>
                [Guard, Items, MetadataValues],
            (null, not null) =>
                [Items, Mask, MetadataValues],
            _ => [Items, MetadataValues],
        };

    public override IReadOnlyList<Value> Outputs => [Result];
}
