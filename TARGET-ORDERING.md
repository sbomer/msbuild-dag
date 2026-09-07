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

## State conflicts and request legality

The build definition may contain unrelated target pipelines that access the
same property, item, or other modeled state location. Unordered read/read
access is harmless. If at least one access writes, however, executing both
targets would make the observed value depend on request order rather than the
global target DAG.

Such a definition is retained. Linking records a fixed
`TargetStateConflict` for each unordered conflicting target pair and location:

```text
global dataflow:
    initial State ----------------> Reader
    initial State -> Writer output

request constraint:
    Conflict(Writer, Reader, State)
```

The writer's output is not wired into the reader merely because one request
might list the writer first. Since the target DAG does not make the writer a
predecessor, the reader keeps its globally determined input value.

After structural activation planning and target-once suppression,
`BuildProgram.GetRequestOrder` checks the complete active set, including
targets completed by earlier requests in the same executor. A request is
rejected if both endpoints of any state conflict are active. This happens
before any newly requested target body executes.

Consequently, either target can be requested independently, but a request or
persistent execution history containing both is illegal:

```text
request Reader         => accepted
request Writer         => accepted
request Writer; Reader => rejected
request Reader; Writer => rejected
```

This validation does not relink or specialize dataflow for a request. The
program contains one global value graph plus a global conflict relation.
Requests only select legal executable slices of that fixed program.

Ordered conflicting accesses do not create a request constraint. Their values
are linked through the target DAG in the usual way. Conditional operations are
currently treated conservatively: a possible read or write contributes to the
target's access set even when its condition happens to be false for the
current evaluation.

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
  must retain the DAG, ordered activation metadata, and state-conflict
  constraints;
- serializers and alternative executors must preserve and consume Prelude and
  Epilogue order and request conflicts; target nodes plus global DAG edges are
  insufficient;
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
- state-conflict discovery currently compares unordered target pairs and is
  quadratic in `n`, multiplied by their state-access counts;
- a request plan is linear in the structural work activated by that request
  plus the number of recorded state conflicts;
- materializing every target's full predecessor set requires `O(n^2)` space in
  the worst case.

The implementation currently computes predecessor closure with per-target
graph walks. A bitset topological closure would reduce constants for very large
target graphs without changing semantics.

## Scope

The Core ordering model assumes every target and orchestration edge is
unconditional. The MSBuild frontend therefore does not model target-level
`Condition` attributes.

A conditional target is lowered to an unsupported target with no body state,
outputs, or `DependsOnTargets`. Translation emits a warning, and activating the
target throws unconditionally at runtime. Its statically evaluated
`BeforeTargets` and `AfterTargets` registrations are retained so a request
cannot silently bypass the unsupported target.

This policy is deliberately conservative. It does not evaluate even a
statically false target condition, and it does not model skipped-target
reevaluation or conditional dependency activation. Unreachable conditional
targets do not prevent graph construction, while any request that activates one
fails before that target can produce state changes.

The current model keeps dependency-to-owner and hook-to-anchor constraints in
one DAG, but does not order independent dependency or hook siblings.
