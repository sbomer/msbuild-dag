namespace MSBuild.Dag.Core;

public abstract class Operation
{
    public abstract IReadOnlyList<Value> Inputs { get; }

    public abstract IReadOnlyList<Value> Outputs { get; }
}
