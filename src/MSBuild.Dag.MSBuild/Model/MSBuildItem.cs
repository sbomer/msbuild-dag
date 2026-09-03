using System.Collections.ObjectModel;

namespace MSBuild.Dag.MSBuild;

public sealed class MSBuildItem
{
    public MSBuildItem(
        string identity,
        IEnumerable<KeyValuePair<string, string>>? metadata = null)
    {
        ArgumentNullException.ThrowIfNull(identity);

        Identity = identity;
        Metadata = new ReadOnlyDictionary<string, string>(
            metadata?.ToDictionary(
                static pair => pair.Key,
                static pair => pair.Value,
                StringComparer.OrdinalIgnoreCase) ??
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase));
    }

    public string Identity { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public string GetMetadataValue(string name) =>
        name.Equals("Identity", StringComparison.OrdinalIgnoreCase)
            ? Identity
            : Metadata.GetValueOrDefault(name, string.Empty);

    public MSBuildItem WithMetadata(
        IReadOnlyDictionary<string, string> metadata)
    {
        var updated = new Dictionary<string, string>(
            Metadata,
            StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in metadata)
        {
            if (value.Length == 0)
            {
                updated.Remove(name);
            }
            else
            {
                updated[name] = value;
            }
        }

        return new MSBuildItem(Identity, updated);
    }

    public override string ToString()
    {
        if (Metadata.Count == 0)
        {
            return Identity;
        }

        return $"{Identity} {{{string.Join(
            ", ",
            Metadata
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(static pair => $"{pair.Key}={pair.Value}"))}}}";
    }
}
