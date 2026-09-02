namespace MSBuild.Dag.Core;

public sealed partial class Target
{
    public Target(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        OperationGraph body)
        : this([], inputs, outputs, body, [])
    {
    }

    public Target(
        IReadOnlyList<Target> prelude,
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        OperationGraph body,
        IReadOnlyList<Target> epilogue)
    {
        ArgumentNullException.ThrowIfNull(prelude);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(epilogue);

        Prelude = CopyTargets(prelude, nameof(prelude));
        Inputs = inputs.ToArray();
        Outputs = outputs.ToArray();
        Body = body;
        Epilogue = CopyTargets(epilogue, nameof(epilogue));

        _inputs = CreateValueSet(Inputs, nameof(inputs));
        _outputs = CreateValueSet(Outputs, nameof(outputs));

        ValidateBoundary();
    }

    public IReadOnlyList<Target> Prelude { get; }

    public IReadOnlyList<Value> Inputs { get; }

    public IReadOnlyList<Value> Outputs { get; }

    public OperationGraph Body { get; }

    public IReadOnlyList<Target> Epilogue { get; }

    private static Target[] CopyTargets(
        IReadOnlyList<Target> targets,
        string parameterName)
    {
        var result = targets.ToArray();

        foreach (var target in result)
        {
            ArgumentNullException.ThrowIfNull(target, parameterName);
        }

        return result;
    }
}
