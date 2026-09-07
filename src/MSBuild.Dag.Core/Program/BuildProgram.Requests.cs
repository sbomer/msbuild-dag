namespace MSBuild.Dag.Core;

public sealed partial class BuildProgram
{
    public IReadOnlyList<Target> GetRequestOrder(
        Target requestedTarget,
        IReadOnlySet<Target>? completedTargets = null) =>
        GetRequestOrder(
            requestedTarget,
            completedTargets,
            validateStateConflicts: true);

    internal IReadOnlyList<Target> GetRequestOrderForStateProjection(
        Target requestedTarget) =>
        GetRequestOrder(
            requestedTarget,
            completedTargets: null,
            validateStateConflicts: false);

    private IReadOnlyList<Target> GetRequestOrder(
        Target requestedTarget,
        IReadOnlySet<Target>? completedTargets,
        bool validateStateConflicts)
    {
        ArgumentNullException.ThrowIfNull(requestedTarget);

        if (!Targets.Contains(
            requestedTarget,
            ReferenceEqualityComparer.Instance))
        {
            throw new ArgumentException(
                "The requested target is not part of the program.",
                nameof(requestedTarget));
        }

        var initiallyCompleted = completedTargets is null
            ? new HashSet<Target>(ReferenceEqualityComparer.Instance)
            : new HashSet<Target>(
                completedTargets,
                ReferenceEqualityComparer.Instance);

        if (initiallyCompleted.Any(
            target => !Targets.Contains(
                target,
                ReferenceEqualityComparer.Instance)))
        {
            throw new ArgumentException(
                "Completed targets must be part of the program.",
                nameof(completedTargets));
        }

        foreach (var target in initiallyCompleted)
        {
            if (GetPredecessors(target).Any(
                predecessor => !initiallyCompleted.Contains(predecessor)))
            {
                throw new ArgumentException(
                    "Completed targets must form a predecessor-closed set.",
                    nameof(completedTargets));
            }
        }

        var planningCompleted = new HashSet<Target>(
            initiallyCompleted,
            ReferenceEqualityComparer.Instance);
        var result = PlanRequest(requestedTarget, planningCompleted);
        var available = new HashSet<Target>(
            initiallyCompleted,
            ReferenceEqualityComparer.Instance);

        foreach (var target in result)
        {
            foreach (var predecessor in GetPredecessors(target))
            {
                if (!available.Contains(predecessor))
                {
                    throw new InvalidOperationException(
                        "The target request violates the program order because " +
                        "an unfinished predecessor was not activated first.");
                }
            }
            available.Add(target);
        }

        if (validateStateConflicts)
        {
            foreach (var conflict in StateConflicts)
            {
                if (available.Contains(conflict.FirstTarget) &&
                    available.Contains(conflict.SecondTarget))
                {
                    throw new RequestStateConflictException(conflict);
                }
            }
        }

        return result.ToArray();
    }

    private static IReadOnlyList<Target> PlanRequest(
        Target requestedTarget,
        HashSet<Target> completed)
    {
        var result = new List<Target>();
        var stack = new List<RequestEntry>
        {
            new(requestedTarget, null),
        };

        while (stack.Count > 0)
        {
            var entry = stack[^1];

            if (completed.Contains(entry.Target))
            {
                stack.RemoveAt(stack.Count - 1);
                continue;
            }

            if (entry.ExecuteBody)
            {
                stack.RemoveAt(stack.Count - 1);
                completed.Add(entry.Target);
                result.Add(entry.Target);
                continue;
            }

            stack.RemoveAt(stack.Count - 1);
            Push(entry.Target.Epilogue, entry.Parent, isEpilogue: true);
            entry.ExecuteBody = true;
            stack.Add(entry);
            Push(entry.Target.Prelude, entry, isEpilogue: false);
        }

        return result;

        void Push(
            IReadOnlyList<Target> targets,
            RequestEntry? parent,
            bool isEpilogue)
        {
            for (var index = targets.Count - 1; index >= 0; index--)
            {
                var target = targets[index];

                if (completed.Contains(target))
                {
                    continue;
                }

                if (isEpilogue)
                {
                    if (stack.Any(
                        candidate => ReferenceEquals(
                            candidate.Target,
                            target)))
                    {
                        continue;
                    }
                }
                else if (HasAncestor(parent, target))
                {
                    throw new InvalidOperationException(
                        "The target request contains a circular Prelude dependency.");
                }

                stack.Add(new RequestEntry(target, parent));
            }
        }

        static bool HasAncestor(RequestEntry? entry, Target target)
        {
            while (entry is not null)
            {
                if (ReferenceEquals(entry.Target, target))
                {
                    return true;
                }

                entry = entry.Parent;
            }

            return false;
        }
    }

    private sealed class RequestEntry(
        Target target,
        RequestEntry? parent)
    {
        public Target Target { get; } = target;

        public RequestEntry? Parent { get; } = parent;

        public bool ExecuteBody { get; set; }
    }
}
