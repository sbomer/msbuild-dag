namespace MSBuild.Dag.Core.Tests;

public sealed class BuildDefinitionTests
{
    [Fact]
    public void EvaluationSnapshotMapsLocationsToConcreteValues()
    {
        var stringLocation = new Location<string>();
        var integerLocation = new Location<int>();
        var snapshot = new EvaluationSnapshot(
            new Dictionary<Location, object?>
            {
                [stringLocation] = "value",
                [integerLocation] = 42,
            });

        Assert.Equal("value", snapshot.Values[stringLocation]);
        Assert.Equal(42, snapshot.Values[integerLocation]);
    }

    [Fact]
    public void EvaluationSnapshotRejectsValueOfWrongType()
    {
        var location = new Location<string>();

        Assert.Throws<ArgumentException>(
            () => new EvaluationSnapshot(
                new Dictionary<Location, object?>
                {
                    [location] = 42,
                }));
    }

    [Fact]
    public void LinksTargetInputToLatestOrderedOutput()
    {
        var location = new Location<string>();
        var writtenValue = new Value<string>();
        var writeOperation = new TestOperation([], [writtenValue]);
        var writer = new TargetDefinition(
            [],
            [],
            [new TargetOutput<string>(location, writtenValue)],
            new OperationGraph([writeOperation]),
            []);
        var read = new TargetInput<string>(location);
        var result = new Value<string>();
        var readOperation = new TestOperation([read.Value], [result]);
        var reader = new TargetDefinition(
            [writer],
            [read],
            [new TargetOutput(result)],
            new OperationGraph([readOperation]),
            []);

        var linked = new BuildDefinition(
            new EvaluationSnapshot(
                new Dictionary<Location, object?>
                {
                    [location] = "initial",
                }),
            [reader, writer])
            .Link();

        var linkedWriter = linked.Targets[writer];
        var linkedReader = linked.Targets[reader];
        var exportedValue = Assert.Single(linkedWriter.Outputs);

        Assert.Null(linkedReader.Body.GetProducer(read.Value));
        Assert.Equal([read.Value], linkedReader.Body.Inputs);
        Assert.Equal([exportedValue], linkedReader.Inputs);
        Assert.Contains(writtenValue, linkedWriter.Body.Outputs);
        Assert.NotSame(writtenValue, exportedValue);
        Assert.Same(
            exportedValue,
            linked.GetStateAfter(reader)[location]);
    }

    [Fact]
    public void LinksTargetInputToEvaluationValueWithoutOutput()
    {
        var location = new Location<string>();
        var evaluation = new EvaluationSnapshot(
            new Dictionary<Location, object?>
            {
                [location] = "initial",
            });
        var read = new TargetInput<string>(location);
        var result = new Value<string>();
        var target = new TargetDefinition(
            [],
            [read],
            [new TargetOutput(result)],
            new OperationGraph(
            [
                new TestOperation([read.Value], [result]),
            ]),
            []);

        var linked = new BuildDefinition(
            evaluation,
            [target])
            .Link();

        var linkedTarget = linked.Targets[target];

        Assert.Null(linkedTarget.Body.GetProducer(read.Value));
        Assert.Equal([read.Value], linkedTarget.Body.Inputs);
        Assert.Equal(
            [linked.InitialValues[location]],
            linkedTarget.Inputs);
        Assert.Same(
            linked.InitialValues[location],
            linked.GetStateAfter(target)[location]);
    }

    [Fact]
    public void LinksTargetInputToBuildInputWithoutBakingContent()
    {
        var location = new Location<string>();
        var read = new TargetInput<string>(location);
        var result = new Value<string>();
        var target = new TargetDefinition(
            [],
            [read],
            [new TargetOutput(result)],
            new OperationGraph(
            [
                new TestOperation([read.Value], [result]),
            ]),
            []);

        var linked = new BuildDefinition(
            new EvaluationSnapshot(
                new Dictionary<Location, object?>()),
            [location],
            [target])
            .Link();
        var linkedTarget = linked.Targets[target];
        var input = linked.Inputs[location];

        Assert.Empty(linked.Program.InitialValues);
        Assert.Equal([input], linked.Program.Inputs);
        Assert.Equal([input], linkedTarget.Inputs);
        Assert.Same(
            input,
            linked.GetStateAfter(target)[location]);
    }

