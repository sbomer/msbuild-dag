namespace MSBuild.Dag.Core;

public sealed class EvaluationSnapshot
{
    public EvaluationSnapshot(
        IReadOnlyDictionary<Location, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var copiedValues = new Dictionary<Location, object?>(
            ReferenceEqualityComparer.Instance);

        foreach (var (location, content) in values)
        {
            ArgumentNullException.ThrowIfNull(location);
            location.ValidateContent(content);

            copiedValues.Add(location, content);
        }

        Values =
            new System.Collections.ObjectModel.ReadOnlyDictionary<
                Location,
                object?>(copiedValues);
    }

    public IReadOnlyDictionary<Location, object?> Values { get; }
}
