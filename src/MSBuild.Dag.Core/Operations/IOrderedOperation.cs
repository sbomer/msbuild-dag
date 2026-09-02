namespace MSBuild.Dag.Core;

public interface IOrderedOperation
{
    Value<OrderToken>? OrderInput { get; }

    Value<OrderToken>? OrderOutput { get; }
}
