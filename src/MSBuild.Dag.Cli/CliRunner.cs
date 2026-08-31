using Microsoft.Build.Exceptions;
using MSBuild.Dag.Core;
using MSBuild.Dag.Execution;
using MSBuild.Dag.MSBuild;
using MSBuild.Dag.Visualization;

namespace MSBuild.Dag.Cli;

internal static class CliRunner
{
    public static async Task<int> RunAsync(string[] args)
    {
        var horizontal = args.Contains(
            "--horizontal",
            StringComparer.OrdinalIgnoreCase);
        var positionalArguments = args
            .Where(
                argument => !argument.Equals(
                    "--horizontal",
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (positionalArguments.Length is < 1 or > 2 ||
            args.Any(
                argument => argument.StartsWith('-') &&
                    !argument.Equals(
                        "--horizontal",
                        StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine(
                "Usage: MSBuild.Dag.Cli <project-path> [target] [--horizontal]");
            return 1;
        }

        var projectPath = Path.GetFullPath(positionalArguments[0]);
        var targetName = positionalArguments.Length == 2
            ? positionalArguments[1]
            : "Build";

        try
        {
            var result = new MSBuildProjectTranslator()
                .Translate(projectPath, targetName);

            if (horizontal)
            {
                AsciiGraphWriter.Write(result.Graph, Console.Out);
            }
            else
            {
                VerticalGraphWriter.Write(result.Graph, Console.Out);
            }

            var values = new ValueStore();

            await new BuildGraphExecutor().ExecuteAsync(
                result.Graph,
                values,
                TranslatedOperationEvaluator.EvaluateAsync);

            WriteResults(result, values);

            return 0;
        }
        catch (InvalidProjectFileException exception)
        {
            return ReportError(exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return ReportError(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return ReportError(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return ReportError(exception.Message);
        }
        catch (IOException exception)
        {
            return ReportError(exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return ReportError(exception.Message);
        }
    }

    private static int ReportError(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 1;
    }

    private static void WriteResults(
        TranslationResult result,
        ValueStore values)
    {
        Console.WriteLine();
        Console.WriteLine("Results");

        foreach (var property in result.Properties)
        {
            Console.WriteLine(
                $"  $({property.Key}) = {Format(values, property.Value)}");
        }

        foreach (var item in result.Items)
        {
            Console.WriteLine(
                $"  @({item.Key}) = {FormatItems(values, item.Value)}");
        }

        foreach (var condition in result.TargetConditions)
        {
            Console.WriteLine(
                $"  Condition({condition.Key}) = {Format(values, condition.Value)}");
        }
    }

    private static string Format<T>(ValueStore values, Value<T> value) =>
        values.IsAvailable(value)
            ? values.Get(value)?.ToString() ?? string.Empty
            : "(unavailable)";

    private static string FormatItems(
        ValueStore values,
        Value<IReadOnlyList<string>> value) =>
        values.IsAvailable(value)
            ? string.Join("; ", values.Get(value))
            : "(unavailable)";
}
