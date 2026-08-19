# Benchmarks

Measured runs, not projections. Reproduce them with the drivers in `tests/Tia.Validation`.

## Correctness: mutation harness

The merge gate. Inject a Stryker-style mutation, select against it, run the **full** suite, and
check that every failing test was in the selection.

| Repository | Samples | Usable | Misses | Typical selection |
|---|---:|---:|---:|---|
| `tests/Tia.Fixtures` (xUnit v3 + NUnit, 12 tests) | 40 | 26 | **0** | 2 / 12 |
| `tests/Tia.Fixtures.Tunit` (TUnit, source-generated, 4 tests) | 40 | 40 | **0** | 1 / 4 |
| **NodaTime** (NUnit, 3,730 tests, 21 projects) | 25 | 20 | **0** | 8 % |
| **FluentValidation** (xUnit, 2,460 tests, source-generated) | 20 | 16 | **0** | - |
| **MediatR** (xUnit, 392 tests, container dispatch) | 20 | 9 | **0** | - |

The TUnit row is the one that matters for the generated-output comparison below: it is a project
whose tests exist only because a generator emitted their registrations, and selection there is
both precise and safe.

Skipped samples are files that offer no mutation site, or mutations that do not compile. A sample
whose outcome cannot be read is reported as inconclusive, never as a pass.

```
dotnet run --project tests/Tia.Validation -- mutate --repo <path> --samples 200
```

## Selection ratio: commit replay

### FluentValidation

25 first-parent commits requested; 12 analysable with the SDK available here (older commits pin
SDKs that could not be restored). 2,460 tests across three target frameworks.

| Change | Selected |
|---|---|
| docs only (4 commits) | **0 %** |
| one test file (1 commit) | **1.8 %** |
| a library change outside the rule engine (1 commit) | **10.6 %** |
| the polymorphic core (5 commits) | **~100 %** |
| build inputs (1 commit) | full run, by design |

**Mean selection 51.0 % · full-run rate 8 % · commits with a widening 50 %**

Cold graph build: 421 types / 2,519 members / 16,075 edges in **18.6 s**.

### Why the library half is 100 %

This was worth chasing, because the first explanation was wrong. It is not the source generator,
and it is not a widening. It is `explain` output on an unrelated test after a one-line change to
`CreditCardValidator.Name`:

```
  CreditCardValidator<T>.Name   (changed)
    |  implementation -> interface member
  PropertyValidator<T, TProperty>.Name
    |  referenced by
  IPropertyValidator.Name
    |  referenced by
  ValidatorConfiguration.DefaultErrorCodeResolver(IPropertyValidator)
    |  referenced by
  RuleBase<T, TProperty, TValue>.CreateValidationError(...)
    |  referenced by
  AbstractValidator<T>.ValidateAsync(T, CancellationToken)
```

Every path is a real one. FluentValidation is a polymorphic rule engine: every validator
implements `IPropertyValidator`, and one shared engine calls it. So *any* change to *any*
validator - including a private helper three calls deep, which also selects all 2,460 - reaches
the shared engine through the interface, and the shared engine is what every test runs.

That is the limit of type-insensitive static analysis, not a defect and not a tuning problem. The
tool cannot know that a test using `NotNull()` never dispatches to `EnumValidator`; only knowing
which concrete validators each test actually constructs would tell it, and that needs type-flow
analysis or the dynamic coverage refinement the design leaves room for.

The practical reading: **`tia` pays off in inverse proportion to how much of a codebase sits
behind shared polymorphic infrastructure.** On a repository whose tests exercise loosely coupled
units it is a large win; on one shaped like FluentValidation's core, changes to that core select
everything, and only changes outside it are selected down.

### What the investigation did fix

Three real defects, each found by running against a real repository rather than by reading code:

- The tool reported **its own `.tia` cache** as a change, so a second run diffed the file the first
  run wrote.
- NUL-separated git output kept the captured trailing newline, producing a phantom entry whose
  path was a bare newline.
- The reflection scan was purely syntactic, so FluentValidation's own `expression.GetMember()`
  extension counted as `Type.GetMember`.

And two widenings that were stronger than the risk they modelled:

- **Reflection** widened the whole project. It now makes the *reflecting member* unconditionally
  impacted - the same safety statement, scoped correctly.
- **Source generators** widened the whole project. `tia` now re-runs the generators over both
  revisions and seeds only the generated documents whose text actually differs. On the leaf change
  above it reports *"re-running them over both revisions shows no generated document changed"* -
  where before it seeded 33 symbols.

And one traversal defect, found by reading the `explain` output above rather than by measuring:
the interface and override edges are needed in both directions, but they were allowed to
**compose**, so a change to one implementation reached its siblings through the member they share.
The traversal now marks a node reached by an upward edge and refuses to leave it by a downward
one. Verified directly rather than left to random sampling: mutating `GermanGreeter.Greet` in the
fixture solution fails exactly one test, that test is in the selection, and the selection is 2 of
9 rather than all of them.

It changed nothing on FluentValidation - the path there goes up to the shared engine, not down to
siblings - so it is recorded here as a correctness fix with no measured benefit on this
repository, which is what it is.

The generated-output comparison moved one real commit from 2,460 selected to **261** - `bae891652`,
a null-check fix in `TestHelper/TestValidationResult.cs`, which does not sit behind the rule
engine. That took the mean from 58.5 % to 51.0 %. The rest did not move the aggregate at all,
because the aggregate was never driven by those rules. It is worth knowing which of your
assumptions a measurement kills.

### NodaTime

A second repository, chosen because it is much less polymorphic than FluentValidation - mostly
concrete structs and calendar arithmetic. 3,730 tests across four test projects, two of them
multi-targeted. Cold graph: 610 types / 7,207 members / 43,770 edges in **24.7 s**.

A one-line change to a leaf calendar (`BadiYearMonthDayCalculator`):

| | Tests |
|---|---|
| impacted by the graph | **2,232 of 3,730 (59.8 %)** |
| actually run | **2,275 of 3,730 (61.0 %)** |
| actually run, before the dialect fixes below | 3,679 of 3,730 (98.6 %) |

So the "inverse proportion to polymorphism" reading from FluentValidation is only **partly**
supported. NodaTime is far less abstract and still impacts 60 % of its suite for a calendar
change - because calendars underpin most of its date types. The better generalisation is duller:
selection tracks how *central* the changed code is, and a library's core is central by
construction. Both repositories are libraries whose tests exercise that core directly.

### Replaying NodaTime's history

20 first-parent commits, the whole of the repository's recent history that is analysable here.

| Change | Commits | Selected |
|---|---:|---|
| CI workflows, shell scripts, docs | 9 | **0 %** |
| build inputs and project files | 4 | full run, by design |
| ordinary library changes | 4 | **7-11 %** |
| TZDB database updates | 3 | **93.5 %** |

**Mean selection 35.5 % · full-run rate 20 % · commits with a widening 30 %**

The mean is dominated by three commits and is the less interesting number. What the distribution
says is that NodaTime's recent history is mostly *peripheral* - dependency bumps, CI, release
scripting - and selection reads that correctly, taking nine commits to zero. The four genuine
library changes select 7-11 %. The three that select almost everything are TZDB updates, and they
should: replacing the embedded time zone database changes what most of the suite observes.

Against FluentValidation's mean of 51 %, this is the centrality reading holding up rather than
being contradicted. FluentValidation's replayed commits were mostly *in* the rule engine, which is
the most central code it has; NodaTime's were mostly nowhere near its core.

### The replay found a miss the mutation gate cannot

Measured before the content-file fix below, the same 20 commits gave a mean of **21.4 %** - and
that number was measuring a defect. The three TZDB commits selected **zero tests**, because
`Tzdb.nzd` was not on the allow-list of data extensions that widen their project. NodaTime reads
that file at runtime and asserts its version in tests.

This is worth separating from the mutation gate's findings because no sample count would have
produced it: a mutation only ever edits C#. Replay reads real commits, and real commits change data
files. The two harnesses answer different questions and neither substitutes for the other.

### Two dialect defects, found only by running the runner

The 98.6 % above was not the engine. The graph selected 59.8 %; the filter was then thrown away.

