namespace MSBuild.Dag.Core;

public sealed partial class BuildLinkResult
{
    private readonly EvaluationSnapshot _evaluation;
    private readonly IReadOnlyList<TargetDefinition> _definitions;
    private readonly BuildDefinition _definition;

    internal BuildLinkResult(
        BuildProgram program,
        IReadOnlyDictionary<TargetDefinition, Target> targets,
        BuildDefinition definition)
    {
        Program = program;
        Targets = targets;
        _definition = definition;
        _evaluation = definition.Evaluation;
        _definitions = definition.Targets;
    }

    public BuildProgram Program { get; }

    public IReadOnlyDictionary<TargetDefinition, Target> Targets { get; }
}
