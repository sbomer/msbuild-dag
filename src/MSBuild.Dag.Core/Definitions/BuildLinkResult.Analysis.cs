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

        foreach (var (location, value) in InitialValues)
        {
            state.Add(location, value);
        }

        foreach (var (location, value) in Inputs)
        {
            state.Add(location, value);
        }

        var definitionsByTarget =
            new Dictionary<Target, TargetDefinition>(
                ReferenceEqualityComparer.Instance);

        foreach (var definition in _definitions)
        {
            definitionsByTarget.Add(Targets[definition], definition);
        }

        foreach (var linkedTarget in
            Program.GetRequestOrder(Targets[requestedTarget]))
        {
            var target = definitionsByTarget[linkedTarget];

            foreach (var output in target.Outputs)
            {
                if (output.Location is null)
                {
                    continue;
                }

                var outputIndex = IndexOfReference(
                    linkedTarget.Body.Outputs,
                    output.Value);
                state[output.Location] = linkedTarget.Outputs[outputIndex];
            }
        }

        return state;

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
