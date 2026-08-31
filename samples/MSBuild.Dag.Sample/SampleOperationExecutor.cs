using MSBuild.Dag.Core;
using MSBuild.Dag.Execution;

namespace MSBuild.Dag.Sample;

internal static class SampleOperationExecutor
{
    public static ValueTask ExecuteAsync(
        Operation operation,
        ValueStore values,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (operation)
        {
            case ComputeConfiguration compute:
                values.Set(compute.Result, "Debug");
                break;

            case ReportConfiguration report:
                Console.WriteLine($"Configuration: {values.Get(report.Configuration)}");
                break;

            case GenerateSources generate:
                values.Set(
                    generate.GeneratedSources,
                    (IReadOnlyList<string>)["Generated.cs"]);
                break;

            case ConcatItems concat:
                values.Set(
                    concat.Result,
                    values.Get(concat.ExistingItems)
                        .Concat(values.Get(concat.AppendedItems))
                        .ToArray());
                break;

            case Compile compile:
                Console.WriteLine(
                    $"Compiling: {string.Join(", ", values.Get(compile.Sources))}");
                values.Set(compile.Assembly, "App.dll");
                break;

            default:
                throw new InvalidOperationException(
                    $"No executor exists for {operation.GetType().Name}.");
        }

        return ValueTask.CompletedTask;
    }
}
