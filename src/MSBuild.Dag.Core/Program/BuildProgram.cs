namespace MSBuild.Dag.Core;

public sealed partial class BuildProgram
{
    public BuildProgram(IReadOnlyList<Target> targets)
        : this(targets, [])
    {
    }

    public BuildProgram(
        IReadOnlyList<Target> targets,
        IReadOnlyList<InitialValue> initialValues)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(initialValues);

        Targets = targets.ToArray();
        InitialValues = initialValues.ToArray();

        var operationOwners = RegisterTargets();
        ValidateInitialValues(operationOwners);
        ValidateTargetReferences();
        ValidateCrossTargetConnections(operationOwners);
        BuildPrecedence();
        EnsureAcyclic();
    }

    public IReadOnlyList<Target> Targets { get; }

    public IReadOnlyList<InitialValue> InitialValues { get; }
}
