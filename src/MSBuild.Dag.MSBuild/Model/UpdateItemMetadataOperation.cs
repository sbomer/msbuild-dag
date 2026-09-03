using MSBuild.Dag.Core;
using System.Collections.ObjectModel;

namespace MSBuild.Dag.MSBuild;

public sealed class UpdateItemMetadataOperation(
    Value<IReadOnlyList<MSBuildItem>> items,
    IReadOnlyDictionary<string, string> metadata,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<IReadOnlyList<MSBuildItem>> Items { get; } = items;

    public IReadOnlyDictionary<string, string> Metadata { get; } =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(
                metadata,
                StringComparer.OrdinalIgnoreCase));

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<MSBuildItem>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Items] : [Guard, Items];

    public override IReadOnlyList<Value> Outputs => [Result];
}
