using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using MSBuild.Dag.Core;
using System.Text.RegularExpressions;
using DagOperation = MSBuild.Dag.Core.Operation;
using MSBuildTarget = Microsoft.Build.Execution.ProjectTargetInstance;

namespace MSBuild.Dag.MSBuild;

public sealed class MSBuildProjectTranslator
{
    private static readonly Regex s_comparisonCondition = new(
        @"^\s*'\$\((?<property>[^)]+)\)'\s*(?<operator>==|!=)\s*'(?<literal>[^']*)'\s*$",
        RegexOptions.CultureInvariant);

    public TranslationResult Translate(string projectPath, string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        using var projectCollection = new ProjectCollection();
        var project = projectCollection.LoadProject(Path.GetFullPath(projectPath));
        var projectInstance = project.CreateProjectInstance();

        if (!projectInstance.Targets.ContainsKey(targetName))
        {
            throw new ArgumentException(
                $"Target '{targetName}' does not exist.",
                nameof(targetName));
        }

        var sourceTargets = projectInstance.Targets.Values.ToArray();
        var links = GetTargetLinks(sourceTargets);
        var translationOrder = GetTranslationOrder(
            sourceTargets,
            links.PrecedenceDependencies);
        var context = new TranslationContext(projectInstance);
        var targetBodies = new Dictionary<string, TargetBody>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var target in translationOrder)
        {
            context.BeginTarget();

            if (!string.IsNullOrWhiteSpace(target.Condition))
            {
                var condition = context.TranslateCondition(target.Condition);
                context.TargetConditions[target.Name] = condition;
                context.SetTargetGuard(context.CreateGuard(condition));
            }

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

            var operations = context.Operations.ToArray();
            var operationGraph = new OperationGraph(operations);
            targetBodies.Add(
                target.Name,
                new TargetBody(
                    GetExternalInputs(operationGraph),
                    GetTargetOutputs(operationGraph, context),
                    operationGraph));
        }

