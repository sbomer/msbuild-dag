namespace MSBuild.Dag.Core;

public sealed class BuildGraph
{
    private readonly Dictionary<Value, Operation> _producers =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Operation, IReadOnlyCollection<Operation>> _dependencies =
        new(ReferenceEqualityComparer.Instance);

    public BuildGraph(IReadOnlyList<Operation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        Operations = operations.ToArray();

        RegisterOperations();
        BuildDependencies();
        EnsureAcyclic();
    }

    public IReadOnlyList<Operation> Operations { get; }

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
}
