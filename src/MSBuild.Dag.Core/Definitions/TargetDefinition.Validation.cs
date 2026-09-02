namespace MSBuild.Dag.Core;

public sealed partial class TargetDefinition
{
    private void Validate()
    {
        var reads = new HashSet<StateLocation>(
            ReferenceEqualityComparer.Instance);
        var readValues = new HashSet<Value>(
            ReferenceEqualityComparer.Instance);

        foreach (var read in Reads)
        {
            ArgumentNullException.ThrowIfNull(read);

            if (!reads.Add(read.Location))
            {
                throw new ArgumentException(
                    "A target cannot read the same state location more than once.",
                    nameof(Reads));
            }

            readValues.Add(read.Value);
        }

        var writes = new HashSet<StateLocation>(
            ReferenceEqualityComparer.Instance);

        foreach (var write in Writes)
        {
            ArgumentNullException.ThrowIfNull(write);

            if (!writes.Add(write.Location))
            {
                throw new ArgumentException(
                    "A target cannot write the same state location more than once.",
                    nameof(Writes));
            }

            if (Body.GetProducer(write.Value) is null &&
                !readValues.Contains(write.Value))
            {
                throw new ArgumentException(
                    "A state write must be produced by the target body or copy a state read.",
                    nameof(Writes));
            }
        }

        foreach (var operation in Body.Operations)
        {
            foreach (var input in operation.Inputs)
            {
                if (Body.GetProducer(input) is null &&
                    !readValues.Contains(input))
                {
                    throw new ArgumentException(
                        "Every external target-definition input must be a state read.",
                        nameof(Reads));
                }
            }
        }

        foreach (var output in Outputs)
        {
            ArgumentNullException.ThrowIfNull(output);

            if (Body.GetProducer(output) is null)
            {
                throw new ArgumentException(
                    "A target-definition output must be produced by its body.",
                    nameof(Outputs));
            }
        }
    }
}
