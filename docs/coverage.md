# Dynamic coverage: design note and go/no-go

## Why this was investigated

The biggest limit on what `dotnet tia` is worth is not a defect. It is the centrality ceiling: a
change inside a polymorphic core selects nearly the whole suite because every consumer reaches the
core through an interface, and no type-insensitive static analysis can rule the dispatch out.
FluentValidation is the measured case — a change in the validation engine selects ~100% of tests,
because every validator reaches it through `IPropertyValidator`, and nothing in the source says a
test using `NotNull()` never dispatches to `EnumValidator`.

`docs/plan.md` names dynamic coverage as the post-v1 remedy. This note is the spike that decides
whether to build it.

## The design constraint that would make it shippable

Coverage would have to be an **optional refinement, never a prerequisite**:

- `tia` keeps working identically on a cold clone with no coverage data. That property is what the
  whole design rests on — it is why the tool can be run on a fresh PR builder with no history.
- When data is present it is used **only to subtract** tests the static graph over-selected. It
  never adds a test, and it never removes a safety rule. A stale or missing entry can only leave
  the static answer intact.

That keeps the change additive in the same sense as the request-type edge, and confines the risk
to one place: subtraction is the only operation that can introduce a miss, so the mutation gate is
the thing that would have to sign it off.

## What subtraction actually needs

Per-test attribution: for each *test*, the set of members it executed. Not "which lines the suite
covered" — which test covered them.

That distinction is the whole spike, and it is where it ends.

## Finding 1 — the format the plan proposed cannot express the answer

`dotnet test --collect:"XPlat Code Coverage"` produces Cobertura XML through Coverlet. Cobertura
has no test dimension at all. Reproduced on a two-test project built for the purpose, whose shape
is the FluentValidation problem in miniature:

```csharp
public sealed class NotNullValidator : IValidator { public bool IsValid(object? v) => v is not null; }
public sealed class EnumValidator    : IValidator { public bool IsValid(object? v) => v is Enum; }
public sealed class Engine { public bool Run(IValidator v, object? value) => v.IsValid(value); }
```

One test exercises each validator through `Engine`. The Cobertura output:

```xml
<class name="Lib.NotNullValidator" ...>  <line number="9"  hits="1" /> </class>
<class name="Lib.EnumValidator"    ...>  <line number="14" hits="1" /> </class>
<class name="Lib.Engine"           ...>  <line number="19" hits="2" /> </class>
```

Both validators are covered, `Engine.Run` is covered twice, and nothing anywhere says *which* test
did which. `hits` is a run-wide counter. This is exactly the fact subtraction needs and exactly the
fact the format cannot hold.

## Finding 2 — isolation produces the answer, at a price that rules it out

Running each test alone does work. Same project, filtered to one test at a time, total hits per
class:

| | `Lib.NotNullValidator` | `Lib.EnumValidator` | `Lib.Engine` |
|---|---|---|---|
| `NotNullTests` alone | 2 | **0** | 2 |
| `EnumTests` alone | **0** | 2 | 2 |

The zeros are the answer: `NotNullTests` never touches `EnumValidator`, so a change confined to
`EnumValidator` should not select it. The static graph cannot know that; this does.

The cost is fatal. Each isolated run took **3.3–3.5 s**, almost all of it test-host startup, on a
project with two tests and no fixtures. NodaTime has 3,730 tests: roughly **3.5 hours** per
collection, repeated whenever the code moves enough to invalidate it. A refinement that costs
hours to keep current cannot pay for a run that takes 27 seconds.

## Finding 3 — the capability exists in the `.coverage` format, and is not reachable

The Microsoft Code Coverage collector (`Microsoft.CodeCoverage`, shipped with the SDK's test
tooling) is a different path from Coverlet, and its binary `.coverage` format *does* have the
dimension. Reading one back through `Microsoft.CodeCoverage.Core`:

```
module: Name=lib.dll SupportsSnapshotCoverage=True LineCoverage=100.00
SnapshotTags: 0
```

`SupportsSnapshotCoverage=True`, and `CoverageReport` exposes a `SnapshotTags` array — the file
format and its reader both model per-test snapshots. Strings inside the collector confirm the
feature is real and named: `PerTestCodeCoverage`, `TestImpactData`,
`TestImpactCollectorFriendlyName`.

It could not be switched on:

- A `.runsettings` with `<Format>PerTestCodeCoverage</Format>` is *accepted* — the collector
  validates that name and emits a `.coverage` rather than failing — but the resulting file
  contains **zero snapshot tags**. The format was selected; the snapshotting was not activated.
- The per-test machinery that does activate it is the Azure DevOps Test Impact collector
  (`TestImpactDataCollector`), which is Windows-only and is not part of what the SDK ships for
  `dotnet test` on any platform.
- Reading `.coverage` at all needs `Microsoft.CodeCoverage.Core`. Its `CoverageFileReader` is
  `internal`; the public `CoverageFileUtilityV2` works, but only after setting
  `ReadSnapshotsData`, an undocumented property found by reflecting over the assembly. Building a
  shipped feature on an undocumented property of a transitive package is not a dependency worth
  taking.

One correction worth recording, because it nearly produced the opposite conclusion: the raw
`.coverage` binary *does* contain the strings `NotNullTests` and `EnumTests`, which looks like
per-test identity. It is not. The test assembly is instrumented like any other, so those are its
own class names appearing as covered code. Reading the file properly is what showed
`SnapshotTags: 0`.

## Verdict: no-go, for now

Not because the design is wrong — the constraint in the second section is sound and the isolation
experiment shows the refinement would work. Because **the data cannot be obtained at a cost that
makes it worth obtaining**, on any platform this tool targets, with the tooling that exists today.

Shipping `--coverage <file>` now would mean shipping a flag that no ordinary `dotnet test`
invocation can produce input for. That is the same failure as the options this codebase has just
spent a phase removing: a switch that parses, prints in `--help`, and does nothing.

## What would change the answer

Any one of these, and this note gets revisited:

1. **A supported per-test collector for `dotnet test` on Linux.** The format already carries it,
   `SupportsSnapshotCoverage` is already true, and the reader already models snapshot tags. This is
   a plumbing gap in the tooling, not a missing capability, so it is the most likely to close.
2. **A cheap in-process harness.** A Microsoft.Testing.Platform extension that observes test start
   and end in the same process could snapshot a hit table per test without a host restart, which is
   what removes the 3.4 s. It would have to force serial execution — xUnit v3 parallelises within
   an assembly, and concurrent tests cannot be attributed by a shared table — so the collection run
   would still be slower than a normal run, but by a factor, not by three orders of magnitude.
3. **Evidence that the ceiling costs more than it appears to.** The replay benchmarks put
   FluentValidation at a 51.0% mean selection and NodaTime at 35.5%. If a repository shows the
   ceiling dominating — most commits selecting ~100% — the case for paying a large collection cost
   improves. Neither gated repository does.

## What is being done instead

The ceiling is stated plainly rather than papered over. `README.md` says a change to
FluentValidation's core selects ~100% and why; `docs/benchmarks.md` carries the distributions that
show it. The honest near-term mitigations are the ones already in the tool: `--coverage-threshold`
decides when a project stops filtering and runs whole, and `explain` answers why a given test was
picked, which is what lets an adopter see the ceiling for themselves rather than take it on trust.
