using MSBuild.Dag.Core;
using MSBuild.Dag.Execution;
using MSBuild.Dag.Sample;
using MSBuild.Dag.Visualization;

var properties = new Dictionary<string, Value<string>>();
var items = new Dictionary<string, Value<IReadOnlyList<string>>>();

var computeConfiguration = new ComputeConfiguration();
properties["Configuration"] = computeConfiguration.Result;

var reportConfiguration = new ReportConfiguration(
    properties["Configuration"]);

var initialCompileItems = new Value<IReadOnlyList<string>>();
items["Compile"] = initialCompileItems;

var generateSources = new GenerateSources();
var appendGeneratedSources = new ConcatItems(
    items["Compile"],
    generateSources.GeneratedSources);
items["Compile"] = appendGeneratedSources.Result;

var compile = new Compile(items["Compile"]);

var graph = new BuildGraph([
    computeConfiguration,
    reportConfiguration,
    generateSources,
    appendGeneratedSources,
    compile,
]);

AsciiGraphWriter.Write(graph, Console.Out);

Console.WriteLine();

var values = new ValueStore();
values.Set(
    initialCompileItems,
    (IReadOnlyList<string>)["Program.cs"]);

await new BuildGraphExecutor().ExecuteAsync(
    graph,
    values,
    SampleOperationExecutor.ExecuteAsync);

Console.WriteLine($"Produced: {values.Get(compile.Assembly)}");
