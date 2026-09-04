namespace MSBuild.Dag.Core;

public sealed partial class BuildProgram
{
    private readonly Dictionary<Value, Target> _producers =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Target, IReadOnlyCollection<Target>> _predecessors =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Target, IReadOnlyCollection<Target>>
        _orderPredecessors =
            new(ReferenceEqualityComparer.Instance);

    public Target? GetProducer(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _producers.GetValueOrDefault(value);
    }

    public IReadOnlyCollection<Target> GetPredecessors(Target target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!_predecessors.TryGetValue(target, out var predecessors))
        {
            throw new ArgumentException(
                "The target is not part of this program.",
                nameof(target));
        }

        return predecessors;
    }

    public IReadOnlyCollection<Target> GetOrderPredecessors(Target target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!_orderPredecessors.TryGetValue(target, out var predecessors))
        {
            throw new ArgumentException(
                "The target is not part of this program.",
                nameof(target));
        }

        return predecessors;
    }

    private Dictionary<Operation, Target> RegisterTargets()
    {
        var targets = new HashSet<Target>(ReferenceEqualityComparer.Instance);
        var operationOwners = new Dictionary<Operation, Target>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (!targets.Add(target))
            {
                throw new ArgumentException("A target cannot appear more than once.", nameof(Targets));
            }

            foreach (var operation in GetOperations(target.Body))
            {
                if (!operationOwners.TryAdd(operation, target))
                {
                    throw new ArgumentException(
                        "An operation cannot belong to more than one target.",
                        nameof(Targets));
                }
            }

            foreach (var output in target.Outputs)
            {
                if (!_producers.TryAdd(output, target))
                {
                    throw new ArgumentException(
                        "A value cannot be exported by more than one target.",
                        nameof(Targets));
                }
            }
        }

        return operationOwners;

        static IEnumerable<Operation> GetOperations(OperationGraph graph)
        {
            foreach (var operation in graph.Operations)
            {
                yield return operation;

                if (operation is ConditionalRegionOperation conditional)
                {
                    foreach (var nested in GetOperations(conditional.WhenTrue))
                    {
                        yield return nested;
                    }

                    foreach (var nested in GetOperations(conditional.WhenFalse))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }

    private void ValidateInitialValues(
        IReadOnlyDictionary<Operation, Target> operationOwners)
    {
        var values = new HashSet<Value>(
            ReferenceEqualityComparer.Instance);
        var operationOutputs = new HashSet<Value>(
            operationOwners.Keys.SelectMany(operation => operation.Outputs),
            ReferenceEqualityComparer.Instance);

        foreach (var initialValue in InitialValues)
        {
            ArgumentNullException.ThrowIfNull(initialValue);

            if (!values.Add(initialValue.Value))
            {
                throw new ArgumentException(
                    "A value cannot be initialized more than once.",
                    nameof(InitialValues));
            }

            if (operationOutputs.Contains(initialValue.Value))
            {
                throw new ArgumentException(
                    "An initial value cannot also be produced by an operation.",
                    nameof(InitialValues));
            }
        }
    }

    private void ValidateTargetReferences()
    {
        var targets = new HashSet<Target>(Targets, ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            foreach (var referencedTarget in target.Prelude.Concat(target.Epilogue))
            {
                if (!targets.Contains(referencedTarget))
                {
                    throw new ArgumentException(
                        "Every target reference must be part of the program.",
                        nameof(Targets));
                }
            }
        }
    }

    private void ValidateCrossTargetConnections(
        IReadOnlyDictionary<Operation, Target> operationOwners)
    {
        var operationProducers = new Dictionary<Value, Target>(
            ReferenceEqualityComparer.Instance);

        foreach (var (operation, owner) in operationOwners)
        {
            foreach (var output in operation.Outputs)
            {
                if (!operationProducers.TryAdd(output, owner))
                {
                    throw new ArgumentException(
                        "A value cannot have multiple operation producers.",
                        nameof(Targets));
                }
            }
        }

        foreach (var target in Targets)
        {
            foreach (var input in target.Inputs)
            {
                if (operationProducers.TryGetValue(input, out var producer) &&
                    !ReferenceEquals(producer, target) &&
                    !producer.Exports(input))
                {
                    throw new ArgumentException(
                        "A cross-target value must be exported by its producing target.",
                        nameof(Targets));
                }
            }
        }
    }

    private void BuildPrecedence()
    {
        var predecessors = new Dictionary<Target, HashSet<Target>>(
            ReferenceEqualityComparer.Instance);
        var orderPredecessors = new Dictionary<Target, HashSet<Target>>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            predecessors.Add(
                target,
                new HashSet<Target>(ReferenceEqualityComparer.Instance));
            orderPredecessors.Add(
                target,
                new HashSet<Target>(ReferenceEqualityComparer.Instance));
        }

        foreach (var target in Targets)
        {
            foreach (var input in target.Inputs)
            {
                if (_producers.TryGetValue(input, out var producer))
                {
                    AddPrecedence(producer, target, isOrdering: false);
                }
            }
        }

        foreach (var target in Targets)
        {
            var sequence = target.Prelude
                .Append(target)
                .Concat(target.Epilogue)
                .ToArray();

            for (var index = 1; index < sequence.Length; index++)
            {
                AddPrecedence(
                    sequence[index - 1],
                    sequence[index],
                    isOrdering: true);
            }
        }

        foreach (var (target, targetPredecessors) in predecessors)
        {
            _predecessors.Add(target, targetPredecessors.ToArray());
            _orderPredecessors.Add(
                target,
                orderPredecessors[target].ToArray());
        }

        void AddPrecedence(
            Target before,
            Target after,
            bool isOrdering)
        {
            predecessors[after].Add(before);

            if (isOrdering)
            {
                orderPredecessors[after].Add(before);
            }
        }
    }

    private void EnsureAcyclic()
    {
        var visiting = new HashSet<Target>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<Target>(ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            Visit(target, visiting, visited);
        }
    }

    private void Visit(
        Target target,
        HashSet<Target> visiting,
        HashSet<Target> visited)
    {
        if (visited.Contains(target))
        {
            return;
        }

        if (!visiting.Add(target))
        {
            throw new ArgumentException(
                "The build program order must be acyclic.",
                nameof(Targets));
        }

        foreach (var predecessor in _predecessors[target])
        {
            Visit(predecessor, visiting, visited);
        }

        visiting.Remove(target);
        visited.Add(target);
    }
}