    [Fact]
    public void RejectsUnorderedStateConflict()
    {
        var location = new Location<string>();
        var writtenValue = new Value<string>();
        var writer = new TargetDefinition(
            [],
            [],
            [new TargetOutput<string>(location, writtenValue)],
            new OperationGraph(
            [
                new TestOperation([], [writtenValue]),
            ]),
            []);
        var read = new TargetInput<string>(location);
        var reader = new TargetDefinition(
            [],
            [read],
            [],
            new OperationGraph(
            [
                new TestOperation([read.Value], []),
            ]),
            []);

        var exception = Assert.Throws<StateConflictException>(
            () => new BuildDefinition(
                new EvaluationSnapshot(
                    new Dictionary<Location, object?>
                    {
                        [location] = "initial",
                    }),
                [writer, reader])
                .Link());

        Assert.Same(writer, exception.FirstTarget);
        Assert.Same(reader, exception.SecondTarget);
        Assert.Same(location, exception.Location);
    }

    [Fact]
    public void RejectsConditionalTargetOutput()
    {
        var location = new Location<string>();
        var value = new Value<string>();
        var target = new TargetDefinition(
            [],
            [],
            [
                new TargetOutput<string>(location, value)
                {
                    IsConditional = true,
                },
            ],
            new OperationGraph(
            [
                new TestOperation([], [value]),
            ]),
            []);

        var exception = Assert.Throws<ConditionalTargetOutputException>(
            () => new BuildDefinition(
                new EvaluationSnapshot(
                    new Dictionary<Location, object?>
                    {
                        [location] = "initial",
                    }),
                [target])
                .Link());

        Assert.Same(target, exception.Target);
    }

    [Fact]
    public void RejectsContradictoryNestedPreludeOrder()
    {
        var b = EmptyTarget();
        var a = EmptyTarget([b]);
        var anchor = EmptyTarget([a, b]);

        var exception = Assert.Throws<TargetOrderCycleException>(
            () => Link([anchor, a, b]));

        Assert.Contains(a, exception.Cycle);
        Assert.Contains(b, exception.Cycle);
    }

    [Fact]
    public void ComputesNestedPreludeClosuresInRequestOrder()
    {
        var c = EmptyTarget();
        var a = EmptyTarget([c]);
        var d = EmptyTarget();
        var b = EmptyTarget([d]);
        var root = EmptyTarget([a, b]);

        var linked = Link([root, a, b, c, d]);

        var expected = new HashSet<Target>(
            [
                linked.Targets[c],
                linked.Targets[a],
                linked.Targets[d],
                linked.Targets[b],
            ],
            ReferenceEqualityComparer.Instance);

        Assert.True(
            expected.SetEquals(
                linked.Program.GetOrderPredecessors(linked.Targets[root])));
    }

    [Fact]
    public void LeavesEpilogueOfPreludeUnorderedWithParentBody()
    {
        var after = EmptyTarget();
        var dependency = EmptyTarget([], [after]);
        var root = EmptyTarget([dependency]);

        var linked = Link([root, dependency, after]);

        Assert.DoesNotContain(
            linked.Targets[after],
            linked.Program.GetOrderPredecessors(linked.Targets[root]));
    }

    [Fact]
    public void DeduplicatesRepeatedTargetRequests()
    {
        var dependency = EmptyTarget();
        var root = EmptyTarget([dependency, dependency]);

        var linked = Link([root, dependency]);

        var expected = new HashSet<Target>(
            [linked.Targets[dependency]],
            ReferenceEqualityComparer.Instance);

        Assert.True(
            expected.SetEquals(
                linked.Program.GetOrderPredecessors(linked.Targets[root])));
    }

