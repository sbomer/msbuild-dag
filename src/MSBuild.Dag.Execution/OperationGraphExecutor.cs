using MSBuild.Dag.Core;

namespace MSBuild.Dag.Execution;

public sealed class OperationGraphExecutor
{
    public async ValueTask ExecuteAsync(
        OperationGraph graph,
        ValueStore values,
        Func<Operation, ValueStore, CancellationToken, ValueTask> executeOperation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(executeOperation);

        foreach (var input in graph.Inputs)
        {
            if (!values.Contains(input))
            {
                throw new InvalidOperationException(
                    "An operation-graph input has not been assigned.");
            }
        }

        var completed = new HashSet<Operation>(ReferenceEqualityComparer.Instance);

        foreach (var operation in graph.Operations)
        {
            await ExecuteAsync(operation);
        }

        foreach (var output in graph.Outputs)
        {
            if (!values.Contains(output))
            {
                throw new InvalidOperationException(
                    "An operation-graph output has not been assigned.");
            }
        }

        async ValueTask ExecuteAsync(Operation operation)
        {
            if (completed.Contains(operation))
            {
                return;
            }

            foreach (var dependency in graph.GetDependencies(operation))
            {
                await ExecuteAsync(dependency);
            }

            cancellationToken.ThrowIfCancellationRequested();

            foreach (var input in operation.Inputs)
            {
                if (!values.Contains(input))
                {
                    throw new InvalidOperationException(
                        $"An input for {operation.GetType().Name} has not been assigned.");
                }
            }

            var shouldSkip = operation.Inputs.Any(
                input => !values.IsAvailable(input));

            if (!shouldSkip &&
                operation is IGuardedOperation { Guard: not null } guardedOperation)
            {
                shouldSkip = !values.Get(guardedOperation.Guard).IsActive;
            }

            if (shouldSkip)
            {
                foreach (var output in operation.Outputs)
                {
                    values.SetUnavailable(output);
                }

                completed.Add(operation);
                return;
            }

            if (operation is ISelectOperation select)
            {
                values.Copy(
                    values.Get(select.Condition)
                        ? select.WhenTrue
                        : select.WhenFalse,
                    select.Result);
            }
            else if (operation is ConditionalRegionOperation conditional)
            {
                await ExecuteConditionalAsync(conditional);
            }
            else
            {
                await executeOperation(operation, values, cancellationToken);
            }

            if (operation is IOrderedOperation
                {
                    OrderOutput: not null,
                } orderedOperation)
            {
                values.Set(
                    orderedOperation.OrderOutput,
                    orderedOperation.OrderInput is null
                        ? new OrderToken()
                        : values.Get(orderedOperation.OrderInput));
            }

            foreach (var output in operation.Outputs)
            {
                if (!values.Contains(output))
                {
                    throw new InvalidOperationException(
                        $"{operation.GetType().Name} did not assign one of its outputs.");
                }
            }

            completed.Add(operation);
        }

        async ValueTask ExecuteConditionalAsync(
            ConditionalRegionOperation conditional)
        {
            var branch = values.Get(conditional.Condition)
                ? conditional.WhenTrue
                : conditional.WhenFalse;

            for (var index = 0; index < branch.Inputs.Count; index++)
            {
                values.Copy(
                    conditional.Inputs[index + 1],
                    branch.Inputs[index]);
            }

            await new OperationGraphExecutor().ExecuteAsync(
                branch,
                values,
                executeOperation,
                cancellationToken);

            for (var index = 0; index < conditional.Outputs.Count; index++)
            {
                values.Copy(
                    branch.Outputs[index],
                    conditional.Outputs[index]);
            }
        }
    }
}
