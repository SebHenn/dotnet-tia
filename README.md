# dotnet tia

Test impact analysis for .NET. A `dotnet` global tool that takes a git diff and runs **only the tests that diff can actually affect**.

```
$ dotnet tia run --base origin/main

  Base                  origin/main (73be09d03)
  Diff                  1 file (1 C#), 1 symbol changed
  Graph                 97 types / 747 members / 2,296 edges  (7 projects built)
  Impacted tests        14 of 74  (18.9 %)

  Projects
    Tia.Core.Tests                        3 / 63      filtered (XUnitV3/MicrosoftTestingPlatform)
    Tia.Integration.Tests                11 / 11      unfiltered - selection covers 100% of the project

  Elapsed               7.5s

  > dotnet test tests/Tia.Core.Tests/Tia.Core.Tests.csproj -- --filter-method Tia.Core.Tests.GitDiffParserTests.Hunks_reads_both_sides ...
```

That output is real: it is `tia` selecting against a one-line change to its own `GitDiffParser.ParseHunks`. Three of the 63 engine tests exercise that method, and the tool picks exactly those three.

## Why

Per-test impact analysis for .NET currently exists only behind a paywall or a SaaS — Datadog Test Optimization, Tricentis SeaLights, NCrunch ($159/yr, IDE-only), or Azure DevOps' legacy VSTest-only collector. The free OSS options ([`dotnet-affected`](https://github.com/leonardochaia/dotnet-affected), Incrementalist) resolve impact at **project** granularity, so changing one file in a core library still runs every downstream test project in full.

Meanwhile .NET 10's Microsoft.Testing.Platform replaced VSTest's assembly-scanning plugins with first-class extension APIs, and xUnit v3, NUnit, MSTest and TUnit have all shipped MTP runners. The extension surface exists and nobody has built free test-level selection on it.

## How

Fully static — no instrumentation, no profiler, no prior coverage run:

1. Resolve the git diff to changed files and line ranges
2. Load the solution via `MSBuildWorkspace`
3. Map changed lines to changed symbols, including deletions via the base revision's tree
4. Walk a reverse reference graph — callee→caller, interface↔implementation, base↔override, fixture→test
5. BFS to reach test methods
6. Emit per-project filters and invoke `dotnet test`

Interface edges mean dependency injection needs no special handling: a test calling `IFoo.Bar()` is already connected to `Foo.Bar()`.

Blind spots that static analysis genuinely has — reflection, source generators, `const` inlining, non-`.cs` test data — get explicit widening or full-run rules, and every one of them is reported rather than applied silently.

## Install

```
dotnet tool install -g dotnet-tia
```

Or from a clone:

```
dotnet pack src/Tia.Cli -c Release
dotnet tool install -g --add-source artifacts/nupkg dotnet-tia
```

Requires the .NET 10 SDK. The solution must be restored — `tia` bails out to a full run if it finds a project that does not compile, and an unrestored project is the most common cause.

## Commands

| Command | Purpose |
|---|---|
| `dotnet tia analyze --base origin/main [--json]` | Print impacted tests. Runs nothing. The primary CI integration point. |
| `dotnet tia run --base origin/main [-- passthrough]` | Analyse, then invoke `dotnet test` with the generated filters. |
| `dotnet tia explain <TestName>` | Show the graph path from a changed symbol to a test — or say why nothing reaches it. |
| `dotnet tia graph [--output graph.json]` | Build or refresh the cached graph. The CI warming step. |
| `dotnet tia verify --mutate N` | Mutation-based correctness harness. |

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

## Correctness

A tool that skips a test which would have failed is worse than no tool, so the safety model has three tiers and the merge gate is a mutation harness.

**Full-run triggers** — bail out, run everything, say why: project files, `Directory.Build.*`, `Directory.Packages.props`, `global.json`, `nuget.config`, `.editorconfig`, lockfiles; any workspace load failure or compilation error; a base commit that cannot be reached (a shallow clone); any unhandled exception.

**Widening triggers** — expand scope, don't bail: reflection in a changed file or anywhere in the impact set; projects that actually emit source-generated code; non-`.cs` content files; `const` and enum members, which callers inline at compile time.

Every widening and every bail-out is printed and included in `--json`. Silent conservatism is indistinguishable from a bug.

**The gate.** `dotnet tia verify --mutate N` injects a Stryker-style mutation, selects against it, then runs the **full** suite. Any test that fails but was not selected is a miss. Zero misses, or it doesn't merge. A sample whose outcome cannot be read is reported as inconclusive rather than as a pass.

```
  9 usable sample(s), 3 skipped, 0 miss(es)
  PASS - no failing test was left out of a selection.
```

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

Parameterised tests (`[Theory]`, `[TestCase]`, `[DataRow]`, `[TestCaseSource]`, `[Arguments]`) are selected whole. Sub-case selection is not reliably expressible in any of these dialects, and guessing is how you get a miss.

If a project's filter would exceed a safe command-line length, or if the selection already covers most of the project, the filter is dropped and the project runs whole — always safe, usually faster.

## Caching

`.tia/graph-<key>.bin` holds one fragment per project, keyed on a hash of the project file, every source file and the resolved references. On a rerun only projects whose fingerprint changed are rebuilt — and a project is invalidated when *any project it depends on* changed, because its edges point at symbol keys owned by those dependencies.

Cache the `.tia` directory against the base branch in CI and add a `dotnet tia graph` warming step; the sample workflow in [`.github/workflows/ci.yml`](.github/workflows/ci.yml) does both.

## What this does not do yet

Being honest about the edges, because over-claiming is what discredits these tools:

- **Cache granularity is per project, not per document.** A one-line change rebuilds that project's whole fragment and every dependent project's fragment. That is correct but coarser than it could be.
- **The replay benchmark has not been run against the candidate OSS repos** (Polly, NodaTime, FluentValidation, TUnit). The harness exists — `tests/Tia.Validation` — but there are no published selection-ratio numbers yet, so this README does not print any.
- **The TUnit dialect emits a segment cross-product** when a selection spans several classes, because the tree-node grammar alternates within a path segment rather than across whole paths. That is a superset, never a subset, and the extra matches are reported as a widening.
- **The mutation harness needs a TRX-capable runner** — `Microsoft.NET.Test.Sdk` for VSTest, `Microsoft.Testing.Extensions.TrxReport` for Microsoft.Testing.Platform. Without one it reports inconclusive rather than passing.
- **MSBuild property detection reads project XML directly** rather than evaluating it, so a runner property set through a condition or a property function is not seen. The referenced-assembly signal covers the common cases.
- **Only C# is analysed.** F# and VB projects load but contribute no symbols.

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
  Tia.Fixtures/          a small solution exercising the hard cases
  Tia.Validation/        nightly mutation and commit-replay drivers
```

`Tia.Core` references only `Microsoft.CodeAnalysis.CSharp` — no MSBuild, no Roslyn workspaces — so the engine is testable against `CSharpCompilation.Create` with no SDK resolution in unit tests.

The design rationale lives in [`docs/plan.md`](docs/plan.md).

## License

MIT.
