namespace MSBuild.Dag.Core;

public sealed partial class Target
{
    public Target(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        IReadOnlyList<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(operations);

        Inputs = inputs.ToArray();
        Outputs = outputs.ToArray();
        Body = new OperationGraph(operations);

        _inputs = CreateValueSet(Inputs, nameof(inputs));
        _outputs = CreateValueSet(Outputs, nameof(outputs));

        ValidateBoundary();
    }

    public IReadOnlyList<Value> Inputs { get; }

    public IReadOnlyList<Value> Outputs { get; }

    public OperationGraph Body { get; }
}
