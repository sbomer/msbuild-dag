namespace MSBuild.Dag.Core;

public sealed partial class TargetDefinition
{
    private void Validate()
    {
        var inputLocations = new HashSet<Location>(
            ReferenceEqualityComparer.Instance);
        var inputValues = new HashSet<Value>(
            ReferenceEqualityComparer.Instance);

        foreach (var input in Inputs)
        {
            ArgumentNullException.ThrowIfNull(input);

            if (!inputLocations.Add(input.Location))
            {
                throw new ArgumentException(
                    "A target cannot bind the same input location more than once.",
                    nameof(Inputs));
            }

            inputValues.Add(input.Value);
        }

        var outputLocations = new HashSet<Location>(
            ReferenceEqualityComparer.Instance);

        foreach (var output in Outputs)
        {
            ArgumentNullException.ThrowIfNull(output);

            if (!outputLocations.Add(output.Location))
            {
                throw new ArgumentException(
                    "A target cannot bind the same output location more than once.",
                    nameof(Outputs));
            }

            if (Body.GetProducer(output.Value) is null &&
                !inputValues.Contains(output.Value))
            {
                throw new ArgumentException(
                    "A target output must be produced by the target body or " +
                    "copy a target input.",
                    nameof(Outputs));
            }
        }

        foreach (var operation in Body.Operations)
        {
            foreach (var input in operation.Inputs)
            {
                if (Body.GetProducer(input) is null &&
                    !inputValues.Contains(input))
                {
                    throw new ArgumentException(
                        "Every external target-definition value must be a " +
                        "declared target input.",
                        nameof(Inputs));
                }
            }
        }

        foreach (var result in Results)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (Body.GetProducer(result) is null)
            {
                throw new ArgumentException(
                    "A target-definition result must be produced by its body.",
                    nameof(Results));
            }
        }
    }
}
