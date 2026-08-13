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

### Verdict on the route gap: closable in principle, not closable honestly here

The claim above that the link "is not in the source at all" is too strong, and worth correcting
rather than leaving. Both halves usually *are* literals in the source: the test writes
`GetAsync("/Contributors")` and the endpoint writes `MapGet("/Contributors", ...)` or carries a
`[Route]` attribute saying the same thing. An edge between members that mention the same route
literal would connect them, and being an added edge it can only over-select - it cannot introduce
a miss, which is the property that made the request-type edge shippable.

It is not shipped, and the reason is not difficulty:

- **It cannot be measured here.** The one repository that exhibits the gap runs its functional
  tests against containers this environment cannot start. Every other engine change in this file
  earned its place on a gate or a benchmark; this one could only be argued for.
- **The literal is often not literal.** `MapGroup("/api")` plus `MapGet("/contributors")`,
  interpolated versions, route templates with parameters, and constants shared through a
  `Routes` class all break exact matching, so the real implementation is a route-template
  *matcher*, not a string comparison - and a matcher tuned against no repository is a guess.
- **A bare string-literal edge is worse than nothing.** Connecting any two members that share a
  string constant would join every member mentioning `"id"` or `"/"`, and unmeasured
  over-selection is exactly how a tool stops being worth running.

So the position is: the gap is real, it is the binding limit for applications, and the fix is a
route-template edge guarded the same way the request-type edge is guarded. It waits for a
repository that can gate it. Until then it stays a documented miss rather than an untested
mitigation, and the mitigation available to an adopter today is the one the safety model already
offers - treat the endpoint assembly as widened, or run functional tests unconditionally.

## What is not measured yet

- Polly pins an SDK feature band that is not installable here.
- Wall-clock is measured on NodaTime only, and on an already-built solution. Build time is
  excluded from both sides, which flatters neither.
- Neither gated repository is an application. The section above confirms the miss that follows
  from that, on a real one, without being able to gate it - a Docker-capable environment, or an
  application whose dispatch paths are exercised by tests that run without containers, would turn
  that inspection into a measurement.
- The selection figures above the gate section predate the reflection and static-initializer
  fixes, and are therefore lower than the same changes would produce today. They are left as
  measured rather than rewritten, because the fixes were a deliberate trade of precision for
  soundness and hiding the before-figure would hide the trade.
- MSTest has attribute-discovery tests but no fixture project of its own; it shares the VSTest
  dialect with NUnit, which is executed end to end.
