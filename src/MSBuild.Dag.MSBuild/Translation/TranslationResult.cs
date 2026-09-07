using MSBuild.Dag.Core;

namespace MSBuild.Dag.MSBuild;

public sealed record TranslationResult(
    BuildDefinition Definition,
    BuildProgram Program,
    IReadOnlyDictionary<string, TargetDefinition> TargetDefinitions,
    IReadOnlyDictionary<string, Target> Targets,
    IReadOnlyDictionary<string, Value<string>> Properties,
    IReadOnlyDictionary<string, Value<IReadOnlyList<MSBuildItem>>> Items,
    IReadOnlyDictionary<string, Value<FileContents?>> Files,
    Value<string>? IsRunningFromVisualStudio,
    IReadOnlyDictionary<Value, IReadOnlyList<ValueSymbol>> ValueSymbols,
    IReadOnlyList<string> Warnings);
