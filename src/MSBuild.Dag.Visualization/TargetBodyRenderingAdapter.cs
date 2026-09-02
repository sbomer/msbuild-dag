using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization;

internal sealed class TargetBodyRenderingAdapter
{
    private readonly IReadOnlyDictionary<Operation, string> _labels;

    private TargetBodyRenderingAdapter(
        OperationGraph graph,
        IReadOnlyDictionary<Operation, string> labels,
        IReadOnlyList<Operation> inputs,
        IReadOnlyList<Operation> outputs)
    {
        Graph = graph;
        _labels = labels;
        Inputs = inputs;
        Outputs = outputs;
    }

    public OperationGraph Graph { get; }

    public IReadOnlyList<Operation> Inputs { get; }

    public IReadOnlyList<Operation> Outputs { get; }

    public string GetLabel(Operation operation) =>
        _labels.GetValueOrDefault(operation) ?? GetTypeDisplayName(operation.GetType());

    public static TargetBodyRenderingAdapter Create(Target target)
    {
        var operations = new List<Operation>(
            target.Inputs.Count +
            target.Body.Operations.Count +
            target.Outputs.Count);
        var labels = new Dictionary<Operation, string>(
            ReferenceEqualityComparer.Instance);
        var inputs = new List<Operation>(target.Inputs.Count);
        var outputs = new List<Operation>(target.Outputs.Count);

        for (var index = 0; index < target.Inputs.Count; index++)
        {
            var input = new TargetInputBoundaryOperation(target.Inputs[index]);
            operations.Add(input);
            inputs.Add(input);
            labels.Add(input, string.Empty);
        }

        operations.AddRange(target.Body.Operations);

        for (var index = 0; index < target.Outputs.Count; index++)
        {
            var output = new TargetOutputBoundaryOperation(target.Outputs[index]);
            operations.Add(output);
            outputs.Add(output);
            labels.Add(output, string.Empty);
        }

        return new TargetBodyRenderingAdapter(
            new OperationGraph(operations),
            labels,
            inputs,
            outputs);
    }

    private static string GetTypeDisplayName(Type type)
    {
        var name = type.Name;
        var genericMarker = name.IndexOf('`');
        return genericMarker < 0 ? name : name[..genericMarker];
    }
}

internal interface ITargetBoundaryOperation
{
    TargetBoundaryKind BoundaryKind { get; }
}

internal enum TargetBoundaryKind
{
    Input,
    Output,
}

internal sealed class TargetInputBoundaryOperation(Value value)
    : Operation, ITargetBoundaryOperation
{
    public TargetBoundaryKind BoundaryKind => TargetBoundaryKind.Input;

    public override IReadOnlyList<Value> Inputs => [];

    public override IReadOnlyList<Value> Outputs => [value];
}

internal sealed class TargetOutputBoundaryOperation(Value value)
    : Operation, ITargetBoundaryOperation
{
    public TargetBoundaryKind BoundaryKind => TargetBoundaryKind.Output;

    public override IReadOnlyList<Value> Inputs => [value];

    public override IReadOnlyList<Value> Outputs => [];
}
