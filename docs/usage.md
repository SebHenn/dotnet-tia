# Using `dotnet tia`

## Options

| Option | Default | Meaning | Commands |
|---|---|---|---|
| `--base`, `-b` | `origin/main` | Revision to diff against. The diff runs against the working tree, so uncommitted edits count. | `analyze`, `run`, `explain`, `shadow`, `watch` |
| `--path`, `-p` | current directory | Directory holding the solution. May be below the git root. | all |
| `--solution`, `-s` | discovered | Solution or project to analyse. Required when discovery finds several of one kind - see below. | all but `replay`, `stats` |
| `--json` | off | Emit the result as JSON on stdout. | all but `run`, `watch` |
| `--verbose`, `-v` | off | List every selected test and log workspace diagnostics to stderr. | all but `stats` |
| `--no-cache` | off | Ignore and do not write `.tia/graph-*.bin`. | all but `stats` |
| `--cache-dir` | `.tia` | Directory holding the cached graph, relative to the repository root. | all |
| `--full` | off | Skip selection and report a full run. | `analyze`, `run`, `explain`, `watch` |
| `--default-branch` | unset | Branch that always runs the whole suite. | `analyze`, `run`, `explain`, `watch` |
| `--no-fallback-full-on-error` | off | Fail instead of falling back to a full run when analysis throws. | all but `stats` |
| `--max-filter-length` | platform limit | Longest filter to pass on the command line. A longer one goes in a file the runner reads, or where it can't, the project runs unfiltered. | all but `stats` |
| `--coverage-threshold` | `0.6` | Fraction of a project's tests above which it runs unfiltered instead of filtered. | all but `stats` |
| `--type-flow` | off | Bound each interface hop by the concrete types a member can obtain rather than merely reach. | all but `stats` |

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

