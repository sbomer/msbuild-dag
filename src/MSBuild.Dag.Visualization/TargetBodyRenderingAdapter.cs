using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization;

internal sealed class TargetBodyRenderingAdapter
{
    private readonly IReadOnlyDictionary<Operation, string> _labels;
    private readonly Func<Operation, string?>? _labelProvider;

    private TargetBodyRenderingAdapter(
        OperationGraph graph,
        IReadOnlyDictionary<Operation, string> labels,
        Func<Operation, string?>? labelProvider,
        IReadOnlyList<Operation> inputs,
        IReadOnlyList<Operation> outputs)
    {
        Graph = graph;
        _labels = labels;
        _labelProvider = labelProvider;
        Inputs = inputs;
        Outputs = outputs;
    }

    public OperationGraph Graph { get; }

    public IReadOnlyList<Operation> Inputs { get; }

    public IReadOnlyList<Operation> Outputs { get; }

    public string GetLabel(Operation operation)
    {
        if (_labels.TryGetValue(operation, out var label))
        {
            return label;
        }

        return _labelProvider?.Invoke(operation) ??
            GetTypeDisplayName(operation.GetType());
    }

    public static TargetBodyRenderingAdapter Create(
        Target target,
        Func<Operation, string?>? labelProvider) =>
        Create(target.Body, labelProvider, labelBoundaries: false);

    public static TargetBodyRenderingAdapter Create(
        OperationGraph graph,
        Func<Operation, string?>? labelProvider,
        bool labelBoundaries)
    {
        var operations = new List<Operation>(
            graph.Inputs.Count +
            graph.Operations.Count +
            graph.Outputs.Count);
        var labels = new Dictionary<Operation, string>(
            ReferenceEqualityComparer.Instance);
        var inputs = new List<Operation>(graph.Inputs.Count);
        var outputs = new List<Operation>(graph.Outputs.Count);

        for (var index = 0; index < graph.Inputs.Count; index++)
        {
            var input = new TargetInputBoundaryOperation(graph.Inputs[index]);
            operations.Add(input);
            inputs.Add(input);
            labels.Add(input, labelBoundaries ? $"i{index}" : string.Empty);
        }

        operations.AddRange(graph.Operations);

        for (var index = 0; index < graph.Outputs.Count; index++)
        {
            var output = new TargetOutputBoundaryOperation(graph.Outputs[index]);
            operations.Add(output);
            outputs.Add(output);
            labels.Add(
                output,
                labelBoundaries ? $"o{index}" : string.Empty);
        }

        return new TargetBodyRenderingAdapter(
            new OperationGraph(operations),
            labels,
            labelProvider,
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
