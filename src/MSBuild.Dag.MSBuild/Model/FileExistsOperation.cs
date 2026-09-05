using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed class FileExistsOperation(
    Value<FileContents?> contents) : Operation
{
    public Value<FileContents?> Contents { get; } = contents;

    public Value<bool> Result { get; } = new();

    public override IReadOnlyList<Value> Inputs => [Contents];

    public override IReadOnlyList<Value> Outputs => [Result];
}
