# Using `dotnet tia`

## Options

| Option | Default | Meaning | Commands |
|---|---|---|---|
| `--base`, `-b` | `origin/main` | Revision to diff against. The diff runs against the working tree, so uncommitted edits count. | `analyze`, `run`, `explain`, `watch` |
| `--path`, `-p` | current directory | Directory holding the solution. May be below the git root. | all |
| `--solution`, `-s` | discovered | Solution or project to analyse. | all |
| `--json` | off | Emit the result as JSON on stdout. | `analyze`, `explain`, `graph`, `verify` |
| `--verbose`, `-v` | off | List every selected test and log workspace diagnostics to stderr. | all |
| `--no-cache` | off | Ignore and do not write `.tia/graph-*.bin`. | all |
| `--cache-dir` | `.tia` | Directory holding the cached graph, relative to the repository root. | all |
| `--full` | off | Skip selection and report a full run. | `analyze`, `run`, `explain`, `watch` |
| `--default-branch` | unset | Branch that always runs the whole suite. | `analyze`, `run`, `explain`, `watch` |
| `--no-fallback-full-on-error` | off | Fail instead of falling back to a full run when analysis throws. | all |
| `--max-filter-length` | platform limit | Longest filter argument to emit before a project runs unfiltered. | all |
| `--coverage-threshold` | `0.6` | Fraction of a project's tests above which it runs unfiltered instead of filtered. | all |
| `--type-flow` | off | Bound each interface hop by the concrete types a member can obtain rather than merely reach. | all |

`--type-flow` is experimental and off for a reason: it is sound on every gate and, on the two
repositories measured, it did not change the selection at all while roughly doubling analysis time.
Turning it on costs a second semantic pass and keeps its own cache file, so the first run after
switching rebuilds the graph. The measurement, and why a sharper bound cannot fix what it was aimed
at, is in [`benchmarks.md`](benchmarks.md).

An option a command would ignore is not offered to it, and passing one is a usage error rather
than a silent no-op: `graph` builds the cache and `verify` writes its own mutation, so neither
resolves a diff, and `--base` on either used to parse, print in `--help` and be discarded.
`run` has no `--json` because it interleaves its output with the test runner's; `analyze --json`
is the machine-readable form of the same analysis. `watch` has none for the same reason one step
further: it emits a report per edit, so there is no single analysis for a document to describe.

Falling back to a full run is **on** by default; `--no-fallback-full-on-error` turns it off. The cost of an unnecessary full run is minutes; the cost of a missed test is a broken main branch.

One case is not a fallback and does not pass: if analysis fails *before* the solution loads, there are no test projects to name, so `run` has nothing to invoke. It exits non-zero rather than reporting that nothing was impacted.

### Command-specific

- `run` takes `--dry-run`, `--fail-fast` and `--no-prebuild`, and forwards everything after `--` to
  `dotnet test`:
  `dotnet tia run --base origin/main -- --no-build --configuration Release`
  `--fail-fast` stops at the first failing invocation instead of running the rest. The default runs
  everything selected, because a pull request needs the complete list.

  **`run` builds while it analyses.** The analysis and the build do not depend on each other, so
  whichever is shorter is free: measured here at 4.59 s of analysis and 2.16 s of build, 6.75 s in
  sequence against **5.25 s** together. `dotnet test` is then invoked with `--no-build`. The saving
  is bounded by whichever of the two is smaller, so it is worth most on exactly the repositories
  where this tool is worth least - one whose suite is short enough that the build is most of it.

  A failed build is reported as a failed build, with its own exit code and no tests run. Without
  that it would surface as whatever the analysis made of a tree that does not compile, which is a
  full run - a decision, where the news is a failure.

  Two things switch it off, because `--no-build` against a build that is not the one `dotnet test`
  would have run means testing stale binaries and reporting them as current. **Anything after `--`**
  disables it outright rather than being reasoned about: `--configuration Release` alone changes
  what "the build" means. And `--no-prebuild` disables it by hand.
- `explain <TestName>` matches any test whose fully qualified name ends with the argument, so `WidgetTests.Adds` is enough.
- `graph` takes `--output <file>` to write the graph summary and the discovered test list as JSON. `--json` writes the same document to stdout.
- `verify` takes `--mutate <n>` (default 25) and `--seed <n>` so a failing run can be replayed. `--json` emits every sample and the pass verdict.
  It **mutates your working tree in place** and restores each file afterwards, so it refuses to
  start unless `git status` is clean — including untracked files, which the diff picks up too. An
  edit that was already there would sit in every sample's diff, growing the selection until a miss
  could no longer be detected, and the run would report PASS regardless.