    [Fact]
    public void PartialOrderIsIndependentOfTargetDefinitionOrder()
    {
        var b = EmptyTarget();
        var a = EmptyTarget([b]);
        var root = EmptyTarget([a]);
        var disconnected = EmptyTarget();

        foreach (var targets in Permute([root, a, b, disconnected]))
        {
            var linked = Link(targets);

            AssertPredecessors(a, [b]);
            AssertPredecessors(root, [a, b]);
            AssertPredecessors(b, []);
            AssertPredecessors(disconnected, []);

            void AssertPredecessors(
                TargetDefinition target,
                IReadOnlyList<TargetDefinition> expected)
            {
                var actual = linked.Program.GetOrderPredecessors(
                    linked.Targets[target]);
                var expectedTargets = new HashSet<Target>(
                    expected.Select(item => linked.Targets[item]),
                    ReferenceEqualityComparer.Instance);

                Assert.True(expectedTargets.SetEquals(actual));
            }
        }
    }

    [Fact]
    public void RejectsIncompatiblePreludeOrders()
    {
        var a = EmptyTarget();
        var b = EmptyTarget();
        var first = EmptyTarget([a, b]);
        var second = EmptyTarget([b, a]);

        foreach (var targets in Permute([first, second, a, b]))
        {
            var exception = Assert.Throws<TargetOrderCycleException>(
                () => Link(targets));

            Assert.Contains(a, exception.Cycle);
            Assert.Contains(b, exception.Cycle);
        }
    }

    [Fact]
    public void UsesOnlyStrictDefinitionOrderForRunOnceTargets()
    {
        var firstHook = EmptyTarget();
        var sharedHook = EmptyTarget();
        var afterClean = EmptyTarget();
        var clean = EmptyTarget([sharedHook], [afterClean]);
        var build = EmptyTarget([firstHook, sharedHook]);
        var rebuild = EmptyTarget([clean, build]);

        var linked = Link(
            [
                rebuild,
                build,
                clean,
                firstHook,
                sharedHook,
                afterClean,
            ]);

        Assert.DoesNotContain(
            linked.Targets[afterClean],
            linked.Program.GetOrderPredecessors(linked.Targets[build]));
        Assert.DoesNotContain(
            linked.Targets[build],
            linked.Program.GetOrderPredecessors(linked.Targets[afterClean]));
    }

