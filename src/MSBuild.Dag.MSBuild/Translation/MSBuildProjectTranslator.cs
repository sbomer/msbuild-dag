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
    private static readonly Regex s_itemIdentityCondition = new(
        @"^\s*'%\((?<item>[^.()]+)\.Identity\)'\s*==\s*'(?<literal>[^']*)'\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_itemListCondition = new(
        @"^\s*'@\((?<item>[^)]+)\)'\s*(?<operator>==|!=)\s*'(?<literal>[^']*)'\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_itemMetadataCondition = new(
        @"^\s*'%\((?:(?<item>[^.()]+)\.)?(?<metadata>[^)]+)\)'\s*(?<operator>==|!=)\s*'(?<literal>[^']*)'\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_itemMetadataReference = new(
        @"%\((?:(?<item>[^.()]+)\.)?(?<metadata>[^)]+)\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_itemMetadataTransform = new(
        @"^\s*@\((?<item>[^()]+)->'%\((?<metadata>[^)]+)\)'(?:,\s*'(?<separator>[^']*)')?\)\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_propertyReference = new(
        @"\$\((?<property>[^()]+)\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_unescapeItemsExpression = new(
        @"^\$\(\[MSBuild\]::Unescape\(\$\((?<property>[^.()]+)(?<replacements>(?:\.Replace\('[^']*',\s*'[^']*'\))+)\)\)\)$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_stringReplacement = new(
        @"\.Replace\('(?<old>[^']*)',\s*'(?<new>[^']*)'\)",
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
            new Dictionary<string, StateLocation<IReadOnlyList<MSBuildItem>>>(
                StringComparer.OrdinalIgnoreCase);
        var valueSymbols = new ValueSymbolTableBuilder();

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
                    new StateLocation<IReadOnlyList<MSBuildItem>>());
            }
        }

        var evaluation = new EvaluationSnapshot(
        [
            .. propertyLocations.Select(
                pair => new StateInitialization<string>(
                    pair.Value,
                    projectInstance.GetPropertyValue(pair.Key))),
            .. itemLocations.Select(
                pair => new StateInitialization<IReadOnlyList<MSBuildItem>>(
                    pair.Value,
                    projectInstance.GetItems(pair.Key)
                        .Select(CreateItem)
                        .ToArray())),
        ]);

        foreach (var initialization in evaluation.Initializations)
        {
            var property = propertyLocations.FirstOrDefault(
                pair => ReferenceEquals(pair.Value, initialization.Location));
            if (property.Key is not null)
            {
                valueSymbols.Add(
                    initialization.InitialValue.Value,
                    $"$({property.Key})");
                continue;
            }

            var item = itemLocations.First(
                pair => ReferenceEquals(pair.Value, initialization.Location));
            valueSymbols.Add(
                initialization.InitialValue.Value,
                $"@({item.Key})");
        }
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
                name => new StateRead<IReadOnlyList<MSBuildItem>>(
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
                    StringComparer.OrdinalIgnoreCase),
                valueSymbols);

            foreach (var (name, read) in propertyReads)
            {
                valueSymbols.Add(read.Value, $"$({name})");
            }

            foreach (var (name, read) in itemReads)
            {
                valueSymbols.Add(read.Value, $"@({name})");
            }
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
                            name => new StateWrite<IReadOnlyList<MSBuildItem>>(
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
            var body = targetBodies[sourceTarget];
            createdDefinitions.Add(
                sourceTarget,
                new TargetDefinition(
                    [],
                    body.Reads,
                    body.Writes,
                    body.Outputs,
                    body.Graph,
                    []));
        }

        var definitionPreludes = new Dictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);
        var definitionEpilogues = new Dictionary<
            TargetDefinition,
            IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);

        foreach (var sourceTarget in sourceTargets)
        {
            definitionPreludes.Add(
                createdDefinitions[sourceTarget],
                links.Preludes[sourceTarget]
                    .Select(reference => createdDefinitions[reference])
                    .ToArray());
            definitionEpilogues.Add(
                createdDefinitions[sourceTarget],
                links.Epilogues[sourceTarget]
                    .Select(reference => createdDefinitions[reference])
                    .ToArray());
        }

        BuildLinkResult linked;
        var definition = new BuildDefinition(
            evaluation,
            sourceTargets
                .Select(target => createdDefinitions[target])
                .ToArray(),
            definitionPreludes,
            definitionEpilogues);

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
            var linkedTarget =
                linked.Targets[createdDefinitions[sourceTarget]];

            for (var index = 0;
                index < linkedTarget.Body.Outputs.Count;
                index++)
            {
                valueSymbols.Copy(
                    linkedTarget.Body.Outputs[index],
                    linkedTarget.Outputs[index]);
            }

            targetDefinitions.Add(
                sourceTarget.Name,
                createdDefinitions[sourceTarget]);
            targets.Add(
                sourceTarget.Name,
                linkedTarget);
        }

        var properties = new Dictionary<string, Value<string>>(
            StringComparer.OrdinalIgnoreCase);
        var items =
            new Dictionary<string, Value<IReadOnlyList<MSBuildItem>>>(
                StringComparer.OrdinalIgnoreCase);
        var state = linked.GetStateAfter(
            createdDefinitions[projectInstance.Targets[targetName]]);

        foreach (var (name, location) in propertyLocations)
        {
            properties.Add(name, (Value<string>)state[location]);
            valueSymbols.Add(state[location], $"$({name})");
        }

        foreach (var (name, location) in itemLocations)
        {
            items.Add(
                name,
                (Value<IReadOnlyList<MSBuildItem>>)state[location]);
            valueSymbols.Add(state[location], $"@({name})");
        }

        return new TranslationResult(
            definition,
            linked.Program,
            targetDefinitions,
            targets,
            properties,
            items,
            valueSymbols.Build(),
            targetConditions,
            links.Warnings);

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

        return new TargetLinks(
            CopyLists(preludes),
            CopyLists(epilogues),
            warnings,
            missingDependencies);
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
            AddConditionRead(target.Condition);
        }

        foreach (var child in target.Children)
        {
            switch (child)
            {
                case ProjectPropertyGroupTaskInstance propertyGroup:
                    if (!string.IsNullOrWhiteSpace(propertyGroup.Condition))
                    {
                        AddConditionRead(propertyGroup.Condition);
                    }

                    foreach (var property in propertyGroup.Properties)
                    {
                        AddPropertyExpressionRead(property.Value);

                        if (!string.IsNullOrWhiteSpace(propertyGroup.Condition) ||
                            !string.IsNullOrWhiteSpace(property.Condition))
                        {
                            AddPropertyRead(property.Name);
                        }

                        if (!string.IsNullOrWhiteSpace(property.Condition))
                        {
                            AddConditionRead(property.Condition);
                        }

                        writeProperties.Add(property.Name);
                    }

                    break;

                case ProjectItemGroupTaskInstance itemGroup:
                    foreach (var item in itemGroup.Items)
                    {
                        AddItemRead(item.ItemType);
                        AddItemExpressionRead(item.Include);
                        AddItemExpressionRead(item.Exclude);
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
                        AddMessageExpressionRead(
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

        void AddMessageExpressionRead(string expression)
        {
            var transform = s_itemMetadataTransform.Match(expression);

            if (transform.Success)
            {
                AddItemRead(transform.Groups["item"].Value);
                return;
            }

            AddPropertyExpressionRead(expression);
        }

        void AddConditionRead(string expression)
        {
            var match = s_comparisonCondition.Match(expression);

            if (match.Success)
            {
                AddPropertyRead(match.Groups["property"].Value);
                return;
            }

            match = s_itemIdentityCondition.Match(expression);

            if (match.Success)
            {
                AddItemRead(match.Groups["item"].Value);
                return;
            }

            match = s_itemListCondition.Match(expression);

            if (match.Success)
            {
                AddItemRead(match.Groups["item"].Value);
            }
        }

        void AddItemExpressionRead(string expression)
        {
            if (TryGetReference(expression, "@(", out var itemType))
            {
                AddItemRead(itemType);
            }
            else if (TryParseUnescapeItemsExpression(
                expression,
                out var propertyName,
                out _))
            {
                AddPropertyRead(propertyName);
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
            TranslateConditionalPropertyGroup(propertyGroup, context);
            return;
        }

        foreach (var property in propertyGroup.Properties)
        {
            if (string.IsNullOrWhiteSpace(property.Condition))
            {
                context.SetProperty(
                    property.Name,
                    context.ResolvePropertyExpression(property.Value));
                continue;
            }

            var previousValue = context.GetProperty(property.Name);
            var condition = context.TranslateCondition(property.Condition);
            var inputs = new List<Value<string>> { previousValue };
            var whenTrueInputs = new List<Value<string>>
            {
                new(),
            };
            var whenFalseInputs = new List<Value<string>>
            {
                new(),
            };
            var whenTrueOperations = new List<DagOperation>();
            Value<string> whenTrueOutput;

            context.CopySymbols(previousValue, whenTrueInputs[0]);
            context.CopySymbols(previousValue, whenFalseInputs[0]);

            if (TryGetReference(
                property.Value,
                "$(",
                out var referencedProperty))
            {
                var referencedValue =
                    context.GetProperty(referencedProperty);
                var referencedIndex = inputs.FindIndex(
                    value => ReferenceEquals(value, referencedValue));

                if (referencedIndex < 0)
                {
                    referencedIndex = inputs.Count;
                    inputs.Add(referencedValue);
                    whenTrueInputs.Add(new Value<string>());
                    whenFalseInputs.Add(new Value<string>());
                    context.CopySymbols(
                        referencedValue,
                        whenTrueInputs[referencedIndex]);
                    context.CopySymbols(
                        referencedValue,
                        whenFalseInputs[referencedIndex]);
                }

                whenTrueOutput = whenTrueInputs[referencedIndex];
            }
            else
            {
                if (ContainsReference(property.Value))
                {
                    throw Unsupported(
                        $"property expression '{property.Value}'");
                }

                var constant = new ReplaceOperation<string>(
                    whenTrueInputs[0],
                    property.Value,
                    context.TargetGuard);
                whenTrueOperations.Add(constant);
                whenTrueOutput = constant.Result;
            }

            var result = new Value<string>();
            context.AddPropertySymbol(property.Name, whenTrueOutput);
            var conditional = new ConditionalRegionOperation(
                condition,
                inputs.Cast<Value>().ToArray(),
                new OperationGraph(
                    whenTrueInputs.Cast<Value>().ToArray(),
                    whenTrueOperations,
                    [whenTrueOutput]),
                new OperationGraph(
                    whenFalseInputs.Cast<Value>().ToArray(),
                    [],
                    [whenFalseInputs[0]]),
                [result]);
            context.AddOperation(conditional);
            context.SetProperty(property.Name, result);
        }
    }

    private static void TranslateConditionalPropertyGroup(
        ProjectPropertyGroupTaskInstance propertyGroup,
        TranslationContext context)
    {
        if (propertyGroup.Properties.Any(
            property => !string.IsNullOrWhiteSpace(property.Condition)))
        {
            throw Unsupported(
                "property conditions inside a conditioned PropertyGroup");
        }

        if (propertyGroup.Properties.Any(
            property => ContainsReference(property.Value)))
        {
            throw Unsupported(
                "property references inside a conditioned PropertyGroup");
        }

        var condition = context.TranslateCondition(propertyGroup.Condition);
        var inputs = new List<Value>();
        var whenTrueInputs = new List<Value>();
        var whenFalseInputs = new List<Value>();
        var whenTrueOperations = new List<DagOperation>();
        var whenTrueOutputs = new List<Value>();
        var whenFalseOutputs = new List<Value>();
        var outputs = new List<Value>();
        var properties = new List<(string Name, Value<string> Result)>();

        foreach (var property in propertyGroup.Properties)
        {
            var previousValue = context.GetProperty(property.Name);
            var whenTruePrevious = new Value<string>();
            var whenFalsePrevious = new Value<string>();
            var assignedValue = new ReplaceOperation<string>(
                whenTruePrevious,
                property.Value,
                context.TargetGuard);
            var result = new Value<string>();

            context.CopySymbols(previousValue, whenTruePrevious);
            context.CopySymbols(previousValue, whenFalsePrevious);
            context.AddPropertySymbol(property.Name, assignedValue.Result);
            inputs.Add(previousValue);
            whenTrueInputs.Add(whenTruePrevious);
            whenFalseInputs.Add(whenFalsePrevious);
            whenTrueOperations.Add(assignedValue);
            whenTrueOutputs.Add(assignedValue.Result);
            whenFalseOutputs.Add(whenFalsePrevious);
            outputs.Add(result);
            properties.Add((property.Name, result));
        }

        var conditional = new ConditionalRegionOperation(
            condition,
            inputs,
            new OperationGraph(
                whenTrueInputs,
                whenTrueOperations,
                whenTrueOutputs),
            new OperationGraph(
                whenFalseInputs,
                [],
                whenFalseOutputs),
            outputs);
        context.AddOperation(conditional);

        foreach (var property in properties)
        {
            context.SetProperty(property.Name, property.Result);
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
            if (IsMetadataUpdate(item))
            {
                if (item.Metadata.Any(
                    metadata =>
                        !string.IsNullOrWhiteSpace(metadata.Condition) ||
                        !IsSupportedMetadataExpression(
                            item.ItemType,
                            metadata.Value)))
                {
                    throw Unsupported(
                        $"item operation {FormatItemOperation(item)}; metadata " +
                        "updates currently support only literal text and " +
                        "metadata references on the updated item");
                }

                Value<IReadOnlyList<bool>>? mask = null;

                if (!string.IsNullOrWhiteSpace(item.Condition))
                {
                    if (!TryParseItemMetadataCondition(
                        item.ItemType,
                        item.Condition,
                        out var metadataName,
                        out var comparison,
                        out var literal))
                    {
                        throw Unsupported(
                            $"condition on item operation {FormatItemOperation(item)}");
                    }

                    var metadataValues = new GetItemMetadataOperation(
                        context.GetItems(item.ItemType),
                        metadataName,
                        context.TargetGuard);
                    var expected = new ConstantOperation<string>(
                        literal,
                        context.TargetGuard);
                    var equal = new EqualItemValuesOperation<string>(
                        metadataValues.Result,
                        expected.Result,
                        context.TargetGuard);
                    context.AddOperation(metadataValues);
                    context.AddOperation(expected);
                    context.AddOperation(equal);
                    mask = equal.Result;

                    if (comparison is ItemMetadataComparison.NotEqual)
                    {
                        var not = new NotItemValuesOperation(
                            mask,
                            context.TargetGuard);
                        context.AddOperation(not);
                        mask = not.Result;
                    }
                }

                TranslateItemMetadataUpdate(item, mask, context);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.Condition))
            {
                throw Unsupported(
                    $"condition on item operation {FormatItemOperation(item)}");
            }

            if (string.IsNullOrWhiteSpace(item.Include) ||
                !string.IsNullOrWhiteSpace(item.Remove) ||
                item.Metadata.Count > 0 ||
                !string.IsNullOrWhiteSpace(item.MatchOnMetadata) ||
                !string.IsNullOrWhiteSpace(item.MatchOnMetadataOptions) ||
                !string.IsNullOrWhiteSpace(item.KeepMetadata) ||
                !string.IsNullOrWhiteSpace(item.RemoveMetadata) ||
                !string.IsNullOrWhiteSpace(item.KeepDuplicates))
            {
                throw Unsupported(
                    $"item operation {FormatItemOperation(item)}; only Include " +
                    "with optional Exclude and metadata-only updates are " +
                    "currently supported");
            }

            var existingItems = context.GetItems(item.ItemType);
            var appendedItems = context.ResolveItemsExpression(item.Include);
            context.AddItemSymbol(item.ItemType, appendedItems);

            if (!string.IsNullOrWhiteSpace(item.Exclude))
            {
                var excludedItems =
                    context.ResolveItemsExpression(item.Exclude);
                var exclude = new ExcludeItemsOperation(
                    appendedItems,
                    excludedItems,
                    context.TargetGuard);
                context.AddOperation(exclude);
                appendedItems = exclude.Result;
            }

            var concat = new ConcatItemsOperation(
                existingItems,
                appendedItems,
                context.TargetGuard);

            context.AddOperation(concat);
            context.SetItems(item.ItemType, concat.Result);
        }
    }

    private static bool IsMetadataUpdate(
        ProjectItemGroupTaskItemInstance item) =>
        string.IsNullOrWhiteSpace(item.Include) &&
        string.IsNullOrWhiteSpace(item.Exclude) &&
        string.IsNullOrWhiteSpace(item.Remove) &&
        string.IsNullOrWhiteSpace(item.MatchOnMetadata) &&
        string.IsNullOrWhiteSpace(item.MatchOnMetadataOptions) &&
        string.IsNullOrWhiteSpace(item.KeepMetadata) &&
        string.IsNullOrWhiteSpace(item.RemoveMetadata) &&
        string.IsNullOrWhiteSpace(item.KeepDuplicates) &&
        item.Metadata.Count > 0;

    private static bool TryParseItemMetadataCondition(
        string itemType,
        string expression,
        out string metadataName,
        out ItemMetadataComparison comparison,
        out string literal)
    {
        var match = s_itemMetadataCondition.Match(expression);

        if (!match.Success ||
            (match.Groups["item"].Success &&
             !match.Groups["item"].Value.Equals(
                 itemType,
                 StringComparison.OrdinalIgnoreCase)))
        {
            metadataName = string.Empty;
            comparison = default;
            literal = string.Empty;
            return false;
        }

        metadataName = match.Groups["metadata"].Value;
        comparison = match.Groups["operator"].Value == "=="
            ? ItemMetadataComparison.Equal
            : ItemMetadataComparison.NotEqual;
        literal = match.Groups["literal"].Value;
        return true;
    }

    private static void TranslateItemMetadataUpdate(
        ProjectItemGroupTaskItemInstance item,
        Value<IReadOnlyList<bool>>? mask,
        TranslationContext context)
    {
        var items = context.GetItems(item.ItemType);

        foreach (var metadata in item.Metadata)
        {
            var metadataValues = TranslateItemMetadataExpression(
                items,
                metadata.Value,
                context);
            var setMetadata = new SetItemMetadataOperation(
                items,
                metadata.Name,
                metadataValues,
                mask,
                context.TargetGuard);
            context.AddOperation(setMetadata);
            items = setMetadata.Result;
            context.AddItemSymbol(item.ItemType, items);
        }

        context.SetItems(item.ItemType, items);
    }

    private static Value<IReadOnlyList<string>>
        TranslateItemMetadataExpression(
            Value<IReadOnlyList<MSBuildItem>> items,
            string expression,
            TranslationContext context)
    {
        Value<IReadOnlyList<string>>? result = null;
        var position = 0;

        foreach (Match match in s_itemMetadataReference.Matches(expression))
        {
            AppendLiteral(expression[position..match.Index]);

            var metadata = new GetItemMetadataOperation(
                items,
                match.Groups["metadata"].Value,
                context.TargetGuard);
            context.AddOperation(metadata);
            Append(metadata.Result);
            position = match.Index + match.Length;
        }

        AppendLiteral(expression[position..]);

        if (result is null)
        {
            AppendLiteral(string.Empty, includeEmpty: true);
        }

        return result!;

        void AppendLiteral(
            string literal,
            bool includeEmpty = false)
        {
            if (literal.Length == 0 && !includeEmpty)
            {
                return;
            }

            var constant = new ConstantOperation<string>(
                literal,
                context.TargetGuard);
            var broadcast = new BroadcastItemValueOperation<string>(
                items,
                constant.Result,
                context.TargetGuard);
            context.AddOperation(constant);
            context.AddOperation(broadcast);
            Append(broadcast.Result);
        }

        void Append(Value<IReadOnlyList<string>> value)
        {
            if (result is null)
            {
                result = value;
                return;
            }

            var concat = new ConcatItemValuesOperation(
                result,
                value,
                context.TargetGuard);
            context.AddOperation(concat);
            result = concat.Result;
        }
    }

    private static bool IsSupportedMetadataExpression(
        string itemType,
        string expression)
    {
        if (expression.Contains("$(", StringComparison.Ordinal) ||
            expression.Contains("@(", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (Match match in s_itemMetadataReference.Matches(expression))
        {
            if (match.Groups["item"].Success &&
                !match.Groups["item"].Value.Equals(
                    itemType,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return !s_itemMetadataReference.Replace(expression, string.Empty)
            .Contains("%(", StringComparison.Ordinal);
    }

    private static string FormatItemOperation(
        ProjectItemGroupTaskItemInstance item)
    {
        var attributes = new List<string>();

        AddAttribute("Include", item.Include);
        AddAttribute("Exclude", item.Exclude);
        AddAttribute("Remove", item.Remove);
        AddAttribute("MatchOnMetadata", item.MatchOnMetadata);
        AddAttribute("MatchOnMetadataOptions", item.MatchOnMetadataOptions);
        AddAttribute("KeepMetadata", item.KeepMetadata);
        AddAttribute("RemoveMetadata", item.RemoveMetadata);
        AddAttribute("KeepDuplicates", item.KeepDuplicates);
        AddAttribute("Condition", item.Condition);

        foreach (var metadata in item.Metadata)
        {
            AddAttribute(metadata.Name, metadata.Value);
        }

        return $"<{item.ItemType}" +
            (attributes.Count == 0
                ? string.Empty
                : " " + string.Join(" ", attributes)) +
            " />";

        void AddAttribute(string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                attributes.Add(
                    $"{name}=\"{value.Replace("\"", "&quot;", StringComparison.Ordinal)}\"");
            }
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
        var itemSources = context.ResolveItemsExpression(sourcesExpression);
        var identities = new ProjectItemIdentitiesOperation(
            itemSources,
            context.TargetGuard);
        context.AddOperation(identities);
        var configuration =
            context.ResolvePropertyExpression(configurationExpression);
        var compile = new ToyCompileOperation(
            identities.Result,
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

            context.SetProperty(propertyOutput.PropertyName, compile.Assembly);
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

        var textExpression = GetRequiredParameter(task, "Text");
        var transform = s_itemMetadataTransform.Match(textExpression);
        Value<string> text;

        if (transform.Success)
        {
            var metadata = new GetItemMetadataOperation(
                context.GetItems(transform.Groups["item"].Value),
                transform.Groups["metadata"].Value,
                context.TargetGuard);
            var separator = new ConstantOperation<string>(
                transform.Groups["separator"].Success
                    ? transform.Groups["separator"].Value
                    : ";",
                context.TargetGuard);
            var join = new JoinItemValuesOperation(
                metadata.Result,
                separator.Result,
                context.TargetGuard);
            context.AddOperation(metadata);
            context.AddOperation(separator);
            context.AddOperation(join);
            text = join.Result;
        }
        else
        {
            text = context.ResolvePropertyExpression(textExpression);
        }

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

    private static bool TryParseUnescapeItemsExpression(
        string expression,
        out string propertyName,
        out IReadOnlyList<StringReplacement> replacements)
    {
        var expressionMatch = s_unescapeItemsExpression.Match(expression);

        if (!expressionMatch.Success)
        {
            propertyName = string.Empty;
            replacements = [];
            return false;
        }

        propertyName = expressionMatch.Groups["property"].Value;
        replacements = s_stringReplacement.Matches(
                expressionMatch.Groups["replacements"].Value)
            .Select(match => new StringReplacement(
                match.Groups["old"].Value,
                match.Groups["new"].Value))
            .ToArray();
        return true;
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

    private static MSBuildItem CreateItem(ProjectItemInstance item) =>
        new(
            item.EvaluatedInclude,
            item.Metadata.Select(
                static metadata =>
                    KeyValuePair.Create(
                        metadata.Name,
                        metadata.EvaluatedValue)));

    private sealed class TranslationContext
    {
        public TranslationContext(
            IReadOnlyDictionary<string, Value<string>> properties,
            IReadOnlyDictionary<string, Value<IReadOnlyList<MSBuildItem>>> items,
            ValueSymbolTableBuilder valueSymbols)
        {
            Properties = new Dictionary<string, Value<string>>(
                properties,
                StringComparer.OrdinalIgnoreCase);
            Items =
                new Dictionary<string, Value<IReadOnlyList<MSBuildItem>>>(
                    items,
                    StringComparer.OrdinalIgnoreCase);
            ValueSymbols = valueSymbols;
        }

        public Dictionary<string, Value<string>> Properties { get; }

        public Dictionary<string, Value<IReadOnlyList<MSBuildItem>>> Items
        {
            get;
        }

        private ValueSymbolTableBuilder ValueSymbols { get; }

        public Value<OrderToken>? CurrentOrderToken { get; private set; }

        public Value<GuardToken>? TargetGuard { get; private set; }

        public List<DagOperation> Operations { get; } = [];

        public void AddPropertySymbol(string name, Value value) =>
            ValueSymbols.Add(value, $"$({name})");

        public void AddItemSymbol(string name, Value value) =>
            ValueSymbols.Add(value, $"@({name})");

        public void CopySymbols(Value source, Value target) =>
            ValueSymbols.Copy(source, target);

        public void SetProperty(string name, Value<string> value)
        {
            Properties[name] = value;
            AddPropertySymbol(name, value);
        }

        public void SetItems(
            string name,
            Value<IReadOnlyList<MSBuildItem>> value)
        {
            Items[name] = value;
            AddItemSymbol(name, value);
        }

        public void SetTargetGuard(Value<GuardToken> guard) =>
            TargetGuard = guard;

        public OperationControl CreateControl() =>
            new(
                CurrentOrderToken is null ? TargetGuard : null,
                CurrentOrderToken);

        public void AddOperation(DagOperation operation)
        {
            Operations.Add(operation);

            if (operation is IOrderedOperation
                {
                    OrderOutput: not null,
                } orderedOperation)
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

            if (match.Success)
            {
                var left = GetProperty(match.Groups["property"].Value);
                var right = AddConstant(match.Groups["literal"].Value);

                DagOperation comparison = match.Groups["operator"].Value switch
                {
                    "==" => new EqualOperation<string>(left, right),
                    "!=" => new NotEqualOperation<string>(left, right),
                    _ => throw new InvalidOperationException(
                        "The condition parser produced an unknown comparison operator."),
                };

                AddOperation(comparison);

                return comparison switch
                {
                    EqualOperation<string> equal => equal.Result,
                    NotEqualOperation<string> notEqual => notEqual.Result,
                    _ => throw new InvalidOperationException(
                        "The condition parser produced an unknown comparison operation."),
                };
            }

            match = s_itemIdentityCondition.Match(expression);

            if (match.Success)
            {
                var identities = new ProjectItemIdentitiesOperation(
                    GetItems(match.Groups["item"].Value),
                    TargetGuard);
                AddOperation(identities);
                var contains = new ContainsOperation<string>(
                    identities.Result,
                    AddConstant(match.Groups["literal"].Value));
                AddOperation(contains);
                return contains.Result;
            }

            match = s_itemListCondition.Match(expression);

            if (match.Success &&
                match.Groups["literal"].Value.Length == 0)
            {
                var isEmpty = new IsEmptyOperation<MSBuildItem>(
                    GetItems(match.Groups["item"].Value));
                AddOperation(isEmpty);

                if (match.Groups["operator"].Value == "==")
                {
                    return isEmpty.Result;
                }

                var not = new NotOperation(isEmpty.Result);
                AddOperation(not);
                return not.Result;
            }

            throw Unsupported($"target condition '{expression}'");
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

        public Value<IReadOnlyList<MSBuildItem>> ResolveItemsExpression(
            string expression)
        {
            if (TryGetReference(expression, "@(", out var itemType))
            {
                return GetItems(itemType);
            }

            if (TryParseUnescapeItemsExpression(
                expression,
                out var propertyName,
                out var replacements))
            {
                var operation = new ExpandItemsExpressionOperation(
                    GetProperty(propertyName),
                    replacements,
                    TargetGuard);
                AddOperation(operation);
                return operation.Result;
            }

            if (ContainsReference(expression))
            {
                throw Unsupported($"item expression '{expression}'");
            }

            IReadOnlyList<MSBuildItem> values = expression
                .Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries)
                .Select(static identity => new MSBuildItem(identity))
                .ToArray();

            return AddConstant(values);
        }

        public Value<IReadOnlyList<MSBuildItem>> GetItems(string itemType)
        {
            if (Items.TryGetValue(itemType, out var value))
            {
                return value;
            }

            throw new InvalidOperationException(
                $"Item state '{itemType}' was not declared as a target read.");
        }

        public Value<string> GetProperty(string propertyName)
        {
            if (Properties.TryGetValue(propertyName, out var value))
            {
                return value;
            }

            throw new InvalidOperationException(
                $"Property state '{propertyName}' was not declared as a target read.");
        }

        private Value<T> AddConstant<T>(T content)
        {
            var operation = new ConstantOperation<T>(
                content,
                TargetGuard);
            AddOperation(operation);
            return operation.Result;
        }
    }

    private static bool ContainsReference(string expression) =>
        expression.Contains("$(", StringComparison.Ordinal) ||
        expression.Contains("@(", StringComparison.Ordinal) ||
        expression.Contains("%(", StringComparison.Ordinal);

    private sealed class ValueSymbolTableBuilder
    {
        private readonly Dictionary<Value, List<ValueSymbol>> _symbols =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, int> _nextVersions =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(Value value, string symbol)
        {
            if (!_symbols.TryGetValue(value, out var symbols))
            {
                symbols = [];
                _symbols.Add(value, symbols);
            }

            if (symbols.Any(
                existing => existing.Name.Equals(
                    symbol,
                    StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            var version = _nextVersions.GetValueOrDefault(symbol);
            symbols.Add(new ValueSymbol(symbol, version));
            _nextVersions[symbol] = version + 1;
        }

        public void Copy(Value source, Value target)
        {
            if (!_symbols.TryGetValue(source, out var symbols))
            {
                return;
            }

            foreach (var symbol in symbols)
            {
                Add(target, symbol.Name);
            }
        }

        public IReadOnlyDictionary<Value, IReadOnlyList<ValueSymbol>> Build()
        {
            var result =
                new Dictionary<Value, IReadOnlyList<ValueSymbol>>(
                    ReferenceEqualityComparer.Instance);

            foreach (var (value, symbols) in _symbols)
            {
                result.Add(value, symbols.ToArray());
            }

            return result;
        }
    }
}
