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

        var dependencies = BuildOrderDependencies();
        var linkOrder = GetLinkOrder(dependencies);
        var orderPredecessors = GetOrderPredecessors(dependencies);

        ValidateConditionalWrites();
        ValidateStateOrdering(orderPredecessors);

        var linkedBodies = new Dictionary<TargetDefinition, LinkedTargetBody>(
            ReferenceEqualityComparer.Instance);
        var exports =
            new Dictionary<TargetDefinition, IReadOnlyDictionary<Value, Value>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in linkOrder)
        {
            var inputs = new List<Value>(target.Reads.Count);
            var parameters = new List<Value>(target.Reads.Count);

            foreach (var read in target.Reads)
            {
                var source = GetReachingValue(
                    target,
                    read.Location,
                    linkOrder,
                    orderPredecessors,
                    exports);
                inputs.Add(source);
                parameters.Add(read.Value);
            }

            var bodyOutputs = new List<Value>(target.Outputs);

            foreach (var write in target.Writes)
            {
                if (!bodyOutputs.Contains(
                    write.Value,
                    ReferenceEqualityComparer.Instance))
                {
                    bodyOutputs.Add(write.Value);
                }
            }

            var externalOutputs = bodyOutputs
                .Select(output => output.CreateSibling())
                .ToArray();
            var targetExports = new Dictionary<Value, Value>(
                ReferenceEqualityComparer.Instance);

            for (var index = 0; index < bodyOutputs.Count; index++)
            {
                targetExports.Add(bodyOutputs[index], externalOutputs[index]);
            }

            exports.Add(target, targetExports);
            linkedBodies.Add(
                target,
                new LinkedTargetBody(
                    inputs.ToArray(),
                    new OperationGraph(
                        parameters,
                        target.Body.Operations,
                        bodyOutputs),
                    externalOutputs));
        }

        var linkedTargets = new Dictionary<TargetDefinition, Target>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            var linkedBody = linkedBodies[target];
            linkedTargets.Add(
                target,
                new Target(
                    [],
                    linkedBody.Inputs,
                    linkedBody.Graph,
                    linkedBody.Outputs,
                    []));
        }

        foreach (var target in Targets)
        {
            linkedTargets[target].SetOrchestration(
                GetPrelude(target)
                    .Select(reference => linkedTargets[reference])
                    .ToArray(),
                GetEpilogue(target)
                    .Select(reference => linkedTargets[reference])
                    .ToArray());
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
            this);
    }

    private void ValidateReferences(
        IReadOnlySet<TargetDefinition> targets)
    {
        foreach (var target in Targets)
        {
            foreach (var referencedTarget in
                GetPrelude(target).Concat(GetEpilogue(target)))
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
            var sequence = GetPrelude(target)
                .Append(target)
                .Concat(GetEpilogue(target))
                .ToArray();

            for (var index = 1; index < sequence.Length; index++)
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
            orderPredecessors,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyDictionary<Value, Value>> exports)
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
            var bodyValue = writer.Writes.Single(
                write => ReferenceEquals(write.Location, location)).Value;
            return exports[writer][bodyValue];
        }

        return Evaluation.GetInitialization(location)?.InitialValue.Value ??
            throw new MissingInitialStateException(target, location);
    }

}
