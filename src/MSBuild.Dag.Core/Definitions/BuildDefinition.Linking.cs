namespace MSBuild.Dag.Core;

public sealed partial class BuildDefinition
{
    public BuildLinkResult Link()
    {
        var targetSet = new HashSet<TargetDefinition>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            ArgumentNullException.ThrowIfNull(target);

            if (!targetSet.Add(target))
            {
                throw new ArgumentException(
                    "A target definition cannot appear more than once.",
                    nameof(Targets));
            }
        }

        ValidateReferences(targetSet);
        EnsureOrchestrationAcyclic();

        var dependencies = BuildOrderDependencies();
        var linkOrder = GetLinkOrder(dependencies);
        var orderPredecessors = GetOrderPredecessors(dependencies);

        ValidateConditionalWrites();
        ValidateStateOrdering(orderPredecessors);

        var linkedBodies = new Dictionary<TargetDefinition, LinkedTargetBody>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in linkOrder)
        {
            var bindings = new List<Operation>();
            var inputs = new HashSet<Value>(
                ReferenceEqualityComparer.Instance);

            foreach (var read in target.Reads)
            {
                var source = GetReachingValue(
                    target,
                    read.Location,
                    linkOrder,
                    orderPredecessors);
                bindings.Add(read.CreateBinding(source));
                inputs.Add(source);
            }

            linkedBodies.Add(
                target,
                new LinkedTargetBody(
                    inputs.ToArray(),
                    new OperationGraph(
                        bindings.Concat(target.Body.Operations).ToArray())));
        }

        var linkedTargets = new Dictionary<TargetDefinition, Target>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            CreateTarget(target);
        }

        var program = new BuildProgram(
            Targets.Select(target => linkedTargets[target]).ToArray(),
            Evaluation.Initializations
                .Select(initialization => initialization.InitialValue)
                .ToArray());

        return new BuildLinkResult(
            program,
            new Dictionary<TargetDefinition, Target>(
                linkedTargets,
                ReferenceEqualityComparer.Instance),
            Evaluation,
            Targets);

        Target CreateTarget(TargetDefinition definition)
        {
            if (linkedTargets.TryGetValue(definition, out var existing))
            {
                return existing;
            }

            var linkedBody = linkedBodies[definition];
            var outputs = new HashSet<Value>(
                definition.Outputs,
                ReferenceEqualityComparer.Instance);
            outputs.UnionWith(
                definition.Writes.Select(write => write.Value));
            var target = new Target(
                definition.Prelude.Select(CreateTarget).ToArray(),
                new OperationGraph(
                    linkedBody.Inputs,
                    linkedBody.Graph.Operations,
                    outputs.ToArray()),
                definition.Epilogue.Select(CreateTarget).ToArray());
            linkedTargets.Add(definition, target);
            return target;
        }
    }

    private void ValidateReferences(
        IReadOnlySet<TargetDefinition> targets)
    {
        foreach (var target in Targets)
        {
            foreach (var referencedTarget in
                target.Prelude.Concat(target.Epilogue))
            {
                if (!targets.Contains(referencedTarget))
                {
                    throw new ArgumentException(
                        "Every target-definition reference must be part of the build definition.",
                        nameof(Targets));
                }
            }
        }
    }

    private void EnsureOrchestrationAcyclic()
    {
        var visiting = new HashSet<TargetDefinition>(
            ReferenceEqualityComparer.Instance);
        var visited = new HashSet<TargetDefinition>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            Visit(target);
        }

        void Visit(TargetDefinition target)
        {
            if (visited.Contains(target))
            {
                return;
            }

            if (!visiting.Add(target))
            {
                throw new ArgumentException(
                    "Target-definition orchestration must be acyclic.",
                    nameof(Targets));
            }

            foreach (var referencedTarget in
                target.Prelude.Concat(target.Epilogue))
            {
                Visit(referencedTarget);
            }

            visiting.Remove(target);
            visited.Add(target);
        }
    }

    private IReadOnlyDictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>
        BuildOrderDependencies()
    {
        var dependencies =
            new Dictionary<TargetDefinition, List<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            dependencies.Add(target, []);
        }

        foreach (var target in Targets)
        {
            var sequence = GetExecutionSequence(target);

            for (var index = 1; index < sequence.Count; index++)
            {
                var predecessors = dependencies[sequence[index]];
                var predecessor = sequence[index - 1];

                if (!predecessors.Contains(
                    predecessor,
                    ReferenceEqualityComparer.Instance))
                {
                    predecessors.Add(predecessor);
                }
            }
        }

        var result =
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);

        foreach (var (target, predecessors) in dependencies)
        {
            result.Add(target, predecessors.ToArray());
        }

        return result;
    }

    private IReadOnlyList<TargetDefinition> GetLinkOrder(
        IReadOnlyDictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>
            dependencies)
    {
        var result = new List<TargetDefinition>(Targets.Count);
        var visiting = new HashSet<TargetDefinition>(
            ReferenceEqualityComparer.Instance);
        var visited = new HashSet<TargetDefinition>(
            ReferenceEqualityComparer.Instance);
        var path = new List<TargetDefinition>();

        foreach (var target in Targets)
        {
            Visit(target);
        }

        return result;

        void Visit(TargetDefinition target)
        {
            if (visited.Contains(target))
            {
                return;
            }

            if (!visiting.Add(target))
            {
                var start = path.FindIndex(
                    candidate => ReferenceEquals(candidate, target));
                throw new TargetOrderCycleException(
                    path.Skip(start).Append(target).ToArray());
            }

            path.Add(target);

            foreach (var predecessor in dependencies[target])
            {
                Visit(predecessor);
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(target);
            visited.Add(target);
            result.Add(target);
        }
    }

    private IReadOnlyDictionary<TargetDefinition, IReadOnlySet<TargetDefinition>>
        GetOrderPredecessors(
            IReadOnlyDictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>
                dependencies)
    {
        var result =
            new Dictionary<TargetDefinition, IReadOnlySet<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            var predecessors = new HashSet<TargetDefinition>(
                ReferenceEqualityComparer.Instance);
            AddPredecessors(target, predecessors);
            result.Add(target, predecessors);
        }

        return result;

        void AddPredecessors(
            TargetDefinition target,
            HashSet<TargetDefinition> predecessors)
        {
            foreach (var predecessor in dependencies[target])
            {
                if (predecessors.Add(predecessor))
                {
                    AddPredecessors(predecessor, predecessors);
                }
            }
        }
    }

    private void ValidateConditionalWrites()
    {
        foreach (var target in Targets)
        {
            if (target.Writes.Any(write => write.IsConditional))
            {
                throw new ConditionalStateWriteException(target);
            }
        }
    }

    private void ValidateStateOrdering(
        IReadOnlyDictionary<TargetDefinition, IReadOnlySet<TargetDefinition>>
            orderPredecessors)
    {
        for (var firstIndex = 0; firstIndex < Targets.Count; firstIndex++)
        {
            var first = Targets[firstIndex];

            for (var secondIndex = firstIndex + 1;
                secondIndex < Targets.Count;
                secondIndex++)
            {
                var second = Targets[secondIndex];

                if (orderPredecessors[first].Contains(second) ||
                    orderPredecessors[second].Contains(first))
                {
                    continue;
                }

                foreach (var firstWrite in first.Writes)
                {
                    if (second.Reads.Any(
                            read => ReferenceEquals(
                                read.Location,
                                firstWrite.Location)) ||
                        second.Writes.Any(
                            write => ReferenceEquals(
                                write.Location,
                                firstWrite.Location)))
                    {
                        throw new StateConflictException(
                            first,
                            second,
                            firstWrite.Location);
                    }
                }

                foreach (var secondWrite in second.Writes)
                {
                    if (first.Reads.Any(
                        read => ReferenceEquals(
                            read.Location,
                            secondWrite.Location)))
                    {
                        throw new StateConflictException(
                            first,
                            second,
                            secondWrite.Location);
                    }
                }
            }
        }
    }

    private Value GetReachingValue(
        TargetDefinition target,
        StateLocation location,
        IReadOnlyList<TargetDefinition> linkOrder,
        IReadOnlyDictionary<TargetDefinition, IReadOnlySet<TargetDefinition>>
            orderPredecessors)
    {
        TargetDefinition? writer = null;

        foreach (var candidate in linkOrder)
        {
            if (ReferenceEquals(candidate, target))
            {
                break;
            }

            if (orderPredecessors[target].Contains(candidate) &&
                candidate.Writes.Any(
                    write => ReferenceEquals(write.Location, location)))
            {
                writer = candidate;
            }
        }

        if (writer is not null)
        {
            return writer.Writes.Single(
                write => ReferenceEquals(write.Location, location)).Value;
        }

        return Evaluation.GetInitialization(location)?.InitialValue.Value ??
            throw new MissingInitialStateException(target, location);
    }

    private IReadOnlyList<TargetDefinition> GetExecutionSequence(
        TargetDefinition requestedTarget)
    {
        var sequence = new List<TargetDefinition>();
        var ensured = new HashSet<TargetDefinition>(
            ReferenceEqualityComparer.Instance);

        Ensure(requestedTarget);
        return sequence;

        void Ensure(TargetDefinition target)
        {
            if (!ensured.Add(target))
            {
                return;
            }

            foreach (var preludeTarget in target.Prelude)
            {
                Ensure(preludeTarget);
            }

            sequence.Add(target);

            foreach (var epilogueTarget in target.Epilogue)
            {
                Ensure(epilogueTarget);
            }
        }
    }

}
