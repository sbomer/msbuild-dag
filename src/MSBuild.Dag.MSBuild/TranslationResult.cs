using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed record TranslationResult(
    OperationGraph Graph,
    IReadOnlyDictionary<string, Value<string>> Properties,
    IReadOnlyDictionary<string, Value<IReadOnlyList<string>>> Items,
    IReadOnlyDictionary<string, Value<bool>> TargetConditions);