- `watch` holds the workspace open and re-analyses on every edit. `--run` runs the impacted tests
  each time instead of only listing them, `--fail-fast` stops that run at the first failure, and
  `--once` analyses once and exits. Everything after `--` is forwarded to `dotnet test` as with
  `run`. See below.

- `shadow` selects, then runs the **whole** suite anyway, and reports which failures the selection
  would have skipped. Nothing is skipped while it runs — that is the point. See below.

- `replay` walks your own history and reports what selection would have done on each commit, so you
  can answer "would this have paid off here?" before adopting anything. `--commits <n>` (default 20),
  `--first-parent`, `--output <file>`, `--json`.
  It **checks out historical commits**, so it refuses to start unless `git status` is clean of
  modified tracked files, and it returns to where it started when it finishes. It takes no
  `--solution`: a path given once is pinned to today's layout, and a solution moved inside the
  walked range would then resolve against a tree that does not contain it, silently skipping every
  commit before the move. Discovery runs per checkout instead.
  A replay measures **selection ratio and widening rate only**. Real commits are almost all green,
  so it says nothing about misses — `verify` and `shadow` are what answer that.

## Watch mode

```
dotnet tia watch --base origin/main --run
```

Every other command is a process that opens the solution, answers one question and exits. That is
the right shape for CI and the wrong one at a keyboard, because the largest single cost in an
analysis is MSBuild evaluating the solution and a fresh process pays it every time. `watch` pays it
once and then re-analyses on each save:

| Same one-line edit | one-shot `analyze` | `watch`, per edit |
|---|---:|---:|
| elapsed | 9.07 s | **2.35 s** |
| workspace load | 3.8 | 0 (paid once, 3.67 s) |
| graph rebuild | 3.9 | 1.4 |
| diff + change resolution | 0.7 | 0.7 |

Two savings, not one. The load is not paid again - and the rebuild is cheaper too, because a
resident Roslyn keeps the parsed trees of every document that did not change, while a fresh process
re-parses a whole project to rebuild one fragment. What does not move is the part that is git and
the part that is reading the diff.

Two attempts to cache the load away were measured and declined for the same reason each time - a
cache can only save the work a run did not need, and the load is needed on every run that is not a
repeat of the last one. It is only needed once *per process*, which is what this exploits.

What it watches is the repository, minus `bin`, `obj`, `.git`, `.tia` and the usual output
directories. On each batch of changes it re-reads the documents the solution holds and rebinds the
ones whose **content** moved - not whose timestamp moved, so a formatter that rewrites a file
unchanged costs nothing.

Two kinds of change reload the workspace outright and pay the load again, and both are printed when
they happen: a project, `.props`, `.targets` or solution file changing, and a source file appearing
that no project yet holds. Which files a project compiles is MSBuild's answer, and a resident
snapshot may not improvise it.

`--run` does not write to the run ledger. The ledger's job is to know what the whole suite costs,
and a watch loop would fill it with dozens of partial selections taken seconds apart. Run `tia run`
when you want that recorded.

## Shadow mode

`verify` proves the engine cannot miss a fault *it* injected, into C#, in a repository that was
green to begin with. That is a strong claim about a narrow case. Real diffs change data files and
configuration; real applications dispatch through containers, message buses and HTTP routes that no
static edge records, and [`docs/benchmarks.md`](benchmarks.md) documents one such gap on a real
application that could not be gated here at all.

Shadow mode is how *your* repository answers the question, without taking any of that on trust:

```
dotnet tia shadow --base origin/main
```

It costs one analysis on top of a suite that was going to run in full anyway, so it is safe to leave
on for weeks before deciding whether to act on it. In CI:

```yaml
- name: Shadow-mode impact analysis
  continue-on-error: true          # gathering evidence, not gating on it yet
  run: dotnet tia shadow --base origin/${{ github.base_ref }} --json > shadow.json

- uses: actions/upload-artifact@v4
  if: always()
  with:
    name: shadow-${{ github.run_id }}
    path: shadow.json
```

Collect those artifacts and read `verdict` across them. `miss` is the only one that matters;
`noFailures` is every green run and proves nothing, which is why it is not reported as safety.

Drop `continue-on-error` once you trust it, and the same command becomes a gate.

**It assumes your base is green.** A test already broken before the diff — or a flake — is still a
test that failed and was not selected, so it is reported as a miss. On the evidence available to a
single run the two are genuinely indistinguishable, and the alternative would be to swallow real
misses whenever a suite happens to be red. `verify` avoids this by refusing to start on a dirty tree
and mutating from a known-good state; `shadow` cannot, because the point is to run against a diff it
did not choose. A red baseline makes a run's misses uninformative rather than wrong: fix the
baseline, then read the result.

## Exit codes

