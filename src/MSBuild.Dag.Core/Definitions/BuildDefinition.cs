namespace MSBuild.Dag.Core;

public sealed partial class BuildDefinition
{
    public BuildDefinition(
        EvaluationSnapshot evaluation,
        IReadOnlyList<TargetDefinition> targets)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(targets);

        Evaluation = evaluation;
        Targets = targets.ToArray();
    }

    public EvaluationSnapshot Evaluation { get; }

    public IReadOnlyList<TargetDefinition> Targets { get; }
}
