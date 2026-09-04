using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class GetItemMetadataOperation(
    Value<IReadOnlyList<MSBuildItem>> items,
    string metadataName,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<MSBuildItem>> Items { get; } = items;

    public string MetadataName { get; } = metadataName;

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Items] : [Guard, Items];

    public override IReadOnlyList<Value> Outputs => [Result];
}
