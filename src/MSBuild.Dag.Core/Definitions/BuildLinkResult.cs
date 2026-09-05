namespace MSBuild.Dag.Core;

public sealed partial class BuildLinkResult
{
    private readonly IReadOnlyList<TargetDefinition> _definitions;
    private readonly BuildDefinition _definition;

    internal BuildLinkResult(
        BuildProgram program,
        IReadOnlyDictionary<TargetDefinition, Target> targets,
        BuildDefinition definition,
        IReadOnlyDictionary<Location, Value> initialValues)
    {
        Program = program;
        Targets = targets;
        _definition = definition;
        _definitions = definition.Targets;
        InitialValues =
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                Location,
                Value>(
                new Dictionary<Location, Value>(
                    initialValues,
                    ReferenceEqualityComparer.Instance));
    }

    public BuildProgram Program { get; }

    public IReadOnlyDictionary<TargetDefinition, Target> Targets { get; }

    public IReadOnlyDictionary<Location, Value> InitialValues { get; }
}
