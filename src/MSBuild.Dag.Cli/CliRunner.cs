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
        if (args.Length is < 1 or > 2 ||
            args.Any(argument => argument.StartsWith('-')))
        {
            Console.Error.WriteLine(
                "Usage: MSBuild.Dag.Cli <project-path> [target]");
            return 1;
        }

        var projectPath = Path.GetFullPath(args[0]);
        var targetName = args.Length == 2
            ? args[1]
            : "Build";

        try
        {
            var result = new MSBuildProjectTranslator()
                .Translate(
                    projectPath,
                    targetName,
                    warning => Console.Error.WriteLine($"warning: {warning}"));

            var targetNames = new Dictionary<Target, string>(
                ReferenceEqualityComparer.Instance);

            foreach (var (name, target) in result.Targets)
            {
                targetNames.Add(target, name);
            }

            AsciiGraphWriter.Write(
                result.Program,
                Console.Out,
                targetNames);

            var values = new ValueStore();
            var executor = new BuildProgramExecutor(
                result.Program,
                values,
                TranslatedOperationEvaluator.EvaluateAsync);

            await executor.ExecuteAsync(result.Targets[targetName]);

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
