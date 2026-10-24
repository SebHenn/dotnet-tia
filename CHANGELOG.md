# Changelog

Notable changes to `dotnet-tia`. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While the major version is `0`, the CLI surface and the JSON schema may change between minor
versions. What will not change quietly is the safety model: a release that makes selection *narrower*
says so here, in its own entry, because that is the only kind of change that can turn a passing
suite into a missed test.

## [Unreleased]

### Added

- **Every JSON document carries `schemaVersion`** ([#44](https://github.com/SebHenn/dotnet-tia/issues/44)).
  `analyze`, `graph`, `explain`, `shadow`, `verify`, `replay` and `stats` now open their `--json`
  output with `"schemaVersion": 1`, which goes up when a field is removed, renamed or changes
  meaning.

- **A cache directory that `tia` creates holds a `.gitignore` containing `*`**, as pytest's and
  mypy's do, so a repository that does not list `.tia/`, or a custom `--cache-dir`, can't commit
  the cache by accident. A directory that already exists is left alone, because `--cache-dir` may
  name one that holds other files.

### Changed

- **The mutation gate draws distinct mutations, only where a test can see them** ([#39](https://github.com/SebHenn/dotnet-tia/issues/39)).
  `verify` drew a file and then a site in it, both with replacement, from every production project.
  On the fixture gate, 47 mutated samples covered about 14 distinct mutations, one of them six
  times. A file with no site cost a skipped sample. On NodaTime, 7 of 25 samples mutated benchmark
  projects that no test references, each followed by about 100 s of suite with nothing able to
  fail. Every site in the candidate files is now enumerated once and shuffled by the seed, then
  drawn without replacement. A dropped statement is one site per statement, not one per block. When
  `--mutate` asks for more than there are, the run stops with `site space exhausted after K distinct
  mutation(s)`. Only production projects that a test project reaches through references are
  mutated, and the rest are named in the header. The seed, which defaults to the clock, is printed
  first and is `seed` in `--json`. Per-sample analyses no longer write the graph cache. The next
  sample used to rebuild the previous sample's project, and `.tia` described a mutation once the run
  ended. A given seed now draws a different sequence than before.


- **`verify` exits 2 when it could conclude nothing, and keeps 1 for a defect** ([#36](https://github.com/SebHenn/dotnet-tia/issues/36)).
  A miss, a run with no usable sample and a refusal to start all exited 1. On 2026-10-20 the gate
  on tia itself had 0 usable samples (14 false out-of-memory results), and the Actions UI showed
  it exactly as it shows a selection miss. The two call for different responses, and `shadow` has
  always told them apart. A refusal (a dirty tree, no test projects, a runner that cannot write
  TRX) and a run with no usable sample now exit **2**, as `shadow` does for an inconclusive
  verdict. A miss and `--max-unobserved` being exceeded still exit **1**. So does a run abandoned
  part-way, because a sample did not restore a file, when a sample it finished had found a miss;
  the misses are now printed rather than dropped with the run. A script that treats any non-zero
  exit as failure sees no difference.

### Fixed

- **A changed top-level statement or assembly attribute now widens its project** ([#35](https://github.com/SebHenn/dotnet-tia/issues/35)).
  A changed line outside every declaration, in a file that declares no type, was recorded as
  unmapped and then selected nothing. That covers a minimal-API `Program.cs` without
  `partial class Program`, which `WebApplicationFactory<Program>` tests exercise, and a file of
  `[assembly: InternalsVisibleTo]` or `[assembly: CollectionBehavior]`. The report printed an
  `Unmapped` widening for it, and nothing was widened.

  Such a change now widens its project under that same `Unmapped` cause, which reruns the
  project's tests and those of every project that depends on it. Neither kind of code has a
  symbol to seed: statements compile into an entry point no declaration names, and an attribute
  applies to the whole assembly. In a file that holds either, a using directive or any other line
  outside a declaration widens as well, because it can rebind them. The rule follows the code
  wherever it is, so a hunk that runs from the last statement into a `partial class Program` below
  it widens too, and so does a statement or attribute that was removed or a deleted
  `AssemblyInfo.cs`, read from the base revision. The same
  goes for a `global using` in a hunk that also reaches a declaration: it now widens as
  `GlobalUsing`, where the declaration used to settle the hunk. The widening's `scope` is now the
  project rather than `path:lines`.

  This widens selection, but only for those files. A using directive, a namespace line or a
  comment beside the types of an ordinary file still marks those types and nothing more. A global
  using still widens as `GlobalUsing`, and generated documents keep their old rules. Otherwise the
  Razor generator's `[assembly: RazorCompiledItem]` in every view would widen each such project on
  every change. A change in a file holding only usings, a namespace and comments is now a
  diagnostic note rather than a widening that widened nothing.

- **`watch --run --once` exits non-zero when the tests it ran fail** ([#36](https://github.com/SebHenn/dotnet-tia/issues/36)).
  It ran the selection, discarded the exit code, and returned 0, so the form of `watch` meant for
  a script reported green on a red suite. `--once` now exits as `run` would with `--run`, and as
  `analyze` would without it. That includes **2** for a workspace that did not resolve, which it
  also used to report as 0. `docs/usage.md` gains the missing `watch` and `stats` rows in its
  exit-code table. The `explain` and `run` rows now list the 1 each of them can return: `explain`
  when no test matches the name, and `run` for a full run with no test project to invoke.

- **Two solutions of one kind at the root are refused, not chosen between alphabetically** ([#37](https://github.com/SebHenn/dotnet-tia/issues/37)).
  Without `--solution`, discovery sorted the matches and analysed the first. The choice showed only
  in the `--verbose` `Loading X...` line and the report did not record it, so a selection made on
  the wrong solution silently missed every test project only the other one contains. Two `.sln`
  files (or two `.slnx`, or two `.slnf`) now make `analyze`, `graph`, `run`, `explain`, `shadow`,
  `verify` and `watch` name the candidates, say to pass `--solution`, and exit **2**. The refusal
  bypasses the fall-back to a full run, which would otherwise have turned it into a full run of no
  project at all and exit 0. `run` settles the solution before it starts its build, and hands that
  build the same file. `replay` discovers per commit, so a commit holding several is skipped with
  the reason. A `.slnx` beside a `.sln` is still resolved by preferring the `.slnx`, unchanged. The
  report now carries the solution it analysed, relative to `--path` with forward slashes, as
  `solution` in the JSON and a `Solution` line at the head of the human-readable output.

- **A `verify` run that was killed no longer leaves its mutation for the next run to call your work** ([#38](https://github.com/SebHenn/dotnet-tia/issues/38)).
  The harness writes each mutation into the working tree and restores it in a `finally`, which a
  hard kill skips: a CI job timeout, a closed terminal, SIGKILL. The mutated file stayed on disk,
  and the next `verify` refused on a dirty tree and told the user to commit or stash it. That meant
  committing the mutation. Two killed runs recorded in `docs/benchmarks.md` did exactly this.

  Before each write the harness now saves a record in the cache directory
  (`.tia/mutation-in-progress.json`): the file's path, its original bytes, and hashes of the
  original and the mutated content. It is written through a temporary file and a move, and deleted
  once the restore has succeeded. The next `verify` reads it before checking the tree. If the file
  still holds the mutated bytes, it puts the original back and prints a line saying so. If it holds
  the original, only the record goes. If it holds anything else, someone has edited it since. The
  harness will not overwrite that edit, so it refuses with exit 2 and prints the
  `git checkout HEAD -- <path>` that restores the file and where the record is. A commit can
  carry a record into `.tia/` even though git ignores it, so a record is only acted on when it
  names a file inside the repository, outside `.git`, that is not a symbolic link, and holds real
  SHA-256 hashes. Any other record is refused the same way, and nothing is written.

  The clean-tree check also failed open: when `git status` itself failed, it returned as though the
  tree were clean. It now refuses with exit 2 as well. **`verify` can now exit 2**, in both cases,
  because neither is a defect found: the harness cannot tell what state the tree is in.

- **A changed line that looks like a diff header no longer hides the rest of its file's changes** ([#53](https://github.com/SebHenn/dotnet-tia/issues/53)).
  `git diff -U0` still prints the changed lines. A removed line starting with `-- ` or an added one
  starting with `++ ` arrives as `--- …` or `+++ …`, which is what a file header looks like. An SQL
  comment at column 0 of a verbatim string is enough. If the line held a `/`, the parser took it for
  a header and filed every later hunk of that file under the path it named, so the members those
  hunks changed selected no tests. Headers are now read only between `diff --git` and the block's
  first hunk. This widens selection, and only for such files.

- **A change under a `bin/` or `obj/` folder that is not a project's own output is a change again** ([#54](https://github.com/SebHenn/dotnet-tia/issues/54)).
  Every path with a `bin` or `obj` segment was dropped from the diff as build output. The SDK
  leaves out only a project's own `bin/` and `obj/`, though, so `src/App/Feature/obj/Parser.cs` is
  compiled and a `.props` under `tools/bin/` is imported. A change to either selected no tests. For
  a file git tracks, a `bin/` or `obj/` folder now counts as output only when it sits next to a
  project file. A rename is dropped only when both sides are output, because moving a source
  file into `obj/` still deletes that source file. Untracked files keep the broad rule, since
  that is where build output lands in a repository that does not ignore it. This widens
  selection, and only for such paths.

- **A graph cache that git tracks is no longer trusted** ([#52](https://github.com/SebHenn/dotnet-tia/issues/52)).
  Ignoring `.tia/` does not stop a commit from adding a file there, and the diff never reports
  `.tia/` as a change. So a branch could carry a `graph-*.bin` in which a changed method has no
  dependent tests, and `run` would select fewer tests than the change needed, with nothing to
  show why. A cache file that git tracks is now neither read nor overwritten. The run rebuilds
  the graph and logs `not using the graph cache: git tracks …`, naming the `git rm --cached` that
  fixes it. It costs one `git ls-files` per analysis.

- **Assembly- and session-wide setup reaches every test** ([#41](https://github.com/SebHenn/dotnet-tia/issues/41)).
  NUnit's `[SetUpFixture]`, MSTest's `[AssemblyInitialize]` and `[AssemblyCleanup]`,
  `[ModuleInitializer]`, xUnit v3's `[assembly: AssemblyFixture(typeof(T))]` and
  `[assembly: TestPipelineStartup(typeof(T))]`, and TUnit's `[Before]`/`[After]` for `Assembly`,
  `TestSession` and `TestDiscovery`, plus `[BeforeEvery]`/`[AfterEvery]`, usually sit in a class
  with no tests of its own. Discovery skipped such a class before wiring anything, so a change to
  one selected nothing. A hook in a test class reached only that class's tests. Each test project now
  has one hub: every such hook points at it, and it points at every test in the project, so the edge
  count grows with hooks plus tests. The graph cache format moves to 12. This widens selection, and
  only for changes to those hooks or what they call.

- **A change to a user-defined implicit conversion selects the code that converts** ([#42](https://github.com/SebHenn/dotnet-tia/issues/42)).
  An `implicit operator` is a call that nothing names. Its body could change and no test was
  selected, because only explicit casts recorded an edge to the operator. Every expression is now
  asked whether Roslyn applies a user-defined conversion to it, so an argument, a return, an
  initialiser, an assignment, a lambda body, a collection element and a `foreach` element all
  reach the operator. The deconstructing `foreach (var (a, b) in pairs)` recorded nothing at
  all, and now reaches the enumerator it uses and the `Deconstruct` it calls. On this
  repository, the graph walk costs about 2 % more CPU. The graph cache format moves from 10 to
  11, so the first run after upgrading rebuilds it. This widens selection.

- **Test and analyzer configuration forces a full run** ([#42](https://github.com/SebHenn/dotnet-tia/issues/42)).
  `*.runsettings`, `testconfig.json` and `<project>.testconfig.json`, `xunit.runner.json`, and
  `*.globalconfig` change how tests build or run without being code. One in a project directory
  used to widen only that project, as content. One at the root belonged to no project and selected
  nothing. Each is now a full-run trigger, like `.editorconfig`.

- **F# and VB test projects run** ([#44](https://github.com/SebHenn/dotnet-tia/issues/44)).
  `run` invoked C# test projects only, so an F# or VB test project was not run on any diff, a
  full run included, and a change it would have caught passed. Such a project is now recognised by
  the same rule as a C# one, the test framework it references with `IsTestProject` able to say no.
  It is listed under Projects as not analysed and run whole on a full run. On a selective run, it
  runs when anything in its transitive references holds an impacted member or was widened, itself
  included. It has no tests to select and its tests are in no count. `--json` lists it under
  `unanalysedTestProjects`, with whether it runs and why.

- **Smaller fixes from the v0.4.0 review** ([#44](https://github.com/SebHenn/dotnet-tia/issues/44)).
  `stats` no longer accepts `--solution` and `--verbose`, which it ignored, and the option table
  in `docs/usage.md` now matches what each command takes. The graph cache is saved through a
  temporary file of its own, so `watch` and a `run` saving at once cannot interleave into a file
  neither wrote. The run ledger is rewritten through a temporary file instead of in place. A
  custom `--cache-dir` inside the repository is no longer a change in the diff, or a dirty tree to
  `verify`, as `.tia/` never was.

- **No solution, or a `--path` that is not a repository, is refused with exit 2 instead of passing as an empty full run** ([#50](https://github.com/SebHenn/dotnet-tia/issues/50)).
  The same silent success as above, from three more setups: no solution or project in `--path`,
  a `--path` that does not exist, and one that is not inside a git repository. Each threw inside
  the analysis, the full-run fallback caught it, and the report named no project. `analyze` and
  `graph` printed that and exited 0, `run` refused with exit 1, and `verify` on a missing path
  crashed with a stack trace from `git`. All of them now print `Nothing to analyse:` with the
  reason and exit **2**, before any solution is loaded or build is started. **`watch` exits 2
  rather than 1 when it finds no solution**, and now also refuses a directory outside a
  repository before spending the load on it.

## [0.4.0] — 2026-10-22

This release is about safety. The mutation gate had been red every night since mid-August for
reasons outside this repository, and once those were cleared it found two real misses. Both are
fixed here, and both fixes **widen** selection. Overloaded test methods were dropped by
discovery ([#24](https://github.com/SebHenn/dotnet-tia/issues/24)), and overrides that only the BCL
calls had no path back to the tests that hand it the instance
([#30](https://github.com/SebHenn/dotnet-tia/issues/30)). The second has a measured cost, stated in
its entry: NodaTime's mean replay selection goes from 60.7 % to 65.3 %, and FluentValidation's is
unchanged.

An unrestored solution is now a failed load rather than a result. It used to be a confident report
about zero tests ([#20](https://github.com/SebHenn/dotnet-tia/issues/20)). **`analyze` and `graph`
can now exit 2**, so scripts that treat any non-zero exit as failure will see one where they saw
success before. That is the only behaviour change a caller has to act on.

**Nothing here deliberately narrows selection.** The graph cache format moves from 8 to 10, so the
first run after upgrading rebuilds it.

### Added

- **`verify` reports the mutations no test observed** ([#21](https://github.com/SebHenn/dotnet-tia/issues/21)). A sample
  whose mutation broke no test is correctly `OK` about selection, since there was no failure to
  miss. But it is the one fact about the *suite* that only `verify` can establish, and it used to be
  counted by hand from the per-sample lines. In the run that prompted the issue, one of six
  unobserved samples was a live defect that every existing assertion passed over. Such a sample now
  reads `UNOBSERVED`, the summary adds `6 of 8 usable mutation(s) were observed by no test` with
  their indices, `--json` carries them as `unobserved`, and the validation harness's markdown report
  counts them. `--max-unobserved <n>` fails the run above `n`. It is off by default, because it
  gates the suite and not the tool, and when it trips it prints its own `FAIL` line beneath the
  selection verdict rather than replacing it.

### Changed

- **A skipped `verify` sample says why the analysis widened, not just that it did** ([#20](https://github.com/SebHenn/dotnet-tia/issues/20)). The
  per-sample line read *"the analysis bailed out to a full run"* and the summary then guessed at the
  commonest cause - a modified project file - and sent the reader to a `git status` that was clean.
  `analyze` has always printed the actual reason (`! Directory.Build.props changed - it applies to
  every project beneath it`); that line is now carried through to the skip, and the summary points at
  it rather than guessing.

### Fixed

- **A mutation that allocates without end no longer takes the machine down with it** ([#28](https://github.com/SebHenn/dotnet-tia/issues/28)).
  Each mutated suite run already had a time budget, because mutating a loop is one of the commonest
  ways to make one that never ends. Nothing bounded memory, and a loop that allocates runs away
  faster than any timeout. On NodaTime, `Instant <=` mutated to `<` took a test host from 0.6 GB to
  15 GB in three minutes. On CI the runner died with nothing in its log but a shutdown signal, and
  on a workstation the same sample swaps the machine.

  Every process of a mutated run now gets `DOTNET_GCHeapHardLimit`: a quarter of the machine's
  memory, at least 2 GiB. A quarter, because a multi-targeted test project runs a host per framework
  in parallel beside the harness, MSBuild and the compiler server. A run that hits the limit is its
  own outcome, `MEM`, counted as `ran out of memory`. It is never usable and never a miss: under the
  limit an `OutOfMemoryException` can land on any test running alongside the runaway one, so the
  failures it leaves are not evidence of anything. The baseline run is not limited, and a value
  already exported is left alone. The CI-only cap added for the nightly is gone.

- **A change the BCL reaches by dispatch now selects the tests that hand it the instance** ([#30](https://github.com/SebHenn/dotnet-tia/issues/30)).
  Interface and override edges stopped at the solution's edge, because a BCL member has no node.
  So an override of `object.Equals`, `ToString` or `Comparer<T>.Compare`, or an implementation of
  `IComparable<T>` or `IComparer<T>`, was reached by nothing: its caller is a member the graph
  does not have, and often runs inside the BCL itself. NodaTime's
  `PeriodTest.Comparer_DurationablePeriods` calls `IComparer<Period?>.Compare` on what
  `Period.CreateComparer` returns, which lands in `PeriodComparer.Compare`, which calls
  `Period.ToDuration`. Breaking `ToDuration` failed the test with no path to draw. The nightly
  mutation gate found it.

  The BCL can only call into an instance the solution handed it, and that hand-over is in the
  source. It is either a conversion to an external type (`CreateComparer` returning a
  `PeriodComparer` as an `IComparer<Period?>`, a value boxed into `Assert.AreEqual(object, object)`)
  or an external generic over the type (`Assert.Equal<Duration>`, `List<Instant>.Sort`). Every member
  the BCL can dispatch to is now connected, through one hub per type, to every member that hands
  that type over. Hubs are linked along the solution's own type hierarchy, so a value handed over as
  its base type still reaches a subclass's override. Interfaces only serializers call, such as
  `IXmlSerializable`, are left out, because serializers are already reflection seeds.

  This widens selection. Over the 20 first-parent commits the nightly replays:

  | Repository | Mean selection before | After |
  |---|---:|---:|
  | NodaTime | 60.7 % | 65.3 % |
  | FluentValidation | 52.9 % | 52.9 % (identical on every commit) |

  On NodaTime, two pattern-parser commits go from 447 to 682 tests. A coverage commit that touches
  `HashCodeHelper` goes from 811 to the full suite, because a changed `GetHashCode`/`ToString` on
  the core value types is reachable from every assertion that boxes one. The original miss now
  selects 291 of 3,748, including the test. Cold analysis on NodaTime is unchanged: a median of
  18.3 s before and 18.4 s after.

  The first version connected these members to their type's constructors instead. It closed the
  miss and took NodaTime to 73.5 %, because a struct like `Duration` is constructed almost
  everywhere. A struct that exists without any hand-over in the source, such as a `default` value
  that only BCL code touches, is still not covered.

  The graph cache format moves to 10.

- **A change reached only through a later overload of a test now selects the test** ([#24](https://github.com/SebHenn/dotnet-tia/issues/24)).
  Discovery kept the first test method of each name and dropped the rest, so of NodaTime's two
  `SimpleWeekYearRuleTest.GetWeeksInWeekYear` overloads only the `(int, int)` one existed in the
  graph. The other one is the overload that calls `WeekYearRules.FromCalendarWeekRule`, and
  mutating that method failed 78 test cases the selection had left out. The nightly mutation gate
  reported it for two weeks, behind a scheduled run that was already red for unrelated reasons.

  Overloads are still one test, because runners report them under one name and no filter can
  pick between them. But every overload now has an edge to that test, so reaching any overload
  reaches it. On the NodaTime mutation the selection goes from 276 to 280 of 3,748. This only
  ever widens selection.

  The same line also let a non-test helper hide a test: it de-duplicated by name *before* checking
  for a test attribute, so a private helper declared above a test of the same name made the test
  disappear entirely. Only test methods are de-duplicated now.

  The graph cache format moves to 9. A version-8 fragment of a test project that has not changed
  would otherwise be reused without the new edges and keep missing the same tests.

- **An unrestored solution is now a failed load, not a result** ([#20](https://github.com/SebHenn/dotnet-tia/issues/20)). `MSBuildWorkspace` needs
  `obj/project.assets.json` to resolve a `PackageReference`, and a fresh CI runner has none. The
  solution still opens, project references still resolve, most of the graph is still built - and
  xunit's `[Fact]` is an error type, so no method is a test and the graph is empty of the one thing
  it exists to select. `graph` printed `0 tests discovered across 1 test project(s)` and exited **0**.
  Every command downstream was then vacuous and reported success: `analyze` selected a full run of
  nothing and printed its break-even arithmetic, and `shadow` reported *"nothing at risk"* while
  watching an empty set - the worst of them, because `shadow` exists to be the safety evidence. Only
  `verify` caught it, and only because it refuses to pass without a usable sample.

  A test project that yields **zero tests** is now a load failure rather than a number. It is
  reported under its own heading - `WORKSPACE DID NOT RESOLVE` - separately from the ordinary
  full-run reasons beside it, because a full run is a decision and this is the absence of one. Where
  `obj/project.assets.json` is missing the message says the restore has not happened, in those words;
  where it is present, it says so, so nobody is told to run a command they have already run. A
  project that deliberately contains no tests declares `<IsTestProject>false</IsTestProject>`, which
  the tool already honours to leave a project out of the suite.

  Acting on it, per command:
  - `analyze` and `graph` **exit 2**. A full run still exits 0, as before: it is a decision, and
    these commands report decisions.
  - `run` refuses before invoking anything. Its existing guard could not catch this - the test
    projects are all present and enumerable, they simply hold nothing.
  - `shadow` reports `INCONCLUSIVE` naming the empty suite, instead of `NOTHING AT RISK`.
  - `verify` refuses to start, instead of spending every sample to confirm nothing was missed out of
    a set with nothing in it.
  - `graph` does not write its cache from such a load. Warming the cache is the whole point of the
    command, and leaving a fragment behind that records a suite as empty is the opposite of it.

## [0.3.0] — 2026-08-21

The analysis got cheaper, and this release is mostly the measurements that made it so. `A` - the cost
of deciding what to run - falls from 6.85 s to 3.07 s on a warm run, and from 7.4 s to 2.35 s on the
run a developer actually makes. Three new commands answer the questions that decide whether any of
this is worth adopting: `replay` for "would it have paid off on my history", `stats` for "what has it
cost here", and `watch` for the edit-test loop the one-shot commands are the wrong shape for.

**Nothing here deliberately narrows selection.** One change comes close enough to say so plainly:
resolving a diff's symbols from the cached graph rather than from a fresh compilation is the change
in this release most able to introduce a miss, and it is described under Changed with the reason it
is sound and the gate it was held to.

### Added

- **`dotnet tia watch`** - keeps the workspace loaded and re-analyses on every edit, with `--run` to
  run the impacted tests each time, plus `--fail-fast` and `--once`. Measured on this repository
  against the same one-line edit: **9.07 s for a fresh process against 2.35 s per edit**. Two savings
  rather than one - the 3.7 s MSBuild evaluation is not paid again, and the graph rebuild costs 1.4 s
  instead of 3.9 s because a resident Roslyn keeps the parsed trees of every document that did not
  change. The refresh re-reads every document and compares **content**, not timestamps: a document
  left stale keeps its project's content hash matching, so a missed change would be a missed test
  rather than a slow run. That costs 0.73 s on the first sweep and 0.01-0.02 s afterwards. A file
  appearing, or a project, `.props`, `.targets` or solution file changing, reloads the workspace
  outright and says so - which files a project compiles is MSBuild's answer, and a refreshed snapshot
  may not improvise it.
- **`dotnet tia replay`** - walks your own history and reports what selection would have done on each
  commit, so "is this worth it for my repository?" is answerable without cloning anyone else's.
  `--commits <n>` (default 20), `--first-parent`, `--output <file>`, `--json`. It checks out
  historical commits, so it refuses a dirty tree and returns to where it started. It deliberately
  takes no `--solution`: a path pinned to today's layout silently skipped every commit before a
  solution move, turning a replay of 20 commits into a report on 1. Discovery runs per checkout
  instead. Zero rows replayed exits non-zero rather than publishing "no data" as "no benefit".
  A replay measures **selection ratio and widening rate only** - real commits are almost all green,
  so it says nothing about misses; `verify` and `shadow` are what answer that.
- **`dotnet tia stats`, and a run ledger behind it** - what selection has actually cost or saved
  here, from runs that happened rather than from a claim. `run` now records the suite time it
  observed, which is the one term of the break-even this tool spawns and had never measured:
  "Worth it if the full suite takes more than 14s" could previously be printed by a tool that had
  just watched that suite take two.
- **`dotnet tia run --fail-fast`** - stops at the first failing invocation instead of running the
  rest. The default still runs everything selected, because a pull request needs the complete list.
- **The nearest tests run first, in their own invocation.** A project's selection is divided so the
  tests closest to the change go to a first `dotnet test` and the remainder follows. The guard is the
  feature: the extra invocation is paid on every run while the saving lands only on a run that fails,
  so the projected saving must be worth three times the estimated start-up before anything divides,
  and that estimate comes from the ledger. Two ways a split filter can be wrong while looking right -
  a wave matching into the remainder, and two filters together matching what one would not - are
  refused at plan time by asking the dialect rather than discovered later. Measured across 137
  mutation samples on five repositories: ordering buys what the selection has room for, from 100 % of
  the ordered run at n=1 down to 19-24 % where selections run to dozens.
- **`run` builds while it analyses**, then invokes `dotnet test --no-build`. The two do not depend on
  each other, so whichever is shorter is free: 4.59 s of analysis and 2.16 s of build cost 6.75 s in
  sequence and **5.25 s** together. The saving is bounded by the smaller of the two, which is what
  makes it worth having - it pays most on exactly the repositories where this tool pays least. A
  failed build is reported as a failed build, with its own exit code and no tests run. Anything after
  `--` disables the arrangement outright rather than being reasoned about, because `--configuration
  Release` alone changes what "the build" means and `--no-build` against the wrong build tests stale
  binaries. `--no-prebuild` disables it by hand.
- **Phase timings in `--json`.** The phase that turned out to be 45 % of a warm run had no name at
  all, and 2.56 s of every run was unattributed. It is now 0.27 s, which is the difference between
  optimising and guessing.

### Changed

- **A diff's changed symbols are resolved from the cached graph, not from a fresh compilation.**
  This is the largest performance change in the release and the one most able to introduce a miss, so
  the reasoning is worth stating. `ChangeResolver` forced a compilation for four things, and each is
  now answered by something the fragment already stores: changed lines map onto stored declaration
  spans, a per-file bind verdict is recorded at rebuild time, generator output is a stored count, and
  the old side's type index is read out of graph keys. It is sound because a fragment is reused
  **only** when its project's content hash matches, so those spans describe byte for byte the file
  the diff is about, and no key is derived or invented. Cache format 8, so the first run after
  upgrading rebuilds. A/B over 23 changed files, three runs per arm, both binaries staged: elapsed
  7.81 s to 5.47 s (**-30 %**), change resolution 4.05 s to 1.72 s (**-58 %**), compilation CPU
  1.35 s to zero. Ranges disjoint; the reports agree field for field.
- **One `git diff` for the whole change set instead of one per file**, and every changed file's base
  revision read concurrently. `diffSeconds` -68 %, `oldSideFetchSeconds` -51 %. `git show` answers in
  about a millisecond and costs about thirty to start, so a sixteen-file change spent half a second
  waiting for processes rather than for git.
- **Content hashing runs in parallel**, like the surface hashing beside it. `fingerprintSeconds`
  0.14 s to 0.07 s on a warm run.
- **The graph cache is no longer rewritten when nothing was rebuilt.** A warm run used to serialise
  every project and write several hundred kilobytes to record nothing that had changed.
- **The README's break-even guidance was wrong by an order of magnitude.** It said the tool pays off
  above about a minute of suite time; measured, it is about ten seconds. That error pointed adopters
  away from repositories where this works.
- **Two planned optimisations were measured and declined, and the measurements are the deliverable.**
  Caching the workspace shape saves nothing on the run that actually happens - a project that must be
  rebuilt needs a compilation, which needs the project loaded - because a warm run is the second run
  over an unchanged tree, and nobody runs this tool twice without editing in between. The "generator
  probe" was neither a probe nor on that run: 1.26 s of it was a parse and 0.003 s was generators.
  Both are written up in `docs/benchmarks.md`, including what would change the answer.

### Fixed

- **A nested class was matched by a VSTest filter and reported by nobody.** `BuildArguments` emits
  `Namespace.Class.` for a class selected whole, while `ExtraMatches` compared candidates against the
  selected *method* names - so the tests of `App.Alpha.Nested` ran under a filter for `App.Alpha` and
  appeared in no widening. Both now derive from the same terms. Found by asking the dialect whether a
  split filter would be safe, which is a question nothing used to ask.
- **`generatorProbeSeconds` reported a parse as the cost of source generators.** The timer sat
  outside the call that asks Roslyn for a project's generated documents, and that call produces the
  compilation on the way. The compilation is now realised before the timer starts, where it is
  charged like every other compilation in the tool.
- **A duration printed as `6,8s` beside `6.8s` on a comma-decimal machine.** Report formatting is
  invariant.

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
- **An SDK too old for the project was reported as the project not compiling.** Pointed at a
  `net10.0` project, MSBuild 9 does not refuse the load — it produces a project with no references
  resolved and raises no failure diagnostic at all, so the mismatch was only ever noticed as
  `CS0518: Predefined type 'System.Object' is not defined`. The run bailed out to a full run either
  way, so this was never unsafe; it blamed the project for the toolchain, which is the same
  wrong-target complaint [#13](https://github.com/SebHenn/dotnet-tia/issues/13) was opened about.
  The reason now names the registered MSBuild and the framework it cannot reach. `CS0518` alone
  does not trigger it — an unrestored project produces the identical error — so the project's
  target framework has to actually outrun the SDK reading it.
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

[Unreleased]: https://github.com/SebHenn/dotnet-tia/compare/v0.4.0...HEAD
[0.4.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/SebHenn/dotnet-tia/releases/tag/v0.1.0
