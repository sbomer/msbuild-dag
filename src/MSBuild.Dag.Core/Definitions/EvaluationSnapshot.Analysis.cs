namespace MSBuild.Dag.Core;

public sealed partial class EvaluationSnapshot
{
    internal StateInitialization? GetInitialization(
        Location location) =>
        _initializations.GetValueOrDefault(location);
}
