using MSBuild.Dag.Core;
using MSBuild.Dag.Visualization;

var configuration = new Value<string>();
var assembly = new Value<string>();

var prepare = new Target(
    inputs: [],
    outputs: [configuration],
    body: new OperationGraph([new PrepareOperation(configuration)]));

var compile = new Target(
    prelude: [prepare],
    inputs: [configuration],
    outputs: [assembly],
    body: new OperationGraph([new CompileOperation(configuration, assembly)]),
    epilogue: []);

var report = new Target(
    prelude: [compile],
    inputs: [],
    outputs: [],
    body: new OperationGraph([new ReportOperation()]),
    epilogue: []);

var program = new BuildProgram([prepare, compile, report]);

var names = new Dictionary<Target, string>(
    ReferenceEqualityComparer.Instance)
{
    [prepare] = "Prepare",
    [compile] = "Compile",
    [report] = "Report",
};

AsciiGraphWriter.Write(program, Console.Out, names);

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
