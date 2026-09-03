using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed record StringReplacement(
    string OldValue,
    string NewValue);

public sealed class ExpandItemsExpressionOperation(
    Value<string> source,
    IReadOnlyList<StringReplacement> replacements,
    Value<GuardToken>? guard = null)
    : Operation, IGuardedOperation
{
    public Value<string> Source { get; } = source;

    public IReadOnlyList<StringReplacement> Replacements { get; } =
        replacements.ToArray();

    public Value<GuardToken>? Guard { get; } = guard;

    public Value<IReadOnlyList<string>> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs =>
        Guard is null ? [Source] : [Guard, Source];

    public override IReadOnlyList<Value> Outputs => [Result];
}
