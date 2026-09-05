# Findings

## `CallTarget` uses delayed legacy state merging

MSBuild's `CallTarget` does not behave like a normal target dependency or
function call. The called target executes against project state from the start
of the calling target, so it cannot observe mutations made earlier within that
calling target. Its own property and item mutations are likewise invisible to
the remainder of the calling target after `CallTarget` returns.

Internally, MSBuild retains those mutations in a hidden `Lookup` scope. When
the calling target completes, MSBuild first merges the called-target scope into
project state, then merges the calling target's scope on top. Consequently,
later targets see both sets of changes, with conflicting caller property
changes winning. Target completion is cached separately, so a later request
for the called target does not rerun it.

This is explicitly described in MSBuild's `TargetBuilder` as legacy, erroneous
behavior. A faithful DAG model therefore cannot represent `CallTarget` as
either an ordinary dependency or a conventional isolated invocation; it needs
delayed state merging plus target-result caching.
