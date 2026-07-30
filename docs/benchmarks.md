# Benchmarks

Measured runs, not projections. Reproduce them with the drivers in `tests/Tia.Validation`.

## Correctness: mutation harness

The merge gate. Inject a Stryker-style mutation, select against it, run the **full** suite, and
check that every failing test was in the selection.

| Repository | Samples | Usable | Misses | Typical selection |
|---|---:|---:|---:|---|
| `tests/Tia.Fixtures` (xUnit v3 + NUnit, 12 tests) | 40 | 31 | **0** | 1 / 12 |
| `tests/Tia.Fixtures.Tunit` (TUnit, source-generated, 4 tests) | 30 | 30 | **0** | 1 / 4 |

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

## What is not measured yet

- Polly pins an SDK feature band that is not installable here. NodaTime was measured on targeted
  changes but not replayed over its history.
- No wall-clock saving is reported. Selection ratio is measured; time saved depends on the suite's
  own shape, and quoting a figure from one repository would overstate it.
- The mutation gate has only been run against the fixture solutions, not against FluentValidation.
