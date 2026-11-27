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

## Quick start

```
dotnet tool install -g dotnet-tia
dotnet restore                       # every job; an unrestored solution is a full run
dotnet tia replay --commits 50       # what selection would have done on your last 50 commits
dotnet tia shadow --base origin/main # select, run everything anyway, report anything it would have skipped
dotnet tia run --base origin/main    # run only what the diff can affect
dotnet tia init                      # write .github/workflows/tia.yml for pull requests
```

`replay` and `shadow` change nothing you rely on, so run them first and decide from your own history.
Installing, the GitHub Action and the full command list are [below](#install); the reference is
[`docs/usage.md`](docs/usage.md).

## Is it worth it for your repository?

**Rule of thumb: `tia` pays off when your test suite takes more than about ten seconds.** A
selective run costs `A + fT` against a full run's `T`, so it wins only when `T > A / (1 - f)`. Warm
analysis is around three seconds, which puts the break-even near four seconds for a diff that
selects a fifth of the suite — but a cold graph costs more, and the first run on a large solution
costs a great deal more, so ten is the honest threshold to plan around.

This used to say "about a minute", which was wrong by an order of magnitude and wrong in the
direction that costs you nothing to believe and everything to act on: it told repositories that
would have gained to stay away. Two things were mismeasured. Analysis was slower than it needed to
be, and a suite's cost was being compared against the time its tests *execute* — which on a fast
suite is the smaller half, because restore, build checks, host startup and result reporting are the
rest. Compare against the command you actually run. `docs/benchmarks.md` has the repository where
that mistake was found, and the arithmetic that reversed it.

**You do not have to take any of that on trust either.** `dotnet tia replay --commits 50` walks your
own history and reports what selection would have done on each of your last fifty commits — mean
selection, full-run rate, and how often it had to widen. It needs a clean working tree, checks out
each commit, and puts you back where you started.

That command exists because of this section. The claim that started the performance work above —
that a particular application could never benefit — went unchallenged for months and was wrong in
*both* of its terms. It went unchallenged because checking it meant cloning this repository. Now it
means running one command against yours.

And every run prints the threshold for your repository rather than a verdict about somebody else's:

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
  HTTP routes, which is [handled but with a documented edge](docs/limitations.md).

## Why

Per-test impact analysis for .NET currently exists only behind a paywall or a SaaS — Datadog Test Optimization, Tricentis SeaLights, NCrunch ($159/yr, IDE-only), or Azure DevOps' legacy VSTest-only collector. The free OSS options mostly resolve impact at **project** granularity ([`dotnet-affected`](https://github.com/leonardochaia/dotnet-affected), Incrementalist), so changing one file in a core library still runs every downstream test project in full. [`opentia`](https://pypi.org/project/opentia/) goes finer with a symbol strategy, but it is a text search: it takes up to 20 type names declared in a changed file and matches them as words in test-file text. Nothing is bound, so a test that reaches a changed type only through an interface or base type does not contain its name, and nothing measures what it misses. `tia` binds a semantic graph with Roslyn and checks it with a mutation gate that must report zero misses.

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

Blind spots that static analysis genuinely has — reflection, source generators, `const` inlining, top-level statements and assembly attributes, non-`.cs` test data — get explicit widening or full-run rules, and every one of them is reported rather than applied silently.

## Install

```
dotnet tool install -g dotnet-tia
```

Or as a GitHub Action, which installs the tool, selects, runs, and writes a summary to the job page
([`docs/usage.md`](docs/usage.md#ci)):

```yaml
- uses: SebHenn/dotnet-tia@v0.11.0
```

`dotnet tia init` writes that workflow for you, as `.github/workflows/tia.yml`. It sets up a
full clone, a restore before the analysis, a runtime for every framework your projects target,
and the action pinned to the version you ran.

Or from a clone, which is also how you would test a change before releasing it:

```
dotnet pack src/Tia.Cli -c Release
dotnet tool install -g --add-source artifacts/nupkg dotnet-tia
```

The tool installs on the .NET 9 SDK and later. What it can *analyse* is a narrower question, and worth separating: `tia` reads projects through MSBuild, so it can only load projects the SDK it finds is able to load. Point a .NET 9 SDK at a `net10.0` project and the run degrades to a full one with the reason named — the registered MSBuild version, the project's target framework, and that the two do not match — rather than failing obscurely. To analyse this repository, or any other `net10.0` one, install the .NET 10 SDK. The tool runs on the newest .NET runtime installed, so an SDK newer than the tool's own target is one it can load.

If the install itself fails with:

```
The tool package could not be restored.
...
DotnetToolSettings.xml was not found in the package
```

you are on a version before 0.2.0, which shipped a `net10.0` asset only ([#13](https://github.com/SebHenn/dotnet-tia/issues/13)). That message names the package rather than the cause, which is that no asset in it matched your runtime. Upgrade, or install the .NET 10 SDK.

**The solution must be restored, in every job.** `MSBuildWorkspace` reads `obj/project.assets.json` to
resolve a `PackageReference`, so on a fresh runner your test framework's `[Fact]` binds to an error
type — no method is a test, and the graph is empty of the thing selection exists for. `analyze`,
`graph`, `run` and `watch --once` refuse and exit **2** rather than reporting a suite that vanished
as a count of zero ([#20](https://github.com/SebHenn/dotnet-tia/issues/20)). Run `dotnet restore` before any `tia`
command.

**Two solutions of one kind in `--path` need `--solution`.** A `.slnx` is preferred over a `.sln`,
but between two `.sln` files `tia` will not guess: it names them and exits **2**
([#37](https://github.com/SebHenn/dotnet-tia/issues/37)). The report's `Solution` line says which
one was analysed.

## Commands

| Command | Purpose |
|---|---|
| `dotnet tia analyze --base origin/main [--json]` | Print impacted tests. Runs nothing. The primary CI integration point. |
| `dotnet tia run --base origin/main [-- passthrough]` | Analyse *while* building, then invoke `dotnet test --no-build` with the generated filters. |
| `dotnet tia explain <TestName>` | Show the graph path from a changed symbol to a test — or say why nothing reaches it. |
| `dotnet tia explain --symbol <Name>` | Show which tests reach a symbol, or the nearest code a test does reach when none does. |
| `dotnet tia graph [--output graph.json]` | Build or refresh the cached graph. The CI warming step. |
| `dotnet tia verify --mutate N` | Mutation-based correctness harness. |
| `dotnet tia shadow --base origin/main` | Run the whole suite anyway, and report which failures a selection *would* have skipped. |
| `dotnet tia replay --commits 50` | Replay your own history and report what selection would have done on each commit. |
| `dotnet tia watch [--run]` | Keep the workspace loaded and re-analyse on every edit. 2.4 s per edit against 9.1 s for a fresh process. |
| `dotnet tia stats` | What selection has actually cost or saved here, from runs that happened. |
| `dotnet tia init` | Write a GitHub Actions workflow that runs the selected tests on every pull request. |
| `dotnet tia hook install` | Run the selected tests before every push (or `--pre-commit`), against the branch's upstream. |
| `dotnet tia server start` | Keep the workspace loaded in the background, so `analyze` and `run` skip the load: 5.2 s to 0.85 s warm here. |

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

A selective run also names the changed code that no test reaches, which is a review comment the
graph can write for free. `--fail-on-untested` turns it into a gate:

```
  Untested changes - no test reaches these
    ? OrderService.Refund(Order)  (src/App/OrderService.cs)
```

A `tia.json` at the repository root tells `tia` what the graph cannot see: tests to run every
time, paths that run everything, paths that select nothing, and defaults for the flags every CI job
would otherwise repeat. Changing it runs everything, and every file it ignores is reported:

```json
{
  "alwaysRun": ["Web.Tests.SmokeTests"],
  "fullRunPaths": ["db/**", "docker-compose*.yml"],
  "ignorePaths": ["docs/**"],
  "defaults": { "solution": "App.slnx", "defaultBranch": "main" }
}
```

See [`docs/usage.md`](docs/usage.md) for the full option list, [`tia.json`](docs/usage.md#tiajson)
and the CI recipe.

## Does it actually pay off?

Only if your suite is slow enough. Analysis costs wall-clock too, so a selective run beats a full one only when `full suite > analysis / (1 - selected fraction)`. `tia` prints that threshold on every run:

```
  Impacted tests        304 of 3,730  (8.2 %)
  Elapsed               8.4s
  Worth it if           the full suite takes more than 9s
```

On NodaTime the full suite is 28.5s and warm analysis is 8.4s, so a selective run costs about 11s against 28.5s. It pays, but not by much: most of the 8.4s is MSBuild evaluating 21 project files, which is a floor no amount of caching removes. **On suites measured in seconds the margin is thin enough that you should read the printed threshold rather than trust a general claim — but thin is not the same as absent: a 6.4s suite measured on cartographer clears a 4.0s break-even and saves 29% per run.**

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

**Full-run triggers** — bail out, run everything, say why: project files, `Directory.Build.*`, `Directory.Packages.props`, `global.json`, `nuget.config`, `.editorconfig`, lockfiles, and test or analyzer configuration (`*.runsettings`, `testconfig.json`, `xunit.runner.json`, `*.globalconfig`); any workspace load failure or compilation error; a base commit that cannot be reached (a shallow clone); any unhandled exception. A `Directory.Packages.props` or `.csproj` that moved nothing but package versions is the one exception, and widens instead.

**Widening triggers** — expand scope, don't bail:

- **Reflection** makes the reflecting member unconditionally impacted — *every* one in the solution, whether or not anything reaches it, because a reflecting member is dangerous precisely when nothing does. That is the strongest sound statement available about code that reaches things by name at runtime, and the graph then scopes it correctly: a reflecting test selects itself, a reflecting registry selects everything that reaches it. **Serializers count**: `XmlSerializer` and friends walk a type's members and call them, including interface implementations, with nothing in the caller naming any of it. Serializing without a direct call counts too: a type handed to an ASP.NET result, a controller action, a minimal-API handler or `System.Net.Http.Json` leads every property and constructor a serializer would use to that place.
- **Source generators** are re-run over both revisions, with the project's additional files and build properties as the build hands them over, and only the generated documents whose text actually differs count as changed. A document is compared only when the re-run reproduces the build's own text for it. Where they cannot be reproduced — a diff touching files a generator may read, an analyzer that will not load — every generated document counts as changed, and where the generated documents are not in the analysed compilation at all, the project widens instead. So does a project with a generator that did not load at all, such as one built against a newer Roslyn than this tool carries, whenever the diff reaches it: what that generator would emit is not in the graph. The Razor generator in SDK 10.0.403 needs Roslyn 5.9, which this tool carries when it runs on .NET 10; a later SDK that needs a newer one widens such a project until the tool catches up, and the reason says so.
- **Non-`.cs` content files** widen their owning project and its dependents. A project owns a file in its folder, and also one it includes from elsewhere: an additional file, a referenced assembly, an embedded resource or a file copied to its output. A `.cs` file that no project compiles, such as an analyzer test's input or an embedded sample, counts as content too. The exception is a [Verify](https://github.com/VerifyTests/Verify) snapshot, `Class.Method.verified.txt`: it selects the test its name denotes, and widens as above whenever its name cannot be trusted - the project does not reference Verify, any source file in the solution mentions `UseFileName`, `UseDirectory`, `UseTypeName`, `UseMethodName`, `DerivePathInfo`, `VerifyFile`, `VerifyDirectory` or `VerifyZip`, a source file spells the snapshot's file name, another project links it in, or no method of a type of that name matches. The other is a Reqnroll or SpecFlow `.feature` file with an xUnit-generated class: it selects that class, when the class still has a method for exactly the scenarios the file names (an added, renamed or removed scenario, a changed title or a `# language:` comment widens), no source in the solution mentions `.feature`, `"feature"` or manifest resources, nothing in the project file or a `Directory.Build.props`/`.targets` says "feature", and no other project links it in. NUnit and MSTest output is not recognised and widens.
- **A package version bump** in `Directory.Packages.props` widens each project whose restored closure holds that package, read from `obj/project.assets.json`, and everything referencing them. One in a `.csproj` widens that project and everything referencing it. Anything else in the same file is still a full run: a condition, an import, a property, an added or removed entry, a `GlobalPackageReference`. So is a project whose assets file cannot be read or records a failed restore, which includes a repository that relocates `obj/`, and so is a change to `packages.lock.json`.
- **A file no project owns** selects what reaches any C# string naming it, as a changed line there would. `Path.Combine(root, "db", "schema.sql")` and `$"{root}/db/schema.sql"` both name `db/schema.sql`. A file nothing names selects nothing, and the report says so: that is what a README edit should do, and also what a path built at runtime gets, so treat that note as the edge of what this can see.
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

MSTest has two fixture projects of its own, one on VSTest and one on MSTest's own runner under the platform's `dotnet test`, and both are selected from and executed end to end like the others, including `[TestInitialize]`, `[ClassInitialize]`, `[DataRow]` and a filter passed in a file.

`global.json`'s `test.runner` is a third, repository-wide axis and not the same question as which runner executes the tests. Opting into the platform-native `dotnet test` moves the project onto `--project` and drops the `--` separator before runner arguments, so `tia` detects it and emits the right shape.

Parameterised tests (`[Theory]`, `[TestCase]`, `[DataRow]`, `[TestCaseSource]`, `[Arguments]`) are selected whole. Sub-case selection is not reliably expressible in any of these dialects, and guessing is how you get a miss.

A class whose tests are all selected collapses to one filter clause, and the length limit is platform-aware — the 32k cap is a Windows constraint, not a universal one. If a filter still would not fit, or the selection already covers most of the project, it is dropped and the project runs whole: always safe, usually faster. `--json` reports `impactedTests` and `selectedTests` separately so the engine's precision is not confused with what a dropped filter causes to run.

## Caching

`.tia/graph-<key>.bin` holds one fragment per project, keyed on a hash of the project file, every source, additional and `.editorconfig` file, and the resolved references. On a rerun only projects whose key changed are rebuilt.

A project's fragment is a function of its own content plus the *declarations* of everything it references — its edges come from binding its syntax against those declarations, and nothing in it depends on a dependency's method bodies. So dependencies contribute their **declaration surface**, hashed separately: every externally reachable type, base type, interface, member signature, modifier and constant value. A rename, a changed signature or a new base type moves it; a body-only edit, or a new private helper, does not. Folding whole dependency fingerprints in instead was correct and useless — a core library changes on most commits, so a one-line edit invalidated 18 of NodaTime's 21 projects and the cache saved nothing in exactly the case it exists for.

The reuse decision is made from file content alone, before any project is parsed, so a project whose fragment still stands is never compiled at all.

Cache the `.tia` directory against the base branch in CI and add a `dotnet tia graph` warming step. Restore from the base branch only if nothing that runs there executes code you have not reviewed: a pull request restores what any job on the base branch saved, under any key, and a planted graph selects too few tests. This repository's own [`.github/workflows/ci.yml`](.github/workflows/ci.yml) builds other people's repositories on `main`, so its dogfood job keeps no cache and only warms the graph.

Never commit the cache. A graph file that git tracks is neither read nor written: it came from someone else's build, and the diff never shows `.tia/` changing. `tia` says so and rebuilds. When `tia` creates the cache directory, it writes a `.gitignore` containing `*` into it.

## What this does not do yet

Being honest about the edges, because over-claiming is what discredits these tools. The full list,
with what each edge does to a selection and how it is reported, is in
[`docs/limitations.md`](docs/limitations.md). Every one of them either widens the selection or
runs everything, and says so in the report; none is silent.

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
