using MSBuild.Dag.Core;

namespace MSBuild.Dag.Execution;

public sealed class BuildProgramExecutor
{
    private readonly BuildProgram _program;
    private readonly ValueStore _values;
    private readonly Func<Operation, ValueStore, CancellationToken, ValueTask>
        _executeOperation;
    private readonly HashSet<Target> _completed =
        new(ReferenceEqualityComparer.Instance);
    private readonly OperationGraphExecutor _operationExecutor = new();

    public BuildProgramExecutor(
        BuildProgram program,
        ValueStore values,
        Func<Operation, ValueStore, CancellationToken, ValueTask>
            executeOperation)
    {
        ArgumentNullException.ThrowIfNull(program);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(executeOperation);

        _program = program;
        _values = values;
        _executeOperation = executeOperation;
    }

    public async ValueTask ExecuteAsync(
        Target requestedTarget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestedTarget);

        if (!_program.Targets.Contains(
            requestedTarget,
            ReferenceEqualityComparer.Instance))
        {
            throw new ArgumentException(
                "The requested target is not part of the program.",
                nameof(requestedTarget));
        }

        var activated = GetActivatedTargets(requestedTarget);

        foreach (var target in activated)
        {
            foreach (var predecessor in _program.GetPredecessors(target))
            {
                if (!_completed.Contains(predecessor) &&
                    !activated.Contains(predecessor))
                {
                    throw new InvalidOperationException(
                        "The target request violates the program order because " +
                        "an unfinished predecessor was not activated.");
                }
            }
        }

        await EnsureAsync(requestedTarget);

        HashSet<Target> GetActivatedTargets(Target target)
        {
            var result = new HashSet<Target>(
                ReferenceEqualityComparer.Instance);

            Add(target);
            return result;

            void Add(Target current)
            {
                if (_completed.Contains(current) || !result.Add(current))
                {
                    return;
                }

                foreach (var referencedTarget in
                    current.Prelude.Concat(current.Epilogue))
                {
                    Add(referencedTarget);
                }
            }
        }

        async ValueTask EnsureAsync(Target target)
        {
            if (_completed.Contains(target))
            {
                return;
            }

            foreach (var preludeTarget in target.Prelude)
            {
                await EnsureAsync(preludeTarget);
            }

            await _operationExecutor.ExecuteAsync(
                target.Body,
                _values,
                _executeOperation,
                cancellationToken);

            foreach (var epilogueTarget in target.Epilogue)
            {
                await EnsureAsync(epilogueTarget);
            }

            _completed.Add(target);
        }
    }
}
