namespace MSBuild.Dag.Core;

public sealed partial class BuildProgram
{
    public BuildProgram(IReadOnlyList<Target> targets)
        : this(
            targets,
            new Dictionary<Value, object?>(
                ReferenceEqualityComparer.Instance))
    {
    }

    public BuildProgram(
        IReadOnlyList<Target> targets,
        IReadOnlyDictionary<Value, object?> initialValues)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(initialValues);

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

        var operationOwners = RegisterTargets();
        ValidateInitialValues(operationOwners);
        ValidateTargetReferences();
        ValidateCrossTargetConnections(operationOwners);
        BuildPrecedence();
        EnsureAcyclic();
    }

    public IReadOnlyList<Target> Targets { get; }

    public IReadOnlyDictionary<Value, object?> InitialValues { get; }
}
