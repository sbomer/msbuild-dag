using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed record TranslationResult(
    BuildGraph Graph,
    IReadOnlyDictionary<string, Target> Targets,
    IReadOnlyDictionary<string, Value<string>> Properties,
    IReadOnlyDictionary<string, Value<IReadOnlyList<string>>> Items,
    IReadOnlyDictionary<string, Value<bool>> TargetConditions);
