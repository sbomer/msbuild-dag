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
}
