namespace MSBuild.Dag.Core;

public sealed partial class OperationGraph
{
    public OperationGraph(IReadOnlyList<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        Operations = operations.ToArray();

        RegisterOperations();
        BuildDependencies();
        EnsureAcyclic();
    }

    public IReadOnlyList<Operation> Operations { get; }
}
