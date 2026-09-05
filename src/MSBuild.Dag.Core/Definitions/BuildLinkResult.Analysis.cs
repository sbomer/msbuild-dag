namespace MSBuild.Dag.Core;

public sealed partial class BuildLinkResult
{
    public IReadOnlyDictionary<Location, Value> GetStateAfter(
        TargetDefinition requestedTarget)
    {
        if (!_definitions.Contains(
            requestedTarget,
            ReferenceEqualityComparer.Instance))
        {
            throw new ArgumentException(
                "The requested target definition is not part of this build.",
                nameof(requestedTarget));
        }

        var state = new Dictionary<Location, Value>(
            ReferenceEqualityComparer.Instance);

        foreach (var initialization in _evaluation.Initializations)
        {
            state.Add(
                initialization.Location,
                initialization.InitialValue.Value);
        }

        var activated = new HashSet<TargetDefinition>(
            ReferenceEqualityComparer.Instance);
        Activate(requestedTarget);
        var completed = new HashSet<Target>(
            ReferenceEqualityComparer.Instance);
        var remaining = new HashSet<TargetDefinition>(
            activated,
            ReferenceEqualityComparer.Instance);

        while (remaining.Count > 0)
        {
            var target = _definitions.FirstOrDefault(
                candidate =>
                    remaining.Contains(candidate) &&
                    Program.GetPredecessors(Targets[candidate])
                        .All(completed.Contains));

            if (target is null)
            {
                throw new InvalidOperationException(
                    "The activated target definitions cannot be ordered.");
            }

            foreach (var output in target.Outputs)
            {
                var linkedTarget = Targets[target];
                var outputIndex = IndexOfReference(
                    linkedTarget.Body.Outputs,
                    output.Value);
                state[output.Location] = linkedTarget.Outputs[outputIndex];
            }

            completed.Add(Targets[target]);
            remaining.Remove(target);
        }

        return state;

        void Activate(TargetDefinition target)
        {
            if (!activated.Add(target))
            {
                return;
            }

            foreach (var referencedTarget in
                _definition.GetPrelude(target)
                    .Concat(_definition.GetEpilogue(target)))
            {
                Activate(referencedTarget);
            }
        }

        static int IndexOfReference(
            IReadOnlyList<Value> values,
            Value expected)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (ReferenceEquals(values[index], expected))
                {
                    return index;
                }
            }

            throw new InvalidOperationException(
                "A linked target output must be a target body output.");
        }
    }
}
