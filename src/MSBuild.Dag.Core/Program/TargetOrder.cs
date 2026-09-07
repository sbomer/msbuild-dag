namespace MSBuild.Dag.Core;

internal static class TargetOrder
{
    public static IReadOnlyDictionary<T, IReadOnlyList<T>>
        CreateImmediatePredecessors<T>(
            IReadOnlyList<T> targets,
            Func<T, IReadOnlyList<T>> getPrelude,
            Func<T, IReadOnlyList<T>> getEpilogue)
        where T : class
    {
        var predecessors = new Dictionary<T, List<T>>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in targets)
        {
            predecessors.Add(target, []);
        }

        foreach (var target in targets)
        {
            T? previous = null;

            foreach (var current in Distinct(getPrelude(target))
                .Append(target)
                .Concat(Distinct(getEpilogue(target))))
            {
                if (previous is not null)
                {
                    AddPredecessor(predecessors[current], previous);
                }

                previous = current;
            }
        }

        return predecessors.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<T>)pair.Value.ToArray(),
            (IEqualityComparer<T>)ReferenceEqualityComparer.Instance);

        static IEnumerable<T> Distinct(IEnumerable<T> values)
        {
            var seen = new HashSet<T>(ReferenceEqualityComparer.Instance);

            foreach (var value in values)
            {
                if (seen.Add(value))
                {
                    yield return value;
                }
            }
        }

        static void AddPredecessor(List<T> values, T predecessor)
        {
            if (!values.Contains(
                predecessor,
                ReferenceEqualityComparer.Instance))
            {
                values.Add(predecessor);
            }
        }
    }
}
