namespace MSBuild.Dag.Core;

public sealed partial class BuildGraph
{
    private readonly Dictionary<Value, Target> _producers =
        new(ReferenceEqualityComparer.Instance);

    private readonly Dictionary<Target, IReadOnlyCollection<Target>> _dependencies =
        new(ReferenceEqualityComparer.Instance);

    public Target? GetProducer(Value value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return _producers.GetValueOrDefault(value);
    }

    public IReadOnlyCollection<Target> GetDependencies(Target target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!_dependencies.TryGetValue(target, out var dependencies))
        {
            throw new ArgumentException("The target is not part of this graph.", nameof(target));
        }

        return dependencies;
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

            foreach (var operation in target.Body.Operations)
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
    }

    private void RegisterExplicitDependencies()
    {
        var targets = new HashSet<Target>(Targets, ReferenceEqualityComparer.Instance);

        foreach (var dependency in ExplicitDependencies)
        {
            ArgumentNullException.ThrowIfNull(dependency);
            ArgumentNullException.ThrowIfNull(dependency.Prerequisite);
            ArgumentNullException.ThrowIfNull(dependency.Dependent);

            if (!targets.Contains(dependency.Prerequisite) ||
                !targets.Contains(dependency.Dependent))
            {
                throw new ArgumentException(
                    "Both ends of an explicit dependency must be part of the graph.",
                    nameof(ExplicitDependencies));
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

    private void BuildDependencies()
    {
        var dependencies = new Dictionary<Target, HashSet<Target>>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            dependencies.Add(
                target,
                new HashSet<Target>(ReferenceEqualityComparer.Instance));
        }

        foreach (var target in Targets)
        {
            foreach (var input in target.Inputs)
            {
                if (_producers.TryGetValue(input, out var producer))
                {
                    dependencies[target].Add(producer);
                }
            }
        }

        foreach (var dependency in ExplicitDependencies)
        {
            dependencies[dependency.Dependent].Add(dependency.Prerequisite);
        }

        foreach (var (target, targetDependencies) in dependencies)
        {
            _dependencies.Add(target, targetDependencies.ToArray());
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
            throw new ArgumentException("The build graph must be acyclic.", nameof(Targets));
        }

        foreach (var dependency in _dependencies[target])
        {
            Visit(dependency, visiting, visited);
        }

        visiting.Remove(target);
        visited.Add(target);
    }
}
