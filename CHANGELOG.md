# Changelog

Notable changes to `dotnet-tia`. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While the major version is `0`, the CLI surface and the JSON schema may change between minor
versions. What will not change quietly is the safety model: a release that makes selection *narrower*
says so here, in its own entry, because that is the only kind of change that can turn a passing
suite into a missed test.

## [Unreleased]

## [0.2.0] — 2026-08-15

The tool installs on an SDK-9 machine, four gaps are closed, and two experiments are measured and
declined. Most of what is fixed here was found by pointing the engine at a repository nobody in this
project wrote; the fixture solutions are 4 and 12 tests and would not have found any of it.

**Two changes make selection narrower** — the load diagnostic and `IsTestProject` — and that is the
only kind of change that can turn a passing suite into a missed test. Both are marked as such in
their own entries, and the gate was re-run rather than trusted: zero misses across five solutions
plus MediatR.

### Fixed

- **An MSBuild warning during load forced a full run.** `MSBuildWorkspace` raises warnings logged
  during the design-time build through the same `WorkspaceDiagnosticKind.Failure` channel as a real
  load error, wrapped in the same sentence, and every one of them was treated as a project that did
  not load. A test project multi-targeting `net462` alongside a package that warns it does not
  support `net462` — an ordinary combination, and `dotnet build` reports zero errors — therefore
  selected the whole suite on every run. The diagnostic is now evidence and the loaded solution is
  the verdict: a complaint naming a project that is present is logged, one naming nothing that
  loaded still forces a full run. **This makes selection narrower.** The hole it opens is closed
  separately: a multi-targeted test project that loaded for fewer frameworks than it declares is a
  failure on the count alone, with no diagnostic required.
- **A project that says it is not a test project is believed.** Referencing a test framework was
  treated as the verdict, and `IsTestProject` was only ever read to promote a project the reference
  signal had missed — so a project referencing xunit to make documentation examples compile, and
  declaring `IsTestProject` false because it holds no tests, was listed as a test project anyway.
  It is the same property the SDK's own targets read to decide whether to run a project, so
  honouring it is what makes `tia` and `dotnet test` name the same set. Only an evaluated `false`
  counts; the literal XML read honours no conditions, and that error would drop a real test project
  out of the selection. **This makes selection narrower.**
- **The mutation harness hung on a mutation that stopped a loop terminating.** It ran the suite per
  sample and waited with no timeout, so one such sample stalled a whole run indefinitely — observed
  at three hours and 38 seconds of CPU — producing no verdict rather than a miss or a pass. A sample
  is now bounded by four times the baseline preflight run, floored at two minutes and capped at
  thirty, and a killed suite is reported as its own outcome that can never count as a pass.
- **`explain` printed the wrong edge label.** `ImpactTraversal.PathTo` attached to each node the
  edge leading *out* of it while the field was called `IncomingEdge`, so every label sat one place
  too early and a two-node path rendered as the generic "referenced by" whatever the edge actually
  was. The text and JSON renderers disagreed with each other as a result.
- **A change to a non-C# project selected nothing at all.** Projects whose language is not C# were
  skipped during load and then forgotten, so a changed `.fs` or `.vb` file found no owning project,
  widened nothing, and reached no test — a C# test project exercising an F# library ran **zero**
  tests for a change to that library. This was a miss, not a blind spot, and the mutation gate could
  never have found it because a mutation only ever edits C#. Such a project is now recorded and
  widened, and dependent expansion carries that to everything referencing it. A project type the
  workspace cannot load at all is not listed anywhere, so a changed file inside one forces a full
  run naming the project file.