- **Filters were abandoned for length.** 1,037 selected tests produced an 86,000-character VSTest
  filter, over the 24,000 limit, so the project ran whole. Two fixes: a class whose tests are
  *all* selected now collapses to a single clause (86,000 → 33,000 characters), and the length
  limit is platform-aware. The 32,767-character cap is a Windows constraint; applying it on Linux
  and macOS, where the limit is measured in megabytes, discarded filters that would have worked.
- **xUnit v3 filter kinds are AND-ed, not OR-ed.** Collapsing tempted the dialect into emitting
  `--filter-class A --filter-method B.C`, which reads as "tests in A that are also B.C" and
  selects **nothing at all**. Same-kind filters do OR - two class filters ran 3 tests, two method
  filters ran 2 - but mixing them ran 0. The argument list looks entirely reasonable either way,
  and every unit test asserting it passed. It took executing the runner to find.

The second one is a miss-class defect: a filter that runs no tests reports a green build. The
integration suite now runs the emitted filter and compares the tests that actually executed
against the selection, which is the only assertion that could have caught it.

### Bounding the interface hop

The finding above says a change to any implementation reaches every consumer of the interface it
satisfies. That is not quite forced. A consumer is only affected if it can also get hold of the
changed type - through a constructor, a factory, a DI registration, a type argument, or any other
static mention. So what an upward hop reaches is now intersected with what can reach the
implementing type.

The exception is the important one: when **nothing** in the solution mentions the type, whatever
creates it is invisible - a container registering by convention, a plugin from another assembly, a
deserialiser - and a bound drawn from an empty set would exclude every caller. That case falls
back to the unqualified reading, and it is what keeps dependency injection working.

| Change | Before | After |
|---|---|---|
| FluentValidation, private leaf method | 80.5 % impacted | **74.0 %** |
| NodaTime, leaf calendar | 59.8 % impacted / 61.0 % run | **55.3 % / 55.3 %** |

Modest, and it costs roughly twice the analysis time on FluentValidation (8 s to 16 s) because
each hop needs two extra traversals. Against a suite measured in minutes that is still cheap, but
it is not free and the gain is not dramatic.

It is, however, demonstrably *right* rather than merely smaller: the fixture solution has two
greeters behind one interface and a service that takes the interface. The service's test injects
the English one, so a change to the German one cannot affect it - and is no longer selected.

A stricter variant was tried and rejected. Making **every** upward hop earn its own bound, rather
than only the first, is more precise in principle; on FluentValidation it selected nothing at all,
because the chain of hops through a layered validator hierarchy died before reaching any test.
Precision that turns into a miss is not precision. The first hop is bounded; the rest of the walk
is not.

### Type flow: the second attempt, and the second negative

The bound above counts every *mention* of the implementing type. Dispatch needs more than a
mention: a `typeof(German)`, a static call on `German`, a name in a base list all reach the type
and none of them can dispatch to it. So the bound was sharpened from "what can reach the concrete
type" to "what can obtain an instance of it" - object creations, types handed to a factory as type
arguments, DI registrations, and whatever a member can be handed by the members it calls,
propagated to a fixpoint across the merged graph. It ships behind `--type-flow`, default off.

This deliberately differs from the reverted attempt above. That one qualified the *walk*, so
chains died; this one leaves the walk alone and only narrows the bound, intersected with the
existing one so it can never widen. Soundness is the default throughout: obtaining a subclass
obtains its base, and reflection, `dynamic`, an instance arriving from outside the graph, or a
member that reaches more types than are worth tracking all answer "any type" and permit the hop.

