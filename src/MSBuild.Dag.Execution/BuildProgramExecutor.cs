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

        foreach (var initialValue in program.InitialValues)
        {
            _values.SetInitial(initialValue);
        }
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

        var remaining = new HashSet<Target>(
            activated.Where(target => !_completed.Contains(target)),
            ReferenceEqualityComparer.Instance);

        while (remaining.Count > 0)
        {
            var target = _program.Targets.FirstOrDefault(
                candidate =>
                    remaining.Contains(candidate) &&
                    _program.GetPredecessors(candidate)
                        .All(_completed.Contains));

            if (target is null)
            {
                throw new InvalidOperationException(
                    "The activated targets cannot be scheduled in program order.");
            }

            await ExecuteBodyAsync(target);
            _completed.Add(target);
            remaining.Remove(target);
        }

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

        async ValueTask ExecuteBodyAsync(Target target)
        {
            for (var index = 0; index < target.Inputs.Count; index++)
            {
                if (!ReferenceEquals(
                    target.Inputs[index],
                    target.Body.Inputs[index]))
                {
                    _values.Copy(
                        target.Inputs[index],
                        target.Body.Inputs[index]);
                }
            }

            await _operationExecutor.ExecuteAsync(
                target.Body,
                _values,
                _executeOperation,
                cancellationToken);

            for (var index = 0; index < target.Outputs.Count; index++)
            {
                _values.Copy(
                    target.Body.Outputs[index],
                    target.Outputs[index]);
            }
        }
    }
}
