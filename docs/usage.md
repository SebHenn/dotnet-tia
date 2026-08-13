# Using `dotnet tia`

## Options

| Option | Default | Meaning | Commands |
|---|---|---|---|
| `--base`, `-b` | `origin/main` | Revision to diff against. The diff runs against the working tree, so uncommitted edits count. | `analyze`, `run`, `explain` |
| `--path`, `-p` | current directory | Directory holding the solution. May be below the git root. | all |
| `--solution`, `-s` | discovered | Solution or project to analyse. | all |
| `--json` | off | Emit the result as JSON on stdout. | `analyze`, `explain`, `graph`, `verify` |
| `--verbose`, `-v` | off | List every selected test and log workspace diagnostics to stderr. | all |
| `--no-cache` | off | Ignore and do not write `.tia/graph-*.bin`. | all |
| `--cache-dir` | `.tia` | Directory holding the cached graph, relative to the repository root. | all |
| `--full` | off | Skip selection and report a full run. | `analyze`, `run`, `explain` |
| `--default-branch` | unset | Branch that always runs the whole suite. | `analyze`, `run`, `explain` |
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
is the machine-readable form of the same analysis.

Falling back to a full run is **on** by default; `--no-fallback-full-on-error` turns it off. The cost of an unnecessary full run is minutes; the cost of a missed test is a broken main branch.

One case is not a fallback and does not pass: if analysis fails *before* the solution loads, there are no test projects to name, so `run` has nothing to invoke. It exits non-zero rather than reporting that nothing was impacted.

### Command-specific

- `run` takes `--dry-run`, and forwards everything after `--` to `dotnet test`:
  `dotnet tia run --base origin/main -- --no-build --configuration Release`
- `explain <TestName>` matches any test whose fully qualified name ends with the argument, so `WidgetTests.Adds` is enough.
- `graph` takes `--output <file>` to write the graph summary and the discovered test list as JSON. `--json` writes the same document to stdout.
- `verify` takes `--mutate <n>` (default 25) and `--seed <n>` so a failing run can be replayed. `--json` emits every sample and the pass verdict.
  It **mutates your working tree in place** and restores each file afterwards, so it refuses to
  start unless `git status` is clean — including untracked files, which the diff picks up too. An
  edit that was already there would sit in every sample's diff, growing the selection until a miss
  could no longer be detected, and the run would report PASS regardless.

- `shadow` selects, then runs the **whole** suite anyway, and reports which failures the selection
  would have skipped. Nothing is skipped while it runs — that is the point. See below.

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
| `run` | every test project passed | the exit code of the first failing `dotnet test` |
| `verify` | no misses, at least one usable sample | a miss, or no usable sample |
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
      "tests": ["App.Tests.WidgetTests.Adds"]
    }
  ]
}
```

`widenings` is the field to watch. A selection that looks small but carries a `Reflection` widening on your largest project is not small.

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

# Selection ratio and widening rate over real history. Needs a clean working tree:
# it checks out historical commits and restores the starting point afterwards.
dotnet run --project tests/Tia.Validation -- replay --repo /path/to/repo --commits 50 --output replay.md
```

The mutation harness needs to read test outcomes, which means a TRX-capable runner: `Microsoft.NET.Test.Sdk` for VSTest projects, `Microsoft.Testing.Extensions.TrxReport` for Microsoft.Testing.Platform projects. Match the extension's major version to the platform version your test framework pulls in — xUnit v3 3.2.x uses Microsoft.Testing.Platform 1.9.x, so pair it with `Microsoft.Testing.Extensions.TrxReport` 1.9.x. Without a usable reporter the harness now refuses before mutating anything — a single baseline run tells it which projects are unobservable, and it names the package each one is missing rather than spending every sample to report **inconclusive** one at a time.

Where TRX genuinely cannot be had, `--project-granularity` opts into a weaker gate that reads each project's exit code:

```
dotnet tia verify --mutate 30 --project-granularity
```

It supports exactly one sound inference — a project that failed, none of whose tests were selected, contains a failing test that would not have run, which is a definite miss. The converse does not follow: a failed project with *some* tests selected may have failed on a different test than the one selected. So it reports `PROJECT-GRANULARITY GATE`, never `PASS`, and `--json` carries `projectGranularity` next to `passed`. It never reports a pass it could not observe.

## Troubleshooting

**"X does not compile"** and everything runs. Almost always an unrestored solution: a project with no resolved references still parses, so discovery would quietly find no tests in it. Run `dotnet restore` first.

**A constant set of tests runs on every change.** Check `widenings` for `Reflection`. Every reflecting or serializing member in the solution is unconditionally impacted — it can reach things no static edge records, and it is dangerous precisely when nothing reaches it — so everything that reaches one of those members runs whatever you changed. On NodaTime that floor is about 8 % of the suite. The widening names the file and the construct, so you can see which member is responsible; if it is a test helper wrapping `XmlSerializer` or `Activator.CreateInstance`, that is the whole test class it serves.

**Everything is selected on every change.** Check `widenings` for `ConstantInlining`, `SourceGenerator` or `ContentFile`, and `fullRunReasons` for a bail-out. Failing that, run `explain` on a test you did not expect: if it reports a real path, the change is genuinely central, and `docs/benchmarks.md` explains why a library's core selects most of its suite.

**A test you expected is missing.** Run `dotnet tia explain <TestName>`. It either prints the path or tells you nothing reaches it — and if nothing reaches it, that is a graph gap worth reporting. Passing a class name rather than a test name returns no match, since matching is on a suffix of a fully qualified test name; the near misses are listed so you can pick one.

The gaps found so far were all runtime paths the source never spells out: a serializer calling `IXmlSerializable.ReadXml`, an interpolated `$"{value}"` calling `value.ToString()`, a static member read running a type initializer. If your missing test fails through something in that family, it is the same shape and worth reporting.

**The base branch is not found.** `tia` needs the base revision in the local object store. In CI that means `fetch-depth: 0`; locally, `git fetch origin main`.

**A solution below the git root.** Point `--path` at the directory holding the solution and `--solution` at the solution file; diff paths are resolved against the repository root regardless, so a monorepo layout works. `.tia/` is written under `--path`.
