namespace MSBuild.Dag.Core;

public sealed class ConditionalRegionOperation : Operation
{
    public ConditionalRegionOperation(
        Value<bool> condition,
        IReadOnlyList<Value> inputs,
        OperationGraph whenTrue,
        OperationGraph whenFalse,
        IReadOnlyList<Value> outputs)
    {
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(whenTrue);
        ArgumentNullException.ThrowIfNull(whenFalse);
        ArgumentNullException.ThrowIfNull(outputs);

        Condition = condition;
        WhenTrue = whenTrue;
        WhenFalse = whenFalse;
        Inputs = [condition, .. inputs];
        Outputs = outputs.ToArray();

        if (!WhenTrue.HasExplicitBoundary || !WhenFalse.HasExplicitBoundary)
        {
            throw new ArgumentException(
                "Conditional branches must have explicit input and output boundaries.");
        }

        ValidateInputs();
        ValidateOutputs();
        ValidateBranch(WhenTrue, nameof(whenTrue));
        ValidateBranch(WhenFalse, nameof(whenFalse));
    }

    public Value<bool> Condition { get; }

    public OperationGraph WhenTrue { get; }

    public OperationGraph WhenFalse { get; }

    public override IReadOnlyList<Value> Inputs { get; }

    public override IReadOnlyList<Value> Outputs { get; }

    private void ValidateInputs()
    {
        foreach (var input in Inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
        }
    }

    private void ValidateBranch(OperationGraph branch, string parameterName)
    {
        if (branch.Inputs.Count != Inputs.Count - 1)
        {
            throw new ArgumentException(
                "Each conditional branch must accept one input for every conditional input.",
                parameterName);
        }

        for (var index = 1; index < Inputs.Count; index++)
        {
            if (Inputs.Contains(
                branch.Inputs[index - 1],
                ReferenceEqualityComparer.Instance))
            {
                throw new ArgumentException(
                    "Conditional branch inputs must be local values.",
                    parameterName);
            }

            if (branch.Inputs[index - 1].GetType() != Inputs[index].GetType())
            {
                throw new ArgumentException(
                    "Conditional branch input types must match the argument types.",
                    parameterName);
            }
        }

        if (branch.Outputs.Count != Outputs.Count)
        {
            throw new ArgumentException(
                "Each conditional branch must produce one output for every conditional output.",
                parameterName);
        }

        for (var index = 0; index < Outputs.Count; index++)
        {
            if (branch.Outputs[index].GetType() != Outputs[index].GetType())
            {
                throw new ArgumentException(
                    "Conditional branch output types must match the conditional output types.",
                    parameterName);
            }
        }
    }

    private void ValidateOutputs()
    {
        var values = new HashSet<Value>(ReferenceEqualityComparer.Instance);

        foreach (var output in Outputs)
        {
            ArgumentNullException.ThrowIfNull(output);

            if (!values.Add(output))
            {
                throw new ArgumentException(
                    "A conditional output cannot appear more than once.",
                    nameof(Outputs));
            }

            if (Inputs.Contains(output, ReferenceEqualityComparer.Instance))
            {
                throw new ArgumentException(
                    "A conditional output must be a fresh value.",
                    nameof(Outputs));
            }

            if (WhenTrue.Inputs.Contains(
                    output,
                    ReferenceEqualityComparer.Instance) ||
                WhenTrue.Operations.Any(
                    operation => operation.Outputs.Contains(
                        output,
                        ReferenceEqualityComparer.Instance)) ||
                WhenFalse.Inputs.Contains(
                    output,
                    ReferenceEqualityComparer.Instance) ||
                WhenFalse.Operations.Any(
                    operation => operation.Outputs.Contains(
                        output,
                        ReferenceEqualityComparer.Instance)))
            {
                throw new ArgumentException(
                    "A conditional output must not be local to a branch.",
                    nameof(Outputs));
            }
        }
    }
}
