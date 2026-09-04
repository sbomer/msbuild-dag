namespace MSBuild.Dag.Core;

public sealed partial class Target
{
    public Target(OperationGraph body)
        : this(
            [],
            CreateBoundaryValues(body.Inputs),
            body,
            CreateBoundaryValues(body.Outputs),
            [])
    {
    }

    public Target(
        IReadOnlyList<Target> prelude,
        OperationGraph body,
        IReadOnlyList<Target> epilogue)
        : this(
            prelude,
            CreateBoundaryValues(body.Inputs),
            body,
            CreateBoundaryValues(body.Outputs),
            epilogue)
    {
    }

    public Target(
        IReadOnlyList<Target> prelude,
        IReadOnlyList<Value> inputs,
        OperationGraph body,
        IReadOnlyList<Target> epilogue)
        : this(
            prelude,
            inputs,
            body,
            CreateBoundaryValues(body.Outputs),
            epilogue)
    {
    }

    public Target(
        IReadOnlyList<Target> prelude,
        IReadOnlyList<Value> inputs,
        OperationGraph body,
        IReadOnlyList<Value> outputs,
        IReadOnlyList<Target> epilogue)
    {
        ArgumentNullException.ThrowIfNull(prelude);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(epilogue);

        Prelude = CopyTargets(prelude, nameof(prelude));
        Body = body;
        Inputs = CopyBoundaryValues(inputs, nameof(inputs));
        Outputs = CopyBoundaryValues(outputs, nameof(outputs));
        Epilogue = CopyTargets(epilogue, nameof(epilogue));

        ValidateBoundaryBindings(
            Inputs,
            Body.Inputs,
            "input",
            nameof(inputs));
        ValidateBoundaryBindings(
            Outputs,
            Body.Outputs,
            "output",
            nameof(outputs));

        _inputs = new HashSet<Value>(
            Inputs,
            ReferenceEqualityComparer.Instance);
        _outputs = CreateValueSet(Outputs, nameof(outputs));
    }

    public Target(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        OperationGraph body)
        : this(
            [],
            CreateBoundaryValues(inputs),
            new OperationGraph(inputs, body.Operations, outputs),
            CreateBoundaryValues(outputs),
            [])
    {
    }

    public Target(
        IReadOnlyList<Target> prelude,
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        OperationGraph body,
        IReadOnlyList<Target> epilogue)
        : this(
            prelude,
            CreateBoundaryValues(inputs),
            CreateSignedBody(inputs, outputs, body),
            CreateBoundaryValues(outputs),
            epilogue)
    {
    }

    public IReadOnlyList<Target> Prelude { get; private set; }

    public IReadOnlyList<Value> Inputs { get; }

    public IReadOnlyList<Value> Outputs { get; }

    public OperationGraph Body { get; }

    public IReadOnlyList<Target> Epilogue { get; private set; }

    internal void SetOrchestration(
        IReadOnlyList<Target> prelude,
        IReadOnlyList<Target> epilogue)
    {
        Prelude = CopyTargets(prelude, nameof(prelude));
        Epilogue = CopyTargets(epilogue, nameof(epilogue));
    }

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

    private static Value[] CopyBoundaryValues(
        IReadOnlyList<Value> values,
        string parameterName)
    {
        var result = values.ToArray();

        foreach (var value in result)
        {
            ArgumentNullException.ThrowIfNull(value, parameterName);
        }

        return result;
    }

    private static Value[] CreateBoundaryValues(
        IReadOnlyList<Value> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var result = new Value[values.Count];

        for (var index = 0; index < values.Count; index++)
        {
            ArgumentNullException.ThrowIfNull(values[index], nameof(values));
            result[index] = values[index].CreateSibling();
        }

        return result;
    }

    private static OperationGraph CreateSignedBody(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        OperationGraph body)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        ArgumentNullException.ThrowIfNull(body);

        return new OperationGraph(inputs, body.Operations, outputs);
    }

    private static void ValidateBoundaryBindings(
        IReadOnlyList<Value> externalValues,
        IReadOnlyList<Value> bodyValues,
        string boundaryName,
        string parameterName)
    {
        if (externalValues.Count != bodyValues.Count)
        {
            throw new ArgumentException(
                $"A target must have one external {boundaryName} for each " +
                $"body {boundaryName}.",
                parameterName);
        }

        for (var index = 0; index < externalValues.Count; index++)
        {
            if (ReferenceEquals(externalValues[index], bodyValues[index]))
            {
                throw new ArgumentException(
                    $"A target {boundaryName} must be distinct from its " +
                    $"corresponding body {boundaryName}.",
                    parameterName);
            }

            if (externalValues[index].GetType() != bodyValues[index].GetType())
            {
                throw new ArgumentException(
                    $"Each target {boundaryName} must have the same type as " +
                    $"its corresponding body {boundaryName}.",
                    parameterName);
            }
        }
    }

}
