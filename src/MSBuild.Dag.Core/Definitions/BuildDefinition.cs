namespace MSBuild.Dag.Core;

public sealed partial class BuildDefinition
{
    private readonly IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> _preludes;
    private readonly IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> _epilogues;
    private readonly IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> _orderPredecessors;

    public BuildDefinition(
        EvaluationSnapshot evaluation,
        IReadOnlyList<TargetDefinition> targets)
        : this(
            evaluation,
            [],
            targets,
            CreateDefaultPreludes(targets),
            CreateDefaultEpilogues(targets))
    {
    }

    public BuildDefinition(
        EvaluationSnapshot evaluation,
        IReadOnlyList<Location> inputs,
        IReadOnlyList<TargetDefinition> targets)
        : this(
            evaluation,
            inputs,
            targets,
            CreateDefaultPreludes(targets),
            CreateDefaultEpilogues(targets))
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
        : this(evaluation, [], targets, preludes, epilogues)
    {
    }

    public BuildDefinition(
        EvaluationSnapshot evaluation,
        IReadOnlyList<Location> inputs,
        IReadOnlyList<TargetDefinition> targets,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>> preludes,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>> epilogues)
        : this(
            evaluation,
            inputs,
            targets,
            preludes,
            epilogues,
            orderPredecessors: null)
    {
    }

    public BuildDefinition(
        EvaluationSnapshot evaluation,
        IReadOnlyList<Location> inputs,
        IReadOnlyList<TargetDefinition> targets,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>> preludes,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>> epilogues,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>>? orderPredecessors)
    {
        ArgumentNullException.ThrowIfNull(evaluation);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(preludes);
        ArgumentNullException.ThrowIfNull(epilogues);

        Evaluation = evaluation;
        Inputs = CopyInputs(inputs, evaluation);
        Targets = targets.ToArray();
        _preludes = CopyOrchestration(preludes, nameof(preludes));
        _epilogues = CopyOrchestration(epilogues, nameof(epilogues));
        _orderPredecessors = CopyOrchestration(
            orderPredecessors ??
                TargetOrder.CreateImmediatePredecessors(
                    Targets,
                    GetPrelude,
                    GetEpilogue),
            nameof(orderPredecessors));
    }

    public EvaluationSnapshot Evaluation { get; }

    public IReadOnlyList<Location> Inputs { get; }

    public IReadOnlyList<TargetDefinition> Targets { get; }

    internal IReadOnlyList<TargetDefinition> GetPrelude(
        TargetDefinition target) =>
        _preludes[target];

    internal IReadOnlyList<TargetDefinition> GetEpilogue(
        TargetDefinition target) =>
        _epilogues[target];

    internal IReadOnlyList<TargetDefinition> GetOrderPredecessors(
        TargetDefinition target) =>
        _orderPredecessors[target];

    private static IReadOnlyList<Location> CopyInputs(
        IReadOnlyList<Location> inputs,
        EvaluationSnapshot evaluation)
    {
        var result = new List<Location>(inputs.Count);
        var seen = new HashSet<Location>(
            ReferenceEqualityComparer.Instance);

        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);

            if (!seen.Add(input))
            {
                throw new ArgumentException(
                    "A build input location cannot appear more than once.",
                    nameof(inputs));
            }

            if (evaluation.Values.ContainsKey(input))
            {
                throw new ArgumentException(
                    "A location cannot be both evaluated and supplied as a " +
                    "build input.",
                    nameof(inputs));
            }

            result.Add(input);
        }

        return result;
    }

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
        IReadOnlyList<TargetDefinition>> CreateDefaultPreludes(
        IReadOnlyList<TargetDefinition> targets) =>
        CreateDefaultOrchestration(
            targets,
            target => target.Prelude);

    private static IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> CreateDefaultEpilogues(
        IReadOnlyList<TargetDefinition> targets) =>
        CreateDefaultOrchestration(
            targets,
            target => target.Epilogue);

    private static IReadOnlyDictionary<
        TargetDefinition,
        IReadOnlyList<TargetDefinition>> CreateDefaultOrchestration(
        IReadOnlyList<TargetDefinition> targets,
        Func<TargetDefinition, IReadOnlyList<TargetDefinition>> getReferences)
    {
        var result = new Dictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in targets)
        {
            result.Add(target, getReferences(target));
        }

        return result;
    }
}
