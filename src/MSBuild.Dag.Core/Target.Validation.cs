namespace MSBuild.Dag.Core;

public sealed partial class Target
{
    private readonly HashSet<Value> _inputs;
    private readonly HashSet<Value> _outputs;

    internal bool Imports(Value value) => _inputs.Contains(value);

    internal bool Exports(Value value) => _outputs.Contains(value);

    private static HashSet<Value> CreateValueSet(
        IReadOnlyList<Value> values,
        string parameterName)
    {
        var result = new HashSet<Value>(ReferenceEqualityComparer.Instance);

        foreach (var value in values)
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!result.Add(value))
            {
                throw new ArgumentException(
                    "A target boundary cannot contain the same value more than once.",
                    parameterName);
            }
        }

        return result;
    }

    private void ValidateBoundary()
    {
        foreach (var input in Inputs)
        {
            if (Body.GetProducer(input) is not null)
            {
                throw new ArgumentException(
                    "A target input cannot be produced inside the target.",
                    nameof(Inputs));
            }
        }

        foreach (var output in Outputs)
        {
            if (Body.GetProducer(output) is null)
            {
                throw new ArgumentException(
                    "A target output must be produced inside the target.",
                    nameof(Outputs));
            }
        }

        foreach (var operation in Body.Operations)
        {
            foreach (var input in operation.Inputs)
            {
                if (Body.GetProducer(input) is null && !_inputs.Contains(input))
                {
                    throw new ArgumentException(
                        "Every external operation input must be declared as a target input.",
                        nameof(Inputs));
                }
            }
        }
    }
}
