namespace MSBuild.Dag.Core;

public sealed partial class BuildLinkResult
{
    private readonly EvaluationSnapshot _evaluation;
    private readonly IReadOnlyList<TargetDefinition> _definitions;

    internal BuildLinkResult(
        BuildProgram program,
        IReadOnlyDictionary<TargetDefinition, Target> targets,
        EvaluationSnapshot evaluation,
        IReadOnlyList<TargetDefinition> definitions)
    {
        Program = program;
        Targets = targets;
        _evaluation = evaluation;
        _definitions = definitions;
    }

    public BuildProgram Program { get; }

    public IReadOnlyDictionary<TargetDefinition, Target> Targets { get; }
}
