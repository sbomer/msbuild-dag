namespace MSBuild.Dag.Core;

public sealed partial class OperationGraph
{
    private readonly Dictionary<Value, Operation> _producers =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Operation, IReadOnlyCollection<Operation>> _dependencies =
        new(ReferenceEqualityComparer.Instance);

    public Operation? GetProducer(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _producers.GetValueOrDefault(value);
    }

    public IReadOnlyCollection<Operation> GetDependencies(Operation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!_dependencies.TryGetValue(operation, out var dependencies))
        {
            throw new ArgumentException("The operation is not part of this graph.", nameof(operation));
        }

        return dependencies;
    }

    private void RegisterOperations()
    {
        var operations = new HashSet<Operation>(ReferenceEqualityComparer.Instance);

        foreach (var operation in Operations)
        {
            ArgumentNullException.ThrowIfNull(operation);

            if (!operations.Add(operation))
            {
                throw new ArgumentException("An operation cannot appear more than once.", nameof(Operations));
            }

            foreach (var output in operation.Outputs)
            {
                ArgumentNullException.ThrowIfNull(output);

                if (!_producers.TryAdd(output, operation))
                {
                    throw new ArgumentException("A value cannot have multiple producers.", nameof(Operations));
                }
            }
        }
    }

    private void BuildDependencies()
    {
        foreach (var operation in Operations)
        {
            var dependencies = new HashSet<Operation>(ReferenceEqualityComparer.Instance);

            foreach (var input in operation.Inputs)
            {
                ArgumentNullException.ThrowIfNull(input);

                if (_producers.TryGetValue(input, out var producer))
                {
                    dependencies.Add(producer);
                }
            }

            _dependencies.Add(operation, dependencies.ToArray());
        }
    }

    private void EnsureAcyclic()
    {
        var visiting = new HashSet<Operation>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<Operation>(ReferenceEqualityComparer.Instance);

        foreach (var operation in Operations)
        {
            Visit(operation, visiting, visited);
        }
    }

    private void Visit(
        Operation operation,
        HashSet<Operation> visiting,
        HashSet<Operation> visited)
    {
        if (visited.Contains(operation))
        {
            return;
        }

        if (!visiting.Add(operation))
        {
            throw new ArgumentException("The operation graph must be acyclic.", nameof(Operations));
        }

        foreach (var dependency in _dependencies[operation])
        {
            Visit(dependency, visiting, visited);
        }

        visiting.Remove(operation);
        visited.Add(operation);
    }

    private Value[] GetExternalInputs()
    {
        var result = new List<Value>();
        var seen = new HashSet<Value>(ReferenceEqualityComparer.Instance);

        foreach (var operation in Operations)
        {
            foreach (var input in operation.Inputs)
            {
                if (!_producers.ContainsKey(input) && seen.Add(input))
                {
                    result.Add(input);
                }
            }
        }

        return result.ToArray();
    }

    private Value[] GetUnconsumedOutputs()
    {
        var consumed = new HashSet<Value>(
            Operations.SelectMany(operation => operation.Inputs),
            ReferenceEqualityComparer.Instance);

        return Operations
            .SelectMany(operation => operation.Outputs)
            .Where(output => !consumed.Contains(output))
            .ToArray();
    }

    private static Value[] CopyBoundary(
        IReadOnlyList<Value> values,
        string parameterName)
    {
        var result = values.ToArray();
        var seen = new HashSet<Value>(ReferenceEqualityComparer.Instance);

        foreach (var value in result)
        {
            ArgumentNullException.ThrowIfNull(value, parameterName);

            if (!seen.Add(value))
            {
                throw new ArgumentException(
                    "An operation-graph boundary cannot contain the same value more than once.",
                    parameterName);
            }
        }

        return result;
    }

    private void ValidateBoundary()
    {
        var inputs = new HashSet<Value>(
            Inputs,
            ReferenceEqualityComparer.Instance);

        foreach (var input in Inputs)
        {
            if (_producers.ContainsKey(input))
            {
                throw new ArgumentException(
                    "An operation-graph input cannot be produced inside the graph.",
                    nameof(Inputs));
            }
        }

        foreach (var operation in Operations)
        {
            foreach (var input in operation.Inputs)
            {
                if (!_producers.ContainsKey(input) && !inputs.Contains(input))
                {
                    throw new ArgumentException(
                        "Every external operation input must be declared as an operation-graph input.",
                        nameof(Inputs));
                }
            }
        }

        foreach (var output in Outputs)
        {
            if (!_producers.ContainsKey(output) && !inputs.Contains(output))
            {
                throw new ArgumentException(
                    "An operation-graph output must be an input or be produced inside the graph.",
                    nameof(Outputs));
            }
        }
    }

    private void ValidateNestedScopes()
    {
        var operations = new HashSet<Operation>(
            ReferenceEqualityComparer.Instance);
        RegisterOperationTree(this, operations);

        ValidateGraph(this);

        static void ValidateGraph(OperationGraph graph)
        {
            var localValues = new HashSet<Value>(
                graph.Inputs,
                ReferenceEqualityComparer.Instance);
            localValues.UnionWith(
                graph.Operations.SelectMany(operation => operation.Outputs));

            foreach (var conditional in
                graph.Operations.OfType<ConditionalRegionOperation>())
            {
                var whenTrueValues = GetScopedValues(conditional.WhenTrue);
                var whenFalseValues = GetScopedValues(conditional.WhenFalse);

                if (whenTrueValues.Overlaps(localValues) ||
                    whenFalseValues.Overlaps(localValues) ||
                    whenTrueValues.Overlaps(whenFalseValues))
                {
                    throw new ArgumentException(
                        "Conditional branch values must be local to one branch.",
                        nameof(Operations));
                }

                ValidateGraph(conditional.WhenTrue);
                ValidateGraph(conditional.WhenFalse);
            }
        }

        static HashSet<Value> GetScopedValues(OperationGraph graph)
        {
            var result = new HashSet<Value>(
                graph.Inputs,
                ReferenceEqualityComparer.Instance);
            result.UnionWith(
                graph.Operations.SelectMany(operation => operation.Outputs));

            foreach (var conditional in
                graph.Operations.OfType<ConditionalRegionOperation>())
            {
                result.UnionWith(GetScopedValues(conditional.WhenTrue));
                result.UnionWith(GetScopedValues(conditional.WhenFalse));
            }

            return result;
        }

        static void RegisterOperationTree(
            OperationGraph graph,
            HashSet<Operation> operations)
        {
            foreach (var operation in graph.Operations)
            {
                if (!operations.Add(operation))
                {
                    throw new ArgumentException(
                        "An operation cannot appear in more than one graph scope.",
                        nameof(Operations));
                }

                if (operation is ConditionalRegionOperation conditional)
                {
                    RegisterOperationTree(conditional.WhenTrue, operations);
                    RegisterOperationTree(conditional.WhenFalse, operations);
                }
            }
        }
    }
}
