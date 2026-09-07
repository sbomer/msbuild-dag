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
        : this(targets, initialValues, inputs, orderPredecessors: null)
    {
    }

    internal BuildProgram(
        IReadOnlyList<Target> targets,
        IReadOnlyDictionary<Value, object?> initialValues,
        IReadOnlyList<Value> inputs,
        IReadOnlyDictionary<Target, IReadOnlySet<Target>>? orderPredecessors)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(initialValues);
        ArgumentNullException.ThrowIfNull(inputs);

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

        var operationOwners = RegisterTargets();
        ValidateInitialValues(operationOwners);
        ValidateInputs(operationOwners);
        ValidateTargetReferences();
        ValidateCrossTargetConnections(operationOwners);
        BuildPrecedence(orderPredecessors);
        EnsureAcyclic();
    }

    public IReadOnlyList<Target> Targets { get; }

    public IReadOnlyDictionary<Value, object?> InitialValues { get; }

    public IReadOnlyList<Value> Inputs { get; }
}
