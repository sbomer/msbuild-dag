using MSBuild.Dag.Core;

namespace MSBuild.Dag.Visualization.Tests;

public sealed class AsciiGraphWriterTests
{
    [Fact]
    public void RendersInputsOutputsAndDependencies()
    {
        var external = new Value();
        var producer = new TestOperation([], [new Value(), new Value()]);
        var consumer = new TestOperation(
            [external, producer.Outputs[0]],
            [new Value()]);
        var graph = new OperationGraph([producer, consumer]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Equal(1, CountOccurrences(result, "[0] TestOperation"));
        Assert.Equal(1, CountOccurrences(result, "[1] TestOperation"));
        Assert.Equal(1, CountOccurrences(result, "external[0]"));
        Assert.Equal(1, CountOccurrences(result, "output[0:1]"));
        Assert.Equal(1, CountOccurrences(result, "output[1:0]"));
        Assert.Contains('▼', result);
        Assert.Contains('┌', result);
        Assert.Contains('┘', result);
        Assert.Contains("o0", result);
        Assert.Contains("i1", result);
    }

    [Fact]
    public void RendersEmptyGraph()
    {
        var graph = new OperationGraph([]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Equal($"OperationGraph{Environment.NewLine}(empty){Environment.NewLine}", result);
    }

    [Fact]
    public void RendersOrderEdgeWhenDataAlreadyConnectsOperations()
    {
        var initialOrder = new Value<OrderToken>();
        var producer = new OrderedTestOperation(initialOrder);
        var consumer = new OrderedTestOperation(
            producer.OrderOutput!,
            producer.Result);
        var graph = new OperationGraph([producer, consumer]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Contains('▼', result);
        Assert.Contains("order", result);
        Assert.Contains('╎', result);
    }

    [Fact]
    public void LabelsOrderEdgesSeparatelyFromDataEdges()
    {
        var initialOrder = new Value<OrderToken>();
        var first = new OrderedTestOperation(initialOrder);
        var second = new OrderedTestOperation(first.OrderOutput!);
        var consumer = new TestOperation(
            [first.Result, second.Result],
            [new Value()]);
        var graph = new OperationGraph([first, second, consumer]);

        var result = AsciiGraphWriter.Render(graph);

        Assert.Contains("order", result);
        Assert.Contains('╎', result);
        Assert.Contains("i0", result);
        Assert.Contains("i1", result);
        Assert.Equal(1, CountOccurrences(result, "[2] TestOperation"));
    }

    [Fact]
    public void RendersTargetsWithDataAndExplicitOrderEdges()
    {
        var data = new Value<string>();
        var producer = new TestOperation([], [data]);
        var consumer = new TestOperation([data], []);
        var first = new Target(
            [],
            [data],
            new OperationGraph([producer]));
        var second = new Target(
            [data],
            [],
            new OperationGraph([consumer]));
        var third = new Target(
            [second],
            [],
            [],
            new OperationGraph([]),
            []);
        var program = new BuildProgram([first, second, third]);
        var names = new Dictionary<Target, string>(
            ReferenceEqualityComparer.Instance)
        {
            [first] = "Prepare",
            [second] = "Compile",
            [third] = "Report",
        };

        var result = AsciiGraphWriter.RenderCompact(program, names);

        Assert.StartsWith($"BuildProgram{Environment.NewLine}", result);
        Assert.Contains("[0] Prepare", result);
        Assert.Contains("[1] Compile", result);
        Assert.Contains("[2] Report", result);
        Assert.Contains('─', result);
        Assert.Contains("order", result);
        Assert.Contains('╎', result);
    }

    [Fact]
    public void RendersExplicitTargetOrderAlongsideDataEdge()
    {
        var data = new Value<string>();
        var producer = new Target(
            [],
            [data],
            new OperationGraph(
            [
                new TestOperation([], [data]),
            ]));
        var consumer = new Target(
            [producer],
            [data],
            [],
            new OperationGraph(
            [
                new TestOperation([data], []),
            ]),
            []);

        var result = AsciiGraphWriter.RenderCompact(
            new BuildProgram([producer, consumer]));

        Assert.Contains('─', result);
        Assert.Contains('╎', result);
        Assert.Contains("order", result);
    }

    [Fact]
    public void RendersDifferentPreludeOrdersDifferently()
    {
        var first = EmptyTarget();
        var second = EmptyTarget();
        var firstThenSecond = new Target(
            [first, second],
            [],
            [],
            new OperationGraph([]),
            []);
        var secondThenFirst = new Target(
            [second, first],
            [],
            [],
            new OperationGraph([]),
            []);
        var names = new Dictionary<Target, string>(
            ReferenceEqualityComparer.Instance)
        {
            [first] = "A",
            [second] = "B",
            [firstThenSecond] = "Build",
            [secondThenFirst] = "Build",
        };

        var forward = AsciiGraphWriter.RenderCompact(
            new BuildProgram([first, second, firstThenSecond]),
            names);
        var reverse = AsciiGraphWriter.RenderCompact(
            new BuildProgram([first, second, secondThenFirst]),
            names);

        Assert.NotEqual(forward, reverse);
        Assert.Contains("order", forward);
        Assert.Contains("order", reverse);
    }

    [Fact]
    public void ExpandedBuildProgramIncludesTargetBodies()
    {
        var bodyOutput = new Value<string>();
        var producer = new TestOperation([], [bodyOutput]);
        var first = new Target(
            new OperationGraph([producer]));
        var data = Assert.Single(first.Outputs);
        var parameter = new Value<string>();
        var consumer = new TestOperation([parameter], []);
        var second = new Target(
            [],
            [data],
            new OperationGraph([parameter], [consumer], []),
            []);
        var program = new BuildProgram([first, second]);
        var names = new Dictionary<Target, string>(
            ReferenceEqualityComparer.Instance)
        {
            [first] = "Prepare",
            [second] = "Compile",
        };

        var result = AsciiGraphWriter.Render(program, names);

        Assert.Contains("BuildProgram", result);
        Assert.Contains("[0] Prepare", result);
        Assert.Contains("[1] Compile", result);
        Assert.Equal(2, CountOccurrences(result, "TestOperation"));
        Assert.DoesNotContain("Target bodies", result);
        Assert.DoesNotContain("external[", result);
        Assert.DoesNotContain("output[", result);
        Assert.Equal(2, CountOccurrences(result, "i0"));
        Assert.Equal(2, CountOccurrences(result, "o0"));
    }

    [Fact]
    public void ExpandedBuildProgramUsesProvidedBodyOperationLabels()
    {
        var value = new Value<string>();
        var operation = new TestOperation([], [value]);
        var target = new Target(
            [],
            [value],
            new OperationGraph([operation]));

        var result = AsciiGraphWriter.Render(
            new BuildProgram([target]),
            operationLabelProvider: candidate =>
                ReferenceEquals(candidate, operation)
                    ? "'value'"
                    : null);

        Assert.Contains("[0] 'value'", result);
        Assert.DoesNotContain("[0] TestOperation", result);
    }

    [Fact]
    public void ExpandedBuildProgramUsesProvidedValueLabels()
    {
        var value = new Value<string>();
        var operation = new TestOperation([], [value]);
        var target = new Target(
            [],
            [value],
            new OperationGraph([operation]));

        var result = AsciiGraphWriter.Render(
            new BuildProgram([target]),
            valueLabelProvider: candidate =>
                ReferenceEquals(candidate, value)
                    ? "$(Configuration)#0"
                    : ReferenceEquals(candidate, target.Outputs[0])
                        ? "$(Configuration)#1"
                        : null);

        Assert.Contains("$(Configuration)#0", result);
        Assert.Contains("$(Configuration)#1", result);
        Assert.DoesNotContain("output[", result);
    }

    [Fact]
    public void ExpandedBuildProgramShowsInitialContentAsValueSource()
    {
        var initial = new Value<string>();
        var parameter = new Value<string>();
        var consumer = new TestOperation([parameter], []);
        var target = new Target(
            [],
            [initial],
            new OperationGraph(
                [parameter],
                [consumer],
                []),
            []);
        var program = new BuildProgram(
            [target],
            [new InitialValue<string>(initial, "0")]);

        var result = AsciiGraphWriter.Render(
            program,
            valueLabelProvider: value =>
                ReferenceEquals(value, initial)
                    ? "$(X)#0"
                    : ReferenceEquals(value, parameter)
                        ? "$(X)#1"
                        : null);

        Assert.Contains(
            result.Split(Environment.NewLine),
            line => line.Contains("│ 0 ", StringComparison.Ordinal));
        Assert.Contains("$(X)#0", result);
        Assert.DoesNotContain("│ $(X)#0 │", result);
    }

    [Fact]
    public void ExpandedBuildProgramLabelsSharedValueOnceAtProducer()
    {
        var shared = new Value<string>();
        var producer = new TestOperation([], [shared]);
        var firstConsumer = new TestOperation([shared], []);
        var secondConsumer = new TestOperation([shared], []);
        var target = new Target(
            new OperationGraph(
                [producer, firstConsumer, secondConsumer]));

        var result = AsciiGraphWriter.Render(
            new BuildProgram([target]),
            valueLabelProvider: value =>
                ReferenceEquals(value, shared)
                    ? "$(X)#1"
                    : null);

        Assert.Equal(1, CountOccurrences(result, "$(X)#1"));
        Assert.Contains("i0", result);
    }

    [Fact]
    public void ExpandedBuildProgramSeparatesMultipleOutputValueLabels()
    {
        var first = new Value<string>();
        var second = new Value<string>();
        var producer = new TestOperation([], [first, second]);
        var firstConsumer = new TestOperation([first], []);
        var secondConsumer = new TestOperation([second], []);
        var target = new Target(
            new OperationGraph(
                [producer, firstConsumer, secondConsumer]));

        var result = AsciiGraphWriter.Render(
            new BuildProgram([target]),
            valueLabelProvider: value =>
                ReferenceEquals(value, first)
                    ? "$(FirstProperty)#12"
                    : ReferenceEquals(value, second)
                        ? "$(SecondProperty)#34"
                        : null);

        Assert.Contains("$(FirstProperty)#12", result);
        Assert.Contains("$(SecondProperty)#34", result);
        Assert.DoesNotContain(
            "$(FirstProperty)#12$(SecondProperty)#34",
            result);
    }

    [Fact]
    public void ExpandedBuildProgramRendersConditionalRegions()
    {
        var condition = new Value<bool>();
        var argument = new Value<string>();
        var whenTrueInput = new Value<string>();
        var whenFalseInput = new Value<string>();
        var whenTrueResult = new Value<string>();
        var whenFalseResult = new Value<string>();
        var whenTrue = new TestOperation(
            [whenTrueInput],
            [whenTrueResult]);
        var whenFalse = new TestOperation(
            [whenFalseInput],
            [whenFalseResult]);
        var resultValue = new Value<string>();
        var conditional = new ConditionalRegionOperation(
            condition,
            [argument],
            new OperationGraph(
                [whenTrueInput],
                [whenTrue],
                [whenTrueResult]),
            new OperationGraph(
                [whenFalseInput],
                [whenFalse],
                [whenFalseResult]),
            [resultValue]);
        var target = new Target(
            new OperationGraph(
                [condition, argument],
                [conditional],
                [resultValue]));

        var result = AsciiGraphWriter.Render(
            new BuildProgram([target]),
            operationLabelProvider: operation =>
                ReferenceEquals(operation, conditional)
                    ? "if"
                    : ReferenceEquals(operation, whenTrue)
                        ? "true branch"
                        : ReferenceEquals(operation, whenFalse)
                            ? "false branch"
                            : null);

        Assert.Contains("true", result);
        Assert.Contains("false", result);
        Assert.Contains('┬', result);
        Assert.Contains('┴', result);
        Assert.Contains('═', result);
        Assert.Contains('║', result);
        Assert.Contains('╦', result);
        Assert.Contains('╩', result);
        Assert.Contains('╥', result);
        Assert.Contains('╨', result);
        Assert.DoesNotContain('╫', result);
        Assert.Contains('◆', result);
        Assert.DoesNotContain("then", result);
        Assert.DoesNotContain("else", result);
        Assert.DoesNotContain("─ true ", result);
        Assert.DoesNotContain("─ false ", result);
        Assert.Contains("│ true", result);
        Assert.Contains("│ false", result);
        Assert.Contains("i0", result);
        Assert.Contains("i1", result);
        Assert.Contains("o0", result);
        Assert.Contains("true branch", result);
        Assert.Contains("false branch", result);
    }

    [Fact]
    public void ExpandedBuildProgramRendersEmptyTargetBody()
    {
        var target = EmptyTarget();
        var program = new BuildProgram([target]);

        var result = AsciiGraphWriter.Render(program);

        Assert.Contains("[0] Target 0", result);
        Assert.Contains("(empty)", result);
    }

    [Fact]
    public void ExpandedBuildProgramKeepsBoundaryLabelsClearOfOrderEdges()
    {
        var firstValue = new Value<string>();
        var secondValue = new Value<string>();
        var first = new Target(
            [],
            [firstValue],
            new OperationGraph([new TestOperation([], [firstValue])]));
        var second = new Target(
            [],
            [secondValue],
            new OperationGraph([new TestOperation([], [secondValue])]));
        var consumer = new Target(
            [first, second],
            [firstValue, secondValue],
            [],
            new OperationGraph(
                [new TestOperation([firstValue, secondValue], [])]),
            []);
        var program = new BuildProgram([first, second, consumer]);

        var result = AsciiGraphWriter.Render(program);

        Assert.Equal(2, CountOccurrences(result, "i0"));
        Assert.Equal(2, CountOccurrences(result, "i1"));
        Assert.DoesNotContain("i0▼", result);
        Assert.DoesNotContain("i1▼", result);
    }

    [Fact]
    public void ExpandedBuildProgramAlignsInputsWithReorderedBodyConsumers()
    {
        var firstInput = new Value<string>();
        var secondInput = new Value<string>();
        var firstResult = new Value<string>();
        var secondResult = new Value<string>();
        var output = new Value<string>();
        var first = new TestOperation([firstInput], [firstResult]);
        var second = new TestOperation([secondInput], [secondResult]);
        var consumer = new TestOperation(
            [secondResult, firstResult],
            [output]);
        var target = new Target(
            [firstInput, secondInput],
            [output],
            new OperationGraph([first, second, consumer]));

        var result = AsciiGraphWriter.Render(
            new BuildProgram([target]));

        Assert.DoesNotContain(
            result.Split(Environment.NewLine),
            line => line.StartsWith('│') &&
                line.Contains('┌') &&
                line.Contains('┼') &&
                line.Contains('┐'));
    }

    [Fact]
    public void ExpandedBuildProgramKeepsStaggeredInputsInEntryRank()
    {
        var firstInput = new Value<string>();
        var secondInput = new Value<string>();
        var thirdInput = new Value<string>();
        var firstResult = new Value<string>();
        var secondResult = new Value<string>();
        var output = new Value<string>();
        var first = new TestOperation([firstInput], [firstResult]);
        var second = new TestOperation(
            [firstResult, secondInput],
            [secondResult]);
        var third = new TestOperation(
            [secondResult, thirdInput],
            [output]);
        var target = new Target(
            [firstInput, secondInput, thirdInput],
            [output],
            new OperationGraph([first, second, third]));

        var result = AsciiGraphWriter.Render(
            new BuildProgram([target]));

        Assert.Contains("i0", result);
        Assert.Contains("i1", result);
        Assert.Contains("i2", result);
    }

    [Fact]
    public void ExpandedBuildProgramKeepsIncomingRoutesOffBodyOutputs()
    {
        var firstInput = new Value<string>();
        var secondInput = new Value<string>();
        var shared = new Value<string>();
        var firstResult = new Value<string>();
        var secondResult = new Value<string>();
        var producer = new TestOperation([firstInput], [shared]);
        var firstConsumer = new TestOperation(
            [shared, secondInput],
            [firstResult]);
        var secondConsumer = new TestOperation(
            [shared],
            [secondResult]);
        var target = new Target(
            [firstInput, secondInput],
            [firstResult, secondResult],
            new OperationGraph(
                [producer, firstConsumer, secondConsumer]));

        var lines = AsciiGraphWriter.Render(
                new BuildProgram([target]))
            .Split(Environment.NewLine);
        var producerLine = Array.FindIndex(
            lines,
            line => line.Contains("[2] TestOperation"));
        var outputColumn = lines[producerLine + 1].IndexOf('┬');

        Assert.DoesNotContain(
            lines.Skip(producerLine + 2).Take(2),
            line =>
                outputColumn < line.Length &&
                line[outputColumn] == '┼');
        Assert.DoesNotContain(
            Enumerable.Range(0, lines.Length - 1),
            lineIndex => Enumerable.Range(
                    0,
                    Math.Min(lines[lineIndex].Length, lines[lineIndex + 1].Length))
                .Any(column =>
                    (lines[lineIndex][column] is '│' or '╎') &&
                    (lines[lineIndex + 1][column] is '┌' or '┐')));
        Assert.DoesNotContain(
            Enumerable.Range(0, lines.Length - 1),
            lineIndex => Enumerable.Range(
                    0,
                    Math.Min(lines[lineIndex].Length, lines[lineIndex + 1].Length))
                .Any(column =>
                    (lines[lineIndex][column] is '─' or '╌') &&
                    (lines[lineIndex + 1][column] is '│' or '╎')));
    }

    [Fact]
    public void ExpandedBuildProgramConnectsEarlyOutputToTargetBoundary()
    {
        var input = new Value<string>();
        var output = new Value<string>();
        var intermediate = new Value<string>();
        var ignored = new Value<string>();
        var producer = new TestOperation([input], [output]);
        var later = new TestOperation([output], [intermediate]);
        var last = new TestOperation([intermediate], [ignored]);
        var target = new Target(
            [input],
            [output],
            new OperationGraph([producer, later, last]));

        var lines = AsciiGraphWriter.Render(
                new BuildProgram([target]))
            .Split(Environment.NewLine);
        var targetBottom = Array.FindLastIndex(
            lines,
            line => line.StartsWith('└') && line.Contains('┼'));
        var outputPorts = lines[targetBottom]
            .Select((character, index) => (character, index))
            .Where(entry => entry.character == '┼')
            .Select(entry => entry.index)
            .ToArray();

        Assert.NotEmpty(outputPorts);
        Assert.All(
            outputPorts,
            outputPort => Assert.NotEqual(
                ' ',
                lines[targetBottom - 1][outputPort]));
    }

    private static Target EmptyTarget() =>
        new([], [], new OperationGraph([]));

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;

        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }

    private sealed class TestOperation(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs) : Operation
    {
        public override IReadOnlyList<Value> Inputs { get; } = inputs;

        public override IReadOnlyList<Value> Outputs { get; } = outputs;
    }

    private sealed class OrderedTestOperation : Operation, IOrderedOperation
    {
        private readonly IReadOnlyList<Value> _inputs;

        public OrderedTestOperation(
            Value<OrderToken> orderInput,
            Value<int>? input = null)
        {
            OrderInput = orderInput;
            _inputs = input is null
                ? [orderInput]
                : [orderInput, input];
        }

        public Value<OrderToken>? OrderInput { get; }

        public Value<OrderToken>? OrderOutput { get; } = new();

        public Value<int> Result { get; } = new();

        public override IReadOnlyList<Value> Inputs => _inputs;

        public override IReadOnlyList<Value> Outputs =>
            [OrderOutput!, Result];
    }
}