        var createdTargets = new Dictionary<string, Target>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var sourceTarget in sourceTargets)
        {
            CreateTarget(sourceTarget);
        }

        var targets = new Dictionary<string, Target>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var sourceTarget in sourceTargets)
        {
            targets.Add(sourceTarget.Name, createdTargets[sourceTarget.Name]);
        }

        return new TranslationResult(
            new BuildProgram(
                sourceTargets.Select(target => targets[target.Name]).ToArray()),
            targets,
            new Dictionary<string, Value<string>>(
                context.Properties,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Value<IReadOnlyList<string>>>(
                context.Items,
                StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Value<bool>>(
                context.TargetConditions,
                StringComparer.OrdinalIgnoreCase));

        Target CreateTarget(MSBuildTarget sourceTarget)
        {
            if (createdTargets.TryGetValue(sourceTarget.Name, out var existing))
            {
                return existing;
            }

            var body = targetBodies[sourceTarget.Name];
            var target = new Target(
                links.Preludes[sourceTarget]
                    .Select(CreateTarget)
                    .ToArray(),
                body.Inputs,
                body.Outputs,
                body.Graph,
                links.Epilogues[sourceTarget]
                    .Select(CreateTarget)
                    .ToArray());
            createdTargets.Add(sourceTarget.Name, target);
            return target;
        }
    }

    private static TargetLinks GetTargetLinks(
        IReadOnlyList<MSBuildTarget> targets)
    {
        var targetsByName = targets.ToDictionary(
            target => target.Name,
            StringComparer.OrdinalIgnoreCase);
        var preludes = new Dictionary<MSBuildTarget, List<MSBuildTarget>>(
            ReferenceEqualityComparer.Instance);
        var epilogues = new Dictionary<MSBuildTarget, List<MSBuildTarget>>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in targets)
        {
            preludes.Add(
                target,
                ResolveTargetList(
                    target,
                    target.DependsOnTargets,
                    nameof(target.DependsOnTargets),
                    targetsByName));
            epilogues.Add(target, []);
        }

        foreach (var target in targets)
        {
            foreach (var anchor in ResolveTargetList(
                target,
                target.BeforeTargets,
                nameof(target.BeforeTargets),
                targetsByName))
            {
                AddDistinct(preludes[anchor], target);
            }

            foreach (var anchor in ResolveTargetList(
                target,
                target.AfterTargets,
                nameof(target.AfterTargets),
                targetsByName))
            {
                AddDistinct(epilogues[anchor], target);
            }
        }

        EnsureOrchestrationAcyclic(targets, preludes, epilogues);

        var precedenceDependencies =
            new Dictionary<MSBuildTarget, List<MSBuildTarget>>(
                ReferenceEqualityComparer.Instance);

        foreach (var target in targets)
        {
            precedenceDependencies.Add(target, []);
        }

        foreach (var target in targets)
        {
            AddPrecedenceSequence(preludes[target], target);
            AddPrecedenceSequence([target, .. epilogues[target]], dependent: null);
        }

        return new TargetLinks(
            CopyLists(preludes),
            CopyLists(epilogues),
            CopyLists(precedenceDependencies));

        void AddPrecedenceSequence(
            IReadOnlyList<MSBuildTarget> sequence,
            MSBuildTarget? dependent)
        {
            MSBuildTarget? prerequisite = null;

            foreach (var current in sequence)
            {
                if (prerequisite is not null)
                {
                    AddDistinct(
                        precedenceDependencies[current],
                        prerequisite);
                }

                prerequisite = current;
            }

            if (prerequisite is not null && dependent is not null)
            {
                AddDistinct(
                    precedenceDependencies[dependent],
                    prerequisite);
            }
        }
    }

    private static IReadOnlyDictionary<MSBuildTarget, IReadOnlyList<MSBuildTarget>>
        CopyLists(
            IReadOnlyDictionary<MSBuildTarget, List<MSBuildTarget>> source)
    {
        var result =
            new Dictionary<MSBuildTarget, IReadOnlyList<MSBuildTarget>>(
                ReferenceEqualityComparer.Instance);

        foreach (var (target, targets) in source)
        {
            result.Add(target, targets.ToArray());
        }

        return result;
    }

    private static List<MSBuildTarget> ResolveTargetList(
        MSBuildTarget declaringTarget,
        string expression,
        string attributeName,
        IReadOnlyDictionary<string, MSBuildTarget> targetsByName)
    {
        if (ContainsReference(expression))
        {
            throw Unsupported(
                $"non-literal {attributeName} on target " +
                $"'{declaringTarget.Name}'");
        }

        var result = new List<MSBuildTarget>();

        foreach (var targetName in expression.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries))
        {
            if (!targetsByName.TryGetValue(targetName, out var target))
            {
                throw new InvalidOperationException(
                    $"Target '{declaringTarget.Name}' references missing target " +
                    $"'{targetName}' through {attributeName}.");
            }

            AddDistinct(result, target);
        }

        return result;
    }

    private static void AddDistinct(
        List<MSBuildTarget> targets,
        MSBuildTarget target)
    {
        if (!targets.Contains(target, ReferenceEqualityComparer.Instance))
        {
            targets.Add(target);
        }
    }

    private static void EnsureOrchestrationAcyclic(
        IReadOnlyList<MSBuildTarget> targets,
        IReadOnlyDictionary<MSBuildTarget, List<MSBuildTarget>> preludes,
        IReadOnlyDictionary<MSBuildTarget, List<MSBuildTarget>> epilogues)
    {
        var visiting = new HashSet<MSBuildTarget>(
            ReferenceEqualityComparer.Instance);
        var visited = new HashSet<MSBuildTarget>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in targets)
        {
            Visit(target);
        }

        void Visit(MSBuildTarget target)
        {
            if (visited.Contains(target))
            {
                return;
            }

            if (!visiting.Add(target))
            {
                throw new InvalidOperationException(
                    "Target orchestration must be acyclic.");
            }

            foreach (var referencedTarget in
                preludes[target].Concat(epilogues[target]))
            {
                Visit(referencedTarget);
            }

            visiting.Remove(target);
            visited.Add(target);
        }
    }

    private static IReadOnlyList<MSBuildTarget> GetTranslationOrder(
        IReadOnlyList<MSBuildTarget> targets,
        IReadOnlyDictionary<MSBuildTarget, IReadOnlyList<MSBuildTarget>>
            dependencies)
    {
        var result = new List<MSBuildTarget>(targets.Count);
        var visiting = new HashSet<MSBuildTarget>(
            ReferenceEqualityComparer.Instance);
        var visited = new HashSet<MSBuildTarget>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in targets)
        {
            Visit(target);
        }

        return result;

        void Visit(MSBuildTarget target)
        {
            if (visited.Contains(target))
            {
                return;
            }

            if (!visiting.Add(target))
            {
                throw new InvalidOperationException(
                    "The target dependency graph must be acyclic.");
            }

            foreach (var dependency in dependencies[target])
            {
                Visit(dependency);
            }

            visiting.Remove(target);
            visited.Add(target);
            result.Add(target);
        }
    }

    private static IReadOnlyList<Value> GetExternalInputs(OperationGraph graph)
    {
        var inputs = new HashSet<Value>(ReferenceEqualityComparer.Instance);

        foreach (var operation in graph.Operations)
        {
            foreach (var input in operation.Inputs)
            {
                if (graph.GetProducer(input) is null)
                {
                    inputs.Add(input);
                }
            }
        }

        return inputs.ToArray();
    }

    private static IReadOnlyList<Value> GetTargetOutputs(
        OperationGraph graph,
        TranslationContext context)
    {
        var outputs = new HashSet<Value>(ReferenceEqualityComparer.Instance);

        foreach (var value in context.Properties.Values.Cast<Value>()
            .Concat(context.Items.Values)
            .Concat(context.TargetConditions.Values))
        {
            if (graph.GetProducer(value) is not null)
            {
                outputs.Add(value);
            }
        }

        return outputs.ToArray();
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
            var concat = new ConcatItemsOperation(
                existingItems,
                appendedItems,
                context.CreateControl());

            context.AddOperation(concat);
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
        var sources = context.ResolveItemsExpression(sourcesExpression);
        var configuration =
            context.ResolvePropertyExpression(configurationExpression);
        var compile = new ToyCompileOperation(
            sources,
            configuration,
            context.CreateControl());

        context.AddOperation(compile);

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

    private sealed record TargetBody(
        IReadOnlyList<Value> Inputs,
        IReadOnlyList<Value> Outputs,
        OperationGraph Graph);

    private sealed record TargetLinks(
        IReadOnlyDictionary<MSBuildTarget, IReadOnlyList<MSBuildTarget>> Preludes,
        IReadOnlyDictionary<MSBuildTarget, IReadOnlyList<MSBuildTarget>> Epilogues,
        IReadOnlyDictionary<MSBuildTarget, IReadOnlyList<MSBuildTarget>>
            PrecedenceDependencies);

    private sealed class TranslationContext(ProjectInstance project)
    {
        public Dictionary<string, Value<string>> Properties { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Value<IReadOnlyList<string>>> Items { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Value<bool>> TargetConditions { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public Value<OrderToken>? CurrentOrderToken { get; private set; }

        private Value<GuardToken>? TargetGuard { get; set; }

        private bool IsTranslatingTarget { get; set; }

        public List<DagOperation> Operations { get; } = [];

        public void BeginTarget()
        {
            Operations.Clear();
            TargetGuard = null;
            CurrentOrderToken = null;
            IsTranslatingTarget = true;
        }

        public void SetTargetGuard(Value<GuardToken> guard) =>
            TargetGuard = guard;

        public OperationControl CreateControl() =>
            new(
                CurrentOrderToken is null ? TargetGuard : null,
                CurrentOrderToken);

        public void AddOperation(DagOperation operation, bool ordered = true)
        {
            Operations.Add(operation);

            if (ordered &&
                operation is IOrderedOperation { OrderOutput: not null } orderedOperation)
            {
                CurrentOrderToken = orderedOperation.OrderOutput;
            }
        }

        public Value<GuardToken> CreateGuard(Value<bool> condition)
        {
            var guard = new ConditionGuardOperation(condition);
            Operations.Add(guard);
            return guard.Result;
        }

        public Value<bool> TranslateCondition(string expression)
        {
            var match = s_comparisonCondition.Match(expression);

            if (!match.Success)
            {
                throw Unsupported($"target condition '{expression}'");
            }

            var left = GetProperty(match.Groups["property"].Value);
            var right = AddConstant(
                match.Groups["literal"].Value,
                ordered: false);

            DagOperation comparison = match.Groups["operator"].Value switch
            {
                "==" => new EqualOperation<string>(left, right),
                "!=" => new NotEqualOperation<string>(left, right),
                _ => throw new InvalidOperationException(
                    "The condition parser produced an unknown comparison operator."),
            };

            AddOperation(comparison, ordered: false);

            return comparison switch
            {
                EqualOperation<string> equal => equal.Result,
                NotEqualOperation<string> notEqual => notEqual.Result,
                _ => throw new InvalidOperationException(
                    "The condition parser produced an unknown comparison operation."),
            };
        }

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

            value = AddConstant(initialItems, ordered: false);
            Items.Add(itemType, value);
            return value;
        }

        private Value<string> GetProperty(string propertyName)
        {
            if (Properties.TryGetValue(propertyName, out var value))
            {
                return value;
            }

            value = AddConstant(
                project.GetPropertyValue(propertyName),
                ordered: false);
            Properties.Add(propertyName, value);
            return value;
        }

        private Value<T> AddConstant<T>(T content, bool ordered = true)
        {
            var operation = new ConstantOperation<T>(
                content,
                ordered && IsTranslatingTarget
                    ? CreateControl()
                    : null);
            AddOperation(operation, ordered && IsTranslatingTarget);
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

    }

    private static bool ContainsReference(string expression) =>
        expression.Contains("$(", StringComparison.Ordinal) ||
        expression.Contains("@(", StringComparison.Ordinal) ||
        expression.Contains("%(", StringComparison.Ordinal);
}
