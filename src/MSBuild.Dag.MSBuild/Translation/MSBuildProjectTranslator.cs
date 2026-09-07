using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;
using MSBuild.Dag.Core;
using System.Text.RegularExpressions;
using DagOperation = MSBuild.Dag.Core.Operation;
using MSBuildTarget = Microsoft.Build.Execution.ProjectTargetInstance;

namespace MSBuild.Dag.MSBuild;

public sealed class MSBuildProjectTranslator
{
    private readonly HashSet<string> _activeBuildRequests =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, TranslationResult>
        _translatedInvocationPrograms = new(StringComparer.Ordinal);

    private sealed class DeferredTargetTranslationException(string message)
        : NotSupportedException(message);

    private sealed record PropertyReference(
        int Index,
        int Length,
        string Content);

    private sealed record ExpressionReference(
        int Index,
        int Length,
        string? PropertyContent,
        string? ItemName);

    private static readonly Regex s_itemIdentityCondition = new(
        @"^\s*'%\((?<item>[^.()]+)\.Identity\)'\s*==\s*'(?<literal>[^']*)'\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_itemListCondition = new(
        @"^\s*'@\((?<item>[^)]+)\)'\s*(?<operator>==|!=)\s*'(?<literal>[^']*)'\s*$",
        RegexOptions.CultureInvariant);
    private static readonly Regex s_existsCondition = new(
        @"^\s*Exists\(\s*'(?<path>[^']*)'\s*\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex s_escapeSequence = new(
        @"%[0-9a-fA-F]{2}",
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
    private static readonly Regex s_itemReference = new(
        @"@\((?<item>[A-Za-z_][A-Za-z0-9_.-]*)\)",
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
        Action<string>? reportWarning = null) =>
        Translate(
            projectPath,
            targetName,
            new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase),
            reportWarning);

    private TranslationResult Translate(
        string projectPath,
        string targetName,
        IReadOnlyDictionary<string, string> globalProperties,
        Action<string>? reportWarning)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ArgumentNullException.ThrowIfNull(globalProperties);

        projectPath = Path.GetFullPath(projectPath);
        var requestKey = CreateBuildRequestKey(
            projectPath,
            globalProperties);

        if (!_activeBuildRequests.Add(requestKey))
        {
            throw Unsupported(
                $"recursive MSBuild invocation of '{projectPath}'");
        }

