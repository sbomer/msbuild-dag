using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed record MSBuildFileInputBinding(
    Value<FileContents?> Source,
    Value<FileContents?> Destination);

public sealed record MSBuildStringInputBinding(
    Value<string> Source,
    Value<string> Destination);

public sealed class MSBuildInvocationOperation(
    string projectPath,
    BuildProgram program,
    IReadOnlyDictionary<string, Target> targets,
    Value<string> requestedTargets,
    IReadOnlyList<MSBuildFileInputBinding> fileInputBindings,
    IReadOnlyList<MSBuildStringInputBinding> stringInputBindings,
    OperationControl control)
    : Operation, IGuardedOperation, IOrderedOperation
{
    public string ProjectPath { get; } = projectPath;

    public BuildProgram Program { get; } = program;

    public IReadOnlyDictionary<string, Target> Targets { get; } = targets;

    public Value<string> RequestedTargets { get; } = requestedTargets;

    public IReadOnlyList<MSBuildFileInputBinding> FileInputBindings { get; } =
        fileInputBindings;

    public IReadOnlyList<MSBuildStringInputBinding> StringInputBindings
    {
        get;
    } = stringInputBindings;

    public Value<GuardToken>? Guard => control.Guard;

    public Value<OrderToken>? OrderInput => control.OrderInput;

    public Value<OrderToken> OrderOutput => control.OrderOutput;

    public override IReadOnlyList<Value> Inputs =>
        (OrderInput is null ? [] : new Value[] { OrderInput })
            .Concat(Guard is null ? [] : [Guard])
            .Append(RequestedTargets)
            .Concat(FileInputBindings.Select(binding => binding.Source))
            .Concat(StringInputBindings.Select(binding => binding.Source))
            .ToArray();

    public override IReadOnlyList<Value> Outputs => [OrderOutput];
}
