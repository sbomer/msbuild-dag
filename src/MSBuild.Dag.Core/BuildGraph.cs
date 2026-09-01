namespace MSBuild.Dag.Core;

public sealed partial class BuildGraph
{
    public BuildGraph(
        IReadOnlyList<Target> targets,
        IReadOnlyList<TargetDependency>? explicitDependencies = null)
    {
        ArgumentNullException.ThrowIfNull(targets);

        Targets = targets.ToArray();
        ExplicitDependencies = explicitDependencies?.ToArray() ?? [];

        var operationOwners = RegisterTargets();
        RegisterExplicitDependencies();
        ValidateCrossTargetConnections(operationOwners);
        BuildDependencies();
        EnsureAcyclic();
    }

    public IReadOnlyList<Target> Targets { get; }

    public IReadOnlyList<TargetDependency> ExplicitDependencies { get; }
}
