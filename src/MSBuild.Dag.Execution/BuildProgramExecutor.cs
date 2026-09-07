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

        foreach (var (value, content) in program.InitialValues)
        {
            _values.SetInitial(value, content);
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

        var requestOrder = _program.GetRequestOrder(
            requestedTarget,
            _completed);

        foreach (var target in requestOrder)
        {
            await ExecuteBodyAsync(target);
            _completed.Add(target);
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
