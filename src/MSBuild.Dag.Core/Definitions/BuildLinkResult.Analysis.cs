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
                state[write.Location] = write.Value;
            }

            foreach (var epilogueTarget in target.Epilogue)
            {
                Ensure(epilogueTarget);
            }
        }
    }
}
