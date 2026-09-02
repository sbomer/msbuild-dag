namespace MSBuild.Dag.Core;

public sealed partial class TargetDefinition
{
    public TargetDefinition(
        IReadOnlyList<TargetDefinition> prelude,
        IReadOnlyList<StateRead> reads,
        IReadOnlyList<StateWrite> writes,
        IReadOnlyList<Value> outputs,
        OperationGraph body,
        IReadOnlyList<TargetDefinition> epilogue)
    {
        ArgumentNullException.ThrowIfNull(prelude);
        ArgumentNullException.ThrowIfNull(reads);
        ArgumentNullException.ThrowIfNull(writes);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(epilogue);

        Prelude = prelude.ToArray();
        Reads = reads.ToArray();
        Writes = writes.ToArray();
        Outputs = outputs.ToArray();
        Body = body;
        Epilogue = epilogue.ToArray();

        Validate();
    }

    public IReadOnlyList<TargetDefinition> Prelude { get; }

    public IReadOnlyList<StateRead> Reads { get; }

    public IReadOnlyList<StateWrite> Writes { get; }

    public IReadOnlyList<Value> Outputs { get; }

    public OperationGraph Body { get; }

    public IReadOnlyList<TargetDefinition> Epilogue { get; }
}
