using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed record TranslationResult(
    BuildDefinition Definition,
    BuildProgram Program,
    IReadOnlyDictionary<string, TargetDefinition> TargetDefinitions,
    IReadOnlyDictionary<string, Target> Targets,
    IReadOnlyDictionary<string, Value<string>> Properties,
    IReadOnlyDictionary<string, Value<IReadOnlyList<string>>> Items,
    IReadOnlyDictionary<string, Value<bool>> TargetConditions);
