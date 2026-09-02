namespace MSBuild.Dag.Core;

public sealed partial class BuildProgram
{
    public BuildProgram(IReadOnlyList<Target> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        Targets = targets.ToArray();

        var operationOwners = RegisterTargets();
        ValidateTargetReferences();
        EnsureOrchestrationAcyclic();
        ValidateCrossTargetConnections(operationOwners);
        BuildPrecedence();
        EnsureAcyclic();
    }

    public IReadOnlyList<Target> Targets { get; }
}
