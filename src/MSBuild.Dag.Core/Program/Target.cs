namespace MSBuild.Dag.Core;

public sealed partial class Target
{
    public Target(OperationGraph body)
        : this([], body, [])
    {
    }

    public Target(
        IReadOnlyList<Target> prelude,
        OperationGraph body,
        IReadOnlyList<Target> epilogue)
    {
        ArgumentNullException.ThrowIfNull(prelude);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(epilogue);

        Prelude = CopyTargets(prelude, nameof(prelude));
        Body = body;
        Inputs = body.Inputs;
        Outputs = body.Outputs;
        Epilogue = CopyTargets(epilogue, nameof(epilogue));

        _inputs = CreateValueSet(Inputs, nameof(body));
        _outputs = CreateValueSet(Outputs, nameof(body));

        ValidateOutputs();
    }

    public Target(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        OperationGraph body)
        : this(
            [],
            new OperationGraph(inputs, body.Operations, outputs),
            [])
    {
    }

    public Target(
        IReadOnlyList<Target> prelude,
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        OperationGraph body,
        IReadOnlyList<Target> epilogue)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(body);

        var signedBody = new OperationGraph(
            inputs,
            body.Operations,
            outputs);
        ArgumentNullException.ThrowIfNull(prelude);
        ArgumentNullException.ThrowIfNull(epilogue);

        Prelude = CopyTargets(prelude, nameof(prelude));
        Body = signedBody;
        Inputs = signedBody.Inputs;
        Outputs = signedBody.Outputs;
        Epilogue = CopyTargets(epilogue, nameof(epilogue));

        _inputs = CreateValueSet(Inputs, nameof(inputs));
        _outputs = CreateValueSet(Outputs, nameof(outputs));

        ValidateOutputs();
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

    private void ValidateOutputs()
    {
        foreach (var output in Outputs)
        {
            if (Body.GetProducer(output) is null)
            {
                throw new ArgumentException(
                    "A target output must be produced inside the target.",
                    nameof(Body));
            }
        }
    }
}
