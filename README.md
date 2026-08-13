# dotnet tia

[![NuGet](https://img.shields.io/nuget/v/dotnet-tia.svg)](https://www.nuget.org/packages/dotnet-tia)
[![Downloads](https://img.shields.io/nuget/dt/dotnet-tia.svg)](https://www.nuget.org/packages/dotnet-tia)
[![CI](https://github.com/SebHenn/dotnet-tia/actions/workflows/ci.yml/badge.svg)](https://github.com/SebHenn/dotnet-tia/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Test impact analysis for .NET. A `dotnet` global tool that takes a git diff and runs **only the tests that diff can actually affect**.

```
$ dotnet tia run --base origin/main

  Base                  origin/main (7dd3bef24)
  Diff                  1 file (1 C#), 1 symbol changed
  Graph                 125 types / 1,035 members / 3,593 edges  (7 projects built)
  Impacted tests        37 of 165  (22.4 %)

  Widenings
    ! Reflection       Tia.Core: AnalysisReport.cs uses JsonSerializer.Serialize (line 165); the reflecting member(s) are treated as always impacted

  Projects
    Tia.Core.Tests                        9 / 115     filtered (XUnitV3/MicrosoftTestingPlatform)
    Tia.Integration.Tests                28 / 50      filtered (XUnitV3/MicrosoftTestingPlatform)

  Elapsed               8.8s

  > dotnet test tests/Tia.Core.Tests/Tia.Core.Tests.csproj -- --filter-method Tia.Core.Tests.GitDiffParserTests.Hunks_reads_both_sides ...
```

That output is real: it is `tia` selecting against a one-line change to its own
`GitDiffParser.ParseHunks`, captured at `7dd3bef2`. Nine of the 115 engine tests that existed at
that commit exercise that method — the suite has grown since, so read the shape rather than the
counts. Most of the integration tests come with it, and correctly so — every one of them runs an
analysis, so every one goes through the diff parser.

The widening is `tia` catching itself: `AnalysisReport` serializes with `System.Text.Json`, which reaches its properties by reflection, so the members that serialize are unconditionally impacted. That is the rule doing its job on the tool's own code.

## Is it worth it for your repository?

**Rule of thumb: `tia` pays off when your test suite takes more than about a minute.** Below that,
analysis costs more wall-clock than selection saves, and no amount of accuracy changes that — a
selective run costs `A + fT` against a full run's `T`, so it only wins when `T > A / (1 - f)`.
Analysis is several seconds warm and longer on a cold graph, so a suite measured in seconds is not a
candidate, and you should not have to read to the bottom of this page to find that out.

You never have to take the estimate on trust, because every run prints the threshold for your
repository rather than a verdict about somebody else's:

```
  Impacted tests        304 of 3,730  (8.2 %)
  Elapsed               8.4s
  Worth it if           the full suite takes more than 9s
```

Two things worth knowing before ruling it out on other grounds:

- **Microsoft.Testing.Platform is not a requirement.** The framing below is about why this became
  possible now, not about what you have to be running. xUnit v2 on VSTest is supported, as are NUnit
  and MSTest on either runner — see [Supported frameworks](#supported-frameworks).
- **You do not have to believe the accuracy claims below.** `dotnet tia shadow --base origin/main`
  selects, runs your whole suite anyway, and reports which failures the selection *would* have
  skipped. Nothing is at risk while it runs, so your repository can answer the safety question from
  its own history instead of from a benchmark on someone else's code. It is the honest way to
  evaluate this tool, and it is the first thing to reach for if your application dispatches through
  HTTP routes, which is [handled but with a documented edge](#what-this-does-not-do-yet).

## Why

Per-test impact analysis for .NET currently exists only behind a paywall or a SaaS — Datadog Test Optimization, Tricentis SeaLights, NCrunch ($159/yr, IDE-only), or Azure DevOps' legacy VSTest-only collector. The free OSS options ([`dotnet-affected`](https://github.com/leonardochaia/dotnet-affected), Incrementalist) resolve impact at **project** granularity, so changing one file in a core library still runs every downstream test project in full.

Meanwhile .NET 10's Microsoft.Testing.Platform replaced VSTest's assembly-scanning plugins with first-class extension APIs, and xUnit v3, NUnit, MSTest and TUnit have all shipped MTP runners. The extension surface exists and nobody has built free test-level selection on it.

## How

Fully static — no instrumentation, no profiler, no prior coverage run:

1. Resolve the git diff to changed files and line ranges
2. Load the solution via `MSBuildWorkspace`
3. Map changed lines to changed symbols, including deletions via the base revision's tree — a file whose token sequence is unchanged moved only comments or formatting and seeds nothing
4. Walk a reverse reference graph — callee→caller, interface↔implementation, base↔override, fixture→test
5. BFS to reach test methods
6. Emit per-project filters and invoke `dotnet test`

Interface edges mean dependency injection needs no special handling: a test calling `IFoo.Bar()` is already connected to `Foo.Bar()`. They point both ways but do not compose — going up from one implementation and straight back down to its siblings would claim that changing `EnglishGreeter.Greet` says something about `GermanGreeter.Greet`.

Going *up* is also bounded. A caller of `IFoo.Bar()` is only affected by a change to `Foo.Bar()` if it can also get hold of a `Foo` — via a constructor, a factory, a registration, a type argument. When nothing in the solution mentions `Foo` at all, whatever creates it is invisible and the bound is dropped rather than guessed.

Blind spots that static analysis genuinely has — reflection, source generators, `const` inlining, non-`.cs` test data — get explicit widening or full-run rules, and every one of them is reported rather than applied silently.

## Install

```
dotnet tool install -g dotnet-tia
```

Or from a clone, which is also how you would test a change before releasing it:

```
dotnet pack src/Tia.Cli -c Release
dotnet tool install -g --add-source artifacts/nupkg dotnet-tia
```

The tool installs on the .NET 9 SDK and later. What it can *analyse* is a narrower question, and worth separating: `tia` reads projects through MSBuild, so it can only load projects the SDK it finds is able to load. Point a .NET 9 SDK at a `net10.0` project and the run degrades to a full one with the reason named — the registered MSBuild version, the project's target framework, and that the two do not match — rather than failing obscurely. To analyse this repository, or any other `net10.0` one, install the .NET 10 SDK.

If the install itself fails with:

```
The tool package could not be restored.
...
DotnetToolSettings.xml was not found in the package
```

you are on a version before 0.2.0, which shipped a `net10.0` asset only ([#13](https://github.com/SebHenn/dotnet-tia/issues/13)). That message names the package rather than the cause, which is that no asset in it matched your runtime. Upgrade, or install the .NET 10 SDK.

The solution must be restored — `tia` bails out to a full run if it finds a project that does not compile, and an unrestored project is the most common cause.

## Commands

| Command | Purpose |
|---|---|
| `dotnet tia analyze --base origin/main [--json]` | Print impacted tests. Runs nothing. The primary CI integration point. |
| `dotnet tia run --base origin/main [-- passthrough]` | Analyse, then invoke `dotnet test` with the generated filters. |
| `dotnet tia explain <TestName>` | Show the graph path from a changed symbol to a test — or say why nothing reaches it. |
| `dotnet tia graph [--output graph.json]` | Build or refresh the cached graph. The CI warming step. |
| `dotnet tia verify --mutate N` | Mutation-based correctness harness. |
| `dotnet tia shadow --base origin/main` | Run the whole suite anyway, and report which failures a selection *would* have skipped. |

`explain` prints the actual path:

```
  Fixtures.Tests.GreeterServiceTests.Welcomes_through_the_interface
    selected - reached from a changed symbol:

      EnglishGreeter.Greet(string)   (changed)
        |  interface member <-> implementation
      IGreeter.Greet(string)
        |  referenced by
      GreeterService.Welcome(string)
        |  referenced by
      GreeterServiceTests.Welcomes_through_the_interface()
```

See [`docs/usage.md`](docs/usage.md) for the full option list and the CI recipe.

## Does it actually pay off?

Only if your suite is slow enough. Analysis costs wall-clock too, so a selective run beats a full one only when `full suite > analysis / (1 - selected fraction)`. `tia` prints that threshold on every run:

```
  Impacted tests        304 of 3,730  (8.2 %)
  Elapsed               8.4s
  Worth it if           the full suite takes more than 9s
```

On NodaTime the full suite is 28.5s and warm analysis is 8.4s, so a selective run costs about 11s against 28.5s. It pays, but not by much: most of the 8.4s is MSBuild evaluating 21 project files, which is a floor no amount of caching removes. **`tia` pays off on suites measured in minutes; on suites measured in seconds the margin is thin enough that you should read the printed threshold rather than trust a general claim.**

Selection ratio itself, measured on FluentValidation (2,460 tests, 12 replayed commits):

| Change | Selected |
|---|---|
| docs only | 0 % |
| one test file | 1.8 % |
| a library change outside the rule engine | 10.6 % |
| the polymorphic core | ~100 % |

Mean selection **51.0 %**, full-run rate **8 %**. Replaying NodaTime's own history gives a mean of **35.5 %** over 20 commits, but the distribution matters more than the mean: nine commits — CI, scripts, docs — select **0 %**, four ordinary library changes select **7-11 %**, and three select 93 % because they replace the embedded time zone database, which is exactly what should happen. The split is not noise, and the cause is not a widening — `explain` traces it to a real path. FluentValidation is a polymorphic rule engine: every validator implements `IPropertyValidator` and one shared engine calls it, so a change to any validator — even a private helper three calls deep — reaches that engine through the interface, and the engine is what every test runs.

That is the limit of type-insensitive static analysis. Knowing that a test using `NotNull()` never dispatches to `EnumValidator` needs type-flow analysis, or the dynamic coverage refinement this design leaves room for. That refinement was spiked and is a **no-go for now** — not because the design is wrong, but because `dotnet test` on Linux cannot produce per-test coverage at a cost worth paying. [`docs/coverage.md`](docs/coverage.md) has the experiments and the three things that would change the answer.

NodaTime tempers that reading: it is far less polymorphic and still impacts 60 % for a calendar change, because calendars underpin most of its types. The duller generalisation fits both: **selection tracks how central the changed code is, and a library's core is central by construction.** Expect a large win on changes outside the core, and little on changes inside it.

[`docs/benchmarks.md`](docs/benchmarks.md) has the full table, the `explain` output that pins the cause, and the assumptions the measurement killed.

## Correctness

A tool that skips a test which would have failed is worse than no tool, so the safety model has three tiers and the merge gate is a mutation harness.

**Full-run triggers** — bail out, run everything, say why: project files, `Directory.Build.*`, `Directory.Packages.props`, `global.json`, `nuget.config`, `.editorconfig`, lockfiles; any workspace load failure or compilation error; a base commit that cannot be reached (a shallow clone); any unhandled exception.

**Widening triggers** — expand scope, don't bail:

- **Reflection** makes the reflecting member unconditionally impacted — *every* one in the solution, whether or not anything reaches it, because a reflecting member is dangerous precisely when nothing does. That is the strongest sound statement available about code that reaches things by name at runtime, and the graph then scopes it correctly: a reflecting test selects itself, a reflecting registry selects everything that reaches it. **Serializers count**: `XmlSerializer` and friends walk a type's members and call them, including interface implementations, with nothing in the caller naming any of it.
- **Source generators** are re-run over both revisions, and only the generated documents whose text actually differs count as changed. Where they cannot be reproduced — a diff touching files a generator may read, an analyzer that will not load — every generated document counts as changed, and where the generated documents are not in the analysed compilation at all, the project widens instead.
- **Non-`.cs` content files** widen their owning project and its dependents.
- **`const` fields and enum members** widen the referencing projects, because callers inline the value at compile time and carry no reference to follow. A constant in a newly added file does not: nothing could have inlined it.

Every widening and every bail-out is printed and included in `--json`. Silent conservatism is indistinguishable from a bug.

**The gate.** `dotnet tia verify --mutate N` injects a Stryker-style mutation, selects against it, then runs the **full** suite. Any test that fails but was not selected is a miss. Zero misses, or it doesn't merge. A sample whose outcome cannot be read is reported as inconclusive rather than as a pass.

```
  26 usable sample(s), 14 skipped, 0 miss(es)
  PASS - no failing test was left out of a selection.
```

Run against both fixture solutions — including the source-generated TUnit one — that is 66 usable samples and zero misses.

More usefully, it runs against **NodaTime**: 3,730 tests, 21 projects, NUnit on VSTest, multi-targeted. 20 usable samples, **zero misses** — but only after five rounds, because pointing the gate at a real repository for the first time found two defects in the gate and three in the engine, none of them reachable by any unit test. `docs/benchmarks.md` has the whole account; the short version is that a static call graph misses what the *compiler* generates and what the *runtime* dispatches:

| Missed | Because |
|---|---|
| every XML round-trip test | a serializer calls `IXmlSerializable.ReadXml`; nothing in the caller names it |
| every `ToString` test downstream of a pattern | `$"{instant}"` binds to the interpolation handler, not to `Instant.ToString` |
| the XML schema tests | reading a static property runs a type initializer the source never calls |

Each is now an exact edge or an explicit widening rather than a hole — and **FluentValidation then passed on its first run**, 16 usable samples and zero misses, on a repository shaped nothing like NodaTime: a polymorphic rule engine with a source generator, xUnit on the testing platform rather than NUnit on VSTest. That is the result that says those three were classes rather than one repository's quirks.

## Supported frameworks

Framework and runner are detected per project from referenced assemblies plus the `TestingPlatformDotnetTestSupport` / `UseMicrosoftTestingPlatformRunner` / `EnableMSTestRunner` properties and `global.json`'s test runner setting.

| Framework | Runner | Filter dialect |
|---|---|---|
| xUnit v2 | VSTest | `--filter "FullyQualifiedName~…"` |
| xUnit v3 | MTP native | `--filter-method` (repeatable) |
| xUnit v3 | VSTest bridge | VSTest syntax |
| NUnit | either | VSTest syntax |
| MSTest | either | VSTest syntax |
| TUnit | MTP | `--treenode-filter` |

All three dialects are verified end to end against real runners: the fixture solutions assert both the emitted arguments and that `dotnet test` then runs exactly the intended tests. Asserting the arguments alone is not enough — the xUnit v3 dialect once emitted an entirely reasonable-looking filter that selected **nothing**, and only executing it found that out.

MSTest is the one row without its own fixture project. It shares the VSTest dialect with NUnit, which *is* executed end to end, so what is verified separately is attribute discovery: `[TestMethod]`, `[DataTestMethod]` with `[DataRow]`, and lifecycle methods reaching every test in their class.

`global.json`'s `test.runner` is a third, repository-wide axis and not the same question as which runner executes the tests. Opting into the platform-native `dotnet test` moves the project onto `--project` and drops the `--` separator before runner arguments, so `tia` detects it and emits the right shape.

Parameterised tests (`[Theory]`, `[TestCase]`, `[DataRow]`, `[TestCaseSource]`, `[Arguments]`) are selected whole. Sub-case selection is not reliably expressible in any of these dialects, and guessing is how you get a miss.

A class whose tests are all selected collapses to one filter clause, and the length limit is platform-aware — the 32k cap is a Windows constraint, not a universal one. If a filter still would not fit, or the selection already covers most of the project, it is dropped and the project runs whole: always safe, usually faster. `--json` reports `impactedTests` and `selectedTests` separately so the engine's precision is not confused with what a dropped filter causes to run.

## Caching

`.tia/graph-<key>.bin` holds one fragment per project, keyed on a hash of the project file, every source, additional and `.editorconfig` file, and the resolved references. On a rerun only projects whose key changed are rebuilt.

A project's fragment is a function of its own content plus the *declarations* of everything it references — its edges come from binding its syntax against those declarations, and nothing in it depends on a dependency's method bodies. So dependencies contribute their **declaration surface**, hashed separately: every externally reachable type, base type, interface, member signature, modifier and constant value. A rename, a changed signature or a new base type moves it; a body-only edit, or a new private helper, does not. Folding whole dependency fingerprints in instead was correct and useless — a core library changes on most commits, so a one-line edit invalidated 18 of NodaTime's 21 projects and the cache saved nothing in exactly the case it exists for.

The reuse decision is made from file content alone, before any project is parsed, so a project whose fragment still stands is never compiled at all.

Cache the `.tia` directory against the base branch in CI and add a `dotnet tia graph` warming step; the sample workflow in [`.github/workflows/ci.yml`](.github/workflows/ci.yml) does both.

## What this does not do yet

Being honest about the edges, because over-claiming is what discredits these tools:

- **Cache granularity is per project, not per document** — and, measured, that is the right place for it. A one-line change rebuilds that project's whole fragment. Splitting the fragment per document could not save parsing (Roslyn parses every document to bind one of them), only the semantic walk of unchanged trees: **2.7 s of CPU** on this repository's warm one-project change. Reusing a document's edges is sound only while no *other* document's declarations moved — a new private overload changes what an untouched call resolves to — so the key has to be a declaration surface including private members, strictly larger than the public one that already costs **3.0 s** to hash. The key costs more than the reuse saves. Written up in [`docs/benchmarks.md`](docs/benchmarks.md), including what would change the answer.
- **A change deep inside a layered hierarchy still reaches broadly** — and type-awareness is now measured not to be the fix. An upward hop is bounded by what can reach the implementing type; `--type-flow` sharpens that bound to what can *obtain an instance* of it, which found no miss on any gate that could be run and changed the selection on neither FluentValidation nor NodaTime. It is off by default and stays that way. The reason is structural: a change two hops down is bounded on the abstract base the whole hierarchy derives from, and everything holding any subclass holds one of those. Two attempts, one unsound and one inert, are written up in [`docs/benchmarks.md`](docs/benchmarks.md). What would settle it is a record of what actually ran — dynamic coverage, spiked and declined in [`docs/coverage.md`](docs/coverage.md).
- **Only FluentValidation has been replayed over its history.** NodaTime was measured on targeted changes; Polly pins an SDK feature band that could not be installed here. Two repositories, both libraries, is not a benchmark suite.
- **Wall-clock is measured on one repository only.** `tia` prints its own break-even on every run — a selective run takes `A + fT` against a full run's `T`, so it wins only when `T > A / (1 - f)` — and [`docs/benchmarks.md`](docs/benchmarks.md) publishes NodaTime's figures. One repository's numbers are not a general claim about yours, which is why the tool prints the arithmetic rather than a verdict.
- **The TUnit dialect still emits a segment cross-product** when a selection spans several classes *and* any of them is only partly selected. Classes selected whole now collapse to `/*/ns/(A|B)/*`, which matches exactly the same tests in a fraction of the characters — and length is what decides whether a filter survives the command line rather than being dropped for a whole-project run. The cross-product cannot be removed outright: the tree-node grammar alternates within a path segment rather than across whole paths, `--treenode-filter` is not repeatable (passing it twice selects **nothing**), and a union of two paths written with `|` silently parses as an alternation inside the first path's method segment — a *subset*, which is the one wrong answer that costs a missed test. All three were established by running the real runner, not by reading the grammar. What remains is a superset, never a subset, and the extra matches are reported as a widening.
- **The mutation harness needs a TRX-capable runner** — `Microsoft.NET.Test.Sdk` for VSTest, `Microsoft.Testing.Extensions.TrxReport` for Microsoft.Testing.Platform. It now finds that out from a single baseline run before the first mutation and refuses, naming the missing package per project, instead of spending every sample to report inconclusive one at a time. Where TRX is genuinely unavailable, `--project-granularity` opts into a weaker gate that reads each project's exit code instead. It can prove a miss — a project that failed, none of whose tests were selected, was definitely missed — and it can never prove the absence of one, so it reports `PROJECT-GRANULARITY GATE` and never `PASS`. That distinction is the whole point of the flag: a clean-looking verdict on a run that could not see individual outcomes is exactly what makes a correctness gate worthless.
- **MSBuild properties are evaluated for test projects only.** A test project's runner properties are now read as MSBuild computes them, so a condition, a property function or an SDK-supplied value is seen — and a property inside a false condition is no longer reported as set, which the old XML read did. Evaluation is a second full pass over the project, measured at 1.79 s against this repository's seven projects versus 0.30 s for the test projects alone, so everything else still takes the literal read. That only decides `IsTestProject` for a project referencing no recognised test framework, and that is a literal wherever it is set at all. A project evaluation cannot open — an uninstalled workload, an unresolvable SDK — falls back to the literal read; `--json` reports `propertySource` per project so you can tell which answered.
- **HTTP route dispatch is handled, with one case that widens instead.** A functional test that calls `/contributors` names a route string and a response DTO, never the endpoint class — and that used to select **zero** tests. Route templates are now collected positionally (a `Map*` call's route argument, a `[Route]`/`[Http*]` attribute), normalised with parameter segments wildcarded and `MapGroup` prefixes combined, and joined to the members naming a matching path. Guarded like the mediator edge: followed only when nothing in the solution names the endpoint's type, so an endpoint registered by naming its handler gains nothing. On [`tests/Tia.Fixtures.Web`](tests/Tia.Fixtures.Web) a one-line endpoint change went from 0 of 4 tests to exactly the 1 that exercises it, and the fixture gates at zero misses.

  **The case that cannot be traced:** a change to a route template itself. The graph is built from the new source, so the endpoint's new route no longer matches the old path its callers still name — the edge disappears exactly when it is needed. That is a by-value binding, like `const` inlining, and it gets the same answer: a diff touching a route declaration widens that project with a stated reason. Scoped to the changed lines, so editing an endpoint body still selects precisely. Both behaviours are measured in [`docs/benchmarks.md`](docs/benchmarks.md).

  **`dotnet tia shadow` remains the answer for anyone unsure whether their dispatch is visible.** It selects, runs the whole suite anyway, and reports which failures the selection *would* have skipped — so a repository whose dispatch this engine cannot see finds that out from its own history rather than from a claim made about somebody else's. It costs one analysis on top of a suite that was going to run in full regardless. See [`docs/usage.md`](docs/usage.md#shadow-mode).
- **Only C# is analysed**, but a non-C# project no longer disappears. It contributes no symbols, so nothing can be traced *through* it; instead a change to one widens that project and, by ordinary dependent expansion, everything referencing it. A project type the workspace cannot load at all — an `.esproj`, a shared project — is not even listed, so a changed file inside one forces a full run with that named as the reason. Both are supersets, reported as widenings. Before this they were neither: the file found no owning project, widened nothing, and a C# test project exercising an F# library selected **zero** tests for a change to that library.

## Repository layout

```
src/
  Tia.Core/         impact engine: diff, graph, selection, safety, cache. No MSBuild, no workspaces.
  Tia.Workspace/    MSBuildWorkspace loading, the analysis pipeline, the validation harnesses
  Tia.Frameworks/   test discovery and the filter dialects
  Tia.Cli/          System.CommandLine host, `dotnet tia`
tests/
  Tia.Core.Tests/        engine unit tests over in-memory compilations
  Tia.Integration.Tests/ end-to-end selection over the fixture solution, real git and real MSBuild
  Tia.Fixtures/          xUnit v3 on MTP and NUnit on the VSTest bridge, plus the hard cases
  Tia.Fixtures.Tunit/    TUnit, on a repository opted into the platform-native `dotnet test`
  Tia.Validation/        nightly mutation and commit-replay drivers
```

`Tia.Core` references only `Microsoft.CodeAnalysis.CSharp` — no MSBuild, no Roslyn workspaces — so the engine is testable against `CSharpCompilation.Create` with no SDK resolution in unit tests.

The design rationale lives in [`docs/plan.md`](docs/plan.md), the release procedure in
[`docs/releasing.md`](docs/releasing.md), and the repository settings in
[`docs/maintaining.md`](docs/maintaining.md).

## Contributing

Contributions are welcome — [`CONTRIBUTING.md`](CONTRIBUTING.md) has the setup, the layout and what a
change needs before it merges.

The single most valuable thing you can send is a **missed test**: a case where `tia` selected a set
of tests and a test outside that set would have failed. That is the bug class this project cares
about above all others, and it is the one that cannot be found from the inside — the engine has been
gated against two repositories, and the three real defects found so far all came from pointing the
harnesses at code the author did not write.

`dotnet tia shadow --base origin/main` turns a suspicion into evidence: it runs the whole suite
anyway and reports which failures a selection *would* have skipped, so nothing is at risk while it
gathers the answer.

Please report vulnerabilities privately — [`SECURITY.md`](SECURITY.md) has the channel and the threat
model, including the two properties that look like vulnerabilities and are not. Release history is in
[`CHANGELOG.md`](CHANGELOG.md), and everyone participating is expected to follow the
[Code of Conduct](CODE_OF_CONDUCT.md).

## License

MIT — see [`LICENSE`](LICENSE).
