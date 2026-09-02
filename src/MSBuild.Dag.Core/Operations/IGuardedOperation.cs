namespace MSBuild.Dag.Core;

public interface IGuardedOperation
{
    Value<GuardToken>? Guard { get; }
}
