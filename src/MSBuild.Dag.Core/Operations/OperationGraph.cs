namespace MSBuild.Dag.Core;

public sealed partial class OperationGraph
{
    public OperationGraph(IReadOnlyList<Operation> operations)
        : this(inputs: null, operations, outputs: null, inferBoundary: true)
    {
    }

    public OperationGraph(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Operation> operations,
        IReadOnlyList<Value> outputs)
        : this(
            inputs ?? throw new ArgumentNullException(nameof(inputs)),
            operations,
            outputs ?? throw new ArgumentNullException(nameof(outputs)),
            inferBoundary: false)
    {
    }

    private OperationGraph(
        IReadOnlyList<Value>? inputs,
        IReadOnlyList<Operation> operations,
        IReadOnlyList<Value>? outputs,
        bool inferBoundary)
    {
        ArgumentNullException.ThrowIfNull(operations);

        Operations = operations.ToArray();

        RegisterOperations();
        BuildDependencies();
        EnsureAcyclic();

        HasExplicitBoundary = !inferBoundary;
        Inputs = inferBoundary
            ? GetExternalInputs()
            : CopyBoundary(inputs!, nameof(inputs));
        Outputs = inferBoundary
            ? GetUnconsumedOutputs()
            : CopyBoundary(outputs!, nameof(outputs));

        ValidateBoundary();
        ValidateNestedScopes();
    }

    internal bool HasExplicitBoundary { get; }

    public IReadOnlyList<Value> Inputs { get; }

    public IReadOnlyList<Operation> Operations { get; }

    public IReadOnlyList<Value> Outputs { get; }
}
