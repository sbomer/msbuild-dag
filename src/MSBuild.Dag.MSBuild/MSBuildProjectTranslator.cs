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
    private static readonly Regex s_propertyReference = new(
        @"\$\((?<property>[^()]+)\)",
        RegexOptions.CultureInvariant);

    public TranslationResult Translate(
        string projectPath,
        string targetName,
        Action<string>? reportWarning = null)
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
        var links = GetTargetLinks(
            projectInstance,
            sourceTargets,
            GetTargetPropertyAssignments(sourceTargets),
            reportWarning);
        var stateAccesses = new Dictionary<MSBuildTarget, TargetStateAccess>(
            ReferenceEqualityComparer.Instance);
        var propertyLocations =
            new Dictionary<string, StateLocation<string>>(
                StringComparer.OrdinalIgnoreCase);
        var itemLocations =
            new Dictionary<string, StateLocation<IReadOnlyList<string>>>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var target in sourceTargets)
        {
            var access = GetTargetStateAccess(target);
            stateAccesses.Add(target, access);

            foreach (var name in access.ReadProperties
                .Concat(access.WriteProperties))
            {
                propertyLocations.TryAdd(name, new StateLocation<string>());
            }

            foreach (var name in access.ReadItems.Concat(access.WriteItems))
            {
                itemLocations.TryAdd(
                    name,
                    new StateLocation<IReadOnlyList<string>>());
            }
        }

        var evaluation = new EvaluationSnapshot(
        [
            .. propertyLocations.Select(
                pair => new StateInitialization<string>(
                    pair.Value,
                    projectInstance.GetPropertyValue(pair.Key))),
            .. itemLocations.Select(
                pair => new StateInitialization<IReadOnlyList<string>>(
                    pair.Value,
                    projectInstance.GetItems(pair.Key)
                        .Select(item => item.EvaluatedInclude)
                        .ToArray())),
        ]);
        var targetBodies = new Dictionary<MSBuildTarget, TargetBody>(
            ReferenceEqualityComparer.Instance);
        var targetConditions = new Dictionary<string, Value<bool>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var target in sourceTargets)
        {
            var access = stateAccesses[target];
            var propertyReads = access.ReadProperties.ToDictionary(
                name => name,
                name => new StateRead<string>(propertyLocations[name]),
                StringComparer.OrdinalIgnoreCase);
            var itemReads = access.ReadItems.ToDictionary(
                name => name,
                name => new StateRead<IReadOnlyList<string>>(
                    itemLocations[name]),
                StringComparer.OrdinalIgnoreCase);
            var context = new TranslationContext(
                propertyReads.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Value,
                    StringComparer.OrdinalIgnoreCase),
                itemReads.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Value,
                    StringComparer.OrdinalIgnoreCase));
            Value<bool>? targetCondition = null;

            if (!string.IsNullOrWhiteSpace(target.Condition))
            {
                targetCondition = context.TranslateCondition(target.Condition);
                targetConditions.Add(target.Name, targetCondition);
                context.SetTargetGuard(context.CreateGuard(targetCondition));
            }

            foreach (var missingDependency in links.MissingDependencies
                .Where(missing => ReferenceEquals(
                    missing.DeclaringTarget,
                    target)))
            {
                context.AddOperation(
                    new MissingTargetOperation(
                        target.Name,
                        missingDependency.TargetName,
                        nameof(target.DependsOnTargets),
                        context.CreateControl()));
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
                target,
                new TargetBody(
                    propertyReads.Values.Cast<StateRead>()
                        .Concat(itemReads.Values)
                        .ToArray(),
                    [
                        .. access.WriteProperties.Select(
                            name => new StateWrite<string>(
                                propertyLocations[name],
                                context.Properties[name])
                            {
                                IsConditional =
                                    !string.IsNullOrWhiteSpace(target.Condition),
                            }),
                        .. access.WriteItems.Select(
                            name => new StateWrite<IReadOnlyList<string>>(
                                itemLocations[name],
                                context.Items[name])
                            {
                                IsConditional =
                                    !string.IsNullOrWhiteSpace(target.Condition),
                            }),
                    ],
                    GetTargetOutputs(
                        operationGraph,
                        context,
                        targetCondition),
                    operationGraph));
        }

        var createdDefinitions =
            new Dictionary<MSBuildTarget, TargetDefinition>(
                ReferenceEqualityComparer.Instance);

        foreach (var sourceTarget in sourceTargets)
        {
            CreateDefinition(sourceTarget);
        }

        BuildLinkResult linked;
        var definition = new BuildDefinition(
            evaluation,
            sourceTargets
                .Select(target => createdDefinitions[target])
                .ToArray());

        try
        {
            linked = definition.Link();
        }
        catch (StateConflictException exception)
        {
            var firstName = GetTargetName(exception.FirstTarget);
            var secondName = GetTargetName(exception.SecondTarget);
            var (kind, name) = GetStateName(exception.Location);

            throw new InvalidOperationException(
                $"Targets '{firstName}' and '{secondName}' have conflicting " +
                $"access to {kind} '{name}' but are not ordered by the target " +
                "program.",
                exception);
        }
        catch (ConditionalStateWriteException exception)
        {
            throw Unsupported(
                $"state writes in conditional target " +
                $"'{GetTargetName(exception.Target)}'");
        }
        catch (TargetOrderCycleException exception)
        {
            throw new InvalidOperationException(
                "Target ordering is contradictory; no global order can " +
                "satisfy the precedence cycle " +
                string.Join(
                    " -> ",
                    exception.Cycle.Select(
                        target => $"'{GetTargetName(target)}'")) +
                ".",
                exception);
        }

        var targets = new Dictionary<string, Target>(
            StringComparer.OrdinalIgnoreCase);
        var targetDefinitions =
            new Dictionary<string, TargetDefinition>(
                StringComparer.OrdinalIgnoreCase);

        foreach (var sourceTarget in sourceTargets)
        {
            targetDefinitions.Add(
                sourceTarget.Name,
                createdDefinitions[sourceTarget]);
            targets.Add(
                sourceTarget.Name,
                linked.Targets[createdDefinitions[sourceTarget]]);
        }

        var properties = new Dictionary<string, Value<string>>(
            StringComparer.OrdinalIgnoreCase);
        var items =
            new Dictionary<string, Value<IReadOnlyList<string>>>(
                StringComparer.OrdinalIgnoreCase);
        var state = linked.GetStateAfter(
            createdDefinitions[projectInstance.Targets[targetName]]);

        foreach (var (name, location) in propertyLocations)
        {
            properties.Add(name, (Value<string>)state[location]);
        }

        foreach (var (name, location) in itemLocations)
        {
            items.Add(
                name,
                (Value<IReadOnlyList<string>>)state[location]);
        }

        return new TranslationResult(
            definition,
            linked.Program,
            targetDefinitions,
            targets,
            properties,
            items,
            targetConditions,
            links.Warnings);

        TargetDefinition CreateDefinition(MSBuildTarget sourceTarget)
        {
            if (createdDefinitions.TryGetValue(
                sourceTarget,
                out var existing))
            {
                return existing;
            }

            var body = targetBodies[sourceTarget];
            var definition = new TargetDefinition(
                links.Preludes[sourceTarget]
                    .Select(CreateDefinition)
                    .ToArray(),
                body.Reads,
                body.Writes,
                body.Outputs,
                body.Graph,
                links.Epilogues[sourceTarget]
                    .Select(CreateDefinition)
                    .ToArray());
            createdDefinitions.Add(sourceTarget, definition);
            return definition;
        }

        string GetTargetName(TargetDefinition definition)
        {
            return createdDefinitions.Single(
                pair => ReferenceEquals(pair.Value, definition)).Key.Name;
        }

        (string Kind, string Name) GetStateName(StateLocation location)
        {
            foreach (var (name, propertyLocation) in propertyLocations)
            {
                if (ReferenceEquals(propertyLocation, location))
                {
                    return ("property", name);
                }
            }

            foreach (var (name, itemLocation) in itemLocations)
            {
                if (ReferenceEquals(itemLocation, location))
                {
                    return ("item", name);
                }
            }

            throw new InvalidOperationException(
                "The state location is not part of the translated project.");
        }
    }

    private static TargetLinks GetTargetLinks(
        ProjectInstance project,
        IReadOnlyList<MSBuildTarget> targets,
        IReadOnlyDictionary<string, IReadOnlyList<MSBuildTarget>>
            propertyAssignments,
        Action<string>? reportWarning)
    {
        var targetsByName = targets.ToDictionary(
            target => target.Name,
            StringComparer.OrdinalIgnoreCase);
        var preludes = new Dictionary<MSBuildTarget, List<MSBuildTarget>>(
            ReferenceEqualityComparer.Instance);
        var epilogues = new Dictionary<MSBuildTarget, List<MSBuildTarget>>(
            ReferenceEqualityComparer.Instance);
        var missingDependencies = new List<MissingTargetDependency>();
        var missingRegistrations = new List<MissingTargetRegistration>();

        foreach (var target in targets)
        {
            preludes.Add(
                target,
                ResolveDependsOnTargetList(
                    target,
                    ExpandDependsOnTargets(
                        project,
                        target,
                        propertyAssignments),
                    targetsByName,
                    missingDependencies));
            epilogues.Add(target, []);
        }

        foreach (var target in targets)
        {
            foreach (var anchor in ResolveTargetList(
                target,
                ExpandTargetReferences(
                    project,
                    target,
                    target.BeforeTargets,
                    nameof(target.BeforeTargets),
                    propertyAssignments),
                nameof(target.BeforeTargets),
                targetsByName,
                missingRegistrations))
            {
                AddDistinct(preludes[anchor], target);
            }

            foreach (var anchor in ResolveTargetList(
                target,
                ExpandTargetReferences(
                    project,
                    target,
                    target.AfterTargets,
                    nameof(target.AfterTargets),
                    propertyAssignments),
                nameof(target.AfterTargets),
                targetsByName,
                missingRegistrations))
            {
                AddDistinct(epilogues[anchor], target);
            }
        }

        var warnings = missingDependencies.Select(missing =>
                $"Target '{missing.DeclaringTarget.Name}' references missing " +
                $"target '{missing.TargetName}' through DependsOnTargets.")
            .Concat(missingRegistrations.Select(missing =>
                $"Target '{missing.DeclaringTarget.Name}' references missing " +
                $"target '{missing.TargetName}' through " +
                $"{missing.AttributeName}."))
            .ToArray();

        foreach (var warning in warnings)
        {
            reportWarning?.Invoke(warning);
        }

        EnsureSourceOrchestrationAcyclic(targets, preludes, epilogues);

        return new TargetLinks(
            CopyLists(preludes),
            CopyLists(epilogues),
            warnings,
            missingDependencies);
    }

    private static void EnsureSourceOrchestrationAcyclic(
        IReadOnlyList<MSBuildTarget> targets,
        IReadOnlyDictionary<MSBuildTarget, List<MSBuildTarget>> preludes,
        IReadOnlyDictionary<MSBuildTarget, List<MSBuildTarget>> epilogues)
    {
        var visiting = new HashSet<MSBuildTarget>(
            ReferenceEqualityComparer.Instance);
        var visited = new HashSet<MSBuildTarget>(
            ReferenceEqualityComparer.Instance);
        var path = new List<MSBuildTarget>();

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
                var start = path.FindIndex(
                    candidate => ReferenceEquals(candidate, target));
                var cycle = path.Skip(start).Append(target);

                throw new InvalidOperationException(
                    "Target orchestration must be acyclic. Cycle: " +
                    string.Join(
                        " -> ",
                        cycle.Select(candidate => $"'{candidate.Name}'")) +
                    ".");
            }

            path.Add(target);

            foreach (var referencedTarget in
                preludes[target].Concat(epilogues[target]))
            {
                Visit(referencedTarget);
            }

            path.RemoveAt(path.Count - 1);
            visiting.Remove(target);
            visited.Add(target);
        }
    }

    private static TargetStateAccess GetTargetStateAccess(MSBuildTarget target)
    {
        var readProperties = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var writeProperties = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var readItems = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var writeItems = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(target.Condition))
        {
            var match = s_comparisonCondition.Match(target.Condition);

            if (match.Success)
            {
                AddPropertyRead(match.Groups["property"].Value);
            }
        }

        foreach (var child in target.Children)
        {
            switch (child)
            {
                case ProjectPropertyGroupTaskInstance propertyGroup:
                    foreach (var property in propertyGroup.Properties)
                    {
                        AddPropertyExpressionRead(property.Value);
                        writeProperties.Add(property.Name);
                    }

                    break;

                case ProjectItemGroupTaskInstance itemGroup:
                    foreach (var item in itemGroup.Items)
                    {
                        AddItemRead(item.ItemType);
                        AddItemExpressionRead(item.Include);
                        writeItems.Add(item.ItemType);
                    }

                    break;

                case ProjectTaskInstance task:
                    if (task.Name.Equals(
                        "ToyCompile",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        AddItemExpressionRead(
                            GetRequiredParameter(task, "Sources"));
                        AddPropertyExpressionRead(
                            GetRequiredParameter(task, "Configuration"));
                    }
                    else if (task.Name.Equals(
                        "Message",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        AddPropertyExpressionRead(
                            GetRequiredParameter(task, "Text"));
                        AddPropertyExpressionRead(
                            GetParameter(task, "Importance") ?? "normal");
                    }

                    foreach (var output in task.Outputs
                        .OfType<ProjectTaskOutputPropertyInstance>())
                    {
                        writeProperties.Add(output.PropertyName);
                    }

                    break;
            }
        }

        return new TargetStateAccess(
            readProperties,
            writeProperties,
            readItems,
            writeItems);

        void AddPropertyExpressionRead(string expression)
        {
            if (TryGetReference(expression, "$(", out var propertyName))
            {
                AddPropertyRead(propertyName);
            }
        }

        void AddItemExpressionRead(string expression)
        {
            if (TryGetReference(expression, "@(", out var itemType))
            {
                AddItemRead(itemType);
            }
        }

        void AddPropertyRead(string propertyName)
        {
            if (!writeProperties.Contains(propertyName))
            {
                readProperties.Add(propertyName);
            }
        }

        void AddItemRead(string itemType)
        {
            if (!writeItems.Contains(itemType))
            {
                readItems.Add(itemType);
            }
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<MSBuildTarget>>
        GetTargetPropertyAssignments(IReadOnlyList<MSBuildTarget> targets)
    {
        var assignments = new Dictionary<string, List<MSBuildTarget>>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            foreach (var child in target.Children)
            {
                switch (child)
                {
                    case ProjectPropertyGroupTaskInstance propertyGroup:
                        foreach (var property in propertyGroup.Properties)
                        {
                            AddAssignment(property.Name, target);
                        }

                        break;

                    case ProjectTaskInstance task:
                        foreach (var output in task.Outputs
                            .OfType<ProjectTaskOutputPropertyInstance>())
                        {
                            AddAssignment(output.PropertyName, target);
                        }

                        break;
                }
            }
        }

        return assignments.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<MSBuildTarget>)pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);

        void AddAssignment(string propertyName, MSBuildTarget target)
        {
            if (!assignments.TryGetValue(
                propertyName,
                out var assigningTargets))
            {
                assigningTargets = [];
                assignments.Add(propertyName, assigningTargets);
            }

            if (!assigningTargets.Contains(
                target,
                ReferenceEqualityComparer.Instance))
            {
                assigningTargets.Add(target);
            }
        }
    }

    private static string ExpandDependsOnTargets(
        ProjectInstance project,
        MSBuildTarget target,
        IReadOnlyDictionary<string, IReadOnlyList<MSBuildTarget>>
            propertyAssignments) =>
        ExpandTargetReferences(
            project,
            target,
            target.DependsOnTargets,
            nameof(target.DependsOnTargets),
            propertyAssignments);

    private static string ExpandTargetReferences(
        ProjectInstance project,
        MSBuildTarget target,
        string expression,
        string attributeName,
        IReadOnlyDictionary<string, IReadOnlyList<MSBuildTarget>>
            propertyAssignments)
    {
        foreach (Match match in s_propertyReference.Matches(expression))
        {
            var propertyName = match.Groups["property"].Value;

            if (!propertyAssignments.TryGetValue(
                propertyName,
                out var assigningTargets))
            {
                continue;
            }

            throw Unsupported(
                $"{attributeName} on target '{target.Name}' references " +
                $"property '{propertyName}', which is assigned by target " +
                $"{FormatTargetNames(assigningTargets)}. Target orchestration " +
                "must be fixed after project evaluation and cannot depend on " +
                "target execution");
        }

        return project.ExpandString(expression);
    }

    private static string FormatTargetNames(
        IReadOnlyList<MSBuildTarget> targets) =>
        string.Join(
            ", ",
            targets.Select(target => $"'{target.Name}'"));

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
        IReadOnlyDictionary<string, MSBuildTarget> targetsByName,
        List<MissingTargetRegistration> missingRegistrations)
    {
        var result = new List<MSBuildTarget>();

        foreach (var targetName in expression.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries))
        {
            if (!targetsByName.TryGetValue(targetName, out var target))
            {
                missingRegistrations.Add(
                    new MissingTargetRegistration(
                        declaringTarget,
                        targetName,
                        attributeName));
                continue;
            }

            AddDistinct(result, target);
        }

        return result;
    }

    private static List<MSBuildTarget> ResolveDependsOnTargetList(
        MSBuildTarget declaringTarget,
        string expression,
        IReadOnlyDictionary<string, MSBuildTarget> targetsByName,
        List<MissingTargetDependency> missingDependencies)
    {
        var result = new List<MSBuildTarget>();

        foreach (var targetName in expression.Split(
            ';',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries))
        {
            if (!targetsByName.TryGetValue(targetName, out var target))
            {
                missingDependencies.Add(
                    new MissingTargetDependency(
                        declaringTarget,
                        targetName));
                continue;
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

    private static IReadOnlyList<Value> GetTargetOutputs(
        OperationGraph graph,
        TranslationContext context,
        Value<bool>? targetCondition)
    {
        var outputs = new HashSet<Value>(ReferenceEqualityComparer.Instance);

        foreach (var value in context.Properties.Values.Cast<Value>()
            .Concat(context.Items.Values))
        {
            if (graph.GetProducer(value) is not null)
            {
                outputs.Add(value);
            }
        }

        if (targetCondition is not null)
        {
            outputs.Add(targetCondition);
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

        if (task.Name.Equals("ToyCompile", StringComparison.OrdinalIgnoreCase))
        {
            TranslateToyCompile(task, context);
            return;
        }

        if (task.Name.Equals("Message", StringComparison.OrdinalIgnoreCase))
        {
            TranslateMessage(task, context);
            return;
        }

        throw Unsupported($"task '{task.Name}'");
    }

    private static void TranslateToyCompile(
        ProjectTaskInstance task,
        TranslationContext context)
    {
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

    private static void TranslateMessage(
        ProjectTaskInstance task,
        TranslationContext context)
    {
        if (task.Outputs.Count > 0)
        {
            throw Unsupported("Message outputs");
        }

        var text = context.ResolvePropertyExpression(
            GetRequiredParameter(task, "Text"));
        var importance = context.ResolvePropertyExpression(
            GetParameter(task, "Importance") ?? "normal");

        context.AddOperation(
            new MessageOperation(
                text,
                importance,
                context.CreateControl()));
    }

    private static string GetRequiredParameter(
        ProjectTaskInstance task,
        string parameterName)
    {
        var value = GetParameter(task, parameterName);

        if (value is null)
        {
            throw new InvalidOperationException(
                $"{task.Name} requires the {parameterName} parameter.");
        }

        return value;
    }

    private static string? GetParameter(
        ProjectTaskInstance task,
        string parameterName)
    {
        var parameter = task.Parameters.FirstOrDefault(
            pair => pair.Key.Equals(
                parameterName,
                StringComparison.OrdinalIgnoreCase));

        return parameter.Key is null ? null : parameter.Value;
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

    private static NotSupportedException Unsupported(string construct) =>
        new($"The restricted MSBuild translator does not support {construct}.");

    private sealed record TargetBody(
        IReadOnlyList<StateRead> Reads,
        IReadOnlyList<StateWrite> Writes,
        IReadOnlyList<Value> Outputs,
        OperationGraph Graph);

    private sealed record TargetLinks(
        IReadOnlyDictionary<MSBuildTarget, IReadOnlyList<MSBuildTarget>> Preludes,
        IReadOnlyDictionary<MSBuildTarget, IReadOnlyList<MSBuildTarget>> Epilogues,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<MissingTargetDependency> MissingDependencies);

    private sealed record MissingTargetDependency(
        MSBuildTarget DeclaringTarget,
        string TargetName);

    private sealed record MissingTargetRegistration(
        MSBuildTarget DeclaringTarget,
        string TargetName,
        string AttributeName);

    private sealed record TargetStateAccess(
        IReadOnlySet<string> ReadProperties,
        IReadOnlySet<string> WriteProperties,
        IReadOnlySet<string> ReadItems,
        IReadOnlySet<string> WriteItems);

    private sealed class TranslationContext
    {
        public TranslationContext(
            IReadOnlyDictionary<string, Value<string>> properties,
            IReadOnlyDictionary<string, Value<IReadOnlyList<string>>> items)
        {
            Properties = new Dictionary<string, Value<string>>(
                properties,
                StringComparer.OrdinalIgnoreCase);
            Items =
                new Dictionary<string, Value<IReadOnlyList<string>>>(
                    items,
                    StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, Value<string>> Properties { get; }

        public Dictionary<string, Value<IReadOnlyList<string>>> Items { get; }

        public Value<OrderToken>? CurrentOrderToken { get; private set; }

        private Value<GuardToken>? TargetGuard { get; set; }

        public List<DagOperation> Operations { get; } = [];

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

            throw new InvalidOperationException(
                $"Item state '{itemType}' was not declared as a target read.");
        }

        private Value<string> GetProperty(string propertyName)
        {
            if (Properties.TryGetValue(propertyName, out var value))
            {
                return value;
            }

            throw new InvalidOperationException(
                $"Property state '{propertyName}' was not declared as a target read.");
        }

        private Value<T> AddConstant<T>(T content, bool ordered = true)
        {
            var operation = new ConstantOperation<T>(
                content,
                ordered ? CreateControl() : null);
            AddOperation(operation, ordered);
            return operation.Result;
        }
    }

    private static bool ContainsReference(string expression) =>
        expression.Contains("$(", StringComparison.Ordinal) ||
        expression.Contains("@(", StringComparison.Ordinal) ||
        expression.Contains("%(", StringComparison.Ordinal);
}