- **The tool installs on a machine whose only SDK is 9.0** ([#13](https://github.com/SebHenn/dotnet-tia/issues/13)).
  It shipped a `net10.0` asset only, so `dotnet tool install -g dotnet-tia` there failed with
  `DotnetToolSettings.xml was not found in the package` — a message that blames the package and tells
  the user to contact the author, when the cause was that no asset in it matched the runtime. The
  shipped projects now multi-target `net9.0;net10.0`, and a CI job installs and runs the packed tool
  on an SDK-9 image, which is the only place a regression of this would show up.

### Changed

- **Per-document cache granularity was measured and declined.** No behaviour change; the finding is
  the deliverable. Splitting the cache fragment per document cannot save parsing, only the semantic
  walk of unchanged trees — 2.7 s of CPU on a warm one-project change here — while the only sound
  reuse key is a declaration surface including private members, strictly larger than the public one
  already costing 3.0 s to hash. The key costs more than the reuse saves, so the cache stays per
  project. Recorded in `docs/benchmarks.md` with the numbers and with what would change the answer.
- **Runner properties are evaluated through MSBuild rather than read out of project XML.** The XML
  read honoured no conditions and expanded no expressions, so it disagreed with the build in both
  directions: a property inside a false `Condition` was reported as set, and a property function was
  reported as its unexpanded text. `UsingMicrosoftNETSdkTest` shows the cost — `FrameworkDetector`
  has always tested it, but Microsoft.NET.Test.Sdk's props set it and no project file writes it, so
  that branch could never fire. Evaluation is spent on test projects only: it cost 1.79 s across this
  repository's seven projects against 0.30 s for the two that need it, and nothing else changes an
  answer. A project evaluation cannot open falls back to the literal read rather than to nothing,
  and `--json` gained `propertySource` per project plus a `propertyEvaluationSeconds` timing.
- **`verify --mutate` preflights the suite before mutating anything.** A project whose outcome
  cannot be read can never produce a usable sample, and the harness used to discover that once per
  sample — so a 60-sample run spent its whole budget arriving at a verdict that proved nothing,
  which is easy to mistake for one that passed. It now runs the baseline suite once, refuses up
  front if any project is unobservable, and names the package that project is missing, chosen by its
  runner. `shadow` already had this property for free: it runs the suite exactly once, so its
  inconclusive verdict was never delayed.

### Added

- **A nightly job that gates and replays four outside repositories.** `workflow_dispatch`/`schedule`
  only, never in the pull-request loop, with the reports uploaded as artifacts so the figures in
  `docs/benchmarks.md` have a source. Every defect this tool has shipped and then fixed was found by
  pointing it at a repository nobody here wrote, including the three fixed in this release; the
  fixture solutions are 4 and 12 tests and would not have found any of them. FluentValidation is
  listed as replay-only, because the mutation preflight refuses it for want of a TRX reporter and a
  matrix that hid that would look like a gate it is not.
- **Skip reasons in the validation harness's markdown report.** It printed "0 usable sample(s), 20
  skipped, 0 miss(es)" and then "no failing test was left out of a selection" — a sentence that is
  true of a run which checked nothing, directly beneath the number saying so. `verify` has grouped
  its skip reasons since the preflight landed; the report now does the same.
- **`--type-flow`, off by default, and measured not to pay for itself.** The bound on an upward hop
  counted every *mention* of the implementing type, so a `typeof`, a static call or a name in a base
  list all read as "this caller can dispatch here". The flag sharpens it to what a member can obtain
  an *instance* of, propagated to a fixpoint across the merged graph, and intersected with the
  existing bound so it can only ever narrow. It found no miss on any gate that could be run — all
  four solutions, with and without it — and it changed the selection on neither FluentValidation
  (0 hops narrowed) nor NodaTime (4 hops narrowed, not one test moved), while roughly doubling
  analysis time when on. Neither external gate returned a verdict: FluentValidation references no
  TRX reporter so the preflight refused it, and NodaTime's suite is red on a de-DE machine before
  any mutation, which reports as a miss every sample. Both are recorded rather than rounded off.
  It stays available and off; the reason it cannot help, and why dynamic coverage rather than a
  sharper bound is what would, is written up in `docs/benchmarks.md` next to the earlier attempt.
  Cache `FormatVersion` 6 → 7, and the flag is part of the cache file's key — a fragment built
  without the facts would otherwise be reused under the flag and draw its bound from an empty set,
  which is a missed test rather than a slow run.
- **HTTP route dispatch is no longer a blind spot.** A functional test that calls `/projects` names a
  route string and a response shape, never the endpoint class, so a change to that endpoint used to
  select **nothing**. Route templates are now collected *positionally* — the route argument of a
  `Map*` call, and the argument of a `[Route]`/`[Http*]` attribute, with constants resolved through
  the semantic model — normalised to a key with parameter segments wildcarded, and joined after the
  merge to the members that name a matching path. Guarded exactly like the request-type edge:
  followed only when nothing in the solution names the endpoint's type, so an endpoint that already
  has ordinary edges gains nothing. Adding edges can only widen, so this cannot introduce a miss by
  construction. On the new web fixture a change to an endpoint went from 0 of 4 tests selected to
  exactly the 1 test that exercises it. `explain` prints the hop; `--json` gained
  `routeScanCpuSeconds`. Cache `FormatVersion` 4 → 6.

  One case is widened rather than traced: a change to a route template *itself*. The graph is built
  from the new source, so the endpoint's new route no longer matches the old path its callers still
  name and the edge is absent exactly when it is needed. That is a by-value binding like `const`
  inlining and gets the same treatment — a diff touching a route declaration widens that project,
  scoped to the changed lines so an endpoint body edit still selects precisely. The mutation gate
  found this after the edge was already "working", which is the argument for having it.
- **`verify --project-granularity`**, an opt-in gate for repositories whose test projects cannot
  write TRX. It reads each project's exit code rather than individual test outcomes, which supports
  exactly one sound inference: a project that failed, none of whose tests were selected, contains a
  failing test that would not have run — a definite miss. The converse does not follow, so the run
  reports `PROJECT-GRANULARITY GATE` and **never** `PASS`, and `--json` carries `projectGranularity`
  alongside `passed` so a consumer cannot mistake the two. Off by default.
- **The TUnit dialect collapses wholly selected classes to a wildcard method segment**, so a
  selection that takes classes whole no longer lists every method they contain. The tests that run
  are identical; the filter is a fraction of the length, which is what decides whether it survives
  the command line instead of being dropped for a whole-project run. The cross-product stays for
  selections where any class is only partly selected — probing the real runner established that
  `--treenode-filter` is not repeatable (twice selects nothing) and that a `|`-joined union of two
  paths silently parses as an alternation inside the first path's method segment, matching a
  *subset*. Both look reasonable written down, and both would have been a miss.



- **HTTP route dispatch is no longer a blind spot.** A functional test that calls `/projects` names a
  route string and a response shape, never the endpoint class, so a change to that endpoint used to
  select **nothing**. Route templates are now collected *positionally* — the route argument of a
  `Map*` call, and the argument of a `[Route]`/`[Http*]` attribute, with constants resolved through
  the semantic model — normalised to a key with parameter segments wildcarded, and joined after the
  merge to the members that name a matching path. Guarded exactly like the request-type edge:
  followed only when nothing in the solution names the endpoint's type, so an endpoint that already
  has ordinary edges gains nothing. Adding edges can only widen, so this cannot introduce a miss by
  construction. On the new web fixture a change to an endpoint went from 0 of 4 tests selected to
  exactly the 1 test that exercises it. `explain` prints the hop; `--json` gained
  `routeScanCpuSeconds`. Cache `FormatVersion` 4 → 6.

  One case is widened rather than traced: a change to a route template *itself*. The graph is built
  from the new source, so the endpoint's new route no longer matches the old path its callers still
  name and the edge is absent exactly when it is needed. That is a by-value binding like `const`
  inlining and gets the same treatment — a diff touching a route declaration widens that project,
  scoped to the changed lines so an endpoint body edit still selects precisely. The mutation gate
  found this after the edge was already "working", which is the argument for having it.
- **`verify --project-granularity`**, an opt-in gate for repositories whose test projects cannot
  write TRX. It reads each project's exit code rather than individual test outcomes, which supports
  exactly one sound inference: a project that failed, none of whose tests were selected, contains a
  failing test that would not have run — a definite miss. The converse does not follow, so the run
  reports `PROJECT-GRANULARITY GATE` and **never** `PASS`, and `--json` carries `projectGranularity`
  alongside `passed` so a consumer cannot mistake the two. Off by default.
- **The TUnit dialect collapses wholly selected classes to a wildcard method segment**, so a
  selection that takes classes whole no longer lists every method they contain. The tests that run
  are identical; the filter is a fraction of the length, which is what decides whether it survives
  the command line instead of being dropped for a whole-project run. The cross-product stays for
  selections where any class is only partly selected — probing the real runner established that
  `--treenode-filter` is not repeatable (twice selects nothing) and that a `|`-joined union of two
  paths silently parses as an alternation inside the first path's method segment, matching a
  *subset*. Both look reasonable written down, and both would have been a miss.


- **An SDK-version mismatch is now named rather than surfaced raw.** Installing on .NET 9 does not
  make a `net10.0` project loadable — MSBuild 9 has no targeting pack for it — so that case still
  forces a full run. What changed is the reason: it names the registered MSBuild version, the
  project's target framework, and that the two do not match, instead of passing through
  `NETSDK1045`. Matched on the error code, not the English prose, so it survives a localised SDK.
- **`--json` reports which MSBuild read the projects** (`msBuild`), whether or not registration
  succeeded. On a machine with several SDKs installed this is the difference between a result and a
  puzzle.
- A root `global.json` pinning the SDK to 10.0.1xx or later, so a from-clone build on SDK 9 fails
  immediately with the SDK's own message instead of at pack time with the tool-settings one.

## [0.1.0] — 2026-08-05

First release. Published to [nuget.org](https://www.nuget.org/packages/dotnet-tia) as a `dotnet` global tool.

### Added

- **The impact engine.** Fully static — no instrumentation, no profiler, no prior coverage run. It
  resolves a git diff to changed files and line ranges, loads the solution with `MSBuildWorkspace`,
  maps changed lines to changed symbols (including deletions, via the base revision's tree), walks a
  reverse reference graph of callee→caller, interface↔implementation, base↔override and fixture→test
  edges, and reaches test methods by breadth-first search.
- **Commands:** `analyze`, `run`, `explain`, `graph`, `verify` and `shadow`. `explain` prints the
  actual path from a changed symbol to a test, or says nothing reaches it.
- **A three-tier safety model.** Full-run triggers bail out and say why: project files,
  `Directory.Build.*`, `Directory.Packages.props`, `global.json`, `nuget.config`, `.editorconfig`,
  lockfiles, any workspace load failure or compilation error, an unreachable base commit, any
  unhandled exception. Widening triggers expand scope instead of bailing: reflection and
  serialization, source generators, non-`.cs` content files, and `const`/enum inlining. Every
  widening and every bail-out is printed and included in `--json`.
- **The mutation gate.** `verify --mutate N` injects a Stryker-style mutation, selects against it,
  then runs the full suite; any test that fails but was not selected is a miss. Zero misses is the
  merge gate. A sample whose outcome cannot be read is reported as inconclusive, never as a pass.
- **Shadow mode.** `shadow` selects, runs the whole suite anyway, and reports which failures the
  selection *would* have skipped — so a repository whose dispatch this engine cannot see finds that
  out from its own history rather than from a claim made about somebody else's.
- **Framework and runner detection**, per project, with three filter dialects: VSTest syntax, xUnit
  v3's native `--filter-method`, and TUnit's `--treenode-filter`. Covers xUnit v2 and v3, NUnit,
  MSTest and TUnit across VSTest, Microsoft.Testing.Platform and the platform-native `dotnet test`.
  All three dialects are executed end to end against real runners, not just asserted as strings.
- **A per-project graph cache** at `.tia/graph-<key>.bin`, keyed on project content and on the
  *declaration surface* of everything it references — so a body-only edit upstream does not
  invalidate a dependent. The reuse decision is made from file content alone, before any project is
  parsed, so a project whose fragment still stands is never compiled.
- **Break-even reporting.** Every run prints the suite duration above which selection actually pays,
  because analysis costs wall-clock too and a tool that will not admit that is not worth trusting.
- **Release plumbing:** a cross-platform CI matrix, a nightly mutation gate, and a tag-triggered
  release that verifies the tag matches the package version and installs the built artifact as a
  tool before anything is pushed. Publishing uses nuget.org trusted publishing, so there is no
  long-lived API key in the repository.

### Known limitations at this release

Documented in full under "What this does not do yet" in the README. The ones most likely to affect
you: **HTTP route dispatch is a known miss** (use `shadow` before trusting selection on an
application rather than a library), selection is only coarsely type-aware, cache granularity is per
project rather than per document, and only C# is analysed.

[Unreleased]: https://github.com/SebHenn/dotnet-tia/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/SebHenn/dotnet-tia/releases/tag/v0.1.0
