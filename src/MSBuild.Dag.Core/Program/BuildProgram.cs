namespace MSBuild.Dag.Core;

public sealed partial class BuildProgram
{
    public BuildProgram(IReadOnlyList<Target> targets)
        : this(
            targets,
            new Dictionary<Value, object?>(
                ReferenceEqualityComparer.Instance),
            [])
    {
    }

    public BuildProgram(
        IReadOnlyList<Target> targets,
        IReadOnlyDictionary<Value, object?> initialValues)
        : this(targets, initialValues, [])
    {
    }

    public BuildProgram(
        IReadOnlyList<Target> targets,
        IReadOnlyDictionary<Value, object?> initialValues,
        IReadOnlyList<Value> inputs)
        : this(targets, initialValues, inputs, [], orderPredecessors: null)
    {
    }

    public BuildProgram(
        IReadOnlyList<Target> targets,
        IReadOnlyDictionary<Value, object?> initialValues,
        IReadOnlyList<Value> inputs,
        IReadOnlyList<TargetStateConflict> stateConflicts)
        : this(
            targets,
            initialValues,
            inputs,
            stateConflicts,
            orderPredecessors: null)
    {
    }

    internal BuildProgram(
        IReadOnlyList<Target> targets,
        IReadOnlyDictionary<Value, object?> initialValues,
        IReadOnlyList<Value> inputs,
        IReadOnlyList<TargetStateConflict> stateConflicts,
        IReadOnlyDictionary<Target, IReadOnlySet<Target>>? orderPredecessors)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(initialValues);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(stateConflicts);

        Targets = targets.ToArray();
        var copiedInitialValues = new Dictionary<Value, object?>(
            ReferenceEqualityComparer.Instance);

        foreach (var (value, content) in initialValues)
        {
            ArgumentNullException.ThrowIfNull(value);
            value.ValidateContent(content);
            copiedInitialValues.Add(value, content);
        }

        InitialValues =
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                Value,
                object?>(copiedInitialValues);
        Inputs = inputs.ToArray();
        StateConflicts = stateConflicts.ToArray();

        var operationOwners = RegisterTargets();
        ValidateInitialValues(operationOwners);
        ValidateInputs(operationOwners);
        ValidateTargetReferences();
        ValidateCrossTargetConnections(operationOwners);
        BuildPrecedence(orderPredecessors);
        EnsureAcyclic();
        ValidateStateConflicts();
    }

    public IReadOnlyList<Target> Targets { get; }

    public IReadOnlyDictionary<Value, object?> InitialValues { get; }

    public IReadOnlyList<Value> Inputs { get; }

    public IReadOnlyList<TargetStateConflict> StateConflicts { get; }

    private void ValidateStateConflicts()
    {
        foreach (var conflict in StateConflicts)
        {
            ArgumentNullException.ThrowIfNull(conflict);

            if (!Targets.Contains(
                    conflict.FirstTarget,
                    ReferenceEqualityComparer.Instance) ||
                !Targets.Contains(
                    conflict.SecondTarget,
                    ReferenceEqualityComparer.Instance))
            {
                throw new ArgumentException(
                    "Every state-conflict target must be part of the program.",
                    nameof(StateConflicts));
            }

            if (GetOrderPredecessors(conflict.FirstTarget).Contains(
                    conflict.SecondTarget,
                    ReferenceEqualityComparer.Instance) ||
                GetOrderPredecessors(conflict.SecondTarget).Contains(
                    conflict.FirstTarget,
                    ReferenceEqualityComparer.Instance))
            {
                throw new ArgumentException(
                    "State-conflict targets must be unordered.",
                    nameof(StateConflicts));
            }
        }
    }
}
