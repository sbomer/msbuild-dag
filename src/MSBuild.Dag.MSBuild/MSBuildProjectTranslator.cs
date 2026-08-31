using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using MSBuild.Dag.Core;
using DagOperation = MSBuild.Dag.Core.Operation;

namespace MSBuild.Dag.MSBuild;

public sealed class MSBuildProjectTranslator
{
    public TranslationResult Translate(string projectPath, string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        using var projectCollection = new ProjectCollection();
        var project = projectCollection.LoadProject(Path.GetFullPath(projectPath));
        var projectInstance = project.CreateProjectInstance();

        if (!projectInstance.Targets.TryGetValue(targetName, out var target))
        {
            throw new ArgumentException(
                $"Target '{targetName}' does not exist.",
                nameof(targetName));
        }

        if (!string.IsNullOrWhiteSpace(target.Condition))
        {
            throw Unsupported("Target conditions");
        }

        if (!string.IsNullOrWhiteSpace(target.DependsOnTargets))
        {
            throw Unsupported("DependsOnTargets");
        }

        var context = new TranslationContext(projectInstance);

        foreach (var child in target.Children)
        {
            switch (child)
            {
                case ProjectPropertyGroupTaskInstance propertyGroup:
                    TranslatePropertyGroup(propertyGroup, context);
                    break;

                case ProjectItemGroupTaskInstance itemGroup:
                    TranslateItemGroup(itemGroup, context);
                    break;

                case ProjectTaskInstance task:
                    TranslateTask(task, context);
                    break;

                default:
                    throw Unsupported(child.GetType().Name);
            }
        }

        return new TranslationResult(
            new BuildGraph(context.Operations),
            new Dictionary<string, Value<string>>(
                context.Properties,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Value<IReadOnlyList<string>>>(
                context.Items,
                StringComparer.OrdinalIgnoreCase));
    }

    private static void TranslatePropertyGroup(
        ProjectPropertyGroupTaskInstance propertyGroup,
        TranslationContext context)
    {
        if (!string.IsNullOrWhiteSpace(propertyGroup.Condition))
        {
            throw Unsupported("PropertyGroup conditions");
        }

        foreach (var property in propertyGroup.Properties)
        {
            if (!string.IsNullOrWhiteSpace(property.Condition))
            {
                throw Unsupported("property conditions");
            }

            context.Properties[property.Name] =
                context.ResolvePropertyExpression(property.Value);
        }
    }

    private static void TranslateItemGroup(
        ProjectItemGroupTaskInstance itemGroup,
        TranslationContext context)
    {
        if (!string.IsNullOrWhiteSpace(itemGroup.Condition))
        {
            throw Unsupported("ItemGroup conditions");
        }

        foreach (var item in itemGroup.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.Condition))
            {
                throw Unsupported("item conditions");
            }

            if (string.IsNullOrWhiteSpace(item.Include) ||
                !string.IsNullOrWhiteSpace(item.Exclude) ||
                !string.IsNullOrWhiteSpace(item.Remove) ||
                item.Metadata.Count > 0)
            {
                throw Unsupported("item operations other than a simple Include");
            }

            var existingItems = context.GetItems(item.ItemType);
            var appendedItems = context.ResolveItemsExpression(item.Include);
            var concat = new ConcatItemsOperation(existingItems, appendedItems);

            context.Operations.Add(concat);
            context.Items[item.ItemType] = concat.Result;
        }
    }

    private static void TranslateTask(
        ProjectTaskInstance task,
        TranslationContext context)
    {
        if (!string.IsNullOrWhiteSpace(task.Condition))
        {
            throw Unsupported("task conditions");
        }

        if (!task.Name.Equals("ToyCompile", StringComparison.OrdinalIgnoreCase))
        {
            throw Unsupported($"task '{task.Name}'");
        }

        var sourcesExpression = GetRequiredParameter(task, "Sources");
        var configurationExpression = GetRequiredParameter(task, "Configuration");
        var compile = new ToyCompileOperation(
            context.ResolveItemsExpression(sourcesExpression),
            context.ResolvePropertyExpression(configurationExpression));

        context.Operations.Add(compile);

        foreach (var output in task.Outputs)
        {
            if (output is not ProjectTaskOutputPropertyInstance propertyOutput ||
                !string.IsNullOrWhiteSpace(propertyOutput.Condition) ||
                !propertyOutput.TaskParameter.Equals(
                    "Assembly",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw Unsupported("ToyCompile outputs other than Assembly properties");
            }

            context.Properties[propertyOutput.PropertyName] = compile.Assembly;
        }
    }

    private static string GetRequiredParameter(
        ProjectTaskInstance task,
        string parameterName)
    {
        var parameter = task.Parameters.FirstOrDefault(
            pair => pair.Key.Equals(parameterName, StringComparison.OrdinalIgnoreCase));

        if (parameter.Key is null)
        {
            throw new InvalidOperationException(
                $"{task.Name} requires the {parameterName} parameter.");
        }

        return parameter.Value;
    }

    private static NotSupportedException Unsupported(string construct) =>
        new($"The restricted MSBuild translator does not support {construct}.");

    private sealed class TranslationContext(ProjectInstance project)
    {
        public Dictionary<string, Value<string>> Properties { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Value<IReadOnlyList<string>>> Items { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<DagOperation> Operations { get; } = [];

        public Value<string> ResolvePropertyExpression(string expression)
        {
            if (TryGetReference(expression, "$(", out var propertyName))
            {
                return GetProperty(propertyName);
            }

            if (ContainsReference(expression))
            {
                throw Unsupported($"property expression '{expression}'");
            }

            return AddConstant(expression);
        }

        public Value<IReadOnlyList<string>> ResolveItemsExpression(string expression)
        {
            if (TryGetReference(expression, "@(", out var itemType))
            {
                return GetItems(itemType);
            }

            if (ContainsReference(expression))
            {
                throw Unsupported($"item expression '{expression}'");
            }

            IReadOnlyList<string> values = expression
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return AddConstant(values);
        }

        public Value<IReadOnlyList<string>> GetItems(string itemType)
        {
            if (Items.TryGetValue(itemType, out var value))
            {
                return value;
            }

            IReadOnlyList<string> initialItems = project
                .GetItems(itemType)
                .Select(item => item.EvaluatedInclude)
                .ToArray();

            value = AddConstant(initialItems);
            Items.Add(itemType, value);
            return value;
        }

        private Value<string> GetProperty(string propertyName)
        {
            if (Properties.TryGetValue(propertyName, out var value))
            {
                return value;
            }

            value = AddConstant(project.GetPropertyValue(propertyName));
            Properties.Add(propertyName, value);
            return value;
        }

        private Value<T> AddConstant<T>(T content)
        {
            var operation = new ConstantOperation<T>(content);
            Operations.Add(operation);
            return operation.Result;
        }

        private static bool TryGetReference(
            string expression,
            string prefix,
            out string name)
        {
            if (expression.StartsWith(prefix, StringComparison.Ordinal) &&
                expression.EndsWith(')') &&
                expression.IndexOf(')', prefix.Length) == expression.Length - 1)
            {
                name = expression[prefix.Length..^1];
                return name.Length > 0;
            }

            name = string.Empty;
            return false;
        }

        private static bool ContainsReference(string expression) =>
            expression.Contains("$(", StringComparison.Ordinal) ||
            expression.Contains("@(", StringComparison.Ordinal);
    }
}
