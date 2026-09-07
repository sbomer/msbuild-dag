# Strict target ordering

The compiler derives one global target DAG directly from the evaluated target
definitions. The DAG does not depend on the order of
`BuildDefinition.Targets`, and target requests never add or remove its edges.

A request is accepted only when MSBuild's structural execution order is a
linear extension of the global DAG. Otherwise the request is rejected before
any target body executes.

## Definition-derived order

For the general Core API, each target contributes the ordered sequence:

```text
Prelude, target body, Epilogue
```

Consecutive elements create immediate precedence edges.

The MSBuild frontend preserves more provenance and derives edges as follows:

```text
A DependsOnTargets="B"  => B < A
A BeforeTargets="B"     => A < B
A AfterTargets="B"      => B < A
```

Entries in one `DependsOnTargets` list are independent global predecessors:

```text
A DependsOnTargets="B;C" => B < A and C < A
```

`B` and `C` remain incomparable in the global DAG. Their evaluated list order
is retained in activation metadata, so an MSBuild request for `A` still requests
`B` before `C`.

Multiple targets registered before the same anchor likewise each precede the
anchor, but remain incomparable with one another unless another declaration
relates them. The same applies to multiple after-targets:

```text
A BeforeTargets="Build" => A < Build
B BeforeTargets="Build" => B < Build

A and B remain incomparable.
```

The activation model retains MSBuild's evaluated order. `Prelude` contains
ordered dependencies followed by registered before-targets, and `Epilogue`
contains registered after-targets. This order is used to plan an individual
request, but it is not added to the global DAG between otherwise independent
dependencies or hooks.

## Contradictions

The union of all definition-derived edges must be acyclic. A cycle means the
definitions demand incompatible global precedence, so linking fails.

For example:

```text
C.Prelude  = [A]
C.Epilogue = [A]
```

contributes both:

```text
A < C
C < A
```

MSBuild may execute some requests successfully because its target-once cache
makes one occurrence a no-op. The strict model deliberately rejects the
definition instead of making request-dependent exceptions or choosing one
direction with a tie-breaker.

Ordered dependency siblings do not create such contradictions. For example:

```xml
<Target Name="Early" BeforeTargets="A" />
<Target Name="A" />
<Target Name="Build" DependsOnTargets="A;Early" />
```

The global DAG contains `Early < A`, `A < Build`, and `Early < Build`, but not
`A < Early`. The activation planner requests `A`, first executes its `Early`
hook, and treats the later explicit request for `Early` as a target-once no-op.
The accepted body order is therefore `Early, A, Build`.

## Request validation

The global DAG alone does not determine whether a particular request is
admissible. `BuildProgram.GetRequestOrder` first performs a structural
MSBuild-style stack traversal against the current completed-target set. It
models:

- ordered Prelude and Epilogue activation;
- target-once completion;
- epilogues reserved below the current body;
- suppression of pending duplicate epilogues;
- circular Prelude detection.

The resulting request order is accepted only when every unfinished scheduling
or data predecessor has already completed or appeared earlier in the same
request. In other words, the MSBuild order must be a linear extension of the
global DAG.

Targets that are incomparable in the global DAG may still execute in MSBuild's
request-specific order. That order is not added to the global relation.

The executor then runs the validated order directly. Therefore every accepted
request has exactly the same target-body order as the structural MSBuild
planner, while rejected requests execute no bodies.

## Global precedence versus request order

`BuildProgram` intentionally retains two different structures:

1. The global precedence DAG contains only mandatory,
   definition-order-independent constraints. Each dependency precedes its
   owning target, each before-target precedes its anchor, and each after-target
   follows its anchor. Independent dependency and hook siblings remain
   incomparable.
2. Each target's ordered `Prelude` and `Epilogue` retain the evaluated MSBuild
   activation order, including registration order between sibling hooks.

The global DAG is not a complete execution schedule. An arbitrary topological
ordering may reorder incomparable dependencies or hooks differently from
MSBuild and must not be used directly for execution.
`BuildProgram.GetRequestOrder` structurally plans the exact MSBuild order for
the requested target and current completed set, verifies that it is a linear
extension of the global DAG, and returns that validated order.
`BuildProgramExecutor` executes this order rather than selecting an arbitrary
topological ordering.

Removing dependency-sibling edges accepts more evaluated definitions and can
only remove global precedence cycles. It also makes more arbitrary
topological orders possible, including orders MSBuild would not choose.
Those orders are not accepted as execution requests: execution always starts
from the retained activation metadata, and the resulting MSBuild order must
still satisfy every mandatory DAG predecessor.

Consequently:

- the executor requires no external sidecar, but the lowered `BuildProgram`
  must retain both the DAG and ordered activation metadata;
- serializers and alternative executors must preserve and consume Prelude and
  Epilogue order; target nodes plus global DAG edges are insufficient;
- incomparability in the global DAG does not authorize reordering or
  parallelizing targets from one request unless their request-specific order
  and observable effects are also proven irrelevant; and
- a request is rejected before executing bodies when its MSBuild order
  violates a mandatory DAG predecessor or omits an unfinished predecessor.

Consider:

```text
A.Epilogue = [B]
B.Epilogue = [C]
C.Prelude  = [A]
```

The definition-derived DAG is:

```text
A < B < C
```

Requesting `A` produces `A, B, C` and is accepted. Requesting `B` produces the
MSBuild order `B, A, C`, which violates `A < B`, so it is rejected.

## Mathematical properties

Let `E` be the set of immediate edges contributed by all target sequences.
When `E` is acyclic, its reachability relation `E+` is a strict partial order.

The result is:

- **unique:** `E` is fixed by the target definitions, and transitive closure is
  unique;
- **definition-list independent:** collecting edges is a set union, so
  permuting the top-level target collection cannot change the relation;
- **structural:** every immediate edge relates a dependency or hook to its
  owning target or anchor;
- **minimal:** the relation contains only immediate structural edges and their
  transitive consequences;
- **partially ordered:** disconnected targets remain incomparable.

No topological tie-breaker becomes part of the semantic result.

## Complexity

For `n` targets and `m` target references:

- immediate DAG construction is `O(n + m)`;
- cycle detection is `O(n + m)`;
- a request plan is linear in the structural work activated by that request;
- materializing every target's full predecessor set requires `O(n^2)` space in
  the worst case.

The implementation currently computes predecessor closure with per-target
graph walks. A bitset topological closure would reduce constants for very large
target graphs without changing semantics.

## Scope

Target conditions must be resolved before constructing the target DAG. The
ordering proof assumes that every target and orchestration edge in the Core
model is unconditional.

A frontend may specialize a target condition using the fixed evaluation state,
but it must lower both outcomes with MSBuild semantics. A false target
condition suppresses the target body and its `DependsOnTargets`, while
registered `BeforeTargets` and `AfterTargets` remain active. If the condition
can change during target execution or cannot be resolved statically, the
frontend must reject the project rather than place the unresolved condition in
this model.

Consequently, the target DAG and request planner do not reevaluate target
conditions. Their correctness claim applies only after conditional target
structure has been eliminated or specialized into an unconditional graph.

The current model keeps dependency-to-owner and hook-to-anchor constraints in
one DAG, but does not order independent dependency or hook siblings.
