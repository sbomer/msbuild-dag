namespace MSBuild.Dag.Core;

public sealed partial class TargetDefinition
{
    public TargetDefinition(
        IReadOnlyList<TargetDefinition> prelude,
        IReadOnlyList<TargetInput> inputs,
        IReadOnlyList<TargetOutput> outputs,
        IReadOnlyList<Value> results,
        OperationGraph body,
        IReadOnlyList<TargetDefinition> epilogue)
    {
        ArgumentNullException.ThrowIfNull(prelude);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(epilogue);

        Prelude = prelude.ToArray();
        Inputs = inputs.ToArray();
        Outputs = outputs.ToArray();
        Results = results.ToArray();
        Body = body;
        Epilogue = epilogue.ToArray();

        Validate();
    }

    public IReadOnlyList<TargetDefinition> Prelude { get; }

    public IReadOnlyList<TargetInput> Inputs { get; }

    public IReadOnlyList<TargetOutput> Outputs { get; }

    public IReadOnlyList<Value> Results { get; }

    public OperationGraph Body { get; }

    public IReadOnlyList<TargetDefinition> Epilogue { get; }
}
