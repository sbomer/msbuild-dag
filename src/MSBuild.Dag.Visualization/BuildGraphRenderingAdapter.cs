using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization;

internal sealed class BuildGraphRenderingAdapter
{
    private readonly IReadOnlyDictionary<Operation, string> _labels;
    private readonly IReadOnlyDictionary<Operation, IReadOnlyList<string>> _contents;

    private BuildGraphRenderingAdapter(
        OperationGraph graph,
        IReadOnlyDictionary<Operation, string> labels,
        IReadOnlyDictionary<Operation, IReadOnlyList<string>> contents)
    {
        Graph = graph;
        _labels = labels;
        _contents = contents;
    }

    public OperationGraph Graph { get; }

    public string GetLabel(Operation operation) => _labels[operation];

    public IReadOnlyList<string>? GetContent(Operation operation) =>
        _contents.GetValueOrDefault(operation);

    public static BuildGraphRenderingAdapter Create(
        BuildGraph graph,
        IReadOnlyDictionary<Target, string>? targetNames,
        bool includeTargetBodies = false)
    {
        var inputs = new Dictionary<Target, List<Value>>(
            ReferenceEqualityComparer.Instance);
        var outputs = new Dictionary<Target, List<Value>>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in graph.Targets)
        {
            inputs.Add(target, new List<Value>(target.Inputs));
            outputs.Add(target, new List<Value>(target.Outputs));
        }

        foreach (var dependency in graph.ExplicitDependencies)
        {
            var order = new Value<OrderToken>();
            outputs[dependency.Prerequisite].Add(order);
            inputs[dependency.Dependent].Add(order);
        }

        var operations = new List<Operation>(graph.Targets.Count);
        var labels = new Dictionary<Operation, string>(
            ReferenceEqualityComparer.Instance);
        var contents = new Dictionary<Operation, IReadOnlyList<string>>(
            ReferenceEqualityComparer.Instance);

        for (var index = 0; index < graph.Targets.Count; index++)
        {
            var target = graph.Targets[index];
            var operation = new TargetNodeOperation(inputs[target], outputs[target]);
            operations.Add(operation);

            labels.Add(
                operation,
                targetNames is not null && targetNames.TryGetValue(target, out var name)
                    ? name
                    : $"Target {index}");

            if (includeTargetBodies)
            {
                contents.Add(operation, RenderBody(target.Body));
            }
        }

        return new BuildGraphRenderingAdapter(
            new OperationGraph(operations),
            labels,
            contents);
    }

    private static IReadOnlyList<string> RenderBody(OperationGraph body)
    {
        return AsciiGraphWriter.Render(body)
            .Split(Environment.NewLine)
            .Skip(1)
            .Where(static line => line.Length > 0)
            .ToArray();
    }

    private sealed class TargetNodeOperation(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs) : Operation
    {
        public override IReadOnlyList<Value> Inputs { get; } = inputs;

        public override IReadOnlyList<Value> Outputs { get; } = outputs;
    }
}
