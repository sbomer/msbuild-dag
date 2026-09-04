namespace MSBuild.Dag.Core;

public sealed partial class BuildDefinition
{
    private readonly IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> _preludes;
    private readonly IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> _epilogues;

    public BuildDefinition(
        EvaluationSnapshot evaluation,
        IReadOnlyList<TargetDefinition> targets)
        : this(
            evaluation,
            targets,
            CreateDefaultOrchestration(targets, usePrelude: true),
            CreateDefaultOrchestration(targets, usePrelude: false))
    {
    }

    public BuildDefinition(
        EvaluationSnapshot evaluation,
        IReadOnlyList<TargetDefinition> targets,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>> preludes,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>> epilogues)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(preludes);
        ArgumentNullException.ThrowIfNull(epilogues);

        Evaluation = evaluation;
        Targets = targets.ToArray();
        _preludes = CopyOrchestration(preludes, nameof(preludes));
        _epilogues = CopyOrchestration(epilogues, nameof(epilogues));
    }

    public EvaluationSnapshot Evaluation { get; }

    public IReadOnlyList<TargetDefinition> Targets { get; }

    internal IReadOnlyList<TargetDefinition> GetPrelude(
        TargetDefinition target) =>
        _preludes[target];

    internal IReadOnlyList<TargetDefinition> GetEpilogue(
        TargetDefinition target) =>
        _epilogues[target];

    private IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> CopyOrchestration(
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>> source,
        string parameterName)
    {
        var result = new Dictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            if (!source.TryGetValue(target, out var references))
            {
                throw new ArgumentException(
                    "Every target must have an orchestration entry.",
                    parameterName);
            }

            ArgumentNullException.ThrowIfNull(references, parameterName);
            result.Add(target, references.ToArray());
        }

        if (source.Count != Targets.Count)
        {
            throw new ArgumentException(
                "Orchestration entries must only reference build targets.",
                parameterName);
        }

        return result;
    }

    private static IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> CreateDefaultOrchestration(
        IReadOnlyList<TargetDefinition> targets,
        bool usePrelude)
    {
        var result = new Dictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in targets)
        {
            result.Add(
                target,
                usePrelude ? target.Prelude : target.Epilogue);
        }

        return result;
    }
}
