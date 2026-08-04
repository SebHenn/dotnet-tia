# `dotnet tia` — Test Impact Analysis for .NET

## Context

**The problem.** On any nontrivial .NET solution, CI runs the entire test suite for every push, even when the diff touches one file. Most of that work is provably irrelevant, and teams pay for it in CI minutes and in feedback latency.

**Why this is worth building.** Per-test impact analysis exists for .NET, but only behind a paywall or a SaaS:

| Tool | Cost / model | Limitation |
|---|---|---|
| Datadog Test Optimization | SaaS, paid | Requires shipping test data to Datadog |
| Tricentis SeaLights | Enterprise, paid | Heavyweight platform |
| NCrunch | $159/yr per seat | IDE inner loop only — not a CI tool |
| Azure DevOps TIA | Free (ADO only) | Legacy VSTest collector, Windows-only, doesn't cover the modern `dotnet test` / MTP world |

The free OSS options — [`dotnet-affected`](https://github.com/leonardochaia/dotnet-affected) (394★) and Petabridge's Incrementalist — resolve impact at **project granularity**. Change one file in a core library and every downstream test project still runs in full. Nobody has built test-level selection as free tooling.

**Why now.** .NET 10's Microsoft.Testing.Platform replaced VSTest's assembly-scanning plugin model with first-class extension APIs for discovery, execution and reporting. All of xUnit v3, NUnit, MSTest and TUnit have shipped MTP runners. The extension surface exists and is unoccupied.

**Intended outcome.** A `dotnet tool install -g` CLI, MIT-licensed on GitHub and published to NuGet, that takes a git diff and runs only the affected tests — with a validation harness that proves it never misses a failing test.

**Decisions already made:** CLI/dotnet-tool first (HTML/IDE later, if ever) · open source, portfolio-oriented · target xUnit v2+v3, NUnit, MSTest and TUnit · validate against real OSS repos, not just synthetic fixtures.

---

## Approach: static Roslyn symbol graph

v1 is **fully static** — no instrumentation, no profiler, no coverage run required. Build a reverse reference graph over the solution at member granularity, map the diff to changed symbols, then BFS toward test methods.

```
git diff ──► changed files + line ranges
                    │
MSBuildWorkspace ──► Compilation per project
                    │
                    ▼
       changed line ranges → changed symbols
                    │
   reverse reference graph (callee → caller,
   interface ↔ impl, base ↔ override)
                    │
                    ▼
          BFS ──► impacted test methods
                    │
                    ▼
     per-project filter strings → dotnet test
```

Chosen over dynamic coverage-based selection because it needs no prior instrumented run, works on a cold clone, has no runtime overhead, and is deterministic. Its blind spots (reflection, DI, source generators) are handled by explicit widening rules below rather than pretended away. Dynamic coverage refinement is a post-v1 option and the architecture leaves room for it.

---

## Repository layout

```
tia/
  src/
    Tia.Core/            impact engine: graph, diff model, selection. No MSBuild, no CLI.
    Tia.Workspace/       MSBuildWorkspace loading, project graph, symbol extraction
    Tia.Frameworks/      test discovery + filter dialect emitters
    Tia.Cli/             System.CommandLine host, `dotnet tia`
  tests/
    Tia.Core.Tests/      unit tests over the engine (in-memory compilations, no MSBuild)
    Tia.Fixtures/        small multi-project solution exercising hard cases
    Tia.Validation/      mutation harness + commit-replay benchmark
  .github/workflows/
```

`Tia.Core` must not reference MSBuild or Roslyn workspaces — only `Microsoft.CodeAnalysis.CSharp`. That keeps the engine testable against `CSharpCompilation.Create` in-memory compilations, with no SDK resolution in unit tests.

---

## Commands

| Command | Purpose |
|---|---|
| `dotnet tia analyze --base origin/main [--json]` | Print impacted tests. No execution. Primary CI integration point. |
| `dotnet tia run --base origin/main [-- passthrough]` | Analyze, then invoke `dotnet test` with generated filters. |
| `dotnet tia explain <TestFqn>` | Show the graph path from a changed symbol to this test. Trust and debugging. |
| `dotnet tia graph [--output graph.json]` | Build/refresh the cached graph. Warms CI cache. |
| `dotnet tia verify --mutate N` | Correctness harness (see Validation). |

`explain` is not a nice-to-have. The first question any adopter asks is "why did/didn't it pick this test", and being unable to answer is what kills trust in these tools.

---

## Engine design

### 1. Diff resolution

Shell out to `git` (`git diff --name-status` plus `git diff -U0` for line ranges) rather than taking a LibGit2Sharp dependency — no native binaries, no RID-specific packaging for a global tool.

Old-side symbols come from `git show <base>:<path>` parsed into a syntax tree, so **deleted and renamed members** are treated as changed. A selection built only from the new tree silently misses deletions.

### 2. Workspace loading

`MSBuildLocator.RegisterDefaults()` **before** any type referencing MSBuild is JIT-loaded, then `MSBuildWorkspace`. Two known gotchas to handle up front:

- `Microsoft.Build.*` PackageReferences need `ExcludeAssets="runtime"` in `Tia.Workspace.csproj`, or the locator resolves the tool's own copies and fails with a MEF composition error.
- Set `SkipUnrecognizedProjects`, and treat any `WorkspaceDiagnostic` of `Failure` severity as a **full-run trigger** — a project that didn't load is a project whose tests we cannot reason about.

### 3. Changed lines → changed symbols

For each changed file, find member declarations whose `FileLinePositionSpan` intersects a changed line range, and take their declared symbols. Rules that are easy to get wrong and are correctness-critical:

- **`const` fields and `enum` members** are inlined at compile time. Callers carry no reference to them, so a call-graph walk finds nothing. Treat any change to one as a change to every member of the declaring type, and mark referencing *projects* dirty.
- **Partial classes** — union the symbols from every part.
- **Generics** — reduce constructed symbols to `OriginalDefinition` before graph lookup.
- **Attribute changes** map to the annotated symbol, not the attribute class.
- **Base type / interface list changes** mark the whole type.
- **Global usings / `ImplicitUsings`** changes are project-wide.

### 4. Reverse reference graph

Walk every document; for each `SemanticModel` collect referenced symbols from invocations, member access, object creation, identifiers, base type lists and attributes, and emit an edge `referenced → containing member`. Parallelize per project.

Beyond plain call edges, these are what make selection *correct* rather than merely plausible:

- **Interface member ↔ every implementation**, both directions. This is also what makes DI work without special-casing: `services.AddScoped<IFoo, Foo>()` doesn't need parsing, because a test calling `IFoo.Bar()` is already connected to `Foo.Bar()` through the interface edge.
- **Virtual/abstract member ↔ every override**, both directions.
- **Base type → derived types.**
- **Test fixtures**: constructors, `[SetUp]`/`[OneTimeSetUp]`, `IAsyncLifetime`, `IClassFixture<T>` and collection fixtures belong to *every* test in the class.

### 5. Selection

BFS from changed symbols along caller edges. Any test method reached is selected. Parameterized tests (`[Theory]`, `[TestCase]`, `[DataRow]`, `[TestCaseSource]`, `[DynamicData]`) are selected **whole** — sub-case selection is not reliably expressible in filter syntax, and guessing is how you get a miss.

---

## Safety model

This is the part that determines whether the tool is adoptable. A tool that skips a test that would have failed is worse than no tool. Three tiers:

**Full-run triggers** — bail out, run everything, say why:
- Changes to `*.csproj`, `Directory.Build.*`, `Directory.Packages.props`, `global.json`, `nuget.config`, `.editorconfig`, lockfiles
- Any workspace load failure or compilation error
- Base commit unreachable (shallow clone) or not an ancestor of HEAD
- Any unhandled exception — the fallback is on by default; `--no-fallback-full-on-error` turns it off

**Widening triggers** — expand scope, don't bail:
- Reflection in a changed file or anywhere in its impact set: `Activator.CreateInstance`, `Type.GetMethod/GetType/GetProperty`, `Assembly.GetTypes`, `MethodInfo.Invoke`, `Expression.Compile`, `dynamic`
- Projects containing source generators — generated trees have no file on disk, so treat generator-input changes as project-wide
- Non-`.cs` content files (`.json`, `.resx`, `.sql`, `.txt`, embedded resources) — commonly test data; select all tests in the owning project plus dependents

**Reporting.** Every widening and every bail-out is printed and included in `--json`. Silent conservatism is indistinguishable from a bug.

Additionally: always full-run on the default branch and on scheduled builds. Selection is a PR-loop optimization; the mainline must stay honest.

---

## Test framework support

Discovery is a Roslyn attribute scan producing `{ AssemblyPath, Namespace, Class, Method, Framework, IsParameterized }`. Framework and runner are detected per test project from evaluated `PackageReference`s plus the `TestingPlatformDotnetTestSupport` / `UseMicrosoftTestingPlatformRunner` properties and `global.json`'s `test` runner setting.

Filter syntax is genuinely fragmented — this is the main integration cost, and it's why it's isolated in `Tia.Frameworks` behind one `IFilterDialect` interface:

| Framework | Runner | Emitted filter |
|---|---|---|
| xUnit v2 | VSTest | `--filter "FullyQualifiedName~A\|FullyQualifiedName~B"` |
| xUnit v3 | MTP native | `-- --filter-method "Ns.Cls.Method"` (repeatable; **not** VSTest syntax) |
| xUnit v3 | VSTest bridge | VSTest syntax |
| NUnit | either | VSTest syntax |
| MSTest | either | VSTest syntax |
| TUnit | MTP | `-- --treenode-filter "/*/Ns/Cls/Method"` |

NUnit and MSTest share the VSTest dialect, so covering all four frameworks is three dialects, not four.

**Command-line length.** Windows caps around 32k characters. If a project's filter would exceed a safe threshold, or if selection covers most of the project anyway, drop the filter and run that project unfiltered. Always safe, usually faster.

---

## Caching

`.tia/graph-<key>.bin` keyed on a hash of project file contents, all source file hashes and SDK version. On rerun, re-parse only documents whose content hash changed and patch the graph in place. Ship a sample GitHub Actions workflow that caches `.tia/` against the base branch SHA, plus a `dotnet tia graph` warming step.

Cold-graph build on a large solution is the main performance risk. Budget for it: measure early, parallelize per project, and if a full build exceeds ~60s on the benchmark repos, treat that as a design problem rather than a tuning problem.

---

## Validation

Two harnesses in `Tia.Validation`, because they prove different things.

### Primary: mutation-based correctness (proves no misses)

Real commits are almost all green, so replaying history yields very few failing tests to check against. Injected mutations produce unlimited ground truth:

1. Pick a random method in the solution; apply a Stryker-style mutation (`return true` → `false`, `>` → `>=`, drop a statement).
2. Run `tia analyze` against that mutation as the diff.
3. Run the **full** suite.
4. **Any test that fails must be in the selected set.** A failing test outside the selection is a *miss* — the only fatal defect class.

This is a perfect oracle, it scales to thousands of samples, and the pass criterion is unambiguous. **Zero misses is a merge gate.**

### Secondary: real-commit replay (proves it's worth using)

Replay the last N merge commits of a real repo, record selection ratio and wall-clock saved, and emit the markdown table for the README:

```
Commit    Selected  Full   Misses
a3f21c9      41/912  912        0
7b0e4d1     138/912  912        0
...
Mean selection   6.8%      Mean time saved  ~87%
```

**Candidate target repos** (confirm at M6 — criteria: >500 tests, cross-platform `dotnet build`, actively maintained, permissively licensed):
- **Polly** — xUnit, large suite, fast build
- **NodaTime** — NUnit, large deterministic suite; covers the second dialect
- **FluentValidation** — xUnit
- **TUnit's own repo** — the TUnit dialect

Do not use repos that went source-available (AutoMapper, MassTransit, FluentAssertions v8+, ImageSharp) — cloning them into public CI is a licensing headache for a project whose pitch is trustworthy free tooling.

`Tia.Core.Tests` additionally covers engine internals against in-memory compilations, with `Tia.Fixtures` supplying the hard cases (interface dispatch, open generics, partial classes, `const` inlining, reflection). These run in seconds; the harnesses above are nightly.

---

## Milestones

| # | Deliverable |
|---|---|
| M1 | Solution skeleton; git diff resolution; `MSBuildWorkspace` loading with the locator gotchas resolved |
| M2 | Changed-line→symbol mapping; reverse graph; BFS selection; `analyze` with text + `--json` output |
| M3 | Test discovery, three filter dialects, `run` shelling to `dotnet test`; command-line length chunking |
| M4 | Graph caching + incremental invalidation; `graph` command; GH Actions cache workflow |
| M5 | Safety model: full-run triggers, widening rules, reporting; `explain` |
| M6 | Mutation harness (zero-miss gate) + commit-replay benchmark on the target repos |
| M7 | NuGet packaging as a global tool, README with benchmark table, docs, CI, MIT license |

Ship M1–M3 as a usable `0.1.0` before starting M4 — a slow, cache-less tool that selects correctly is already useful and gets early feedback.

---

## Verification

1. **Unit tests** — `dotnet test tests/Tia.Core.Tests` covers each graph edge rule and each widening trigger against in-memory compilations.
2. **Dogfood** — `dotnet tia analyze --base main` on this repo itself; confirm a change to `Tia.Core` selects core tests and not CLI tests, and that `explain` shows the real path.
3. **Fixture assertions** — change a known symbol in `Tia.Fixtures`, assert the exact expected test set, including the const-inlining and interface-dispatch cases.
4. **Mutation harness** — `dotnet tia verify --mutate 500` against each target repo. **Merge gate: zero misses.**
5. **Replay benchmark** — selection ratio and time saved across 50 commits per target repo; output committed to the README.
6. **End-to-end per dialect** — one real repo per filter dialect (xUnit v2, xUnit v3/MTP, NUnit, MSTest, TUnit), verifying the emitted filter actually runs the intended tests and nothing else.
7. **CI** — the repo's own workflow uses `dotnet tia run` for PRs and a full suite on `main`.

---

## Notable risks

- **Reflection-heavy and DI-heavy codebases** erode the win: widen too eagerly and selection approaches 100%. Measure the widening rate in the replay benchmark and report it honestly in the README — over-claiming here is what discredits TIA tools.
- **Cold graph build cost** may dominate on large solutions; caching (M4) is what makes it viable, so don't defer it far past 0.1.0.
- **Naming** — `tia` is descriptive and searchable but generic. Check NuGet ID availability at M7; alternatives if taken: `winnow`, `sieve`.

## Sources

- [Datadog Test Impact Analysis for .NET](https://docs.datadoghq.com/tests/test_impact_analysis/setup/dotnet/)
- [Azure Pipelines Test Impact Analysis](https://learn.microsoft.com/en-us/azure/devops/pipelines/test/test-impact-analysis?view=azure-devops)
- [NCrunch pricing](https://www.ncrunch.net/buy)
- [dotnet-affected](https://github.com/leonardochaia/dotnet-affected) · [Incrementalist](https://petabridge.com/blog/introducing-incrementalist/)
- [Microsoft.Testing.Platform overview](https://learn.microsoft.com/en-us/dotnet/core/testing/microsoft-testing-platform-intro) · [VSTest→MTP migration](https://learn.microsoft.com/en-us/dotnet/core/testing/migrating-vstest-microsoft-testing-platform)
- [xUnit v3 on MTP](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform) · [`--filter` in MTP core](https://github.com/microsoft/testfx/issues/3780) · [TUnit test filters](https://tunit.dev/docs/execution/test-filters/)
- [Using MSBuildWorkspace](https://gist.github.com/DustinCampbell/32cd69d04ea1c08a16ae5c4cd21dd3a3) · [Roslyn APIs to analyse a solution](https://www.stevejgordon.co.uk/using-the-roslyn-apis-to-analyse-a-dotnet-solution)
- [FOSSED: .NET library licensing changes](https://dariusz-wozniak.github.io/fossed/)