| Command | 0 | non-zero |
|---|---|---|
| `analyze`, `graph`, `explain` | always | only on a usage error |
| `run` | every test project passed | the exit code of the first failing `dotnet test`, or of the build |
| `verify` | no misses, at least one usable sample | a miss, or no usable sample |
| `replay` | at least one commit replayed | a dirty tree, or no commit could be replayed |
| `shadow` | every failure was selected, or nothing failed | **1** a failure was not selected · **2** inconclusive |

`shadow` distinguishes its three answers by exit code because they call for different responses, and
a caller that cannot tell "safe" from "could not tell" is back to guessing. A green suite exits 0 but
reports `noFailures`, not safety: "no failing test was skipped" is true of every green run and says
nothing about the selection.

`analyze` deliberately does not fail on a full run: it is reporting a decision, not a result. Read `mode` from `--json` if you need to branch on it.

## Reading the JSON

```jsonc
{
  "mode": "selective",              // or "full"
  "baseRef": "origin/main",
  "baseCommit": "73be09d0...",
  "fullRunReasons": [],             // populated only when mode is "full"
  "widenings": [                    // every scope expansion, always reported
    { "cause": "ConstantInlining", "scope": "App.Core", "detail": "App.Limits.MaxRetries is compile-time inlined into its callers" }
  ],
  "diff":  { "fileCount": 3, "cSharpFileCount": 2, "changedSymbolCount": 4, "files": ["Modified src/App/Widget.cs"] },
  "diagnostics": [                 // how the diff was read, when it is not obvious
    "src/App/Notes.cs changed only comments or formatting; no token moved, so it seeds nothing"
  ],
  "graph": { "types": 1204, "members": 8930, "edges": 42110, "fromCache": true, "projectsRebuilt": 3, "projectsReused": 9 },
  "totalTests": 3412,
  "impactedTests": 87,           // what the graph selected - the engine's precision
  "selectedTests": 104,          // what will run: higher when a project runs unfiltered
  "projects": [
    {
      "name": "App.Tests",
      "projectPath": "/repo/tests/App.Tests/App.Tests.csproj",
      "assemblyPath": "/repo/tests/App.Tests/bin/Release/net10.0/App.Tests.dll",  // null when the project builds no assembly
      "framework": "XUnitV3",
      "runner": "MicrosoftTestingPlatform",
      "totalTests": 912,
      "selectedTests": 41,
      "filtered": true,
      "filterArguments": ["--filter-method", "App.Tests.WidgetTests.Adds"],
      "firstWave": {                 // absent when the selection cannot be divided safely
        "testCount": 11,
        "filterArguments": ["--filter-method", "App.Tests.WidgetTests.Adds"],
        "remainderFilterArguments": ["--filter-method", "App.Tests.GridTests.Wraps"]
      },
      "unsplitReason": null,         // why it cannot, when `firstWave` is absent
      "tests": ["App.Tests.WidgetTests.Adds"]   // nearest the change first, not alphabetical
    }
  ]
}
```

`widenings` is the field to watch. A selection that looks small but carries a `Reflection` widening on your largest project is not small.

## Running the nearest tests first

Selected tests are ordered by how many steps the graph took to reach them from the change, and that
order is what `tests` carries. Ordering alone changes nothing about when a failure appears, though:
`run` invokes `dotnet test` once per project and the runner picks the order inside it.

So `run` can hand the nearest tests over as an invocation of their own — `firstWave` above — and run
the rest afterwards. A failure among them shows up in seconds rather than after the whole selection.

It does this **only when the arithmetic says so**, and usually it does not:

- The extra invocation costs process start, a build check and a discovery pass on every run. The
  saving only lands on a run that fails, and only when the failure is in the wave. On a fast suite
  that trade loses, so the projected saving has to be worth several times the extra invocation
  before the run is divided at all.
- The estimate comes from the run ledger (`dotnet tia stats`), so nothing is divided until the tool
  has watched your suite at least three times. No ledger means one invocation, as before.
- A project that runs unfiltered is never divided: it has no filter to narrow, so a first wave could
  only repeat tests the run makes again anyway.
- A wave that would run a test twice, or that would together with its remainder match a test one
  filter would not have, is refused. `--verbose` prints `no first wave: …` with the reason.

Nothing about this changes which tests run. The two invocations cover the same selection the single
one would have, and a missing or nonsense ledger costs an invocation, never a test.

`impactedTests` and `selectedTests` differ whenever a project runs unfiltered — because the selection already covers most of it, or because the filter would not fit on a command line. Judge the engine by the first and your CI bill by the second.

## CI

The shape that works:

1. `fetch-depth: 0`. A shallow clone cannot reach the base commit, and `tia` correctly refuses to guess — it bails out to a full run and says the clone is shallow.
2. Cache `.tia/` keyed on the base branch.
3. Warm the graph with `dotnet tia graph`.
4. `dotnet tia run --base origin/$BASE --default-branch main -- --no-build`.
5. Run the whole suite on the default branch and on scheduled builds. Selection is a pull-request optimisation; the mainline has to stay honest about what passes.

`--default-branch main` makes step 5 automatic: on `main`, `tia` reports a full run with that as the stated reason.

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) is a working example of all of it.

## Validation harnesses

Both are slow enough to belong in a nightly job.

```
# Correctness. Zero misses is the gate.
dotnet run --project tests/Tia.Validation -- mutate --repo /path/to/repo --samples 200 --output mutation.md

# Selection ratio and widening rate over real history, across many repositories at once.
dotnet run --project tests/Tia.Validation -- replay --repo /path/to/repo --commits 50 --output replay.md
```

`dotnet tia replay` is the shipped form of the second one and is what you want for your own
repository — the validation project exists to point the same harness at several repositories that
are not the one you are standing in.

The mutation harness needs to read test outcomes, which means a TRX-capable runner: `Microsoft.NET.Test.Sdk` for VSTest projects, `Microsoft.Testing.Extensions.TrxReport` for Microsoft.Testing.Platform projects. Match the extension's major version to the platform version your test framework pulls in — xUnit v3 3.2.x uses Microsoft.Testing.Platform 1.9.x, so pair it with `Microsoft.Testing.Extensions.TrxReport` 1.9.x. Without a usable reporter the harness now refuses before mutating anything — a single baseline run tells it which projects are unobservable, and it names the package each one is missing rather than spending every sample to report **inconclusive** one at a time.

Where TRX genuinely cannot be had, `--project-granularity` opts into a weaker gate that reads each project's exit code:

```
dotnet tia verify --mutate 30 --project-granularity
```

It supports exactly one sound inference — a project that failed, none of whose tests were selected, contains a failing test that would not have run, which is a definite miss. The converse does not follow: a failed project with *some* tests selected may have failed on a different test than the one selected. So it reports `PROJECT-GRANULARITY GATE`, never `PASS`, and `--json` carries `projectGranularity` next to `passed`. It never reports a pass it could not observe.

Each mutated run is also **bounded**, because mutating a loop is one of the ordinary ways to make one that does not terminate — dropping the statement that stores a fixpoint's progress is enough. The budget is four times the baseline preflight run, floored at two minutes and capped at thirty, so it scales with your suite rather than needing to be configured. A project killed for exceeding it is reported as `TIME`, counted separately from `skipped`, and can never count toward a pass: *"there was nothing here to check"* and *"the harness could not finish checking"* are opposite statements. Seeing a few is normal and says something about the mutation, not about your selection.

## Troubleshooting

**"X does not compile"** and everything runs. Almost always an unrestored solution: a project with no resolved references still parses, so discovery would quietly find no tests in it. Run `dotnet restore` first.

**A constant set of tests runs on every change.** Check `widenings` for `Reflection`. Every reflecting or serializing member in the solution is unconditionally impacted — it can reach things no static edge records, and it is dangerous precisely when nothing reaches it — so everything that reaches one of those members runs whatever you changed. On NodaTime that floor is about 8 % of the suite. The widening names the file and the construct, so you can see which member is responsible; if it is a test helper wrapping `XmlSerializer` or `Activator.CreateInstance`, that is the whole test class it serves.

**Everything is selected on every change.** Check `widenings` for `ConstantInlining`, `SourceGenerator` or `ContentFile`, and `fullRunReasons` for a bail-out. Failing that, run `explain` on a test you did not expect: if it reports a real path, the change is genuinely central, and `docs/benchmarks.md` explains why a library's core selects most of its suite.

**A test you expected is missing.** Run `dotnet tia explain <TestName>`. It either prints the path or tells you nothing reaches it — and if nothing reaches it, that is a graph gap worth reporting. Passing a class name rather than a test name returns no match, since matching is on a suffix of a fully qualified test name; the near misses are listed so you can pick one.

The gaps found so far were all runtime paths the source never spells out: a serializer calling `IXmlSerializable.ReadXml`, an interpolated `$"{value}"` calling `value.ToString()`, a static member read running a type initializer. If your missing test fails through something in that family, it is the same shape and worth reporting.

**The base branch is not found.** `tia` needs the base revision in the local object store. In CI that means `fetch-depth: 0`; locally, `git fetch origin main`.

**A solution below the git root.** Point `--path` at the directory holding the solution and `--solution` at the solution file; diff paths are resolved against the repository root regardless, so a monorepo layout works. `.tia/` is written under `--path`.