        try
        {
            return TranslateCore(
                projectPath,
                targetName,
                globalProperties,
                reportWarning);
        }
        finally
        {
            _activeBuildRequests.Remove(requestKey);
        }
    }

    private TranslationResult TranslateCore(
        string projectPath,
        string targetName,
        IReadOnlyDictionary<string, string> globalProperties,
        Action<string>? reportWarning)
    {
        using var projectCollection = new ProjectCollection();
        var project = projectCollection.LoadProject(
            projectPath,
            new Dictionary<string, string>(
                globalProperties,
                StringComparer.OrdinalIgnoreCase),
            toolsVersion: null);
        var projectInstance = project.CreateProjectInstance();
        var warnings = new List<string>();

        if (!projectInstance.Targets.ContainsKey(targetName))
        {
            throw new ArgumentException(
                $"Target '{targetName}' does not exist.",
                nameof(targetName));
        }

        var sourceTargets = projectInstance.Targets.Values.ToArray();
        var targetPropertyAssignments =
            GetTargetPropertyAssignments(
                sourceTargets
                    .Where(target => string.IsNullOrWhiteSpace(target.Condition))
                    .ToArray());
        var msbuildInvocations =
            new Dictionary<ProjectTaskInstance, PreparedMSBuildInvocation>(
                ReferenceEqualityComparer.Instance);
        var links = GetTargetLinks(
            projectInstance,
            sourceTargets,
            targetPropertyAssignments,
            ReportWarning);
        var stateAccesses = new Dictionary<MSBuildTarget, TargetStateAccess>(
            ReferenceEqualityComparer.Instance);
        var deferredTargets = new Dictionary<MSBuildTarget, string>(
            ReferenceEqualityComparer.Instance);
        var propertyLocations =
            new Dictionary<string, Location<string>>(
                StringComparer.OrdinalIgnoreCase);
        var itemLocations =
            new Dictionary<string, Location<IReadOnlyList<MSBuildItem>>>(
                StringComparer.OrdinalIgnoreCase);
        var fileLocations = new Dictionary<string, Location<FileContents?>>(
            StringComparer.Ordinal);
        var isRunningFromVisualStudioLocation = new Location<string>();
        var valueSymbols = new ValueSymbolTableBuilder();

        foreach (var target in sourceTargets)
        {
            TargetStateAccess access;

            if (!string.IsNullOrWhiteSpace(target.Condition))
            {
                const string reason = "target conditions are not supported";
                ReportWarning(
                    $"Target '{target.Name}' cannot be fully translated: " +
                    $"{reason}. The target will fail if executed.");
                deferredTargets.Add(target, reason);
                access = EmptyTargetStateAccess();
            }
            else
            {
                try
                {
                    access = GetTargetStateAccess(
                        target,
                        ResolveStaticFilePath,
                        PrepareMSBuildInvocation);
                }
                catch (DeferredTargetTranslationException exception)
                {
                    var warning =
                        $"Target '{target.Name}' cannot be fully translated: " +
                        $"{exception.Message} The target will fail if executed.";
                    ReportWarning(warning);
                    deferredTargets.Add(target, exception.Message);
                    access = EmptyTargetStateAccess();
                }
            }

            stateAccesses.Add(target, access);

            foreach (var name in access.ReadProperties
                .Concat(access.WriteProperties))
            {
                propertyLocations.TryAdd(name, new Location<string>());
            }

            foreach (var name in access.ReadItems.Concat(access.WriteItems))
            {
                itemLocations.TryAdd(
                    name,
                    new Location<IReadOnlyList<MSBuildItem>>());
            }

            foreach (var path in access.ReadFiles)
            {
                fileLocations.TryAdd(
                    path,
                    new Location<FileContents?>());
            }
        }

        var evaluatedValues = new Dictionary<Location, object?>(
            ReferenceEqualityComparer.Instance);

        foreach (var (name, location) in propertyLocations)
        {
            evaluatedValues.Add(
                location,
                projectInstance.GetPropertyValue(name));
        }

        foreach (var (name, location) in itemLocations)
        {
            evaluatedValues.Add(
                location,
                projectInstance.GetItems(name)
                    .Select(CreateItem)
                    .ToArray());
        }

        var evaluation = new EvaluationSnapshot(evaluatedValues);
        var targetBodies = new Dictionary<MSBuildTarget, TargetBody>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in sourceTargets)
        {
            if (deferredTargets.TryGetValue(target, out var reason))
            {
                targetBodies.Add(
                    target,
                    new TargetBody(
                        [],
                        [],
                        new OperationGraph(
                        [
                            new UnsupportedTargetOperation(
                                target.Name,
                                reason),
                        ])));
                continue;
            }

            var access = stateAccesses[target];
            var propertyReads = access.ReadProperties.ToDictionary(
                name => name,
                name => new TargetInput<string>(propertyLocations[name]),
                StringComparer.OrdinalIgnoreCase);
            var itemReads = access.ReadItems.ToDictionary(
                name => name,
                name => new TargetInput<IReadOnlyList<MSBuildItem>>(
                    itemLocations[name]),
                StringComparer.OrdinalIgnoreCase);
            var fileReads = access.ReadFiles.ToDictionary(
                path => path,
                path => new TargetInput<FileContents?>(fileLocations[path]),
                StringComparer.Ordinal);
            var isRunningFromVisualStudioRead =
                access.ReadsIsRunningFromVisualStudio
                    ? new TargetInput<string>(
                        isRunningFromVisualStudioLocation)
                    : null;
            var context = new TranslationContext(
                propertyReads.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Value,
                    StringComparer.OrdinalIgnoreCase),
                itemReads.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Value,
                    StringComparer.OrdinalIgnoreCase),
                fileReads.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.Value,
                    StringComparer.Ordinal),
                isRunningFromVisualStudioRead?.Value,
                valueSymbols,
                ResolveStaticFilePath,
                PrepareMSBuildInvocation,
                ReportWarning);

            foreach (var (name, read) in propertyReads)
            {
                valueSymbols.Add(read.Value, $"$({name})");
            }

            foreach (var (name, read) in itemReads)
            {
                valueSymbols.Add(read.Value, $"@({name})");
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
                    propertyReads.Values.Cast<TargetInput>()
                        .Concat(itemReads.Values)
                        .Concat(fileReads.Values)
                        .Concat(
                            isRunningFromVisualStudioRead is null
                                ? []
                                : [isRunningFromVisualStudioRead])
                        .ToArray(),
                    [
                        .. access.WriteProperties.Select(
                            name => new TargetOutput<string>(
                                propertyLocations[name],
                                context.Properties[name])),
                        .. access.WriteItems.Select(
                            name => new TargetOutput<IReadOnlyList<MSBuildItem>>(
                                itemLocations[name],
                                context.Items[name])),
                    ],
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
                    body.Inputs,
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
        var definitionOrderPredecessors = new Dictionary<
            TargetDefinition,
            List<TargetDefinition>>(
                ReferenceEqualityComparer.Instance);

        foreach (var sourceTarget in sourceTargets)
        {
            var targetDefinition = createdDefinitions[sourceTarget];
            definitionPreludes.Add(
                targetDefinition,
                links.Dependencies[sourceTarget]
                    .Concat(links.BeforeTargets[sourceTarget])
                    .Select(reference => createdDefinitions[reference])
                    .ToArray());
            definitionEpilogues.Add(
                targetDefinition,
                links.AfterTargets[sourceTarget]
                    .Select(reference => createdDefinitions[reference])
                    .ToArray());
            definitionOrderPredecessors.Add(
                targetDefinition,
                []);
        }

        foreach (var sourceTarget in sourceTargets)
        {
            foreach (var dependency in links.Dependencies[sourceTarget])
            {
                AddDefinitionPredecessor(
                    definitionOrderPredecessors[
                        createdDefinitions[sourceTarget]],
                    createdDefinitions[dependency]);
            }

            foreach (var beforeTarget in links.BeforeTargets[sourceTarget])
            {
                AddDefinitionPredecessor(
                    definitionOrderPredecessors[
                        createdDefinitions[sourceTarget]],
                    createdDefinitions[beforeTarget]);
            }

            foreach (var afterTarget in links.AfterTargets[sourceTarget])
            {
                var predecessors = definitionOrderPredecessors[
                    createdDefinitions[afterTarget]];
                AddDefinitionPredecessor(
                    predecessors,
                    createdDefinitions[sourceTarget]);
            }
        }

        BuildLinkResult linked;
        var definitionInputs = fileLocations.Values
            .Cast<Location>()
            .Concat(
                stateAccesses.Values.Any(
                    access => access.ReadsIsRunningFromVisualStudio)
                    ? [isRunningFromVisualStudioLocation]
                    : [])
            .ToArray();
        var definition = new BuildDefinition(
            evaluation,
            definitionInputs,
            sourceTargets
                .Select(target => createdDefinitions[target])
                .ToArray(),
            definitionPreludes,
            definitionEpilogues,
            definitionOrderPredecessors.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<TargetDefinition>)pair.Value.ToArray(),
                (IEqualityComparer<TargetDefinition>)
                    ReferenceEqualityComparer.Instance));

        try
        {
            linked = definition.Link();
        }
        catch (ConditionalTargetOutputException exception)
        {
            throw Unsupported(
                $"outputs in conditional target " +
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

        foreach (var (name, location) in propertyLocations)
        {
            valueSymbols.Add(
                linked.InitialValues[location],
                $"$({name})");
        }

        foreach (var (name, location) in itemLocations)
        {
            valueSymbols.Add(
                linked.InitialValues[location],
                $"@({name})");
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
        var files = new Dictionary<string, Value<FileContents?>>(
            StringComparer.Ordinal);
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

        foreach (var (path, location) in fileLocations)
        {
            files.Add(path, (Value<FileContents?>)state[location]);
        }

        var isRunningFromVisualStudio =
            stateAccesses.Values.Any(
                access => access.ReadsIsRunningFromVisualStudio)
                ? (Value<string>)state[isRunningFromVisualStudioLocation]
                : null;

        return new TranslationResult(
            definition,
            linked.Program,
            targetDefinitions,
            targets,
            properties,
            items,
            files,
            isRunningFromVisualStudio,
            valueSymbols.Build(),
            warnings);

        void ReportWarning(string warning)
        {
            warnings.Add(warning);
            reportWarning?.Invoke(warning);
        }

        static TargetStateAccess EmptyTargetStateAccess() =>
            new(
                new HashSet<string>(),
                new HashSet<string>(),
                new HashSet<string>(),
                new HashSet<string>(),
                new HashSet<string>(),
                false);

        string ResolveStaticFilePath(
            string expression,
            string sourceFile)
        {
            if (s_itemMetadataReference.IsMatch(expression))
            {
                throw new DeferredTargetTranslationException(
                    $"file path '{expression}' depends on item metadata.");
            }

            if (s_itemReference.IsMatch(expression))
            {
                throw new DeferredTargetTranslationException(
                    $"file path '{expression}' depends on an item list.");
            }

            if (s_escapeSequence.IsMatch(expression) ||
                expression.IndexOfAny(['*', '?']) >= 0)
            {
                throw Unsupported(
                    $"file path '{expression}'; file paths must be concrete " +
                    "during graph construction");
            }

            var references = FindPropertyReferences(expression);
            var result = expression;

            foreach (var reference in references.Reverse())
            {
                if (reference.Content.Contains('(') ||
                    reference.Content.Contains(')') ||
                    reference.Content.Contains("::", StringComparison.Ordinal))
                {
                    throw Unsupported(
                        $"file path '{expression}'; property " +
                        $"'$({reference.Content})' must be fixed during " +
                        "graph construction");
                }

                if (targetPropertyAssignments.ContainsKey(reference.Content))
                {
                    throw new DeferredTargetTranslationException(
                        $"file path '{expression}' depends on target-assigned " +
                        $"property '$({reference.Content})'.");
                }

                result = result.Remove(reference.Index, reference.Length)
                    .Insert(
                        reference.Index,
                        projectInstance.GetPropertyValue(reference.Content));
            }

            if (ContainsReference(result))
            {
                throw Unsupported(
                    $"file path '{expression}'; file paths must be concrete " +
                    "during graph construction");
            }

            var baseDirectory = Path.GetDirectoryName(sourceFile)
                ?? Path.GetDirectoryName(Path.GetFullPath(projectPath))
                ?? Environment.CurrentDirectory;
            return Path.GetFullPath(result, baseDirectory);
        }

        PreparedMSBuildInvocation PrepareMSBuildInvocation(
            ProjectTaskInstance task)
        {
            if (msbuildInvocations.TryGetValue(task, out var prepared))
            {
                return prepared;
            }

            var targetsExpression = GetParameter(task, "Targets");
            string? unsupportedReason = null;

            if (targetsExpression is null)
            {
                unsupportedReason =
                    "the restricted MSBuild task requires explicit Targets";
            }
            else if (task.Outputs.Count > 0)
            {
                unsupportedReason =
                    "MSBuild task outputs are not supported";
            }
            else if (!string.IsNullOrWhiteSpace(task.ContinueOnError))
            {
                unsupportedReason =
                    "MSBuild task ContinueOnError is not supported";
            }
            else if (task.Parameters.FirstOrDefault(
                parameter =>
                    !parameter.Key.Equals(
                        "Projects",
                        StringComparison.OrdinalIgnoreCase) &&
                    !parameter.Key.Equals(
                        "Targets",
                        StringComparison.OrdinalIgnoreCase) &&
                    !parameter.Key.Equals(
                        "Properties",
                        StringComparison.OrdinalIgnoreCase)) is
                var unsupportedParameter &&
                unsupportedParameter.Key is not null)
            {
                unsupportedReason =
                    $"MSBuild task parameter " +
                    $"'{unsupportedParameter.Key}' is not supported";
            }
            else if (task.Parameters.Any(
                parameter => parameter.Value.Contains(
                    "%(",
                    StringComparison.Ordinal)) ||
                task.Condition.Contains("%(", StringComparison.Ordinal))
            {
                unsupportedReason =
                    "MSBuild task batching is not supported";
            }

            var projectsExpression = GetParameter(task, "Projects");
            string? childProjectPath = null;
            var projects = "";

            if (unsupportedReason is null &&
                projectsExpression is null)
            {
                unsupportedReason =
                    "the MSBuild task requires the Projects parameter";
            }
            else if (unsupportedReason is null &&
                !TryResolveStaticExpression(
                    projectsExpression!,
                    out projects))
            {
                unsupportedReason =
                    $"MSBuild task project path '{projectsExpression}' is " +
                    "not statically known";
            }
            else if (unsupportedReason is null)
            {
                var projectPaths = projects
                    .Split(
                        ';',
                        StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries);

                if (projectPaths.Length != 1)
                {
                    unsupportedReason =
                        "the restricted MSBuild task requires exactly one " +
                        "project path";
                }
                else
                {
                    childProjectPath = Path.GetFullPath(
                        projectPaths[0],
                        Path.GetDirectoryName(projectPath)
                            ?? Environment.CurrentDirectory);
                }
            }

            var globalProperties = new Dictionary<string, string>(
                projectInstance.GlobalProperties,
                StringComparer.OrdinalIgnoreCase);
            var propertiesExpression = GetParameter(task, "Properties");
            var properties = "";

            if (unsupportedReason is null &&
                propertiesExpression is not null &&
                !TryResolveStaticExpression(
                    propertiesExpression,
                    out properties))
            {
                unsupportedReason =
                    $"MSBuild task global properties " +
                    $"'{propertiesExpression}' are not statically known";
            }
            else if (unsupportedReason is null &&
                propertiesExpression is not null)
            {
                foreach (var assignment in properties.Split(
                    ';',
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries))
                {
                    var separator = assignment.IndexOf('=');

                    if (separator <= 0)
                    {
                        unsupportedReason =
                            $"MSBuild task property '{assignment}' is not a " +
                            "name=value assignment";
                        break;
                    }

                    globalProperties[assignment[..separator].Trim()] =
                        assignment[(separator + 1)..].Trim();
                }
            }

            TranslationResult? child = null;

            if (unsupportedReason is null)
            {
                using var childCollection = new ProjectCollection();
                var childProject = childCollection.LoadProject(
                    childProjectPath!,
                    globalProperties,
                    toolsVersion: null);
                var childInstance = childProject.CreateProjectInstance();
                var seedTarget = childInstance.Targets.Keys.FirstOrDefault();

                if (seedTarget is null)
                {
                    unsupportedReason =
                        $"MSBuild task project '{childProjectPath}' contains " +
                        "no targets";
                }
                else
                {
                    var childRequestKey = CreateBuildRequestKey(
                        childProjectPath!,
                        globalProperties);

                    if (!_translatedInvocationPrograms.TryGetValue(
                        childRequestKey,
                        out child))
                    {
                        child = Translate(
                            childProjectPath!,
                            seedTarget,
                            globalProperties,
                            ReportWarning);
                        _translatedInvocationPrograms.Add(
                            childRequestKey,
                            child);
                    }
                }
            }

            prepared = new PreparedMSBuildInvocation(
                childProjectPath,
                targetsExpression ?? "",
                child,
                unsupportedReason);
            msbuildInvocations.Add(task, prepared);
            return prepared;

            bool TryResolveStaticExpression(
                string expression,
                out string result)
            {
                if (s_itemMetadataReference.IsMatch(expression) ||
                    s_itemReference.IsMatch(expression) ||
                    s_escapeSequence.IsMatch(expression))
                {
                    result = "";
                    return false;
                }

                result = expression;

                foreach (var reference in FindPropertyReferences(
                    expression).Reverse())
                {
                    if (reference.Content.Contains('(') ||
                        reference.Content.Contains(')') ||
                        reference.Content.Contains(
                            "::",
                            StringComparison.Ordinal) ||
                        targetPropertyAssignments.ContainsKey(
                            reference.Content))
                    {
                        result = "";
                        return false;
                    }

                    result = result.Remove(
                            reference.Index,
                            reference.Length)
                        .Insert(
                            reference.Index,
                            projectInstance.GetPropertyValue(
                                reference.Content));
                }

                return !ContainsReference(result);
            }
        }

        string GetTargetName(TargetDefinition definition)
        {
            return createdDefinitions.Single(
                pair => ReferenceEquals(pair.Value, definition)).Key.Name;
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
        var beforeTargets = new Dictionary<MSBuildTarget, List<MSBuildTarget>>(
            ReferenceEqualityComparer.Instance);
        var afterTargets = new Dictionary<MSBuildTarget, List<MSBuildTarget>>(
            ReferenceEqualityComparer.Instance);
        var missingDependencies = new List<MissingTargetDependency>();
        var missingRegistrations = new List<MissingTargetRegistration>();

        foreach (var target in targets)
        {
            preludes.Add(
                target,
                string.IsNullOrWhiteSpace(target.Condition)
                    ? ResolveDependsOnTargetList(
                        target,
                        ExpandDependsOnTargets(
                            project,
                            target,
                            propertyAssignments),
                        targetsByName,
                        missingDependencies)
                    : []);
            beforeTargets.Add(target, []);
            afterTargets.Add(target, []);
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
                AddDistinct(beforeTargets[anchor], target);
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
                AddDistinct(afterTargets[anchor], target);
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
            CopyLists(beforeTargets),
            CopyLists(afterTargets),
            warnings,
            missingDependencies);
    }

    private static TargetStateAccess GetTargetStateAccess(
        MSBuildTarget target,
        Func<string, string, string> resolveStaticFilePath,
        Func<ProjectTaskInstance, PreparedMSBuildInvocation>
            prepareMSBuildInvocation)
    {
        var readProperties = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var writeProperties = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var readItems = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var writeItems = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var readFiles = new HashSet<string>(StringComparer.Ordinal);
        var readsIsRunningFromVisualStudio = false;

        foreach (var child in target.Children)
        {
            switch (child)
            {
                case ProjectPropertyGroupTaskInstance propertyGroup:
                    if (!string.IsNullOrWhiteSpace(propertyGroup.Condition))
                    {
                        AddConditionRead(
                            propertyGroup.Condition,
                            propertyGroup.Location.File);
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
                            AddConditionRead(
                                property.Condition,
                                property.Location.File);
                        }

                        writeProperties.Add(property.Name);
                    }

                    break;

                case ProjectItemGroupTaskInstance itemGroup:
                    if (!string.IsNullOrWhiteSpace(itemGroup.Condition))
                    {
                        AddConditionRead(
                            itemGroup.Condition,
                            itemGroup.Location.File);
                    }

                    foreach (var item in itemGroup.Items)
                    {
                        AddItemRead(item.ItemType);
                        AddItemExpressionRead(item.Include);
                        AddItemExpressionRead(item.Exclude);

                        if (!string.IsNullOrWhiteSpace(item.Condition))
                        {
                            AddConditionRead(
                                item.Condition,
                                item.Location.File);
                        }

                        writeItems.Add(item.ItemType);
                    }

                    break;

                case ProjectTaskInstance task:
                    if (!string.IsNullOrWhiteSpace(task.Condition))
                    {
                        AddConditionRead(task.Condition, task.Location.File);
                    }

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
                    else if (task.Name.Equals(
                        "Error",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        AddPropertyExpressionRead(
                            GetRequiredParameter(task, "Text"));
                    }
                    else if (task.Name.Equals(
                        "MSBuild",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        var invocation = prepareMSBuildInvocation(task);

                        if (invocation.Child is not null)
                        {
                            AddPropertyExpressionRead(
                                invocation.TargetsExpression);
                            readFiles.UnionWith(
                                invocation.Child.Files.Keys);
                            readsIsRunningFromVisualStudio |=
                                invocation.Child
                                    .IsRunningFromVisualStudio is not null;
                        }
                    }

                    foreach (var output in task.Outputs)
                    {
                        switch (output)
                        {
                            case ProjectTaskOutputPropertyInstance property:
                                if (!string.IsNullOrWhiteSpace(task.Condition))
                                {
                                    AddPropertyRead(property.PropertyName);
                                }

                                writeProperties.Add(property.PropertyName);
                                break;

                            case ProjectTaskOutputItemInstance item:
                                if (!string.IsNullOrWhiteSpace(task.Condition))
                                {
                                    AddItemRead(item.ItemType);
                                }

                                writeItems.Add(item.ItemType);
                                break;
                        }
                    }

                    break;
            }
        }

        return new TargetStateAccess(
            readProperties,
            writeProperties,
            readItems,
            writeItems,
            readFiles,
            readsIsRunningFromVisualStudio);

        void AddPropertyExpressionRead(string expression)
        {
            foreach (Match match in s_propertyReference.Matches(expression))
            {
                AddPropertyRead(match.Groups["property"].Value);
            }

            foreach (Match match in s_itemReference.Matches(expression))
            {
                AddItemRead(match.Groups["item"].Value);
            }

            if (expression.Contains(
                "$([MSBuild]::IsRunningFromVisualStudio())",
                StringComparison.OrdinalIgnoreCase))
            {
                readsIsRunningFromVisualStudio = true;
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

        void AddConditionRead(string expression, string sourceFile)
        {
            if (TrySplitLogicalCondition(
                expression,
                "or",
                out var left,
                out var right))
            {
                AddConditionRead(left, sourceFile);
                AddConditionRead(right, sourceFile);
                return;
            }

            if (TrySplitLogicalCondition(
                expression,
                "and",
                out left,
                out right))
            {
                AddConditionRead(left, sourceFile);
                AddConditionRead(right, sourceFile);
                return;
            }

            if (TryParseNegation(expression, out var operand))
            {
                AddConditionRead(operand, sourceFile);
                return;
            }

            var exists = s_existsCondition.Match(expression);

            if (exists.Success)
            {
                readFiles.Add(
                    resolveStaticFilePath(
                        exists.Groups["path"].Value,
                        sourceFile));
                return;
            }

            var match = s_itemIdentityCondition.Match(expression);

            if (match.Success)
            {
                AddItemRead(match.Groups["item"].Value);
                return;
            }

            match = s_itemListCondition.Match(expression);

            if (match.Success)
            {
                AddItemRead(match.Groups["item"].Value);
                return;
            }

            if (TryParseStringComparison(
                expression,
                out left,
                out _,
                out right))
            {
                AddPropertyExpressionRead(left);
                AddPropertyExpressionRead(right);
            }
        }

        void AddItemExpressionRead(string expression)
        {
            foreach (Match match in s_itemReference.Matches(expression))
            {
                AddItemRead(match.Groups["item"].Value);
            }

            foreach (Match match in s_propertyReference.Matches(expression))
            {
                AddPropertyRead(match.Groups["property"].Value);
            }

            if (TryParseUnescapeItemsExpression(
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

    private static void AddDefinitionPredecessor(
        List<TargetDefinition> predecessors,
        TargetDefinition predecessor)
    {
        if (!predecessors.Contains(
            predecessor,
            ReferenceEqualityComparer.Instance))
        {
            predecessors.Add(predecessor);
        }
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

        TranslatePropertyAssignments(propertyGroup.Properties, context);
    }

    private static void TranslatePropertyAssignments(
        IEnumerable<ProjectPropertyGroupTaskPropertyInstance> properties,
        TranslationContext context)
    {
        foreach (var property in properties)
        {
            if (string.IsNullOrWhiteSpace(property.Condition))
            {
                context.SetProperty(
                    property.Name,
                    context.ResolvePropertyExpression(property.Value));
                continue;
            }

            var condition = context.TranslateCondition(
                property.Condition,
                property.Location.File);
            var whenTrue = context.CreateBranch();
            var whenFalse = context.CreateBranch();
            var whenTrueOutput =
                whenTrue.Context.ResolvePropertyExpression(property.Value);
            var whenFalseOutput =
                whenFalse.Context.GetProperty(property.Name);
            var result = new Value<string>();
            context.AddPropertySymbol(property.Name, whenTrueOutput);
            context.AddOperation(
                new ConditionalRegionOperation(
                    condition,
                    whenTrue.Arguments,
                    new OperationGraph(
                        whenTrue.Parameters,
                        whenTrue.Context.Operations,
                        [whenTrueOutput]),
                    new OperationGraph(
                        whenFalse.Parameters,
                        whenFalse.Context.Operations,
                        [whenFalseOutput]),
                    [result]));
            context.SetProperty(property.Name, result);
        }
    }

    private static void TranslateConditionalPropertyGroup(
        ProjectPropertyGroupTaskInstance propertyGroup,
        TranslationContext context)
    {
        var condition = context.TranslateCondition(
            propertyGroup.Condition,
            propertyGroup.Location.File);
        var whenTrue = context.CreateBranch();
        var whenFalse = context.CreateBranch();

        TranslatePropertyAssignments(
            propertyGroup.Properties,
            whenTrue.Context);
        var propertyNames = propertyGroup.Properties
            .Select(static property => property.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var whenTrueOutputs = new List<Value>();
        var whenFalseOutputs = new List<Value>();
        var outputs = new List<Value>();
        var properties = new List<(string Name, Value<string> Result)>();

        foreach (var propertyName in propertyNames)
        {
            var result = new Value<string>();

            whenTrueOutputs.Add(whenTrue.Context.GetProperty(propertyName));
            whenFalseOutputs.Add(whenFalse.Context.GetProperty(propertyName));
            outputs.Add(result);
            properties.Add((propertyName, result));
        }

        var conditional = new ConditionalRegionOperation(
            condition,
            whenTrue.Arguments,
            new OperationGraph(
                whenTrue.Parameters,
                whenTrue.Context.Operations,
                whenTrueOutputs),
            new OperationGraph(
                whenFalse.Parameters,
                whenFalse.Context.Operations,
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
            TranslateConditionalItemGroup(itemGroup, context);
            return;
        }

        TranslateItemGroupItems(itemGroup, context);
    }

    private static void TranslateConditionalItemGroup(
        ProjectItemGroupTaskInstance itemGroup,
        TranslationContext context)
    {
        var condition = context.TranslateCondition(
            itemGroup.Condition,
            itemGroup.Location.File);
        var whenTrue = context.CreateBranch();
        var whenFalse = context.CreateBranch();
        var itemTypes = itemGroup.Items
            .Select(item => item.ItemType)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        TranslateItemGroupItems(itemGroup, whenTrue.Context);

        var whenTrueOutputs = new List<Value>(itemTypes.Length);
        var whenFalseOutputs = new List<Value>(itemTypes.Length);
        var outputs = new List<Value>(itemTypes.Length);

        foreach (var itemType in itemTypes)
        {
            whenTrueOutputs.Add(whenTrue.Context.GetItems(itemType));
            whenFalseOutputs.Add(whenFalse.Context.GetItems(itemType));
            outputs.Add(new Value<IReadOnlyList<MSBuildItem>>());
        }

        context.AddOperation(
            new ConditionalRegionOperation(
                condition,
                whenTrue.Arguments,
                new OperationGraph(
                    whenTrue.Parameters,
                    whenTrue.Context.Operations,
                    whenTrueOutputs),
                new OperationGraph(
                    whenFalse.Parameters,
                    whenFalse.Context.Operations,
                    whenFalseOutputs),
                outputs));

        for (var index = 0; index < itemTypes.Length; index++)
        {
            context.SetItems(
                itemTypes[index],
                (Value<IReadOnlyList<MSBuildItem>>)outputs[index]);
        }
    }

    private static void TranslateItemGroupItems(
        ProjectItemGroupTaskInstance itemGroup,
        TranslationContext context)
    {
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
                    TranslateUnsupportedItemOperation(
                        item,
                        "metadata updates currently support only literal " +
                        "text and metadata references on the updated item",
                        context);
                    continue;
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
                        TranslateUnsupportedItemOperation(
                            item,
                            "the item condition requires unsupported batching",
                            context);
                        continue;
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

            if (string.IsNullOrWhiteSpace(item.Include) ||
                !string.IsNullOrWhiteSpace(item.Remove) ||
                item.Metadata.Count > 0 ||
                !string.IsNullOrWhiteSpace(item.MatchOnMetadata) ||
                !string.IsNullOrWhiteSpace(item.MatchOnMetadataOptions) ||
                !string.IsNullOrWhiteSpace(item.KeepMetadata) ||
                !string.IsNullOrWhiteSpace(item.RemoveMetadata) ||
                !string.IsNullOrWhiteSpace(item.KeepDuplicates))
            {
                TranslateUnsupportedItemOperation(
                    item,
                    "only Include with optional Exclude and metadata-only " +
                    "updates are currently supported",
                    context);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Condition))
            {
                context.SetItems(
                    item.ItemType,
                    TranslateItemInclude(item, context));
                continue;
            }

            var condition = context.TranslateCondition(
                item.Condition,
                item.Location.File);
            var whenTrue = context.CreateBranch();
            var whenFalse = context.CreateBranch();
            var whenTrueItems = TranslateItemInclude(
                item,
                whenTrue.Context);
            var result = new Value<IReadOnlyList<MSBuildItem>>();
            var conditional = new ConditionalRegionOperation(
                condition,
                whenTrue.Arguments,
                new OperationGraph(
                    whenTrue.Parameters,
                    whenTrue.Context.Operations,
                    [whenTrueItems]),
                new OperationGraph(
                    whenFalse.Parameters,
                    [],
                    [whenFalse.Context.GetItems(item.ItemType)]),
                [result]);
            context.AddOperation(conditional);
            context.SetItems(item.ItemType, result);
        }
    }

    private static void TranslateUnsupportedItemOperation(
        ProjectItemGroupTaskItemInstance item,
        string reason,
        TranslationContext context)
    {
        var description =
            $"item operation {FormatItemOperation(item)}; {reason}";
        context.ReportTranslationWarning(
            $"Unsupported {description}. The item operation will fail if " +
            "executed.");

        if (string.IsNullOrWhiteSpace(item.Condition))
        {
            var unsupported = new UnsupportedItemOperation(
                description,
                context.GetItems(item.ItemType),
                context.TargetGuard);
            context.AddOperation(unsupported);
            context.SetItems(item.ItemType, unsupported.Result);
            return;
        }

        var condition = context.TranslateCondition(
            item.Condition,
            item.Location.File);
        var whenTrue = context.CreateBranch();
        var whenFalse = context.CreateBranch();
        var unsupportedWhenTrue = new UnsupportedItemOperation(
            description,
            whenTrue.Context.GetItems(item.ItemType),
            whenTrue.Context.TargetGuard);
        whenTrue.Context.AddOperation(unsupportedWhenTrue);
        var result = new Value<IReadOnlyList<MSBuildItem>>();

        context.AddOperation(
            new ConditionalRegionOperation(
                condition,
                whenTrue.Arguments,
                new OperationGraph(
                    whenTrue.Parameters,
                    whenTrue.Context.Operations,
                    [unsupportedWhenTrue.Result]),
                new OperationGraph(
                    whenFalse.Parameters,
                    whenFalse.Context.Operations,
                    [whenFalse.Context.GetItems(item.ItemType)]),
                [result]));
        context.SetItems(item.ItemType, result);
    }

    private static Value<IReadOnlyList<MSBuildItem>> TranslateItemInclude(
        ProjectItemGroupTaskItemInstance item,
        TranslationContext context)
    {
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
        return concat.Result;
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

    private static bool TrySplitLogicalCondition(
        string expression,
        string logicalOperator,
        out string left,
        out string right)
    {
        var span = expression.AsSpan();
        var inQuote = false;
        var parenthesisDepth = 0;

        for (var index = 0; index <= span.Length - logicalOperator.Length; index++)
        {
            var character = span[index];

            if (character == '\'')
            {
                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
            {
                continue;
            }

            if (character == '(')
            {
                parenthesisDepth++;
                continue;
            }

            if (character == ')')
            {
                parenthesisDepth--;
                continue;
            }

            if (parenthesisDepth != 0 ||
                !span[index..].StartsWith(
                    logicalOperator,
                    StringComparison.OrdinalIgnoreCase) ||
                index > 0 &&
                !char.IsWhiteSpace(span[index - 1]) ||
                index + logicalOperator.Length < span.Length &&
                !char.IsWhiteSpace(span[index + logicalOperator.Length]))
            {
                continue;
            }

            left = expression[..index].Trim();
            right = expression[(index + logicalOperator.Length)..].Trim();
            return left.Length > 0 && right.Length > 0;
        }

        left = "";
        right = "";
        return false;
    }

    private static bool TryParseNegation(
        string expression,
        out string operand)
    {
        var trimmed = expression.Trim();

        if (trimmed.StartsWith('!') &&
            !trimmed.StartsWith("!=", StringComparison.Ordinal))
        {
            operand = trimmed[1..].Trim();
            return operand.Length > 0;
        }

        operand = "";
        return false;
    }

    private static bool TryParseStringComparison(
        string expression,
        out string left,
        out string comparisonOperator,
        out string right)
    {
        var span = expression.AsSpan().Trim();
        var inQuote = false;
        var parenthesisDepth = 0;

        for (var index = 0; index < span.Length - 1; index++)
        {
            var character = span[index];

            if (character == '\'')
            {
                inQuote = !inQuote;
                continue;
            }

            if (inQuote)
            {
                continue;
            }

            if (character == '(')
            {
                parenthesisDepth++;
                continue;
            }

            if (character == ')')
            {
                parenthesisDepth--;
                continue;
            }

            if (parenthesisDepth != 0 ||
                span[index..(index + 2)] is not "==" and not "!=")
            {
                continue;
            }

            var leftOperand = span[..index].Trim();
            var rightOperand = span[(index + 2)..].Trim();

            if (!TryUnquote(leftOperand, out left) ||
                !TryUnquote(rightOperand, out right))
            {
                break;
            }

            comparisonOperator = span[index..(index + 2)].ToString();
            return true;
        }

        left = "";
        comparisonOperator = "";
        right = "";
        return false;

        static bool TryUnquote(
            ReadOnlySpan<char> operand,
            out string content)
        {
            if (operand.Length >= 2 &&
                operand[0] == '\'' &&
                operand[^1] == '\'')
            {
                content = operand[1..^1].ToString();
                return true;
            }

            content = "";
            return false;
        }
    }

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
        if (string.IsNullOrWhiteSpace(task.Condition))
        {
            TranslateUnconditionalTask(task, context);
            return;
        }

        var condition = context.TranslateCondition(
            task.Condition,
            task.Location.File);
        var whenTrue = context.CreateBranch();
        var whenFalse = context.CreateBranch();

        TranslateUnconditionalTask(task, whenTrue.Context);

        var whenTrueOutputs = new List<Value>();
        var whenFalseOutputs = new List<Value>();
        var outputs = new List<Value>();
        var propertyOutputs = new List<(string Name, Value<string> Result)>();
        var itemOutputs = new List<(
            string Name,
            Value<IReadOnlyList<MSBuildItem>> Result)>();

        foreach (var output in task.Outputs
            .OfType<ProjectTaskOutputPropertyInstance>())
        {
            var result = new Value<string>();
            whenTrueOutputs.Add(
                whenTrue.Context.GetProperty(output.PropertyName));
            whenFalseOutputs.Add(
                whenFalse.Context.GetProperty(output.PropertyName));
            outputs.Add(result);
            propertyOutputs.Add((output.PropertyName, result));
        }

        foreach (var output in task.Outputs
            .OfType<ProjectTaskOutputItemInstance>())
        {
            var result = new Value<IReadOnlyList<MSBuildItem>>();
            whenTrueOutputs.Add(
                whenTrue.Context.GetItems(output.ItemType));
            whenFalseOutputs.Add(
                whenFalse.Context.GetItems(output.ItemType));
            outputs.Add(result);
            itemOutputs.Add((output.ItemType, result));
        }

        var whenTrueOrder = GetBranchOrderOutput(whenTrue.Context);
        var whenFalseOrder = GetBranchOrderOutput(whenFalse.Context);
        var orderResult = new Value<OrderToken>();
        whenTrueOutputs.Add(whenTrueOrder);
        whenFalseOutputs.Add(whenFalseOrder);
        outputs.Add(orderResult);

        context.AddOperation(
            new ConditionalRegionOperation(
                condition,
                whenTrue.Arguments,
                new OperationGraph(
                    whenTrue.Parameters,
                    whenTrue.Context.Operations,
                    whenTrueOutputs),
                new OperationGraph(
                    whenFalse.Parameters,
                    whenFalse.Context.Operations,
                    whenFalseOutputs),
                outputs));

        foreach (var (name, result) in propertyOutputs)
        {
            context.SetProperty(name, result);
        }

        foreach (var (name, result) in itemOutputs)
        {
            context.SetItems(name, result);
        }

        context.SetCurrentOrderToken(orderResult);

        static Value<OrderToken> GetBranchOrderOutput(
            TranslationContext branch)
        {
            if (branch.CurrentOrderToken is not null)
            {
                return branch.CurrentOrderToken;
            }

            var order = new ConstantOperation<OrderToken>(new OrderToken());
            branch.AddOperation(order);
            return order.Result;
        }
    }

    private static void TranslateUnconditionalTask(
        ProjectTaskInstance task,
        TranslationContext context)
    {
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

        if (task.Name.Equals("Error", StringComparison.OrdinalIgnoreCase))
        {
            TranslateError(task, context);
            return;
        }

        if (task.Name.Equals("MSBuild", StringComparison.OrdinalIgnoreCase))
        {
            TranslateMSBuild(task, context);
            return;
        }

        TranslateUnsupportedTask(task, context);
    }

    private static void TranslateUnsupportedTask(
        ProjectTaskInstance task,
        TranslationContext context)
    {
        var reason =
            $"The restricted MSBuild translator does not support task " +
            $"'{task.Name}'.";
        context.ReportTranslationWarning(
            $"{reason} The task will fail if executed.");
        context.AddOperation(
            new UnsupportedTaskOperation(
                task.Name,
                reason,
                context.CreateControl()));
        AddUnsupportedTaskOutputPlaceholders(task, context);
    }

    private static void AddUnsupportedTaskOutputPlaceholders(
        ProjectTaskInstance task,
        TranslationContext context)
    {
        foreach (var output in task.Outputs
            .OfType<ProjectTaskOutputPropertyInstance>())
        {
            var placeholder = new ConstantOperation<string>(
                string.Empty,
                context.TargetGuard);
            context.AddOperation(placeholder);
            context.SetProperty(output.PropertyName, placeholder.Result);
        }

        foreach (var output in task.Outputs
            .OfType<ProjectTaskOutputItemInstance>())
        {
            var placeholder =
                new ConstantOperation<IReadOnlyList<MSBuildItem>>(
                    [],
                    context.TargetGuard);
            context.AddOperation(placeholder);
            context.SetItems(output.ItemType, placeholder.Result);
        }
    }

    private static void TranslateMSBuild(
        ProjectTaskInstance task,
        TranslationContext context)
    {
        var invocation = context.PrepareMSBuildInvocation(task);

        if (invocation.UnsupportedReason is not null)
        {
            context.ReportTranslationWarning(
                $"MSBuild task cannot be fully translated: " +
                $"{invocation.UnsupportedReason}. The task will fail if " +
                "executed.");
            context.AddOperation(
                new UnsupportedTaskOperation(
                    task.Name,
                    invocation.UnsupportedReason,
                    context.CreateControl()));
            AddUnsupportedTaskOutputPlaceholders(task, context);
            return;
        }

        var child = invocation.Child ??
            throw new InvalidOperationException(
                "A supported MSBuild invocation must have a translated child " +
                "program.");
        var fileInputBindings = child.Files
            .Select(pair => new MSBuildFileInputBinding(
                context.GetFileContents(pair.Key),
                pair.Value))
            .ToList();
        var stringInputBindings = new List<MSBuildStringInputBinding>();

        if (child.IsRunningFromVisualStudio is not null)
        {
            stringInputBindings.Add(
                new MSBuildStringInputBinding(
                    context.GetIsRunningFromVisualStudio(),
                    child.IsRunningFromVisualStudio));
        }

        context.AddOperation(
            new MSBuildInvocationOperation(
                invocation.ProjectPath!,
                child.Program,
                child.Targets,
                context.ResolvePropertyExpression(
                    invocation.TargetsExpression),
                fileInputBindings,
                stringInputBindings,
                context.CreateControl()));
    }

    private static void TranslateError(
        ProjectTaskInstance task,
        TranslationContext context)
    {
        if (task.Outputs.Count > 0)
        {
            throw Unsupported("Error outputs");
        }

        var text = context.ResolvePropertyExpression(
            GetRequiredParameter(task, "Text"));
        context.AddOperation(
            new ErrorOperation(text, context.CreateControl()));
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

    private static string CreateBuildRequestKey(
        string path,
        IReadOnlyDictionary<string, string> properties) =>
        Path.GetFullPath(path) + "\n" + string.Join(
            "\n",
            properties
                .OrderBy(
                    pair => pair.Key,
                    StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));

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
        IReadOnlyList<TargetInput> Inputs,
        IReadOnlyList<TargetOutput> Outputs,
        OperationGraph Graph);

    private sealed record TargetLinks(
        IReadOnlyDictionary<
            MSBuildTarget,
            IReadOnlyList<MSBuildTarget>> Dependencies,
        IReadOnlyDictionary<
            MSBuildTarget,
            IReadOnlyList<MSBuildTarget>> BeforeTargets,
        IReadOnlyDictionary<
            MSBuildTarget,
            IReadOnlyList<MSBuildTarget>> AfterTargets,
        IReadOnlyList<string> Warnings,
        IReadOnlyList<MissingTargetDependency> MissingDependencies);

    private sealed record MissingTargetDependency(
        MSBuildTarget DeclaringTarget,
        string TargetName);

    private sealed record MissingTargetRegistration(
        MSBuildTarget DeclaringTarget,
        string TargetName,
        string AttributeName);

    private sealed record PreparedMSBuildInvocation(
        string? ProjectPath,
        string TargetsExpression,
        TranslationResult? Child,
        string? UnsupportedReason);

    private sealed record TargetStateAccess(
        IReadOnlySet<string> ReadProperties,
        IReadOnlySet<string> WriteProperties,
        IReadOnlySet<string> ReadItems,
        IReadOnlySet<string> WriteItems,
        IReadOnlySet<string> ReadFiles,
        bool ReadsIsRunningFromVisualStudio);

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
        public sealed record Branch(
            TranslationContext Context,
            IReadOnlyList<Value> Arguments,
            IReadOnlyList<Value> Parameters);

        public TranslationContext(
            IReadOnlyDictionary<string, Value<string>> properties,
            IReadOnlyDictionary<string, Value<IReadOnlyList<MSBuildItem>>> items,
            IReadOnlyDictionary<string, Value<FileContents?>> files,
            Value<string>? isRunningFromVisualStudio,
            ValueSymbolTableBuilder valueSymbols,
            Func<string, string, string> resolveStaticFilePath,
            Func<ProjectTaskInstance, PreparedMSBuildInvocation>
                prepareMSBuildInvocation,
            Action<string>? reportWarning = null)
        {
            Properties = new Dictionary<string, Value<string>>(
                properties,
                StringComparer.OrdinalIgnoreCase);
            Items =
                new Dictionary<string, Value<IReadOnlyList<MSBuildItem>>>(
                    items,
                    StringComparer.OrdinalIgnoreCase);
            Files = new Dictionary<string, Value<FileContents?>>(
                files,
                StringComparer.Ordinal);
            IsRunningFromVisualStudio = isRunningFromVisualStudio;
            ValueSymbols = valueSymbols;
            ResolveStaticFilePath = resolveStaticFilePath;
            PrepareMSBuildInvocation = prepareMSBuildInvocation;
            ReportWarning = reportWarning;
        }

        public Dictionary<string, Value<string>> Properties { get; }

        public Dictionary<string, Value<IReadOnlyList<MSBuildItem>>> Items
        {
            get;
        }

        public Dictionary<string, Value<FileContents?>> Files { get; }

        private Value<string>? IsRunningFromVisualStudio { get; }

        private ValueSymbolTableBuilder ValueSymbols { get; }

        private Func<string, string, string> ResolveStaticFilePath { get; }

        public Func<ProjectTaskInstance, PreparedMSBuildInvocation>
            PrepareMSBuildInvocation { get; }

        private Action<string>? ReportWarning { get; }

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

        public void SetCurrentOrderToken(Value<OrderToken> order) =>
            CurrentOrderToken = order;

        public Branch CreateBranch()
        {
            var arguments = new List<Value>(
                Properties.Count + Items.Count);
            var parameters = new List<Value>(
                Properties.Count + Items.Count);
            var properties = new Dictionary<string, Value<string>>(
                StringComparer.OrdinalIgnoreCase);
            var items =
                new Dictionary<string, Value<IReadOnlyList<MSBuildItem>>>(
                    StringComparer.OrdinalIgnoreCase);
            var files = new Dictionary<string, Value<FileContents?>>(
                StringComparer.Ordinal);
            Value<string>? isRunningFromVisualStudio = null;

            foreach (var (name, value) in Properties)
            {
                var parameter = new Value<string>();
                arguments.Add(value);
                parameters.Add(parameter);
                properties.Add(name, parameter);
                CopySymbols(value, parameter);
            }

            foreach (var (name, value) in Items)
            {
                var parameter = new Value<IReadOnlyList<MSBuildItem>>();
                arguments.Add(value);
                parameters.Add(parameter);
                items.Add(name, parameter);
                CopySymbols(value, parameter);
            }

            foreach (var (path, value) in Files)
            {
                var parameter = new Value<FileContents?>();
                arguments.Add(value);
                parameters.Add(parameter);
                files.Add(path, parameter);
            }

            if (IsRunningFromVisualStudio is not null)
            {
                isRunningFromVisualStudio = new Value<string>();
                arguments.Add(IsRunningFromVisualStudio);
                parameters.Add(isRunningFromVisualStudio);
            }

            var context = new TranslationContext(
                properties,
                items,
                files,
                isRunningFromVisualStudio,
                ValueSymbols,
                ResolveStaticFilePath,
                PrepareMSBuildInvocation,
                ReportWarning);

            if (TargetGuard is not null)
            {
                var parameter = new Value<GuardToken>();
                arguments.Add(TargetGuard);
                parameters.Add(parameter);
                context.SetTargetGuard(parameter);
            }

            if (CurrentOrderToken is not null)
            {
                var parameter = new Value<OrderToken>();
                arguments.Add(CurrentOrderToken);
                parameters.Add(parameter);
                context.SetCurrentOrderToken(parameter);
            }

            return new Branch(context, arguments, parameters);
        }

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

        public Value<bool> TranslateCondition(
            string expression,
            string sourceFile)
        {
            if (TrySplitLogicalCondition(
                expression,
                "or",
                out var leftExpression,
                out var rightExpression))
            {
                var left = TranslateCondition(leftExpression, sourceFile);
                var right = TranslateCondition(rightExpression, sourceFile);
                var or = new OrOperation(left, right);
                AddOperation(or);
                return or.Result;
            }

            if (TrySplitLogicalCondition(
                expression,
                "and",
                out leftExpression,
                out rightExpression))
            {
                var left = TranslateCondition(leftExpression, sourceFile);
                var right = TranslateCondition(rightExpression, sourceFile);
                var and = new AndOperation(left, right);
                AddOperation(and);
                return and.Result;
            }

            if (TryParseNegation(expression, out var operandExpression))
            {
                var operand = TranslateCondition(
                    operandExpression,
                    sourceFile);
                var not = new NotOperation(operand);
                AddOperation(not);
                return not.Result;
            }

            var match = s_existsCondition.Match(expression);

            if (match.Success)
            {
                var path = ResolveStaticFilePath(
                    match.Groups["path"].Value,
                    sourceFile);
                var exists = new FileExistsOperation(
                    GetFileContents(path));
                AddOperation(exists);
                return exists.Result;
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

            if (s_itemMetadataReference.IsMatch(expression))
            {
                return AddUnsupportedCondition(expression);
            }

            if (TryParseStringComparison(
                expression,
                out leftExpression,
                out var comparisonOperator,
                out rightExpression))
            {
                var left = ResolvePropertyExpression(leftExpression);
                var right = ResolvePropertyExpression(rightExpression);
                DagOperation comparison = comparisonOperator switch
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

            return AddUnsupportedCondition(expression);

            Value<bool> AddUnsupportedCondition(
                string unsupportedExpression)
            {
                ReportWarning?.Invoke(
                    $"Unsupported condition '{unsupportedExpression}' will " +
                    "fail if it is evaluated at runtime.");
                var unsupported = new UnsupportedConditionOperation(
                    unsupportedExpression,
                    TargetGuard);
                AddOperation(unsupported);
                return unsupported.Result;
            }
        }

        public Value<FileContents?> GetFileContents(string path) =>
            Files.TryGetValue(path, out var contents)
                ? contents
                : throw new InvalidOperationException(
                    $"File state '{path}' was not discovered during analysis.");

        public Value<string> GetIsRunningFromVisualStudio() =>
            IsRunningFromVisualStudio ??
            throw new InvalidOperationException(
                "The Visual Studio host input was not discovered during " +
                "target state analysis.");

        public void ReportTranslationWarning(string warning) =>
            ReportWarning?.Invoke(warning);

        public Value<string> ResolvePropertyExpression(string expression)
        {
            if (s_itemMetadataReference.IsMatch(expression) ||
                expression.Contains("->", StringComparison.Ordinal) &&
                expression.Contains("@(", StringComparison.Ordinal))
            {
                return AddUnsupportedPropertyExpression(expression);
            }

            var propertyReferences = FindPropertyReferences(expression);

            if (propertyReferences.Count == 1 &&
                propertyReferences[0].Index == 0 &&
                propertyReferences[0].Length == expression.Length)
            {
                return ResolvePropertyReference(propertyReferences[0].Content);
            }

            var references = propertyReferences
                .Select(reference => new ExpressionReference(
                    reference.Index,
                    reference.Length,
                    reference.Content,
                    null))
                .Concat(
                    s_itemReference.Matches(expression)
                        .Select(match => new ExpressionReference(
                            match.Index,
                            match.Length,
                            null,
                            match.Groups["item"].Value)))
                .OrderBy(reference => reference.Index)
                .ToArray();

            if (references.Length > 0 &&
                ContainsUnparsedReference(expression, references))
            {
                return AddUnsupportedPropertyExpression(expression);
            }

            if (references.Length > 0)
            {
                Value<string>? result = null;
                var position = 0;

                foreach (var reference in references)
                {
                    AppendLiteral(
                        expression[position..reference.Index]);

                    if (reference.PropertyContent is not null)
                    {
                        Append(ResolvePropertyReference(
                            reference.PropertyContent));
                    }
                    else
                    {
                        var identities = new ProjectItemIdentitiesOperation(
                            GetItems(reference.ItemName!),
                            TargetGuard);
                        var separator = new ConstantOperation<string>(
                            ";",
                            TargetGuard);
                        var join = new JoinItemValuesOperation(
                            identities.Result,
                            separator.Result,
                            TargetGuard);
                        AddOperation(identities);
                        AddOperation(separator);
                        AddOperation(join);
                        Append(join.Result);
                    }

                    position = reference.Index + reference.Length;
                }

                AppendLiteral(expression[position..]);
                return result!;

                void AppendLiteral(string literal)
                {
                    if (literal.Length > 0)
                    {
                        Append(AddConstant(literal));
                    }
                }

                void Append(Value<string> value)
                {
                    if (result is null)
                    {
                        result = value;
                        return;
                    }

                    var concat = new ConcatStringsOperation(
                        result,
                        value,
                        TargetGuard);
                    AddOperation(concat);
                    result = concat.Result;
                }
            }

            static bool ContainsUnparsedReference(
                string value,
                IReadOnlyList<ExpressionReference> parsedReferences)
            {
                var position = 0;

                foreach (var reference in parsedReferences)
                {
                    if (ContainsReference(
                        value[position..reference.Index]))
                    {
                        return true;
                    }

                    position = reference.Index + reference.Length;
                }

                return ContainsReference(value[position..]);
            }

            if (ContainsReference(expression))
            {
                return AddUnsupportedPropertyExpression(expression);
            }

            return AddConstant(expression);

            Value<string> AddUnsupportedPropertyExpression(
                string unsupportedExpression)
            {
                ReportWarning?.Invoke(
                    $"Unsupported property expression " +
                    $"'{unsupportedExpression}' will fail if its value is " +
                    "required at runtime.");
                var unsupported =
                    new UnsupportedPropertyExpressionOperation(
                        unsupportedExpression,
                        TargetGuard);
                AddOperation(unsupported);
                return unsupported.Result;
            }

            Value<string> ResolvePropertyReference(string content)
            {
                if (!content.Contains('(') &&
                    !content.Contains(')') &&
                    !content.Contains("::", StringComparison.Ordinal))
                {
                    return GetProperty(content);
                }

                if (TryParseValueOrDefault(
                    content,
                    out var valueExpression,
                    out var defaultExpression))
                {
                    var operation = new ValueOrDefaultOperation(
                        ResolvePropertyExpression(valueExpression),
                        ResolvePropertyExpression(defaultExpression),
                        TargetGuard);
                    AddOperation(operation);
                    return operation.Result;
                }

                if (content.Equals(
                    "[MSBuild]::IsRunningFromVisualStudio()",
                    StringComparison.OrdinalIgnoreCase))
                {
                    return IsRunningFromVisualStudio ??
                        throw new InvalidOperationException(
                            "The Visual Studio host input was not discovered " +
                            "during target state analysis.");
                }

                var functionEnd = content.IndexOf(
                    '(',
                    StringComparison.Ordinal);
                var functionName = functionEnd < 0
                    ? content
                    : content[..functionEnd];
                ReportWarning?.Invoke(
                    $"Unsupported property function '{functionName}' will " +
                    "fail if its value is required at runtime.");
                var unsupported =
                    new UnsupportedPropertyFunctionOperation(
                        functionName,
                        TargetGuard);
                AddOperation(unsupported);
                return unsupported.Result;
            }
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

            if (FindPropertyReferences(expression).Count > 0 &&
                !s_itemReference.IsMatch(expression) &&
                !s_itemMetadataReference.IsMatch(expression))
            {
                var operation = new ExpandItemsExpressionOperation(
                    ResolvePropertyExpression(expression),
                    [],
                    TargetGuard);
                AddOperation(operation);
                return operation.Result;
            }

            if (ContainsReference(expression))
            {
                ReportWarning?.Invoke(
                    $"Unsupported item expression '{expression}' will fail " +
                    "if its value is required at runtime.");
                var unsupported = new UnsupportedItemExpressionOperation(
                    expression,
                    TargetGuard);
                AddOperation(unsupported);
                return unsupported.Result;
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

    private static IReadOnlyList<PropertyReference> FindPropertyReferences(
        string expression)
    {
        var result = new List<PropertyReference>();

        for (var index = 0; index < expression.Length - 1; index++)
        {
            if (expression[index] != '$' || expression[index + 1] != '(')
            {
                continue;
            }

            var depth = 1;
            var inQuote = false;
            var end = index + 2;

            for (; end < expression.Length; end++)
            {
                var character = expression[end];

                if (character == '\'')
                {
                    if (inQuote &&
                        end + 1 < expression.Length &&
                        expression[end + 1] == '\'')
                    {
                        end++;
                        continue;
                    }

                    inQuote = !inQuote;
                    continue;
                }

                if (inQuote)
                {
                    continue;
                }

                if (character == '(')
                {
                    depth++;
                }
                else if (character == ')' && --depth == 0)
                {
                    break;
                }
            }

            if (depth != 0)
            {
                continue;
            }

            result.Add(
                new PropertyReference(
                    index,
                    end - index + 1,
                    expression[(index + 2)..end]));
            index = end;
        }

        return result;
    }

    private static bool TryParseValueOrDefault(
        string content,
        out string value,
        out string defaultValue)
    {
        const string prefix = "[MSBuild]::ValueOrDefault(";

        if (!content.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !content.EndsWith(')'))
        {
            value = string.Empty;
            defaultValue = string.Empty;
            return false;
        }

        var arguments = content[prefix.Length..^1];
        var separator = FindArgumentSeparator(arguments);

        if (separator < 0)
        {
            value = string.Empty;
            defaultValue = string.Empty;
            return false;
        }

        value = Unquote(arguments[..separator].Trim());
        defaultValue = Unquote(arguments[(separator + 1)..].Trim());
        return true;

        static int FindArgumentSeparator(string arguments)
        {
            var depth = 0;
            var inQuote = false;

            for (var index = 0; index < arguments.Length; index++)
            {
                var character = arguments[index];

                if (character == '\'')
                {
                    if (inQuote &&
                        index + 1 < arguments.Length &&
                        arguments[index + 1] == '\'')
                    {
                        index++;
                        continue;
                    }

                    inQuote = !inQuote;
                }
                else if (!inQuote && character == '(')
                {
                    depth++;
                }
                else if (!inQuote && character == ')')
                {
                    depth--;
                }
                else if (!inQuote && depth == 0 && character == ',')
                {
                    return index;
                }
            }

            return -1;
        }

        static string Unquote(string argument) =>
            argument.Length >= 2 &&
            argument[0] == '\'' &&
            argument[^1] == '\''
                ? argument[1..^1].Replace("''", "'", StringComparison.Ordinal)
                : argument;
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
