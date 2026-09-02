using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization;

internal sealed class BuildProgramRenderingAdapter
{
    private readonly IReadOnlyDictionary<Operation, string> _labels;
    private readonly IReadOnlyDictionary<Operation, GraphNodeContent> _contents;

    private BuildProgramRenderingAdapter(
        OperationGraph graph,
        IReadOnlyDictionary<Operation, string> labels,
        IReadOnlyDictionary<Operation, GraphNodeContent> contents)
    {
        Graph = graph;
        _labels = labels;
        _contents = contents;
    }

    public OperationGraph Graph { get; }

    public string GetLabel(Operation operation) => _labels[operation];

    public GraphNodeContent? GetContent(Operation operation) =>
        _contents.GetValueOrDefault(operation);

    public static BuildProgramRenderingAdapter Create(
        BuildProgram program,
        IReadOnlyDictionary<Target, string>? targetNames,
        bool includeTargetBodies = false)
    {
        var inputs = new Dictionary<Target, List<(Value Value, int? Port)>>(
            ReferenceEqualityComparer.Instance);
        var outputs = new Dictionary<Target, List<(Value Value, int? Port)>>(
            ReferenceEqualityComparer.Instance);

        foreach (var target in program.Targets)
        {
            inputs.Add(target, []);
            outputs.Add(
                target,
                target.Outputs
                    .Select((value, index) => (value, (int?)index))
                    .ToList());
        }

        var orderValues =
            new Dictionary<(Target Before, Target After), Value<OrderToken>>();

        foreach (var target in program.Targets)
        {
            foreach (var predecessor in program.GetOrderPredecessors(target))
            {
                var order = new Value<OrderToken>();
                orderValues.Add((predecessor, target), order);
                outputs[predecessor].Add((order, null));
            }
        }

        foreach (var target in program.Targets)
        {
            for (var index = 0; index < target.Inputs.Count; index++)
            {
                if (program.GetProducer(target.Inputs[index]) is null)
                {
                    inputs[target].Add((target.Inputs[index], index));
                }
            }

            foreach (var prerequisite in program.Targets)
            {
                for (var index = 0; index < target.Inputs.Count; index++)
                {
                    if (ReferenceEquals(
                        program.GetProducer(target.Inputs[index]),
                        prerequisite))
                    {
                        inputs[target].Add((target.Inputs[index], index));
                    }
                }

                if (program.GetOrderPredecessors(target).Contains(
                    prerequisite,
                    ReferenceEqualityComparer.Instance))
                {
                    inputs[target].Add(
                        (orderValues[(prerequisite, target)], null));
                }
            }
        }

        var operations = new List<Operation>(program.Targets.Count);
        var labels = new Dictionary<Operation, string>(
            ReferenceEqualityComparer.Instance);
        var contents = new Dictionary<Operation, GraphNodeContent>(
            ReferenceEqualityComparer.Instance);

        for (var index = 0; index < program.Targets.Count; index++)
        {
            var target = program.Targets[index];
            var operation = new TargetNodeOperation(
                inputs[target].Select(static entry => entry.Value).ToArray(),
                outputs[target].Select(static entry => entry.Value).ToArray(),
                inputs[target].Select(static entry => entry.Port).ToArray(),
                outputs[target].Select(static entry => entry.Port).ToArray())
            {
                BodyInputCount = target.Inputs.Count,
                BodyOutputCount = target.Outputs.Count,
            };
            operations.Add(operation);

            labels.Add(
                operation,
                targetNames is not null && targetNames.TryGetValue(target, out var name)
                    ? name
                    : $"Target {index}");

            if (includeTargetBodies)
            {
                contents.Add(operation, RenderBody(target));
            }
        }

        return new BuildProgramRenderingAdapter(
            new OperationGraph(operations),
            labels,
            contents);
    }

    private static GraphNodeContent RenderBody(Target target) =>
        AsciiGraphWriter.RenderTargetBody(target);

    private sealed class TargetNodeOperation(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs,
        IReadOnlyList<int?> inputPorts,
        IReadOnlyList<int?> outputPorts) : Operation, ITargetRenderingOperation
    {
        public int BodyInputCount { get; init; }

        public int BodyOutputCount { get; init; }

        public IReadOnlyList<int?> InputPorts { get; } = inputPorts;

        public IReadOnlyList<int?> OutputPorts { get; } = outputPorts;

        public override IReadOnlyList<Value> Inputs { get; } = inputs;

        public override IReadOnlyList<Value> Outputs { get; } = outputs;
    }
}

internal interface ITargetRenderingOperation
{
    int BodyInputCount { get; }

    int BodyOutputCount { get; }

    IReadOnlyList<int?> InputPorts { get; }

    IReadOnlyList<int?> OutputPorts { get; }
}
