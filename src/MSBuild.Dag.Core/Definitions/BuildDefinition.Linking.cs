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

        var dependencies = _orderPredecessors;
        var linkOrder = GetLinkOrder(dependencies);
        var orderPredecessors = GetOrderPredecessors(dependencies);

        ValidateConditionalOutputs();
        ValidateStateOrdering(orderPredecessors);

        var initialValues =
            new Dictionary<Location, Value>(
                ReferenceEqualityComparer.Instance);
        var initialContents =
            new Dictionary<Value, object?>(
                ReferenceEqualityComparer.Instance);

        foreach (var (location, content) in Evaluation.Values)
        {
            var value = location.CreateValue();
            initialValues.Add(location, value);
            initialContents.Add(value, content);
        }

        var inputValues = new Dictionary<Location, Value>(
            ReferenceEqualityComparer.Instance);

        foreach (var location in Inputs)
        {
            inputValues.Add(location, location.CreateValue());
        }

        var entryValues = new Dictionary<Location, Value>(
            initialValues,
            ReferenceEqualityComparer.Instance);

        foreach (var (location, value) in inputValues)
        {
            entryValues.Add(location, value);
        }

        var linkedBodies = new Dictionary<TargetDefinition, LinkedTargetBody>(
            ReferenceEqualityComparer.Instance);
        var exports =
            new Dictionary<TargetDefinition, IReadOnlyDictionary<Value, Value>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in linkOrder)
        {
            var inputs = new List<Value>(target.Inputs.Count);
            var parameters = new List<Value>(target.Inputs.Count);

            foreach (var input in target.Inputs)
            {
                var source = GetReachingValue(
                    target,
                    input.Location,
                    orderPredecessors,
                    entryValues,
                    exports);
                inputs.Add(source);
                parameters.Add(input.Value);
            }

            var bodyOutputs = new List<Value>(target.Outputs.Count);

            foreach (var output in target.Outputs)
            {
                if (!bodyOutputs.Contains(
                    output.Value,
                    ReferenceEqualityComparer.Instance))
                {
                    bodyOutputs.Add(output.Value);
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

        var linkedOrderPredecessors =
            new Dictionary<Target, IReadOnlySet<Target>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in Targets)
        {
            linkedOrderPredecessors.Add(
                linkedTargets[target],
                new HashSet<Target>(
                    GetOrderPredecessors(target)
                        .Select(predecessor => linkedTargets[predecessor]),
                    ReferenceEqualityComparer.Instance));
        }

        var program = new BuildProgram(
            Targets.Select(target => linkedTargets[target]).ToArray(),
            initialContents,
            inputValues.Values.ToArray(),
            linkedOrderPredecessors);

        return new BuildLinkResult(
            program,
            new Dictionary<TargetDefinition, Target>(
                linkedTargets,
                ReferenceEqualityComparer.Instance),
            this,
            initialValues,
            inputValues);
    }

    private void ValidateReferences(
        IReadOnlySet<TargetDefinition> targets)
    {
        foreach (var target in Targets)
        {
            foreach (var referencedTarget in
                GetPrelude(target)
                    .Concat(GetEpilogue(target))
                    .Concat(GetOrderPredecessors(target)))
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

    private void ValidateConditionalOutputs()
    {
        foreach (var target in Targets)
        {
            if (target.Outputs.Any(
                output => output.Location is not null &&
                    output.IsConditional))
            {
                throw new ConditionalTargetOutputException(target);
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

                foreach (var firstOutput in first.Outputs
                    .Where(output => output.Location is not null))
                {
                    if (second.Inputs.Any(
                        input => ReferenceEquals(
                            input.Location,
                            firstOutput.Location)) ||
                        second.Outputs.Any(
                        output => ReferenceEquals(
                            output.Location,
                            firstOutput.Location)))
                    {
                        throw new StateConflictException(
                            first,
                            second,
                        firstOutput.Location!);
                    }
                }

                foreach (var secondOutput in second.Outputs
                    .Where(output => output.Location is not null))
                {
                    if (first.Inputs.Any(
                        input => ReferenceEquals(
                            input.Location,
                            secondOutput.Location)))
                    {
                        throw new StateConflictException(
                            first,
                            second,
                        secondOutput.Location!);
                    }
                }
            }
        }
    }

    private Value GetReachingValue(
        TargetDefinition target,
        Location location,
        IReadOnlyDictionary<TargetDefinition, IReadOnlySet<TargetDefinition>>
            orderPredecessors,
        IReadOnlyDictionary<Location, Value> initialValues,
        IReadOnlyDictionary<
            TargetDefinition,
            IReadOnlyDictionary<Value, Value>> exports)
    {
        var producers = orderPredecessors[target]
            .Where(
                candidate => candidate.Outputs.Any(
                    output => ReferenceEquals(output.Location, location)))
            .ToArray();
        var producer = producers.SingleOrDefault(
            candidate => producers.All(
                other =>
                    ReferenceEquals(candidate, other) ||
                    orderPredecessors[candidate].Contains(other)));

        if (producer is not null)
        {
            var bodyValue = producer.Outputs.Single(
                output => ReferenceEquals(output.Location, location)).Value;
            return exports[producer][bodyValue];
        }

        return initialValues.TryGetValue(location, out var initialValue)
            ? initialValue
            : throw new MissingTargetInputException(target, location);
    }

}
