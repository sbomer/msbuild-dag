using System.Globalization;
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
                targetNames,
                operation => GetOperationLabel(
                    operation,
                    result.ValueSymbols),
                value => GetValueLabel(value, result.ValueSymbols));

            var values = new ValueStore();

            foreach (var (path, state) in result.Files)
            {
                values.Set(
                    state,
                    File.Exists(path) || Directory.Exists(path)
                        ? new FileContents()
                        : null);
            }

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
        Value<IReadOnlyList<MSBuildItem>> value) =>
        values.IsAvailable(value)
            ? string.Join("; ", values.Get(value).Select(FormatItem))
            : "(unavailable)";

    private static string? GetOperationLabel(
        Operation operation,
        IReadOnlyDictionary<Value, IReadOnlyList<ValueSymbol>> valueSymbols)
    {
        if (operation is IConstantOperation constant)
        {
            return FormatConstant(constant.Content);
        }

        if (operation is ISelectOperation)
        {
            return "?:";
        }

        if (operation is ConditionalRegionOperation)
        {
            return "if";
        }

        if (operation is AndOperation)
        {
            return "and";
        }

        if (operation is OrOperation)
        {
            return "or";
        }

        if (operation is ProjectItemIdentitiesOperation)
        {
            return "identities";
        }

        if (operation is UpdateItemMetadataOperation update)
        {
            return string.Join(
                ", ",
                update.Metadata.Select(
                    static pair =>
                        $"{pair.Key}='{pair.Value.Replace(
                            "'",
                            "''",
                            StringComparison.Ordinal)}'"));
        }

        if (operation is GetItemMetadataOperation getMetadata)
        {
            return $"%({getMetadata.MetadataName})";
        }

        if (operation is SetItemMetadataOperation setMetadata)
        {
            return $"set %({setMetadata.MetadataName})";
        }

        if (operation is ConcatItemValuesOperation)
        {
            return "concat each";
        }

        if (operation is NotItemValuesOperation)
        {
            return "not each";
        }

        if (operation is JoinItemValuesOperation)
        {
            return "join";
        }

        if (operation is ConcatStringsOperation)
        {
            return "concat";
        }

        if (operation is ValueOrDefaultOperation)
        {
            return "value or default";
        }

        if (operation is UnsupportedPropertyFunctionOperation unsupported)
        {
            return $"unsupported {unsupported.FunctionName}";
        }

        if (operation is ErrorOperation)
        {
            return "error";
        }

        if (operation is FileExistsOperation)
        {
            return "exists";
        }

        if (!operation.GetType().IsGenericType)
        {
            return null;
        }

        var operationType = operation.GetType().GetGenericTypeDefinition();

        if (operationType == typeof(EqualOperation<>))
        {
            return "==";
        }

        if (operationType == typeof(BroadcastItemValueOperation<>))
        {
            return "broadcast";
        }

        if (operationType == typeof(EqualItemValuesOperation<>))
        {
            return "== each";
        }

        if (operationType == typeof(ContainsOperation<>))
        {
            return "contains";
        }

        if (operationType == typeof(IsEmptyOperation<>))
        {
            return "empty?";
        }

        return operationType == typeof(NotEqualOperation<>)
            ? "!="
            : null;
    }

    private static string FormatConstant(object? content) =>
        content switch
        {
            null => "null",
            string value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'",
            IReadOnlyList<MSBuildItem> items =>
                $"@({string.Join("; ", items.Select(FormatItem))})",
            IReadOnlyList<string> items =>
                $"@({string.Join("; ", items)})",
            bool value => value ? "true" : "false",
            IFormattable value =>
                value.ToString(format: null, CultureInfo.InvariantCulture),
            _ => content.ToString() ?? string.Empty,
        };

    private static string FormatItem(MSBuildItem item)
    {
        if (item.Metadata.Count == 0)
        {
            return item.Identity;
        }

        var metadata = string.Join(
            ", ",
            item.Metadata
                .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(
                    static pair =>
                        $"{pair.Key}='{pair.Value.Replace(
                            "'",
                            "''",
                            StringComparison.Ordinal)}'"));
        return $"{item.Identity} {{{metadata}}}";
    }

    private static string? GetValueLabel(
        Value value,
        IReadOnlyDictionary<Value, IReadOnlyList<ValueSymbol>> valueSymbols) =>
        valueSymbols.TryGetValue(value, out var symbols)
            ? string.Join(", ", symbols.Select(static symbol => symbol.ToString()))
            : null;
}