    [Fact]
    public void RejectsRecursiveEpilogueActivation()
    {
        var a = EmptyTarget();
        var b = EmptyTarget();
        var definition = new BuildDefinition(
            new EvaluationSnapshot(
                new Dictionary<Location, object?>()),
            [a, b],
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance)
            {
                [a] = [],
                [b] = [],
            },
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance)
            {
                [a] = [b],
                [b] = [a],
            });

        var exception = Assert.Throws<TargetOrderCycleException>(
            definition.Link);

        Assert.Equal(3, exception.Cycle.Count);
        Assert.Same(exception.Cycle[0], exception.Cycle[^1]);
    }

    [Fact]
    public void PreservesAcyclicPrecedenceThroughRecursiveActivation()
    {
        var a = EmptyTarget();
        var c = EmptyTarget([a]);
        var b = EmptyTarget([c]);
        var preludes =
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance)
            {
                [a] = [],
                [b] = [c],
                [c] = [a],
            };
        var epilogues =
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance)
            {
                [a] = [b],
                [b] = [],
                [c] = [],
            };

        foreach (var targets in Permute([a, b, c]))
        {
            var linked = new BuildDefinition(
                new EvaluationSnapshot(
                    new Dictionary<Location, object?>()),
                targets,
                preludes,
                epilogues)
                .Link();
            var aPredecessors = linked.Program.GetOrderPredecessors(
                linked.Targets[a]);
            var cPredecessors = linked.Program.GetOrderPredecessors(
                linked.Targets[c]);
            var bPredecessors = linked.Program.GetOrderPredecessors(
                linked.Targets[b]);

            Assert.Empty(aPredecessors);
            Assert.True(
                new HashSet<Target>(
                    [linked.Targets[a]],
                    ReferenceEqualityComparer.Instance)
                    .SetEquals(cPredecessors));
            Assert.True(
                new HashSet<Target>(
                    [linked.Targets[a], linked.Targets[c]],
                    ReferenceEqualityComparer.Instance)
                    .SetEquals(bPredecessors));
        }
    }

    [Fact]
    public void DoesNotInferOrderThroughNestedActivation()
    {
        var a = EmptyTarget();
        var b = EmptyTarget();
        var c = EmptyTarget();
        var d = EmptyTarget();
        var e = EmptyTarget();
        var f = EmptyTarget();
        var definition = new BuildDefinition(
            new EvaluationSnapshot(
                new Dictionary<Location, object?>()),
            [a, b, c, d, e, f],
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance)
            {
                [a] = [],
                [b] = [c, d],
                [c] = [a],
                [d] = [],
                [e] = [f],
                [f] = [d],
            },
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance)
            {
                [a] = [b],
                [b] = [],
                [c] = [],
                [d] = [e],
                [e] = [],
                [f] = [],
            });

        var linked = definition.Link();

        Assert.DoesNotContain(
            linked.Targets[e],
            linked.Program.GetOrderPredecessors(linked.Targets[b]));
        Assert.DoesNotContain(
            linked.Targets[b],
            linked.Program.GetOrderPredecessors(linked.Targets[e]));
    }

    [Fact]
    public void BuildsStrictOrderFromDefinitionSequences()
    {
        var a = EmptyTarget();
        var b = EmptyTarget();
        var c = EmptyTarget();
        var definition = new BuildDefinition(
            new EvaluationSnapshot(
                new Dictionary<Location, object?>()),
            [c, b, a],
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance)
            {
                [a] = [],
                [b] = [],
                [c] = [a],
            },
            new Dictionary<TargetDefinition, IReadOnlyList<TargetDefinition>>(
                ReferenceEqualityComparer.Instance)
            {
                [a] = [b],
                [b] = [c],
                [c] = [],
            });

        var linked = definition.Link();

        Assert.True(
            new HashSet<Target>(
                [linked.Targets[a], linked.Targets[b]],
                ReferenceEqualityComparer.Instance)
                .SetEquals(
                    linked.Program.GetOrderPredecessors(linked.Targets[c])));

        var exception = Assert.Throws<InvalidOperationException>(
            () => linked.GetStateAfter(b));

        Assert.Contains("program order", exception.Message);
        linked.GetStateAfter(a);
    }

    [Fact]
    public void RejectsTargetOrderedBeforeAndAfterSameBody()
    {
        var a = EmptyTarget();
        var c = EmptyTarget([a], [a]);

        var exception = Assert.Throws<TargetOrderCycleException>(
            () => Link([c, a]));

        Assert.Contains(a, exception.Cycle);
        Assert.Contains(c, exception.Cycle);
    }

    private static BuildLinkResult Link(
        IReadOnlyList<TargetDefinition> targets) =>
        new BuildDefinition(
            new EvaluationSnapshot(
                new Dictionary<Location, object?>()),
            targets)
            .Link();

    private static IEnumerable<IReadOnlyList<T>> Permute<T>(
        IReadOnlyList<T> values)
    {
        if (values.Count == 0)
        {
            yield return [];
            yield break;
        }

        for (var index = 0; index < values.Count; index++)
        {
            var remaining = values
                .Where((_, candidateIndex) => candidateIndex != index)
                .ToArray();

            foreach (var suffix in Permute(remaining))
            {
                yield return [values[index], .. suffix];
            }
        }
    }

    private static TargetDefinition EmptyTarget(
        IReadOnlyList<TargetDefinition>? prelude = null,
        IReadOnlyList<TargetDefinition>? epilogue = null) =>
        new(
            prelude ?? [],
            [],
            [],
            new OperationGraph([]),
            epilogue ?? []);

    private sealed class TestOperation(
        IReadOnlyList<Value> inputs,
        IReadOnlyList<Value> outputs) : Operation
    {
        public override IReadOnlyList<Value> Inputs { get; } = inputs;

        public override IReadOnlyList<Value> Outputs { get; } = outputs;
    }
}
