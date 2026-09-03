namespace MSBuild.Dag.Core;

public sealed partial class BuildLinkResult
{
    public IReadOnlyDictionary<StateLocation, Value> GetStateAfter(
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

        var state = new Dictionary<StateLocation, Value>(
            ReferenceEqualityComparer.Instance);

        foreach (var initialization in _evaluation.Initializations)
        {
            state.Add(
                initialization.Location,
                initialization.InitialValue.Value);
        }

        var ensured = new HashSet<TargetDefinition>(
            ReferenceEqualityComparer.Instance);
        Ensure(requestedTarget);
        return state;

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

            foreach (var write in target.Writes)
            {
                var linkedTarget = Targets[target];
                var outputIndex = IndexOfReference(
                    linkedTarget.Body.Outputs,
                    write.Value);
                state[write.Location] = linkedTarget.Outputs[outputIndex];
            }

            foreach (var epilogueTarget in target.Epilogue)
            {
                Ensure(epilogueTarget);
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
                "A linked state write must be a target body output.");
        }
    }
}
