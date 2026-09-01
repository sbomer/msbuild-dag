using MSBuild.Dag.Core;
using MSBuild.Dag.Visualization;

var configuration = new Value<string>();
var assembly = new Value<string>();

var prepare = new Target(
    inputs: [],
    outputs: [configuration],
    operations: [new PrepareOperation(configuration)]);

var compile = new Target(
    inputs: [configuration],
    outputs: [assembly],
    operations: [new CompileOperation(configuration, assembly)]);

var report = new Target(
    inputs: [],
    outputs: [],
    operations: [new ReportOperation()]);

var graph = new BuildGraph(
    [prepare, compile, report],
    [new TargetDependency(compile, report)]);

var names = new Dictionary<Target, string>(
    ReferenceEqualityComparer.Instance)
{
    [prepare] = "Prepare",
    [compile] = "Compile",
    [report] = "Report",
};

AsciiGraphWriter.Write(graph, Console.Out, names);

sealed class PrepareOperation(Value<string> configuration) : Operation
{
    public override IReadOnlyList<Value> Inputs => [];

    public override IReadOnlyList<Value> Outputs => [configuration];
}

sealed class CompileOperation(
    Value<string> configuration,
    Value<string> assembly) : Operation
{
    public override IReadOnlyList<Value> Inputs => [configuration];

    public override IReadOnlyList<Value> Outputs => [assembly];
}

sealed class ReportOperation : Operation
{
    public override IReadOnlyList<Value> Inputs => [];

    public override IReadOnlyList<Value> Outputs => [];
}
