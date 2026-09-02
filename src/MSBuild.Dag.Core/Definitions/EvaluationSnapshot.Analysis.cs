namespace MSBuild.Dag.Core;

public sealed partial class EvaluationSnapshot
{
    internal StateInitialization? GetInitialization(
        StateLocation location) =>
        _initializations.GetValueOrDefault(location);
}