The exit criteria were fixed before the measurement: zero misses on every gate, **and** a material
fall in FluentValidation's ~100 %-on-core case. The second is not met. The first is met on every
gate that could actually be run, which is not the same as every gate that was asked for - see
[what the two external gates could not answer](#what-the-two-external-gates-could-not-answer)
below.

| Gate | Samples | Misses |
|---|---:|---:|
| `tests/Tia.Fixtures`, `--type-flow` / without | 47 / 44 usable | **0 / 0** |
| `tests/Tia.Fixtures.Tunit`, `--type-flow` / without | 60 / 60 | **0 / 0** |
| `tests/Tia.Fixtures.Web`, `--type-flow` / without | 60 / 60 | **0 / 0** |
| tia itself, `--type-flow` / without | 14 / 14 usable | **0 / 0** |
| NodaTime, less `TzdbCompiler.Test`, `--type-flow` / without | 15 / 15 usable | **0 / 0** |
| NodaTime, whole solution | 19 usable | **void** - red baseline, below |
| FluentValidation | none | **refused** - no TRX reporter, below |

| Change | Without | With `--type-flow` |
|---|---|---|
| FluentValidation, `CreditCardValidator.Name` | 76.0 % impacted | **76.0 %** - 0 hops narrowed |
| NodaTime, leaf calendar | 84.7 % impacted | **84.7 %** - 4 hops narrowed |

It fires and it buys nothing. Two separate reasons, and the second is the interesting one:

- **On FluentValidation it has nothing to remove.** The analysis bounded 59 implementing types and
  narrowed **zero** hops, because on this codebase the two questions have the same answer: the only
  static mentions of a validator class *are* its constructions. There is no `typeof(EnumValidator)`
  sitting between the engine and a test to discard.
- **On NodaTime it removes the wrong four.** Four hops did narrow, and selection did not move a
  single test, because every node the bound dropped was reachable by another path anyway.

Underneath both is the shape the first attempt already ran into, arriving from the other side. The
bound is drawn per hop, on the containing type of the implementation that changed. A change to
`CreditCardValidator.Name` takes *two* upward hops - to `PropertyValidator<T,TProperty>.Name`, then
to `IPropertyValidator.Name` - and the second is bounded on the abstract base every validator
derives from. Everything that obtains any validator obtains a `PropertyValidator`, by the same base
closure soundness requires, so that bound permits everyone. Sharpening what "obtain" means cannot
help when the type being bounded is the base of the whole hierarchy.

Which leaves the honest reading: **the remaining imprecision on a polymorphic core is not a
type-awareness problem.** Two attempts have now been aimed at it, one unsound and one inert, and
both failed for reasons that are about the shape of the walk rather than the sharpness of the
bound. What would actually settle which concrete validator a test dispatches to is a record of what
ran, which is dynamic coverage - spiked and declined for other reasons in
[`coverage.md`](coverage.md), and the place to look first if this is picked up again.

The flag stays, off by default, because it costs nothing when off and the measurement is
reproducible: `--type-flow` on `analyze`, `run`, `verify` and `tests/Tia.Validation`. What it costs
when on is a second semantic pass over every tree - FluentValidation's analysis went from 6.7 s to
14.7 s - for a selection that did not change on either repository.

#### What the two external gates could not answer

Both external gates were attempted and neither produced a usable verdict, which is worth recording
because "the gate did not run" and "the gate passed" are the two things a correctness claim must
never confuse.

**FluentValidation cannot be mutation-gated at all.** The preflight refused before the first
sample: no test project references a TRX reporter, so no sample's outcome could be read. That check
is doing exactly what it was built for - the alternative is spending twenty full suite runs to
report inconclusive twenty times - but it means this repository has a selection measurement and no
correctness measurement.

**NodaTime's first run reported 20 misses, and every one of them was the same two tests.** They are
`NameIdMappingSupportTest.AllDetectedNamesAreMapped{,Correctly}`, and they appeared as misses for
mutations in unrelated projects, including benchmark code that no test observes. On an unmutated
tree both fail:

```
System.ArgumentException : An item with the same key has already been added.
Key: Zentralaustralische Normalzeit
```

The machine is de-DE, two Windows time zones share a German display name, and the test's static
constructor puts them in a dictionary. A test that fails without any mutation fails in every
sample, so it is reported as missed in every sample whatever the selection was. That is the red
baseline the CleanArchitecture note above describes, arriving here from the locale rather than from
Docker, and it makes the run void rather than failed. `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT`
does not help: on Windows the display names come from the OS, not from ICU.

Re-run against a solution that leaves that one test project out, both configurations return **15
usable samples and 0 misses** - identical, which is the only form the answer can take here. It says
the flag cost no test on the samples drawn. It does not say the excluded project would have agreed,
and 15 samples is a weaker statement than the 25 the whole solution was meant to carry.

Two things this leaves for whoever picks it up. A repository whose baseline is red cannot be gated
at all today, and the preflight added for TRX already runs the baseline suite once - so subtracting
tests that fail *before* any mutation is a change the harness has the data for, and it would open
up both this case and the Docker-bound one above. It has to report loudly what it excluded, because
a gate that quietly ignores failures is the one failure mode worse than a gate that refuses.

## Does it actually save time?

The question every other number in this file is a proxy for, and it went unmeasured for far too
long. Selection ratio is not a saving: analysis costs wall-clock too.

With analysis cost `A`, full suite time `T` and selected fraction `f`, a selective run takes
`A + fT`. That beats `T` only when **`T > A / (1 - f)`**, which `tia` now prints on every run as
*"worth it if the full suite takes more than …"*.

### NodaTime

Full suite, 22,704 test cases, already built: **28.5 s**. Analysis, same machine, one binary,
21 projects:

| | Seconds | Projects rebuilt | Impacted |
|---|---:|---:|---|
| cold | 30.9 | 21 / 21 | - |
| warm, nothing changed | **6.7** | 0 / 21 | 0 |
| warm, a comment in `NodaTime.Testing` | **9.4** | 1 / 21 | 42 / 3,730 |
| warm, a private helper in `NodaTime.Testing` | **9.5** | 1 / 21 | 40 / 3,730 |
| warm, a method body in the core library | **20.5** | 2 / 21 | 14 / 3,730 |

The last row is the expensive case and it is expensive for a real reason: a change in the core
library invalidates the core library, and rebuilding its fragment means binding it. Both of its
target frameworks are rebuilt, which is why it says 2.

#### Where the expensive row actually goes

That explanation was right about the cause and said nothing about the distribution, so the row was
attacked with instrumentation before it was attacked with code. Two things had to be fixed before
any number here meant anything. `GraphSeconds` wrapped the entire rebuild - parse, fingerprint,
bind, discover, scan, cache write - as a single figure. And `PhaseClock` *accumulates*, so every
phase inside the parallel rebuild was a **sum across threads reported as a duration**; on an
eight-core machine that can read eight times the wall-clock phase containing it. `PhaseTimings`
now says which is which in the field name: `…Seconds` is wall-clock, `…CpuSeconds` is a
cross-thread sum.

With that in place, the expensive row breaks down as:

| Phase | Seconds | |
|---|---:|---|
| `graphWalkCpuSeconds` | 17.78 | binding every syntax tree - effectively the whole cost |
| `solutionOpenSeconds` | 6.08 | MSBuild evaluating 21 projects; a floor under *every* run |
| `surfaceHashCpuSeconds` | 3.73 | and **sequential**, while everything around it was parallel |
| `compilationCpuSeconds` | 1.78 | parsing, far cheaper than the code comments assumed |
| `compileCheckCpuSeconds` | 0.17 | |
| `reflectionScanCpuSeconds` | 0.13 | |

Splitting the walk further put 8.0 s per target framework in `WalkTree` itself, against 0.16 s for
enumerating declared symbols and 0.05 s for the semantic edge pass. So the cost is
`SemanticModel.GetSymbolInfo`, once per interesting node: Roslyn binding method bodies, which is
the irreducible work of knowing what a body references.

**The obvious optimisation was refuted by this table.** The reflection scan creates a *second*
`SemanticModel` over every tree that the graph walk has already bound, which looks like a doubled
bind and was the leading candidate before measuring. It is 0.13 s - 0.6 % of the run - because
`ReflectionScanner` is mostly syntactic and asks the model very few questions, so it never pays for
a full bind. Merging it would have bought nothing.

Two changes did follow from the table. The surface-hash loop now runs in parallel like the rebuild
it precedes, since re-hashing a surface means producing a compilation and doing that in sequence
left every core but one idle. And the reference walk threads its edge kind down the walk stack
beside the type and member keys, rather than recomputing it per node with two `FirstAncestorOrSelf`
calls that each climb to the root.

Measured A/B on this row - three runs before, six after, with `solutionOpenSeconds` left as an
untouched control:

| | Before | After | |
|---|---:|---:|---|
| elapsed | 19.96 | 18.61 | **−6.8 %** |
| fingerprint (wall) | 3.96 `[3.79–4.08]` | 2.79 `[2.61–3.08]` | **−29.6 %**, ranges disjoint |
| graph walk (CPU) | 17.17 `[17.08–17.26]` | 16.37 `[15.51–17.67]` | −4.7 %, ranges overlap |
| solution open (control) | 5.69 | 5.86 | +3.0 % |

The surface-hash win is established. The walk win is not: its ranges overlap, and it is reported
here as suggestive only because five of six runs fell below the before-minimum. It is kept because
it is strictly less work and the graph is provably unchanged - 50,329 edges in all nine runs.

An earlier reading of this pair appeared to show −12 %, from a comparison whose "before" binary had
failed to compile and so was the "after" binary. The numbers above come from a rebuild that was
checked for compilation errors between arms.

#### What is left, and the only lever that would move it

After the change the row is ~18.6 s: roughly 8 s per target framework of `GetSymbolInfo`, 5.9 s of
MSBuild evaluation that no cache can avoid, and ~2.8 s of surface hashing. Nothing there is waste.
The only remaining lever is **not re-binding files that did not change** - a per-file edge cache
keyed on file content, so a one-file edit re-binds one file instead of five hundred.

It is not attempted here, and the reason is soundness rather than effort. A file's edges depend on
what the rest of the project declares, so reuse needs a hash of the project's *internal* declaration
surface - and the existing `SurfaceHash` cannot serve, because it deliberately excludes private
members so that adding a private helper does not invalidate dependents. A per-file cache keyed on it
would silently keep stale edges the first time someone added a private overload. That is precisely
the silent-miss shape this cache has produced before, so it needs its own hash, its own cache
format, and its own gate evidence before it goes anywhere near the engine.

Re-measured after `SolutionAnalyzer` was split into four phase types, to confirm the refactor cost
nothing: **6.4 s** warm with nothing changed (was 6.7), **9.8 s** for a private helper in
`NodaTime.Testing` (was 9.5), with the same 21 projects and the same rebuild counts. Both within
run-to-run noise on a machine that had just finished a mutation gate. The selection for that change
is 306 of 3,730 - the 8.2 % the table further down reports, not the 40 in the row above, which
predates the reflection fix as noted at the end of this file.

### Where the floor went

The 11-second floor this file used to report was made of three things, and measuring the phases
rather than the total is what separated them.

**The compile check, 1.3 s.** Every project's declarations were bound on every run to check the
solution still compiled - including projects whose cached fragment was about to be reused
unchanged. It is now per fragment and stored with it: same inputs, same verdict.

**Parsing, 4.4 s.** Producing a Roslyn compilation parses every document, and the loader produced
one for every project up front. So the cache saved the graph build and paid the parse anyway.
Compilations are now materialised on demand, and the reuse decision is made from file content
before any of them exist - a project whose fragment still stands is never parsed at all.

**The reflection scan, 4.4 s.** This one was invisible until the phase timings arrived, because it
was inside the selection phase, and selection has no business taking four seconds. The scan
indexed *every* syntax tree in the solution to build a path lookup, which forced every compilation
- and then read a handful of entries out of it. It now compiles only the projects that own a
candidate file, which on a run with nothing to scan is none of them.

What is left is MSBuild: **5.6 s of the 6.7 s floor is `OpenSolutionAsync`**, evaluating 21
project files. Nothing in `tia` can make that cheaper; removing it would mean reconstructing
compilations without MSBuild and reimplementing its semantics, which trades a known 5.6 seconds
for an unknown class of divergence. Not worth it.

### The web fixture, and what it says the route gap actually is

`tests/Tia.Fixtures.Web` exists to answer the third objection below — that the route-template edge
cannot be measured here. It is a minimal ASP.NET Core app plus xUnit v3 functional tests over
`WebApplicationFactory`, in process, no container. Its first gate run, before any engine change:

```
15 usable sample(s), 0 skipped, 4 miss(es)
FAIL - a failing test was not selected.
```

All four are the same test, `Lists_projects_from_the_controller` — but that is an artefact of which
sites the mutation engine happened to pick, not a statement about which endpoints are reachable. A
direct measurement is clearer: with a one-line change to `Contributors.List`, a minimal-API handler,
selection was **0 of 4 tests**. The endpoint half of this fixture was entirely unreachable.

It is worth being precise about why, because the obvious explanation is wrong.
`app.MapGet("/contributors", Contributors.List)` *is* a real static reference to the handler, and the
test *does* name `WebApplicationFactory<Program>`. The chain still does not join: the reference to
the handler lives in the synthesized `<Main>$` of a top-level-statements file, nothing calls
`<Main>$`, and walking upward from a member to its containing type is not an edge the graph has. So
the walk from `Contributors.List` reaches `<Main>$` and stops one step short of the `Program` the
test names.

**After the route-template edge**, the same change selects **1 of 4** — exactly
`Lists_contributors` — and a change to `ProjectsController.List` selects exactly
`Lists_projects_from_the_controller`. Not a widening in either case: the right test and nothing else.

The guard is what keeps it that way. The edge is followed only when nothing in the solution names
the endpoint's type, the same condition the request-type edge uses, so an endpoint that already has
ordinary edges does not acquire a second path to everything mentioning a similar string.

### Per-document cache granularity: measured, and not shipped

The cache's unit is a project, so a one-line edit rebuilds that project's whole fragment. Splitting
it per document is the obvious next move, and it was measured before it was built. It does not pay.

First, what such a split could possibly save. `GraphBuilder` forces `context.Compilation` before any
measured phase, so Roslyn parses every document in the project no matter how few of them changed —
parse cost does not go away. What per-document reuse removes is the semantic walk of unchanged trees
and their reflection scan. On this repository, a body-only edit to one project (two fragments, one
per target framework), warm:

| Phase | CPU seconds | Removable by per-document reuse? |
|---|---|---|
| `CompilationCpuSeconds` | 1.856 | No — parsing is forced regardless |
| `GraphWalkCpuSeconds` | 2.646 | Yes |
| `ReflectionScanCpuSeconds` | 0.063 | Yes |
| `SurfaceHashCpuSeconds` | 3.033 | No — **and the split needs a second one** |

Wall-clock for that run was 7.29 s against a 4.43 s warm floor with nothing changed.

So the ceiling on the saving is **2.71 s of CPU**. Now the cost. Reusing one document's cached edges
is only sound while the *other* documents' declarations have not moved: a new private overload
changes what a call in an untouched file resolves to, and partial classes span files. The reuse key
therefore has to be a declaration surface for the project that **includes private members** — which
is strictly more symbols than the public surface that already costs **3.03 s** to hash.

The split pays for itself with a key that costs more than the work it avoids. It is not close, and
it is not a matter of tuning: the cheaper the walk gets, the worse the trade looks.

Two things worth recording alongside that, because they are what would change the answer:

- The idea is not inherently worthless. If the invalidation key were free, removing the walk would
  take the run from 7.29 s to roughly 5.4 s — about 26 %. It is the *key*, not the reuse, that
  fails to pay.
- Dropping the key is not an option. Rebuilding the symbol-level half unconditionally and reusing
  only the per-document syntactic edges sounds like it avoids the problem, but those edges are
  produced by binding each tree against the compilation, so they move when another document's
  declarations move. Reusing them without the key is a stale fragment, and a stale fragment is a
  wrong answer that merges without complaint.

This is the same shape as the whole-dependency-fingerprint experiment recorded in
`ProjectFingerprint`: correct, and not worth having. The cache stays per project.

### Evaluating properties, and why it is not done for every project

Runner detection reads a handful of MSBuild properties. It used to read them out of the project
XML, which honours no conditions and expands no expressions, so it disagreed with the build in both
directions: a property inside a `Condition` that is false was reported as set, and
`$([System.String]::Copy('true'))` was reported as that string rather than as `true`. The clearest
symptom was `UsingMicrosoftNETSdkTest` — `FrameworkDetector` has always tested it, but no project
file writes it (Microsoft.NET.Test.Sdk's props do), so the dictionary could never contain it and the
branch could never fire. It fires now for any restored project referencing that package; an
unrestored project imports no package props and so still cannot show it.

Evaluating each project through MSBuild fixes all three. It is also a second full evaluation of
work `OpenSolutionAsync` already did once, so it was measured before it was kept, on this
repository (7 projects, warm):

| | `PropertyEvaluationSeconds` | `WorkspaceLoadSeconds` | Total elapsed |
|---|---|---|---|
| Every project | 1.79 s | 5.10 s | 8.56 s |
| Test projects only | **0.30 s** | **3.04 s** | **3.61 s** |

1.79 s to answer a question about two projects is not a trade worth making, so evaluation is spent
only where it changes an answer. Properties decide two things: which runner a test project uses,
and — when the referenced-assembly signal recognises no framework at all — whether the project is a
test project. The first is exactly the set that gets evaluated. The second is `IsTestProject`, which
is a literal in the project file wherever anyone sets it, and the literal read already sees it.

A project evaluation cannot open — an uninstalled workload, an SDK that will not resolve — falls
back to the literal read rather than to nothing, because such a project was probed by XML before
and must not end up worse than it was. `--json` reports `propertySource` per project, so a
detection surprise can be traced to which of the two answered.

### Does it pay off now?

For the leaf change: `A` = 9.4 s, `f` = 1.1 %, so a selective run costs 9.7 s against the full
suite's 28.5 s. It pays. For the core-library change: `A` = 20.5 s, `f` = 0.4 %, so 20.6 s against
28.5 s - it still pays, but the margin is thin enough that a slower disk would erase it.

That is a real change from what this section said before, and the honest reading has moved with
it: **`tia` pays off on suites measured in minutes; on suites measured in seconds it is now
roughly break-even rather than a loss.** The break-even arithmetic is printed on every run
precisely so nobody has to take this paragraph's word for it on their own repository.

### The cache was useless until this was measured

Invalidation folded whole dependency fingerprints in, so a project was rebuilt whenever anything
it referenced changed. Correct, and worthless: a core library changes on most commits, so a
one-line edit to NodaTime rebuilt **18 of its 21 projects** and the cache saved nothing in exactly
the case it exists for.

A project's fragment is a function of its own source plus its dependencies' *declarations* - it is
produced by binding syntax against them - and nothing in it depends on a dependency's method
bodies. Hashing the declaration surface separately keeps the guarantee (a rename, a new base type
or a changed signature all move it) while a body-only change now invalidates **2 of 21** instead
of 18. Warm analysis went from 27.4 s to 11.7 s at the time of that change; the current figures
are in the table above, which is smaller again for reasons the sections below explain.

### The guarantee above was not actually held

That parenthesis - "a changed signature moves it" - was written from the intent of the code rather
than from its behaviour, and it was false. The surface was rendered with Roslyn's
`FullyQualifiedFormat`, which prints a method as its name and **nothing else**: no parameters, no
return type. `Value(int)` and `Value(long)` hashed identically, so changing a parameter type left
every dependent's cached fragment looking valid, and the stale fragment merged into the graph
without a word. That is the worst failure this cache can have - not a slow run, a wrong answer,
reported green.

Constant values were missing for the same reason, and they matter more than they look: the
compiler inlines a `public const` into whoever reads it, so its *value* is part of the surface.
So were accessibility and modifiers, and the graph carries virtual-to-override edges keyed on the
latter.

Found by writing the tests that should have existed when the guarantee was first claimed, which is
the only reason it is in this file as a fixed defect rather than a live one.
`tests/Tia.Integration.Tests/SurfaceHashTests.cs` now pins both directions: what must move the
hash, and what must not.

### Two more things the reuse key ignored

`AdditionalFiles` and `.editorconfig` are generator inputs, and analyzer references carry the
generator version. A fragment keyed only on compile items survived a change to any of them.

### Excluding private members

The other direction. A private member cannot be named from another assembly, so adding, removing
or re-signing one cannot change how a dependent binds - but it moved the surface hash, and adding
a private helper is one of the most ordinary edits there is. On NodaTime that one exclusion took a
private helper added to `NodaTime.Testing` from invalidating **4 of 21** projects to **1**, and
warm analysis from 27.0 s to 13.1 s - and then to 9.5 s once the reflection scan below stopped
compiling the solution behind it.

The exception that is easy to get wrong: Roslyn reports an explicit interface implementation as
private, and by the language rules it is. It is still reachable through the interface, and the
graph has an edge saying so, so it stays in the surface.

## What the gate found when it was finally pointed at a real repository

Every number above was produced by a gate that had only ever run against two synthetic fixture
solutions of 12 and 4 tests. Pointing it at NodaTime - 3,730 tests, 21 projects, NUnit, VSTest,
multi-targeted, BOM'd sources - took **five rounds** to reach zero misses, and every round found
something real.

| Round | Misses | What it was |
|---:|---:|---|
| 1 | 362 | the harness could not read NUnit's result names |
| 2 | 484 | (names now correct, so more of the real misses were visible) |
| 3 | 38 | reflection was seeded only where the traversal reached |
| 4 | 20 | no edge from an interpolated value to the `ToString` it calls |
| 5 | **0** | no edge from a static member to its type initializer |

Two of those rounds were defects in the gate itself. Three were defects in the engine, and none of
them were reachable by any unit test, because each is a *runtime* path that the source never spells
out. That is the argument for a mutation gate over more unit tests, and it needed a repository with
real idioms in it to make the argument.

**FluentValidation then passed on its first run** - 20 samples, 16 usable, zero misses - which is
the result that says the three fixes were classes rather than NodaTime quirks. It is a structurally
different repository: a polymorphic rule engine with a source generator and xUnit on the testing
platform, where NodaTime is concrete structs and calendar arithmetic on NUnit and VSTest. A second
repository that needed its own round of fixes would have suggested the first round was overfitted.
This one did not.

### The gate could not read half its own results

xUnit writes `testName="Ns.Cls.Method"`. NUnit writes `testName="Method"` and records the class
only in the test definition. The harness compares those against the fully qualified names the
selection produces, so on any NUnit repository **every failing test looked unselected**.

It reported 362 misses, all of them its own. The listed tests were unrelated to the mutation -
`XmlSerialization_SwapAttributeOrder` for a change to `InstantPatternParser` - which is the tell
worth remembering, because the instinct on seeing a number that size is to start weakening the
engine to satisfy it. That would have been exactly wrong.

Names now come from the definition. Data-case normalisation changed with it: cutting at the first
bracket is right when the arguments are at the end, but a parameterised NUnit *fixture* puts its
arguments in the class name, so `Ns.Cls(3).Method(4)` became `Ns.Cls` and the method was thrown
away.

### "Always impacted" did not mean always

The safety model says a reflecting member is always impacted, because it can reach things no static
edge records. It was implemented as *"if the traversal reaches it, or its file changed"*. Those are
not the same statement, and the difference is the entire point: **a reflecting member is dangerous
precisely because nothing reaches it.**

NodaTime's `TestHelper.AssertXmlRoundtrip` hands a value to `XmlSerializer`. The serializer calls
the type's `IXmlSerializable.ReadXml`. `ReadXml` parses with `InstantPattern`. So breaking the
pattern parser failed every XML round-trip test, and no static path led from the change to the
helper, so the helper was never scanned and the tests were never selected.

Serializers are reflection wearing a different name and are now recognised as such - matched by
declaring type rather than receiver name, since a serializer lives in a local and
`serializer.Serialize(...)` says nothing on its face. Every reflecting member in the solution is a
seed, reached or not, but only when something else changed: with an empty change set there is
nothing to depend on, and a diff that touches nothing must still select nothing.

Scanning the whole solution on every run would have put the cost of compiling all of it back into a
run that otherwise compiles nothing, so findings are recorded per project when its fragment is
built and cached with it. Warm analysis went *down*, 9.5 s to 8.6 s, because the traversal fixpoint
went away with the old design.

### Two edges the compiler makes and the source does not

**Interpolation.** Nothing in `$"{instant}"` names `Instant.ToString`: the interpolation binds to
the handler's `AppendFormatted`, and the call to the value's own formatting happens inside it. A
method whose body is an interpolated string had no edge to any type it formats.
`ZoneInterval.ToString` formats an `Instant`, `Instant` formats through `InstantPattern`, and three
`ToString` tests failed with no path to draw. The expression's type is known statically even though
the call is not, so the fix is an exact edge rather than a widening.

**Type initializers.** `XmlSchemaTest` reads `XmlSchemaDefinition.NodaTimeXmlSchema`, a static
property whose initializer builds the schema from the TZDB zone list - so corrupting the TZDB
reader failed the test through a call nothing in the source makes. Reading a static member or
constructing an instance now references the type's static constructor. Naming a type does not:
`typeof(Foo)` does not run the initializer, and pretending it does would make every mention of a
type with statics as expensive as using it.

### What the guarantee costs

Soundness is not free and the price should be stated rather than buried. Selection on NodaTime,
before and after this round:

| Change | Before | After |
|---|---:|---:|
| a private helper in a leaf project | 1.1 % | **8.2 %** |
| a method body in the core library | 0.4 % | **7.5 %** |
| the TZDB reader | - | **85 %** |

The constant floor of roughly 8 % is the tests reachable from a reflecting or serializing member,
which now always run. Every one of them is reported as a `Reflection` widening, so the cost is
visible rather than mysterious - and 8 % of a suite is still 92 % skipped.

The 85 % is not over-selection. Corrupting the timezone database really does break most of
NodaTime, and a tool that said otherwise would be wrong.

## The gate was corrupting the repository it measured

Worth recording in full, because it is the second time the validation machinery has been the thing
that was wrong, and because of how it would have failed.

The mutation harness edits a real file, analyses, runs the suite, and restores the file. It read
with `File.ReadAllText`, which strips a UTF-8 byte-order mark, and restored with
`File.WriteAllText`, which does not write one back. Every BOM'd file it touched came back a byte
short of what git has.

The mess is not the problem. Each sample diffs the working tree against `HEAD`, so from the second
sample onward every *previously* mutated file was also in the diff. The diff grew with each
sample, the selection grew with it, and a gate whose selection is drifting toward everything
cannot find a miss. It would have printed **PASS** either way. That is the exact failure mode this
harness exists to rule out, in the harness itself.

Caught on NodaTime, whose sources carry BOMs: three files were dirty fifteen minutes into a run
that had reported nothing wrong. The run was discarded rather than published.

Two changes. Reads and writes go through bytes, and the mark goes back on both the mutated write
and the restore - the mutated write too, because otherwise line 1 changes on every sample and the
diff the harness measures is not the diff it injected. And the harness no longer assumes: it
hashes every candidate file before the run and after every sample, and abandons the run if one did
not go back. A content hash rather than a length or a timestamp, because a restore always moves
the timestamp and the commonest mutation of all - swapping `+` for `-` - does not change the
length.

The replay harness never had this bug, because it restores with `git checkout` and refuses to
start on a dirty tree. The mutation harness did neither, and that asymmetry is what let this
survive.

Both halves are closed now. The drift check catches a change the harness made and failed to undo;
refusing to start on a dirty tree catches one that was already there — the same failure arriving
from the other direction, since an uncommitted edit sits in *every* sample's diff, the selection
grows to cover it, and the gate stops being able to find a miss while printing PASS throughout.
Untracked files count for the mutation harness where they do not for replay: replay excludes them
because a checkout leaves them alone, whereas `DiffResolver` deliberately adds them to the diff so
that a newly written test is not invisible.

## Mediator dispatch, and the boundary behind it

Both repositories gated so far are libraries. The prediction was that an *application* would break
differently - a container, an ORM or a message bus is runtime dispatch the graph cannot see,
exactly as `XmlSerializer` was - so the next repository to try was an application.

`ardalis/CleanArchitecture` could not be *gated* here: its integration and Aspire tests need
Docker, so the baseline is red and every pre-existing failure would read as a miss. Running that
gate would have produced a number worth nothing. The engine can still be probed without ground
truth, and it fails immediately: change `ListContributorsHandler.Handle` and `tia` selects **0 of
18 tests**, including the functional test that lists contributors through the endpoint that
dispatches to it.

### The request type is the missing link

Nothing names the handler. The interface edges that make ordinary dependency injection work are no
help either, because the caller invokes `IMediator.Send(new ListContributorsQuery(...))` rather
than `IQueryHandler.Handle` - the mediator's own indirection is the break, and the registration is
assembly scanning.

But the handler *is* statically connected to its request: it implements
`IQueryHandler<ListContributorsQuery, ...>` and the caller constructs a `ListContributorsQuery`. So
a handler's members now carry an edge to the request type, and the walk continues through
everything that builds one. That is how request dispatch works generally rather than a property of
any one library.

Two things keep it from being a blunt instrument:

- The type argument has to appear as a **parameter** of an interface member.
  `IRequestHandler<TRequest, TResponse>.Handle(TRequest, ...)` qualifies; `IEnumerable<T>` does
  not. "Selected by a T" is not "has some T in it".
- The edge is followed **only when nothing in the solution names the concrete type**. A handler
  discovered by scanning has no static mention anywhere; a type that *is* mentioned already has
  ordinary edges, and following its request type as well would connect every
  `AbstractValidator<Person>` to everything touching a `Person`. Same reasoning as the bound on the
  interface hop.

Cost, measured:

| | Before | After |
|---|---:|---:|
| NodaTime, private helper in a leaf project | 8.2 % | **8.2 %** |
| FluentValidation, `CreditCardValidator.Name` | 74.0 % | **75.9 %** |

Nothing on NodaTime, which has no dispatch of this shape. Just under two points on
FluentValidation, and those are probably misses being closed rather than precision being lost - it
has plenty of validators nothing names, which is exactly the case the guard admits.

It is also **additive**: the change only adds edges, and adding edges can increase a selection but
never shrink one, so it cannot introduce a miss by construction. The only risk it carried was
over-selection, which is what the table measures.

### What it does not fix, and what that says

Being straight about it: **this edge does not fix the CleanArchitecture case**, and cannot. Change
the *endpoint itself* rather than the handler and the functional test is still not selected, so the
chain dies one hop later regardless. The test talks to the app over HTTP - it names a route string
and a response DTO, never the endpoint class - and the framework maps route to endpoint by
registration. Two members that both reference a DTO are not connected to each other, and should
not be.

So the demonstrated benefit is on a constructed case in the unit tests, not on a real repository.
That is a weaker claim than the rest of this file makes and is stated as such. It ships because the
reasoning is sound, the guard is tight, the measured cost is nearly nothing, and it cannot cause a
miss - not because a benchmark showed it winning.

**HTTP route dispatch is now the binding gap for applications**, and it is a harder one: the link
between a route string in a test and the endpoint that serves it is not in the source at all. A
repository whose integration tests resolve `IMediator` from the container and `Send` directly -
a very common shape, and one without an HTTP boundary - is where the request-type edge would
actually be measurable, and finding one is the next piece of work.

### Verdict on the route gap: shipped, and what it cost to get right

This section used to say the edge was closable in principle but not honestly closable here, for
three stated reasons. Two were about the design and one about measurement. All three are now
answered, and the answers are worth keeping because two of them were only half right.

- **"It cannot be measured here."** Answered by building `tests/Tia.Fixtures.Web`, which exhibits
  the gap over `WebApplicationFactory` in process, with no container. That is now a leg of the
  verify matrix like any other.
- **"The literal is often not literal."** Correct, and handled: templates are normalised with
  parameter segments wildcarded, `MapGroup` prefixes combined, and constants resolved through the
  semantic model, so a route held in a shared class reads. A caller's concrete path is matched
  against the template segment by segment rather than compared for equality.
- **"A bare string-literal edge is worse than nothing."** Also correct, and the reason collection
  is *positional*: a template is only ever taken from a `Map*` call's route argument or a routing
  attribute. Everything else is a reference, and a reference joins to a declared template or to
  nothing. A template with no literal segment — `"/"`, `"{id}"`, `"/{controller}/{action}"` — is
  rejected outright, because it would match nearly any path of its length.

The guard is the same one the request-type edge uses: follow the edge only when nothing in the
solution names the endpoint's type. Measured on the web fixture, a one-line change to an endpoint
went from **0 of 4 tests selected to exactly 1** — the test that exercises it, not a widening.

**What the gate found that the reasoning did not.** Two things, both after the edge already
"worked":

1. A handler written as a lambda pinned the route to the lambda, which is not a node in the graph,
   so `MapGet("/contributors/{id}", (int id) => Contributors.ById(id))` still reached nothing. The
   endpoints are what the lambda body calls.
2. **A change to a route template erases its own evidence.** The graph is built from the new
   source, so when the change *is* the template, the endpoint's new route no longer matches the old
   path its callers still name, and the edge that would report it does not exist. No amount of
   care in collecting templates fixes this; it is the same by-value binding as constant inlining
   and it gets the same answer — a diff touching a route declaration widens that project, with a
   stated reason. Scoped to the changed lines, so editing an endpoint body still selects precisely.

The second is worth stating plainly because it qualifies a claim made elsewhere in this file: an
added edge cannot introduce a miss, and that remains true. But a *feature* built on an edge can
still have a case where the edge is absent, and absence was the miss. The gate found it; the
argument did not.

Final gate, all four solutions, zero misses:

| Solution | Usable samples | Misses |
|---|---|---|
| fixtures (xUnit v3 + NUnit) | 15 | 0 |
| fixtures (TUnit) | 20 | 0 |
| fixtures (ASP.NET Core, route dispatch) | 25 | 0 |
| tia itself | 6 | 0 |

Re-run at higher sample counts when the load-diagnostic change below removed full-run triggers -
which makes selections *narrower*, the one direction that can turn a passing suite into a missed
test:

| Solution | Usable samples | Misses |
|---|---|---|
| fixtures (xUnit v3 + NUnit) | 42 / 60 | **0** |
| fixtures (TUnit) | 60 / 60 | **0** |
| fixtures (ASP.NET Core, route dispatch) | 60 / 60 | **0** |
| fixtures, `--type-flow` | 39 / 60 | **0** |
| tia itself | 15 / 15 | **0** |

## The application shape, and what pointing at it cost

Every repository gated up to here was a library. `docs/coverage.md` and the mediator section both
name the shape that was missing: a repository whose tests resolve a service from a container and
dispatch through it, never naming the handler. **MediatR** is that repository - 29 of its test
files build a `ServiceCollection`, resolve `IMediator`, and `Send(new Ping { ... })`, with the
handler registered by assembly scan.

| | Samples | Usable | Misses |
|---|---:|---:|---:|
| **MediatR** (xUnit, 392 tests, container dispatch) | 20 | 9 | **0** |

Its history, 20 first-parent commits:

| Change | Commits | Selected |
|---|---:|---|
| CI workflows, README, release scripting | 13 | **0 %** |
| a doc-comment `cref` fix inside the library | 1 | **0 %** |
| build inputs | 3 | full run, by design |
| library changes behind the mediator | 3 | **100 %**, each with widenings |

**Mean selection 30.0 % · full-run rate 15 % · commits with a widening 15 %**

Every zero was checked rather than trusted, which is what the `Tzdb.nzd` finding taught. Thirteen
touch no `.cs` file at all. The fourteenth, `03f73ae31` "Fixing build error", does touch
`src/MediatR/Pipeline/RequestExceptionActionProcessorBehavior.cs`, and its entire diff is one
`<see cref="..."/>` inside a `///` comment. Zero is the right answer there, and it is the
symbol-level diff earning its keep.

### Three defects, none of them reachable by a unit test

The first run against MediatR reported **0 usable samples out of 20**. Each defect below was found
by the next one being fixed.

**A warning is not a failure.** MediatR's test project multi-targets `net462` and references two
packages that warn they do not support it. `dotnet build` reports zero errors; `MSBuildWorkspace`
raises those warnings through the same `WorkspaceDiagnosticKind.Failure` channel as a real load
error, in the same "failed when processing the file" sentence. Every analysis bailed out to a full
run, so every sample was unusable. The diagnostic is now evidence and the loaded solution is the
verdict: a complaint naming a project that is present is logged, one naming nothing that loaded
still forces a full run. That forgiveness opens a hole - a multi-targeted project arrives as one
loaded project per framework, so it can arrive for one and not another, and the diagnostic saying
so names a path that *did* load - which is closed separately by comparing a test project's declared
framework list against how many arrived.

**A mutation can stop a loop terminating.** The harness ran the suite per sample and waited with no
timeout. Gating tia itself, a sample dropped the statement storing each learned set in the
type-flow fixpoint, so it re-derived the same delta for ever; the run sat for **three hours at 38
seconds of CPU** and produced no verdict. Every established mutation tool bounds a sample for this
reason. The budget is derived rather than configured - four times the baseline preflight, floored
at two minutes and capped at thirty - and a killed suite is its own outcome, never folded into
"skipped", because *"there was nothing here to check"* and *"the harness could not finish
checking"* are opposite statements.

**A project may say it is not a test project.** Referencing a test framework was treated as the
verdict. Polly's `Snippets` project references xunit so its documentation examples compile and sets
`IsTestProject` false because it contains no tests; the mutation preflight refused the whole
repository over it, advising that a TRX reporter be added to a project that should never have been
asked for results. An explicit false now wins - the same property the SDK's targets read to decide
whether to run a project, so honouring it is what makes `tia` and `dotnet test` name the same set.
Only an *evaluated* false counts: the literal read honours no conditions, and that error would drop
a real test project out of the selection.

## Does it save time on more than one repository?

Three repositories, warm and cold, suites timed with `--no-build` against an already-built solution
so that build time is excluded from both sides. The break-even `tia` prints is `A / (1 - f)`.

| Repository | Full suite | Cold | Warm, nothing changed | Warm, a comment in the core |
|---|---:|---:|---:|---:|
| MediatR - 392 tests, 19 projects | 19.7 s | 8.5 s | **5.0 s** | 7.7 s |
| NodaTime - 3,633 tests, 18 projects | 20.6 s | 16.7 s | **5.2 s** | 11.9 s |
| tia itself - 309 tests, 11 projects | 79.8 s | 8.6 s | **3.6 s** | 6.8 s |

The comment is deliberate: it moves the file's content hash, so the project's fragment is rebuilt
and re-bound, which is where the time goes, while moving no symbol - so the selection beside it is
the floor rather than a representative one. The timing is what is being measured.

**The uncomfortable reading, published as such: only tia itself has a suite long enough for
selection to obviously pay.** NodaTime's expensive warm case costs 11.9 s against a 20.6 s suite,
so it must skip more than 42 % of the tests to break even; MediatR's 7.7 s against 19.7 s needs
39 %. Both clear that bar on their ordinary commits - NodaTime's four genuine library changes select
7-11 %, MediatR's non-library commits select nothing - and neither clears it on a change to its
core, where selection approaches 100 % and the analysis is pure cost.

Test count is a poor proxy for suite time, which is the other thing this table says: tia has the
fewest tests of the three and by far the longest suite, because each integration test drives real
MSBuild. A repository deciding whether this tool pays should time its own suite rather than count
its own tests.

NodaTime's figures here are its trimmed 18-project gate solution rather than the 21-project one
measured further up, so they are not a like-for-like replacement for that table.

## What the analysis actually costs, and where it was hiding

Every number in this file above was about *selection* - how much of a suite a diff reaches. None of
them decide whether the tool is worth running. That is `T > A / (1 - f)`, and `A` had never been
broken down on a repository small enough for `A` to matter.

**cartographer** is that repository, and it was built alongside this tool as its proving ground. Its
own design document records the outcome: *"258 tests run in about two seconds"*, so the `tia` job
stays commented out in its workflow against this tool's published advice. Warm analysis there is
**6.85 s against a 2 s suite** - so no selection ratio can pay, including zero. A perfect analysis
that selected nothing would still lose by more than three suites.

### The phases did not sum to the run

| Phase | Seconds | Share |
|---|---:|---|
| `changeResolutionSeconds` | **3.09** | 45 % |
| `workspaceLoadSeconds` (of which `solutionOpenSeconds` 1.70) | 2.22 | 32 % |
| `diffSeconds` | 1.13 | 16 % |
| `graphSeconds` + `fingerprintSeconds` | **0.21** | **3 %** |

The timed phases accounted for 3.94 s of 6.85 s. The missing 2.56 s was the largest single cost in
the run, and it had no name: `ChangeResolver`, `RouteSeeder` and the old-side git reads were never
timed. `PhaseTimings` already warns that a timing which is always zero is worse than none because it
says the phase is free; an absent timing says the same thing more quietly, and this one said it about
45 % of the run.

Four phases now close the gap and unattributed time is 0.27 s, which is process start.

**The finding that matters is the 3 %.** The per-project graph cache - the thing several rounds of
work in this file went into - is not what makes a warm run cost anything. The cost is a preamble that
runs before selection is even reachable.

### The forced parse

`changeResolution` is dominated by a parse that the laziness recorded above was specifically built to
avoid. Compilations were made lazy so that *"a project whose fragment still stands is never parsed at
all"*. That holds - and `ChangeResolver` then forces `context.Compilation` for every project owning a
changed file, so the parse is skipped only on runs where nothing changed, which is no run anybody
makes.

It is visible in the phase attribution moving rather than the total falling: on a run that rebuilds
fragments the compilation is charged to `graphSeconds` and `changeResolution` is ~0.9 s; on a warm run
that rebuilds nothing it is charged to `changeResolution` and reads ~3.9 s. Same work, different
owner.

Two ways out were considered and rejected, both for soundness rather than effort:

- **Narrowing the changed-file diagnostics to the changed spans.** Roslyn supports it and it would be
  most of the saving. But a change can break a usage elsewhere in the same file, whose span did not
  move, and `GraphBuilder`'s per-project check reads declarations only - so nothing else would catch
  it. That trades a silent miss for speed.
- **Deriving symbol keys syntactically.** A documentation comment ID embeds resolved parameter types
  (`M:Ns.C.M(System.Int32)`), so a syntactic derivation is an approximation, and an approximate seed
  is a missed test.

What remains sound is narrower: index each cached fragment by declaration name, and for a changed file
in a fragment that is still valid, resolve parsed declarations against keys **that already exist in
the graph**, falling back to the full compilation on any ambiguity. Sound because no key is ever
invented, and available because a valid fragment was built from the same file content. That is the
route taken, and it went further than this sketch - see *Closing the forced parse* below.

### One diff instead of one per file

`DiffResolver` called `git diff -U0` once per changed C# file, from inside the loop over those files,
because `ParseHunks` ignores the file headers and collects every hunk it sees - correct for a
single-file diff and silently wrong for any other. Hunks are now attributed to the path in the
`---`/`+++` header, so one call serves every file.

A/B on tia's own repository, 16 changed C# files, three runs per arm, only the binary varying:

| | Before | After | |
|---|---:|---:|---|
| `diffSeconds` | 0.59 `[0.57-0.61]` | 0.19 `[0.18-0.20]` | **-68 %**, ranges disjoint |
| `changedSymbolCount` | 57 | 57 | identical - attribution correct |
| elapsed | 8.49 | 7.90 | -7 %, ranges overlap |

The diff-phase win is established. The elapsed win is reported as suggestive only, because the
ranges overlap - the same treatment the graph-walk result above got.

A first attempt at this measurement was discarded rather than published: cartographer's working tree
went clean and its HEAD moved between the two arms, so what looked like a 56 % improvement was an
empty diff being compared against a fifteen-file one.

### The tool could not check its own claim

`BreakEvenSuiteSeconds` printed *"worth it if the full suite takes more than …"* on every run, and
the tool had never timed a suite. It measured `A` and `f` and spawned `T` without looking at it. So
it could print that sentence having just watched the suite take two seconds.

`run` now records one line per invocation - `A`, `f`, selected, total, and the measured suite time -
and `tia stats` reports what selection has actually cost or saved. `T` comes from full runs, because
only a full run observes it; without one it is a lower bound, which understates the loss rather than
inventing a saving. When the ledger shows a net loss, `run` says so unprompted. On cartographer:

```
  Analysis (A)    6.8s
  Selected (f)    62%
  Full suite (T)  2.0s
  Selective run   8.0s  (A + fT)
  Net per run     costs 6.0s more than running everything
```

Nothing reads the ledger to decide what runs. A ledger that changed the selection would be a
correctness surface; this is a reporting one.

## Closing the forced parse

The section above ends by describing a sound route out of the forced compilation: index each cached
fragment by declaration name and resolve parsed declarations against keys the graph already holds.
What was built goes further, and is simpler for it. Rather than matching by *name*, the fragment
records where each declaration **sits**.

That is a stronger position, and the reason is worth stating because it is the whole argument for
the change being sound. A fragment is reused only when its project's content hash still matches, so
the file whose spans it holds is byte-for-byte the file the diff is about. Mapping a changed line
range onto a stored span is therefore not an approximation of what a semantic model would have said
- it is the same answer, recorded earlier. Nothing is derived, guessed or reconstructed, and no key
is ever invented: the key is the one the graph already holds. Name matching would have needed a
tie-break on ambiguity; positions need none.

`ChangeResolver` forced `context.Compilation` for four separate reasons, and each had to go
separately:

| It needed a compilation to | It now |
|---|---|
| read a changed file's declarations back | maps changed lines onto stored declaration spans |
| check the changed file's bodies bound | looks up a per-file verdict recorded at rebuild |
| ask whether the project's generators emit | reads a count stored with the fragment |
| build the old side's type index | reads type name paths and member names out of graph keys |

The last one is the one that would have been easy to get wrong. A documentation comment id embeds
resolved parameter types, so *writing* one from syntax is an approximation - but *reading* a name
back out of one is not, and reading is all the old side needs: it matches a base-revision
declaration to a type by name path and to a member by simple name, and it already marks every
same-named overload because without binding they are indistinguishable. An explicit implementation
arrives spelled `Namespace#IInterface#Member`, an indexer is always `Item`, a constructor stays
`#ctor` - and that last one is correct rather than a defect, because the base-revision tree offers
the type's own identifier for a constructor, so the lookup finds nothing and falls through to the
declaring type, which is what it did when it had a compilation to ask.

### The equivalence is the test

`DeclarationSiteResolver` reimplements `ChangedSymbolResolver`'s rules, and those rules are the ones
that are easy to get wrong: members win over the type that contains them, a change to a type header
is a change to every member, constants widen to their declaring type because callers inline them,
and a change outside every declaration rebinds the whole file. Two copies of a rule set is exactly
the shape that drifts.

So the test is not that each rule works - it is that the two resolvers **agree**, key for key,
widening for widening and unmapped range for unmapped range, over every shape that has a rule of its
own: a method body, a type header, a constant, a plain field, a property, a using directive, a range
spanning the file, and a range past its end. One branch cannot come from positions at all, because a
`global using` is not a declaration; that one still parses the single document, which does not need
the project compiled.

### What it cost and what it saved

A/B on this repository, 23 changed C# files, three runs per arm, both binaries staged and only the
binary varying:

| | Before | After | |
|---|---:|---:|---|
| elapsed | 7.81 `[7.69-7.82]` | 5.47 `[5.44-5.53]` | **-30 %** |
| `changeResolutionSeconds` | 4.05 `[4.04-4.06]` | 1.72 `[1.70-1.74]` | **-58 %** |
| `compilationCpuSeconds` | 1.35 `[1.30-1.35]` | 0.00 | gone |

All three ranges are disjoint. The report is identical field for field - selected tests, impacted
tests, widenings, diagnostics - with widenings compared as a set, because their order has never been
stable across runs on either binary.

`compilationCpuSeconds` reaching zero is the claim that matters: a warm run now produces no
compilation at all. Everything it used to be asked comes off the fragment.

Two behaviours changed deliberately rather than incidentally. Two types sharing a name path at
different arities are now both reported instead of whichever the index happened to enumerate first -
the old answer was arbitrary, not chosen. And a partial type gets a site in every file that declares
it: `SymbolNode` keeps a single `FilePath`, which is enough for what reads it, so a resolver keyed on
the node's file would have found nothing for a change in the other half of a partial class.

### Reading the old side of a diff

Separately, and measured on its own before the change above: base-revision content was read with one
`git show` per file, from inside the loop over changed files. `git show` answers in about a
millisecond and costs about thirty to start, so a sixteen-file change spent half a second waiting for
processes rather than for git.

Reading them concurrently rather than batching through `git cat-file --batch` is deliberate. That
protocol frames each object by its size in *bytes* and everything here is decoded text, so splitting
the stream correctly would mean re-encoding to count. Spawning the same processes in parallel removes
the same wait without inventing a parser.

| | Before | After | |
|---|---:|---:|---|
| `oldSideFetchSeconds` | 0.576 `[0.574-0.578]` | 0.284 `[0.278-0.321]` | **-51 %**, disjoint |
| elapsed | 8.53 `[8.43-8.54]` | 8.20 `[8.09-8.26]` | -4 %, disjoint |

### What is left in a warm run

| Phase | Seconds | Share |
|---|---:|---|
| `workspaceLoadSeconds` (of which `solutionOpenSeconds` ~2.9) | 3.25 | 59 % |
| `changeResolutionSeconds` | 1.72 | 31 % |
| ├ `generatorProbeSeconds` | 1.25 | |
| ├ `oldSideFetchSeconds` | 0.11 | |
| └ `triviaCheckSeconds`, `oldSideResolveSeconds`, the rest | ~0.36 | |
| `diffSeconds` | 0.18 | 3 % |
| `graphSeconds` + `fingerprintSeconds` | 0.33 | 6 % |

The 1.25 s is one project re-running its source generators over both revisions, which is what makes
a change to generator input attributable at symbol granularity instead of widening the whole project.
Both arms paid it; it was hidden inside the compilation it used to force, and naming it is the same
move that found the 2.56 s at the top of this section.

`MSBuildWorkspace.OpenSolutionAsync` is now 59 % of a warm run and is the only large item left.

## What is not measured yet

- **Polly is installable now, and still not gateable here.** The feature band its `global.json`
  pins (10.0.400) downloads and installs, and Polly builds clean under it - so the blocker recorded
  above is gone. Four further accommodations were needed before its baseline was green, and only
  the first announces itself: the user-scope SDK brings only its own runtime, so every non-net10
  test host died while `dotnet test` still exited 0 and printed green rows for the frameworks that
  did run; `net481` fails 16 DataAnnotations assertions because the German GAC satellite answers a
  test asserting the English string; and coverlet fails the run below 100 % line coverage, which is
  Polly's quality gate rather than anything about selection. With all four cleared the harness still
  abandoned the run - a sample reported not restoring a file that `git` then showed clean - and that
  cause is **unidentified**, not explained away. Replay fared no better: 16 of 20 commits failed to
  restore under the harness though they restore by hand, and 3 of the 4 that succeeded were
  scratch commits made to clear the blockers above, so the mean it produced measures this
  investigation rather than Polly.
- **FluentValidation cannot be built here at all.** Its `DependencyInjectionExtensions` project
  fails MSB3030 in both configurations, from clean, single-threaded. Nothing had exercised that
  before: its mutation gate was refused for want of a TRX reporter and replay only restores, so the
  repository this file publishes the most selection data about is one whose suite has never run on
  this machine. The selection figures stand; no wall-clock claim about it can be made from here.
- The selection figures above the gate section predate the reflection and static-initializer
  fixes, and are therefore lower than the same changes would produce today. They are left as
  measured rather than rewritten, because the fixes were a deliberate trade of precision for
  soundness and hiding the before-figure would hide the trade.
- MSTest has attribute-discovery tests but no fixture project of its own; it shares the VSTest
  dialect with NUnit, which is executed end to end.
