# Semantic compatibility TODOs

The restricted translator does not need to reproduce MSBuild's execution
mechanism, but every accepted construct should preserve its observable
semantics. If equivalence cannot be established, translation should fail with a
specific diagnostic rather than silently approximate the behavior.

## Highest priority

### Eliminate linked-target boundary aliases

Linking currently creates distinct external target input/output values and
copies concrete contents across the target boundary. The input copies are
necessary only because operations in an immutable target body still reference
their original target-local input values after linking.

- Add immutable SSA value substitution when linking a `TargetDefinition`.
- Rewrite every operation operand, including operands in nested conditional
  regions, from each target-local input to its reaching linked value.
- Export body-produced output values directly instead of creating sibling
  output aliases.
- Make linked `Target.Inputs` and `Target.Outputs` use the same `Value<T>`
  instances consumed and produced by the linked body.
- Preserve independent target caching: a cache hit supplies the target's
  output values directly and skips its body.
- Remove executor boundary copies after substitution is complete.
- Add structural tests proving cross-target consumers reference producer
  values directly and execution/cache behavior remains correct.

## Confirmed silent divergences

### Built-in item metadata

`MSBuildItem.GetMetadataValue` currently models `Identity` and stored custom
metadata. References such as `%(Filename)`, `%(Extension)`, `%(FullPath)`, and
`%(RecursiveDir)` are accepted but evaluate to an empty string because
`ProjectItemInstance.Metadata` excludes built-in metadata.

- Decide which built-in metadata to model and what path context each requires.
- Until modeled, reject known built-in metadata other than `Identity`.
- Apply the check to metadata updates, conditions, and item transforms.
- Add differential tests for every supported built-in metadata name.

### Sibling metadata assignments use the wrong snapshot

MSBuild evaluates sibling assignments in one item operation from the same
pre-operation metadata snapshot:

```xml
<I A="new" B="%(A)" />
```

With an incoming `A="old"`, MSBuild produces `A="new", B="old"`. The current
lowering applies assignments sequentially and produces `B="new"`.

- Prefer computing all right-hand-side vectors from the original item
  collection and applying the assignments atomically.
- Until atomic updates exist, reject an item operation when a metadata
  expression references metadata assigned by the same operation and could
  observe an earlier lowered assignment.
- Add a differential regression test for the example above.

### MSBuild escaping

Metadata literals and transform separators currently preserve escape text such
as `%0A`. MSBuild unescapes it before the value becomes observable to tasks.
The current lowering can therefore produce the literal characters `%0A`
instead of a newline.

- Define whether string values in the DAG are escaped or unescaped.
- Unescape at the same semantic boundary as MSBuild, or represent escaping as
  an explicit operation.
- Until defined, reject accepted metadata expressions and transform separators
  containing MSBuild escape sequences.
- Cover escaped semicolons, percent signs, newlines, and item identities.

### Metadata in target conditions

MSBuild rejects raw item metadata in a target `Condition` with MSB4116. The
translator currently accepts a qualified `%(I.Identity)` equality and lowers
it to an aggregate collection predicate, allowing a project that MSBuild
rejects.

- Reject `%(` references in target conditions.
- Keep metadata conditions supported only on constructs where the restricted
  pointwise lowering has been proven equivalent.
- Add a regression test asserting the diagnostic.

### Target-time item globs

Target-time `Include` values such as `src/**/*.cs` are currently split into
literal item identities. MSBuild enumerates matching files when the item
operation executes.

- Either add an explicit filesystem/glob operation with declared effects and
  inputs, or reject wildcard item specifications.
- Until modeled, reject `*` and `?` in target-time `Include` and `Exclude`.
- Include project-directory and path-normalization rules in any future model.

### Exclude item-spec matching

`ExcludeItemsOperation` currently removes exact identities using an
ordinal-ignore-case comparison. MSBuild `Exclude` also supports wildcard and
item-spec matching semantics.

- Define and document the exact-item exclusion subset.
- Reject non-exact item specifications outside that subset.
- Add differential tests for casing, relative paths, duplicate identities,
  escaped separators, and wildcard exclusions.

## Semantics requiring an explicit policy

### Target Inputs, Outputs, Returns, and target batching

The translator currently ignores MSBuild target `Inputs`, `Outputs`, and
`Returns`. These attributes affect incremental execution, returned values, and
target batching.

- Decide whether the DAG models only state dataflow or also target invocation
  and incremental execution semantics.
- If these semantics remain out of scope, emit a warning or error when the
  attributes are present rather than silently ignoring them.
- Always reject target batching until a batch-aware target invocation model
  exists.

### Batch-sensitive property writes

Property writes performed by metadata-batched implicit tasks use independent
batch snapshots, and the last writing batch determines the final property
value. An aggregate predicate is equivalent only for narrow cases such as
assigning the same constant whenever any item matches.

- Document the exact property-assignment patterns that can be safely reduced
  to aggregate predicates.
- Reject metadata-derived property values and other last-batch-wins cases
  until ordered batch semantics are modeled.
- Add differential tests with multiple metadata buckets.

### Empty versus absent metadata

`MSBuildItem.WithMetadata` removes entries assigned an empty value. Basic
`%(M)` expansion cannot distinguish absent from empty metadata, but future
metadata enumeration and keep/remove operations might.

- Determine which supported operations can observe metadata presence.
- Preserve explicit empty metadata if it is observable.
- Otherwise document the equivalence and reject operations that would expose
  the distinction.

### Ordered collection semantics

MSBuild item groups preserve ordering and duplicate item instances. Although
the design sometimes calls them sets, the current runtime representation is an
ordered list.

- Treat item values as ordered collections in the public model and
  documentation.
- Do not deduplicate by identity.
- Add regression tests ensuring transforms, masks, and metadata updates retain
  order and duplicates.

## Validation strategy

- Maintain small differential projects that execute under both real MSBuild
  and the restricted evaluator.
- Compare final properties, item identities, metadata, task invocation counts,
  and observable messages.
- Every new accepted `%()` context must be classified as a transform,
  pointwise implicit-task lowering, task batching, or target batching.
- Prefer a precise unsupported diagnostic whenever equivalence has not been
  demonstrated.

## Lowest-priority future optimization

### Lower projects independently of global properties

The current model lowers an evaluated project instance: a project together
with a fixed set of global properties. Global properties are therefore known
before translation and may affect imports, conditions, items, targets, and the
resulting graph structure.

- Explore lowering a project before its global properties are known.
- Represent global properties as parameters where they do not affect graph
  structure.
- Partially evaluate property-dependent expressions and specialize the graph
  once the invocation's global properties are available.
- Preserve project-instance semantics when global properties affect imports or
  other structural evaluation behavior.
- Treat this only as a future graph-reuse optimization; do not complicate the
  current per-project-instance lowering model.