**Nothing to analyse is not a fallback.** That covers no solution or project in `--path`, several of one kind and no `--solution` (see below), and a `--path` that does not exist or is not in a git repository. A full run needs a solution to be a full run of, so every command that analyses refuses before analysing anything, says why on stderr and exits **2** ([#50](https://github.com/SebHenn/dotnet-tia/issues/50)). These cases used to fall back to a full run that named no project, and `analyze` printed it and exited 0.

A failure after that point but before the solution loads is still a fallback with no test projects to name, so `run` has nothing to invoke. It exits non-zero rather than reporting that nothing was impacted.

### Which solution is analysed

Without `--solution`, `tia` looks in `--path` (not below it) for `*.slnx`, then `*.sln`, then
`*.slnf`, then a single `*.csproj`, and the first pattern that matches anything decides. A `.slnx`
next to the `.sln` it is replacing is therefore picked without complaint. Two files of the *same*
kind are not: every command that analyses refuses, names the candidates and exits **2**, and
`--solution` settles it ([#37](https://github.com/SebHenn/dotnet-tia/issues/37)). It used to take
whichever sorted first, and a selection on the wrong solution silently misses every test project
only the other one contains. This refusal is not a fallback: there is no solution to fall back to a
full run of. `replay` discovers per commit, so a commit holding several is skipped with the
reason rather than ending the walk. The solution that was used heads the report (`Solution`) and
is `solution` in the JSON.

### Command-specific

- `run` takes `--dry-run`, `--fail-fast`, `--no-prebuild`, `--failed-first`
  ([below](#running-last-runs-failures-first)) and `--parallel [N]`
  ([below](#running-projects-side-by-side)), and forwards everything after `--` to
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
- `analyze` and `run` take `--fail-on-untested`, which exits 1 when a changed symbol is reached by no
  test. See [Untested changes](#untested-changes).
- `analyze` and `run` take `--shard <i>/<n>`, which keeps part `i` of `n` of what would run. See
  [Sharding](#sharding).
- `analyze` and `run` take `--report <file>`, which also writes the JSON report to a file. `run`
  has no `--json`, because its stdout belongs to the test runners, so this is how its selection
  reaches a later job. `--report-skipped` adds `skippedTests` to the report: the tests this run
  leaves out, per test project. See [Finding misses in CI](#finding-misses-in-ci).
- `explain <TestName>` matches any test whose fully qualified name ends with the argument, so `WidgetTests.Adds` is enough.
- `graph` takes `--output <file>` to write the graph summary and the discovered test list as JSON. `--json` writes the same document to stdout.
- `verify` takes `--mutate <n>` (default 25) and `--seed <n>` so a failing run can be replayed. The seed defaults to the clock and is printed first (`seed` in `--json`), so any run can be replayed. Mutations are drawn without replacement from every site in the production projects a test project reaches. Projects no test project reaches are named and left out. When `n` exceeds the number of sites, the run stops early and says the site space is exhausted. `--json` emits every sample and the pass verdict.
  A sample whose mutation broke no test at all reads `UNOBSERVED` rather than `OK`, and the summary
  counts them: `6 of 8 usable mutation(s) were observed by no test ([2] [4] [5] [6] [7] [8])`.
  That says nothing about selection, which cannot miss a failure that does not exist. It does say
  something about the suite, and `verify` is the only command that runs it against a build known to
  be broken. `--max-unobserved <n>` fails the run when more than `n` samples were unobserved; it is
  off unless given, because it gates the suite rather than the tool.
  Each mutated run is bounded twice, because a mutation can make a loop run away in two ways: in
  time, at four times the baseline suite's duration per project (`TIME`), and in memory, at a
  quarter of the machine's memory of GC heap per process (`MEM`). Neither sample is usable or ever a
  miss. The baseline run is bounded by neither. To choose the memory limit yourself, export
  `DOTNET_GCHeapHardLimit` (in hex, as the runtime reads it) before running; the harness leaves an
  existing value alone.
  It **mutates your working tree in place** and restores each file afterwards, so it refuses to
  start unless `git status` is clean — including untracked files, which the diff picks up too. An
  edit that was already there would sit in every sample's diff, growing the selection until a miss
  could no longer be detected, and the run would report PASS regardless. A `git status` that fails
  is a refusal too, with exit 2.
  A run that is **killed** mid-sample skips the restore, so before each write the harness records
  the file and its original bytes in `.tia/mutation-in-progress.json` (under `--cache-dir` if
  given). The next `verify` reads that record first: a file still holding the mutation is put back
  and a line says so, and a file already back to its original just loses the record. A file edited
  since is left alone, and `verify` exits 2 with the `git checkout HEAD -- <path>` that restores it.

- `watch` holds the workspace open and re-analyses on every edit. `--run` runs the impacted tests
  each time instead of only listing them, `--fail-fast` stops that run at the first failure, and
  `--once` analyses once and exits with the code `analyze` would, or `run` would with `--run`.
  Everything after `--` is forwarded to `dotnet test` as with `run`. See below.

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

## Server mode

```
dotnet tia server start
dotnet tia run --base origin/main      # analysed by the server
dotnet tia server stop
```

`watch` keeps the workspace loaded for one terminal. `server` keeps it loaded for every `analyze` and
`run` in the repository: `start` launches a background process that opens the solution once, and
each later command sends it the analysis instead of loading the solution itself.

| Warm `analyze`, nothing changed | in-process | through the server |
|---|---:|---:|
| this repository | 5.2 s | **0.85 s** |
| NodaTime | 8.3 s | **1.3 s** |

**The report is the one a fresh process would produce.** Before every analysis the server checks the
tree:
- A project, `.props`, `.targets` or solution file, an `obj/project.assets.json` from a restore,
  `global.json` or `nuget.config` changed, or a source file appeared that the workspace doesn't
  have. Each of those changes what MSBuild evaluates, so the workspace is reopened, at the cost of
  one load.
- Anything else is refreshed the way `watch` refreshes: every document is read back and rebound if
  its content moved.

On this repository and on NodaTime, the served and in-process reports agree field for field, apart
from timings and the break-even derived from them.

- **One server per repository root and solution, per user.** `--path`, `--solution` and
  `--cache-dir` choose it, as on every other command. A command run from another directory, or for
  another solution, analyses in its own process.
- **It is only ever a shortcut.** No server, a server of another tool version, a server busy for more
  than 2 s, or any failure: the command analyses in its own process, and says why under `--verbose`.
  `--no-server` on `analyze` and `run` skips it outright. `explain`, `watch`, `shadow`, `verify`
  and `replay` always analyse in-process.
- **Access is local and per-user.** The pipe name is derived from the user, the repository and the
  solution, not read from a file, and both ends open it current-user-only: an ACL on Windows, the
  socket owner on Linux and macOS. `.tia/server.json` only says that a server was started.
- It stops after 30 idle minutes (`--idle-minutes`), with `server stop`, or when killed.
  `server status` shows how many analyses it served and how often it reloaded. It logs to
  `.tia/server.log`. `server run` serves in the foreground, which is what `start` launches.
- **After upgrading tia, restart it.** An old server answers a new client with its version, and the
  client analyses itself until you do.

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
| `analyze`, `graph` | always | **2** the workspace did not resolve, or nothing to analyse · a usage error |
| `explain` | the name matched a test | **1** no test name ends with the one given · **2** nothing to analyse · a usage error |
| `run` | every test project passed | the exit code of the first failing `dotnet test`, or of the build · **1** a full run with no test project to invoke · **2** the workspace did not resolve, or nothing to analyse |
| `verify` | no misses, at least one usable sample | **1** a miss, including one found before a run was abandoned, or more unobserved samples than `--max-unobserved` allows · **2** no usable sample, the harness refused to start or abandoned the run, the working tree cannot be vouched for (`git status` failed, or a file a killed run mutated has been edited since), or nothing to analyse |
| `replay` | at least one commit replayed | a dirty tree, or no commit could be replayed |
| `shadow` | every failure was selected, or nothing failed | **1** a failure was not selected · **2** inconclusive, or nothing to analyse |
| `watch --once` | as `analyze`, or as `run` with `--run` | as `analyze`, or as `run` with `--run` · **2** nothing to analyse, with or without `--once` |
| `stats` | always | only on a usage error |

`verify` and `shadow` distinguish "a test was missed" from "nothing could be concluded" because the
two call for different responses, and a caller that cannot tell "safe" from "could not tell" is back
to guessing. `verify` refusing to start - a dirty tree, a solution with no test projects, a runner
that cannot write TRX - is the second kind: nothing was mutated, so nothing was missed. So is a
run abandoned part-way because a sample did not restore a file, unless a sample it finished had
already found a miss: that sample ran against the tree it meant to, so its miss stays a miss. A
green `shadow` suite exits 0 but reports `noFailures`, not safety: "no failing test was skipped" is
true of every green run and says nothing about the selection.

`watch` without `--once` runs until it is stopped and exits 0. The result of each run it starts is
in its output, and a code from whichever edit happened to come last would describe that edit, not
the session.

`analyze` deliberately does not fail on a full run: it is reporting a decision, not a result. Read `mode` from `--json` if you need to branch on it.

### When the workspace does not resolve

`2` means something different from every other code here: not a defect found, but nothing learned.
It is the answer whenever a project the tool classifies as a test project yields **zero tests**, and
it exists because that state used to be reported as a fact about a repository and exit 0. `verify`
uses it in the same sense for a working tree it cannot vouch for, described under `verify` above.

The way in is a missing restore. `MSBuildWorkspace` reads `obj/project.assets.json` to resolve a
`PackageReference`, so on a runner that has not restored, xunit's `[Fact]` binds to an error type -
no method is a test, and the graph is empty of the one thing selection is for. Everything downstream
then answers confidently about nothing: a full run of no tests, a shadow run watching an empty set.
So a suite that vanished is reported under its own heading rather than as a count:

```
  WORKSPACE DID NOT RESOLVE - these numbers describe a failed load, not this repository

    x App.Tests is a test project and yielded 0 tests: it has not been restored -
      obj\project.assets.json is missing - so its package references did not resolve and no test
      framework attribute is a known type. Run `dotnet restore` first.
```

Restore (and build, if you pass `--no-build` to the runner) before any `tia` command, in every job:

```yaml
- run: dotnet restore
- run: dotnet tia analyze --base origin/main
```

A project that genuinely holds no tests is reported the same way, because nothing can ever be
selected from it. Declare it with `<IsTestProject>false</IsTestProject>` - the same property the SDK
reads to decide whether to run a project at all, and which `tia` already honours to leave it out of
the suite.

## Reading the JSON

Every command's JSON opens with `"schemaVersion": 1`. It goes up when a field is removed, renamed
or changes meaning, so check it before reading on; adding a field leaves it alone.

The report that `analyze --json`, `run --json` and `--report <file>` write is described by a JSON
Schema (2020-12), [`docs/report.schema.json`](report.schema.json), and every GitHub release carries
the copy for its version as `report.schema.json`. It is exported from the report types and tested
against real reports, so it can't drift from what the tool writes. Its objects are closed
(`additionalProperties: false`). A newer release can add fields without bumping `schemaVersion`, so a
consumer validating with an older copy should expect fields it doesn't know, or open the objects up.

```jsonc
{
  "schemaVersion": 1,
  "mode": "selective",              // or "full"
  "baseRef": "origin/main",
  "baseCommit": "73be09d0...",
  "solution": "src/App.slnx",       // what was analysed, relative to --path; absent if none was settled on
  "fullRunReasons": [],             // populated only when mode is "full"
  "loadFailures": [],               // non-empty means the workspace did not resolve and nothing
                                    // here can be relied on; always a subset of fullRunReasons
  "widenings": [                    // every scope expansion, always reported
    { "cause": "ConstantInlining", "scope": "App.Core", "detail": "App.Limits.MaxRetries is compile-time inlined into its callers" }
  ],
  "diff":  { "fileCount": 3, "cSharpFileCount": 2, "changedSymbolCount": 4, "files": ["Modified src/App/Widget.cs"] },
  "diagnostics": [                 // how the diff was read, when it is not obvious
    "src/App/Notes.cs changed only comments or formatting; no token moved, so it seeds nothing"
  ],
  "untestedChanges": [             // changed symbols no test reaches; advisory, see below
    { "symbol": "OrderService.Refund(Order)", "project": "App", "file": "src/App/OrderService.cs" }
  ],
  "uncheckedChanges": 0,           // changed symbols past the check's cap, not counted as tested
  "shard": null,                   // under --shard: { "index": 2, "count": 4, "selectedAcrossShards": 104 }
  "skippedTests": [                // under --report-skipped only; [] on a full run
    { "project": "App.Tests", "tests": ["App.Tests.GridTests.Wraps"] }
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
      "targetFramework": null,       // "net48" for one instance of a multi-targeted project, run with --framework
      "writesTrx": true,             // the runner can write TRX without failing the run (run --failed-first)
      "hasRunSettings": false,       // the project names a RunSettings file (RunSettingsFilePath)
      "totalTests": 912,
      "selectedTests": 41,
      "filtered": true,
      "filterArguments": ["--filter-method", "App.Tests.WidgetTests.Adds"],
      "filterInFile": false,         // true: too long for the command line; see "Long filters"
      "firstWave": {                 // absent when the selection cannot be divided safely
        "testCount": 11,
        "filterArguments": ["--filter-method", "App.Tests.WidgetTests.Adds"],
        "remainderFilterArguments": ["--filter-method", "App.Tests.GridTests.Wraps"],
        "lastFailed": false          // true: the wave is last run's failures (run --failed-first)
      },
      "unsplitReason": null,         // why it cannot, when `firstWave` is absent
      "tests": ["App.Tests.WidgetTests.Adds"]   // nearest the change first, not alphabetical
    }
  ]
}
```

## Long filters

A filter longer than `--max-filter-length` (24,000 characters on Windows, 200,000 elsewhere)
would not survive the command line. `run` passes it in a temporary file instead, and the project
still runs only its selection:

| Runner | What `run` passes |
|---|---|
| Microsoft.Testing.Platform (bridge or `global.json`) | `-- @<file>.rsp`, one quoted argument per line, read by the test application |
| VSTest | `--settings <file>.runsettings`, holding the filter as `RunConfiguration/TestCaseFilter` |

The report says `filterInFile: true` for such a project. A script that builds its own command from
`filterArguments` has to do the same, because `dotnet test @args.rsp` doesn't work: the CLI expands
the file, and MSBuild then starts the runner with the full command line.

Two cases still run the whole project, as every filter this long used to:

- a VSTest project with run settings of its own, `RunSettingsFilePath` in the project or `--settings`
  after `run --`. A second `--settings` would replace them, not add to them.
- a VSTest runner too old to read `TestCaseFilter`. It ignores the setting and runs everything.

`--dry-run` keeps the files, so the printed command can be run by hand. Otherwise they are deleted
when the run ends.

## Untested changes

A selective run lists the changed symbols that no test reaches:

```
  Untested changes - no test reaches these
    ? OrderService.Refund(Order)  (src/App/OrderService.cs)
```

**Nothing about it changes what runs.** It's the selection's own graph answering a second
question, "does anything exercise this?", so a reviewer hears about a gap that would otherwise only
show up as a pleasantly small selection.

- Only symbols the diff changed are checked. A deleted symbol isn't, because its callers changed with
  it. Neither is a symbol in a project that runs whole after a widening, because the graph was not
  trusted there.
- It means *statically* unreached. A test that gets there through reflection the graph can't see
  would still run the code, so read it as "nothing names a path to this", not as proof.
- A full run traverses nothing, so it lists nothing.
- Most symbols are settled by the traversal selection already did. Each of the rest costs a
  traversal of its own, so the check stops after 64 of them and reports the remainder in
  `uncheckedChanges` rather than treating them as covered.

`--fail-on-untested` turns it into a gate: `analyze` and `run` exit 1 when the list isn't empty. It
also lifts the cap, because a gate that stopped checking would pass whatever it never looked at.
`run` runs the tests first, and a failing test decides the exit code. On a full run, the gate says it
did not check and passes.

`widenings` is the field to watch. A selection that looks small but carries a `Reflection` widening on your largest project is not small.

An `Unmapped` widening means a top-level statement or an assembly or module attribute changed, in a `Program.cs` or an `AssemblyInfo.cs` for instance, or that some other line outside every declaration changed in a file that holds them, such as a using directive that can rebind them. Neither belongs to a declaration, so the project that holds it runs whole, and so does every project that depends on it. Its `scope` is that project. A using directive changed beside the types of a file that holds neither does not do this: it marks those types and nothing more.

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

### Running projects side by side

`run --parallel` runs the selected test projects at the same time instead of one after another. A
bare `--parallel` allows one per core, and `--parallel 2` caps it at two.

- **Only against binaries that are already built.** Two `dotnet test` invocations that each build
  would build the projects they share at the same time. So it applies when `run` built ahead, which
  it does unless arguments follow `--`, or when you pass `--no-build` after `--`. Otherwise it says
  why and runs the projects one at a time.
- Each project's output is held and printed whole when it finishes, under a `-- Project: exit n, 12.3s`
  line. Interleaved lines from several runners can't be read, so output no longer streams live.
- The exit code is the first failing project's in project order, as without the flag.
  `--fail-fast` stops the projects still running and doesn't start the rest.
- A parallel run is not recorded in the ledger. Its wall-clock is not the suite time the break-even
  is measured against.

The saving is bounded by the largest project. On NodaTime, whose `NodaTime.Test` takes about 25 s
next to two small projects, `run --full -- --no-build` went from 37 s to 33 s. On a solution
dominated by one project it buys little.

### Running last run's failures first

`run --failed-first` puts the selected tests that failed last time first: the classes holding them
run as an invocation of their own, before the rest. A test that is still broken shows up in seconds,
which matters most while fixing it.

- **It changes the order, never the selection.** A recorded failure that this diff doesn't reach
  isn't added.
- The record is `.tia/last-failed.json` (under `--cache-dir` if given), and only `--failed-first`
  writes it. A test keeps its entry until it runs again, so a failure survives a selection that
  skipped it. The file is capped at 2,000 tests and is safe to delete.
- Outcomes come from TRX, which `run` asks each runner for when the flag is given. VSTest always
  writes it. Microsoft.Testing.Platform needs `Microsoft.Testing.Extensions.TrxReport`, and a project
  without it is not asked, because `--report-trx` would fail its run as an unknown option. `--json`
  reports `writesTrx` per project. The reports go to a temporary directory outside the repository,
  which is deleted afterwards.
- Unlike the nearest-first wave, this split doesn't wait for the ledger: you asked for it. It still
  refuses a division that would run a test twice or match one the selection didn't pick. In a project
  that runs whole there is no filter to take the wave out of, so the failed classes run first and
  then once more with the rest of the project.

`impactedTests` and `selectedTests` differ whenever a project runs unfiltered — because the selection already covers most of it, or because the filter would not fit on a command line. Judge the engine by the first and your CI bill by the second.

## CI

On GitHub Actions, the action in this repository does the steps below, apart from caching:

```yaml
- uses: actions/checkout@v7
  with:
    fetch-depth: 0
- uses: actions/setup-dotnet@v6
  with:
    dotnet-version: '10.0.x'
- run: dotnet restore
- uses: SebHenn/dotnet-tia@v0.6.0   # or a commit SHA
  with:
    test-args: --configuration Release   # optional, passed to dotnet test after --
```

It installs the tool at the version of the action's own release, so pinning the action pins the
tool. It diffs against the pull request's base branch, or against the default branch on a push, and
runs everything on the default branch. It appends a summary to the job page: what was selected, why
not less, the widenings, and the [untested changes](#untested-changes). There is no moving `v1` tag:
release tags are immutable here, so pin a release or a SHA. Inputs: `command` (`run` or
`analyze`), `base`, `default-branch`, `solution`, `args`, `test-args`, `summary`,
`fail-on-untested`, `report`, `version` and `package-source`. Outputs: `exit-code` and `version`. See
[`action.yml`](../action.yml).

### Finding misses in CI

A selection miss is a test that this run skipped, and that then fails once it does run: on the
default branch after the merge, or in a nightly full run. `shadow` finds those locally. To find them
in CI, keep the pull request's selection next to its results:

```yaml
- uses: SebHenn/dotnet-tia@v0.6.0
  with:
    report: tia-report.json          # --report tia-report.json --report-skipped
- uses: actions/upload-artifact@v7
  with: { name: tia-report, path: tia-report.json }
```

`skippedTests` lists, per test project, every test the run will not execute. That includes a
project nothing was selected from, and excludes a test that a filter matches beyond the selection,
because that test runs. A full run lists nothing. The list can be most of the document on a large
suite, so it's there only when asked for.

[ci-failure-intelligence](https://github.com/SebHenn/ci-failure-intelligence) reads it: given the
report and a failing run's test results, it marks each failure of a skipped test as a selection miss.

### Sharding

`run --shard 2/4` runs the second of four parts of the selection, so one selection can be spread
across parallel jobs:

```yaml
strategy:
  matrix:
    shard: [1, 2, 3, 4]
steps:
  # checkout with fetch-depth: 0, setup-dotnet, restore
  - uses: SebHenn/dotnet-tia@v0.6.0
    with:
      args: --shard ${{ matrix.shard }}/4
```

The jobs don't talk to each other. Each one analyses the same commit and computes the same split.
So **every shard must run the same tia version, on the same commit, with the same options.**

- **The unit is the test class.** Classes are dealt largest first to the shard with the fewest
  tests so far. A class is never split, so its setup runs in one job.
- Between them, the shards run exactly what one job would have. Each test is assigned to one
  shard, and a test in two shards can only happen when a filter dialect widens.
- A shard's part of a project whose filter is longer than 24,000 characters goes in a file, as in
  [Long filters](#long-filters). That limit is Windows' command-line budget, applied on every
  platform so that a Linux job and a Windows job agree. `--max-filter-length` overrides it, for all
  shards alike. Where no file can be used (a VSTest project with run settings of its own), the
  project goes to one shard whole.
- An F# or VB test project that runs whole is assigned to one shard.
- A shard with nothing to run exits 0. A sharded run never divides into waves, and doesn't write
  to the ledger, because its suite time is a slice of the suite.
- `--json` reports `shard: {index, count, selectedAcrossShards}`. `selectedTests` and `projects` are
  that shard's part, and everything else describes the whole analysis.

`--summary <file>` on `analyze` and `run` is the piece that writes the summary, and works in any CI
that renders Markdown: it appends rather than overwrites. Its lists are capped, at 50 untested
changes, 100 widenings and 25 full-run reasons or load failures, each followed by a count of what
was left out. GitHub drops a step summary over 1 MiB, and a summary that vanished would hide the
very diff it most needed to describe. The complete lists are in `--report <file>`.

The shape that works:

1. `fetch-depth: 0`. A shallow clone cannot reach the base commit, and `tia` correctly refuses to guess — it bails out to a full run and says the clone is shallow.
2. Cache `.tia/` keyed on the base branch — provided nothing that runs on that branch executes code you have not reviewed. A pull request restores what any job on the base branch saved, under any key, and a planted graph selects too few tests. This repository's own CI builds other people's repositories on `main`, so its dogfood job keeps no cache and only does step 3.
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

**"WORKSPACE DID NOT RESOLVE"**, or **"X does not compile"** and everything runs. Almost always an unrestored solution: a project with no resolved references still parses, so discovery finds no tests in it. Run `dotnet restore` first — see [When the workspace does not resolve](#when-the-workspace-does-not-resolve).

**A constant set of tests runs on every change.** Check `widenings` for `Reflection`. Every reflecting or serializing member in the solution is unconditionally impacted — it can reach things no static edge records, and it is dangerous precisely when nothing reaches it — so everything that reaches one of those members runs whatever you changed. On NodaTime that floor is about 8 % of the suite. The widening names the file and the construct, so you can see which member is responsible; if it is a test helper wrapping `XmlSerializer` or `Activator.CreateInstance`, that is the whole test class it serves.

**Everything is selected on every change.** Check `widenings` for `ConstantInlining`, `SourceGenerator` or `ContentFile`, and `fullRunReasons` for a bail-out. Failing that, run `explain` on a test you did not expect: if it reports a real path, the change is genuinely central, and `docs/benchmarks.md` explains why a library's core selects most of its suite.

**A test you expected is missing.** Run `dotnet tia explain <TestName>`. It either prints the path or tells you nothing reaches it — and if nothing reaches it, that is a graph gap worth reporting. Passing a class name rather than a test name returns no match, since matching is on a suffix of a fully qualified test name; the near misses are listed so you can pick one.

The gaps found so far were all runtime paths the source never spells out: a serializer calling `IXmlSerializable.ReadXml`, an interpolated `$"{value}"` calling `value.ToString()`, a static member read running a type initializer. If your missing test fails through something in that family, it is the same shape and worth reporting.

**The base branch is not found.** `tia` needs the base revision in the local object store. In CI that means `fetch-depth: 0`; locally, `git fetch origin main`.

**A solution below the git root.** Point `--path` at the directory holding the solution and `--solution` at the solution file; diff paths are resolved against the repository root regardless, so a monorepo layout works. `.tia/` is written under `--path`.
