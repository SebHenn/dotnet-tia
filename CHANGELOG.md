# Changelog

Notable changes to `dotnet-tia`. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While the major version is `0`, the CLI surface and the JSON schema may change between minor
versions. What will not change quietly is the safety model: a release that makes selection *narrower*
says so here, in its own entry, because that is the only kind of change that can turn a passing
suite into a missed test.

## [Unreleased]

### Fixed

- **A compile setting changed in an imported file no longer leaves stale graph fragments in the
  cache** ([#115](https://github.com/SebHenn/dotnet-tia/issues/115)). A project's content hash
  covered its project file's own text, not what it imports, so a `DefineConstants` or `LangVersion`
  set in `Directory.Build.props` changed which `#if` code compiled without moving the hash. The
  commit that made the change ran everything, but afterwards the fragments built before it were
  reused with the edges the new symbols add missing, and a later change those edges lead to
  selected too little. The hash now includes the evaluated preprocessor symbols, language version,
  documentation mode, output kind, nullable context and `AllowUnsafeBlocks`. Each project rebuilds
  once after upgrading. A local cache, `tia server`, `watch` and the hook were affected. The action
  keeps no cache, so it was not.
- **The documented action examples pin the current release** ([#116](https://github.com/SebHenn/dotnet-tia/issues/116)).
  They still said `@v0.6.0` beside inputs that 0.7.0 added (`comment`, `annotations`), and a test
  now fails when a pin in `README.md` or `docs/` disagrees with the version. The report schema's
  description and the usage guide no longer mention a `run --json`, which does not exist: `run`
  writes the report with `--report <file>`.
- **A change to what a custom attribute overrides reaches the code that carries it**
  ([#119](https://github.com/SebHenn/dotnet-tia/issues/119)). A test framework constructs an
  attribute by reflection and calls its overrides: xUnit's `DataAttribute.GetData` and
  `BeforeAfterTestAttribute.Before`, an NUnit `ITestAction`, an ASP.NET filter's
  `OnActionExecuting`. Nothing in the solution calls them, so editing a `[CsvData]` or
  `[UseCulture]` attribute in a test project selected none of the tests that use it. An attribute
  usage now counts as handing the attribute to code outside the solution: a change to one of those
  overrides reaches the method that carries it, every member of a class that carries it, and every
  test when it is applied to the assembly. This widens a selection, and only for changes to such
  overrides. **The graph cache format goes from 15 to 16**, so the first run after upgrading
  rebuilds the cache once.

## [0.7.0] — 2026-10-25

The release that puts tia's answers where a reviewer already looks, and that closes the gaps
0.6.0 left in CI.

- **On the pull request:** untested changes as annotations on the diff, and the summary as one
  sticky comment.
- **In CI:** shards balanced by recorded test time instead of test count, a JSON Schema for the
  report, and a job summary that stays under GitHub's size limit on a large refactor.
- **At a keyboard:** `explain --symbol` answers which tests reach a symbol, `tia server` serves
  `explain` and queues analyses instead of turning them away, and `tia hook install` stops a push
  whose selected tests fail.

**One change narrows what runs:** a filter too long for the command line now goes in a file
([#90](https://github.com/SebHenn/dotnet-tia/issues/90)), so a project that used to run whole
because of it now runs only its selection. MSTest 4 projects are now recognised, which matters
for the same reason. **The graph cache format goes from 14 to 15**, so the first run after
upgrading rebuilds the cache once. The JSON gains fields (`untestedChanges[].line`, the `shard`
weighting fields, `filterInFile`, `hasRunSettings`) and loses none, so `schemaVersion` stays 1.

### Added

- **`--shard-durations`: balance shards by recorded test time** ([#91](https://github.com/SebHenn/dotnet-tia/issues/91)).
  `run --record-durations` keeps a per-test moving average in `.tia/durations.json`, read from the
  TRX. With `--shard i/n --shard-durations <file>`, a class weighs what its tests took instead of
  how many there are. On this repository, the slowest of two shards goes from 153 s to 100 s of
  recorded test time; on NodaTime, the slowest of four goes from 30 s to 13 s. Every shard has to
  be given the same file, so a shard reads only the one it is given and refuses one it can't
  read. The report's `shard` gains `weighting`, `durationsFingerprint` and `estimatedSeconds`. TRX
  results now carry their duration. Without the option, sharding is unchanged.
- **The action can post its summary as a pull request comment** ([#93](https://github.com/SebHenn/dotnet-tia/issues/93)).
  With `comment: true` it posts one comment and updates it on later pushes. A hidden marker finds
  it, and `comment-key` keeps shards apart. It needs `pull-requests: write`; without it, or on a
  fork, it warns or notes and doesn't fail. The comment and the job summary are one file written
  by `--summary`, so they always agree. The comment is posted from a separate step, so the token
  never reaches the step that runs the pull request's code. This repository's dogfood job turns it
  on.
- **`tia hook install`** ([#99](https://github.com/SebHenn/dotnet-tia/issues/99)) writes a git
  hook that runs the selected tests and stops a push when one fails. Pre-push measures against the
  branch's upstream, or the remote's default branch for a branch never pushed. `--pre-commit`
  measures against `HEAD`. It goes where git looks for hooks, `core.hooksPath` included, and never
  overwrites or removes a hook it did not write. `hook uninstall` removes it.
- **`tia server` serves `explain`** ([#96](https://github.com/SebHenn/dotnet-tia/issues/96)). The
  query runs on the server, next to the graph it reads, and only its answer comes back. Warm, it
  takes 0.9 s instead of 4.9 s on this repository, and 1.0 s instead of 6.8 s on NodaTime. The text
  and `--json` output are byte for byte what an in-process `explain` prints. `explain` gains
  `--no-server`.
- **`explain --symbol <name>`** ([#98](https://github.com/SebHenn/dotnet-tia/issues/98)): the
  reverse of `explain <test>`. It lists the tests that reach a symbol, with their paths, which are
  the tests a change to it would select. When none does, it lists the nearest symbols a test does
  reach, up to four steps away through what it uses, the type that declares it, and what uses it,
  each with the route and a test confirmed by the real traversal. A new method on a tested type
  points at that type, one step away. It is read-only, and `tia server` serves it.
- **A JSON Schema for the report** ([#94](https://github.com/SebHenn/dotnet-tia/issues/94)):
  [`docs/report.schema.json`](docs/report.schema.json), JSON Schema 2020-12, for what
  `analyze --json`, `run --json` and `--report` write. Each GitHub release also carries it as an
  asset. It is exported from the report types, not written by hand. Tests hold the published file to
  that export, validate hand-built and real reports against it, and fail when a field of
  `schemaVersion` 1 disappears without the version going up. `schemaVersion` is required, with
  `const: 1`.
- **Untested changes as annotations on the pull request diff** ([#92](https://github.com/SebHenn/dotnet-tia/issues/92)).
  `--annotations` on `analyze` and `run` writes a GitHub `::warning` on each untested change's
  declaration line, or `::error` under `--fail-on-untested`. GitHub shows ten of a level per step,
  so ten are written and a `::notice` counts the rest. Outside GitHub Actions it writes nothing.
  The action turns it on by default (`annotations: true`). The report gains
  `untestedChanges[].line`. **The graph cache format goes from 14 to 15**, because nodes now record
  their declaration's line: the first run after upgrading rebuilds the cache once.

### Changed

- **A filter too long for the command line is passed in a file instead of being dropped**
  ([#90](https://github.com/SebHenn/dotnet-tia/issues/90)). **This narrows what runs:** a project
  whose filter exceeded `--max-filter-length` used to run whole, and now runs only its selection.
  On Microsoft.Testing.Platform the filter goes in a response file after `--`, which the test
  application reads itself, on the VSTest bridge and the platform's own `dotnet test` alike. On
  VSTest it goes in a generated RunSettings file as `TestCaseFilter`, passed with `--settings`. A
  VSTest project with run settings of its own (`RunSettingsFilePath`, or `--settings` after `run
  --`) still runs whole, as before, because a second `--settings` would replace them.
  `dotnet test @args.rsp` was not enough: MSBuild relaunches the runner with the expanded command
  line, which failed at 48,000 characters on both runners. Verified with filters of 48,000 and
  54,000 characters, and on NodaTime, where the same selection ran the same 927 and 1,544 test
  cases inline and from the file. The report marks such a project `filterInFile: true`, and adds
  `hasRunSettings`; anything else that builds a command from `filterArguments` has to do the same.
  Shards use the file too, so a long share no longer sends its project to one shard whole.
- `run --failed-first` on the platform's own `dotnet test` now puts `--report-trx` before any `--`.
  After one, that command reads an existing results directory as a directory given in place of
  `--project`, and refuses to run.

### Fixed

- **MSTest 4 projects are recognised as MSTest** ([#101](https://github.com/SebHenn/dotnet-tia/issues/101)).
  MSTest 4 renamed its framework assembly to `MSTest.TestFramework`, and tia only knew the old
  name, so an MSTest 4 project read as an unknown framework with an unknown runner. Its tests were
  still discovered and selected, but it wrote no TRX for `run --failed-first`, and a filter too
  long for the command line ran the project whole instead of going in a file. Found by the new
  MSTest fixtures: one on VSTest, one on MSTest's own runner under the platform's `dotnet test`.
  Both are selected from and executed by the integration suite, including `[TestInitialize]`,
  `[ClassInitialize]`, `[DataRow]` and a filter passed in a file, and both join the mutation gate.
- **`tia server` queues a second analysis instead of turning it away, and stops for a new version**
  ([#95](https://github.com/SebHenn/dotnet-tia/issues/95)). The pipe accepted one connection at a
  time, and a client waited two seconds for it, so a command started while the server was analysing
  loaded the solution itself. The server now always has a connection waiting and answers analyses
  in turn. On the fixture, three analyses started together, the first reloading the workspace, were
  all served; before, two of the three loaded in-process. A server also stops the first time a
  client of another version asks it anything, instead of answering every later command with
  "version mismatch" until someone restarted it. The client says so on stderr, and `server start`
  replaces such a server. A second server for the same solution now refuses to start.
- **The run ledger no longer averages served and in-process analyses together**
  ([#97](https://github.com/SebHenn/dotnet-tia/issues/97)). Since `tia server`, an analysis takes
  about a second when served and about five when loaded in-process, and the ledger's `A` averaged
  the two, so the break-even and `run`'s advice moved with how often the server happened to be
  running. Each run now records `served`, and `A` comes from the runs analysed the way the current
  one was, once there are three of them; until then, from all runs. `stats` judges the way the next
  run would be analysed (by the server while one is running here) and shows both averages. Records
  written before this read as in-process. Selection is unchanged: the ledger decides only whether to
  split a selection into two invocations, never which tests run.
- **The job summary caps its lists** ([#100](https://github.com/SebHenn/dotnet-tia/issues/100)), at
  50 untested changes, 100 widenings and 25 full-run reasons or load failures, and clips any single
  detail at 500 characters. Each capped list ends with a count of what it left out. GitHub drops a
  step summary over 1 MiB, and a large refactor could produce thousands of untested changes, so the
  summary would vanish exactly when it had most to say. 10,000 untested changes and 5,000 widenings
  now render to less than 64 KiB. The JSON report is unchanged and still lists everything.
- **xUnit v3 filters stay class filters when a selected class has a single test**
  ([#88](https://github.com/SebHenn/dotnet-tia/issues/88)). On Microsoft.Testing.Platform, class
  and method filters cannot be mixed, so one fully selected one-test class used to turn every other
  class back into a list of methods. In tia's own two-way shard split, one shard's filter shrank
  from 16,176 to 1,083 characters. A shorter filter starts faster, and before #90 one too long for
  the command line was dropped and its project run whole. The tests that run are the
  same; VSTest and TUnit filters are unchanged.

## [0.6.0] — 2026-10-24

The release that makes tia something to adopt in CI rather than only in a terminal, and that
gives back the time the workspace load costs.

- **In CI:** a GitHub Action, a job-page summary, sharding across jobs, and the report a later job
  needs to recognise a selection miss, which [ci-failure-intelligence](https://github.com/SebHenn/ci-failure-intelligence)
  now reads.
- **For a reviewer:** the changed code that no test reaches, listed on every selective run.
- **At a keyboard:** `tia server` keeps the workspace loaded, so a warm `analyze` takes 0.85 s
  instead of 5.2 s here. `--failed-first` and `--parallel` shorten the run itself.

**Nothing here changes which tests are selected.** Every addition is either a report, an order, a
split of the same selection across jobs, or a faster way to the same analysis. The graph cache
format is unchanged at 14. The JSON gains fields (`untestedChanges`, `uncheckedChanges`, `shard`,
`skippedTests`, `writesTrx`, `lastFailed`) and loses none, so `schemaVersion` stays 1.

### Added

- **A selective run lists the changed symbols that no test reaches** ([#70](https://github.com/SebHenn/dotnet-tia/issues/70)),
  as `untestedChanges` in `--json` and in its own section of the text report. Advisory: selection
  does not change. `--fail-on-untested` makes `analyze` and `run` exit 1 when the list is not empty.
  The check reuses the selection's traversal, and gives each symbol it cannot settle that way a
  traversal of its own, up to 64 (`uncheckedChanges` counts the rest; the flag lifts the cap).
- **A GitHub Action** ([#71](https://github.com/SebHenn/dotnet-tia/issues/71)): `uses: SebHenn/dotnet-tia@<release>`
  installs the tool at the action's own release, diffs against the pull request's base, runs the
  selected tests, and appends a summary to the job page. This repository's CI runs it on every pull
  request, with the tool the pull request builds.
- **`--summary <file>`** on `analyze` and `run` appends that summary as Markdown, such as to
  `$GITHUB_STEP_SUMMARY`.
- **`tia server start|stop|status`** ([#76](https://github.com/SebHenn/dotnet-tia/issues/76)) keeps
  the workspace loaded in a background process. `analyze` and `run` send it their analysis and skip
  loading the solution: a warm `analyze` takes 0.85 s instead of 5.2 s here, and 1.3 s instead of
  8.3 s on NodaTime. The served report is the one a fresh process would produce. The server reopens
  the workspace when a project, props, targets, solution, assets, `global.json` or `nuget.config`
  file changed or a source file appeared, and otherwise refreshes changed documents. Any problem with
  the server falls back to analysing in-process, and `--no-server` skips it. The pipe is named from
  the user, the repository and the solution, and is current-user-only at both ends.
- **`--report <file>` and `--report-skipped`** on `analyze` and `run`
  ([#75](https://github.com/SebHenn/dotnet-tia/issues/75)). The first writes the JSON report to a
  file, which is how `run`'s selection reaches a later job. The second adds `skippedTests`, the
  tests the run leaves out, per project, so that a later failure of one can be recognised as a
  selection miss. The action's `report` input sets both.
- **`--shard <i>/<n>`** on `run` and `analyze` ([#73](https://github.com/SebHenn/dotnet-tia/issues/73))
  keeps part `i` of `n` of what would run, so a selection can be split across parallel CI jobs
  with no coordination between them. Whole test classes are balanced by test count. The shards are
  disjoint and together run exactly what one job would. A project that cannot be split within a
  fixed filter length goes to one shard whole. `--json` reports `shard`.
- **`run --parallel [N]`** ([#74](https://github.com/SebHenn/dotnet-tia/issues/74)) runs the
  selected test projects side by side, up to N at once (one per core by default). It applies only
  against built binaries, and otherwise says why and runs them in turn. Each project's output is
  printed whole when it finishes, and the exit code is still the first failing project's in project
  order. Measured on NodaTime, a full run with prebuilt binaries: 37 s to 33 s.
- **`run --failed-first`** ([#72](https://github.com/SebHenn/dotnet-tia/issues/72)) runs the
  classes of the selected tests that failed last time as their own first invocation, and records
  this run's failures from TRX in `.tia/last-failed.json`. Order only: selection does not change.
  A Microsoft.Testing.Platform project without `Microsoft.Testing.Extensions.TrxReport` is not asked
  for TRX, and `--json` reports `writesTrx` per project and `lastFailed` on a wave.

### Fixed

- **A `--` in what you pass to `run` no longer pulls the filter after it**
  ([#79](https://github.com/SebHenn/dotnet-tia/issues/79)). The generated filter was appended after
  everything following tia's own `--`. So `run -- -- RunConfiguration.MaxCpuCount=1` put
  `--filter` among VSTest's inline RunSettings, and on the Microsoft.Testing.Platform bridge a
  second `--` reached the runner. The filter now goes before your `--` for VSTest, and joins your
  runner arguments behind a single `--` on the bridge.

## [0.5.1] — 2026-10-24

Two fixes found by measuring 0.5.0 on NodaTime.

**This release narrows selection**, once, and on purpose: a change that reaches a value converter
no longer selects everything that merely names the converted type
([#65](https://github.com/SebHenn/dotnet-tia/issues/65)). 0.5.0 had widened that case far past
what can run the converter. On NodaTime, mean replay selection goes back from 71.0 % to 62.8 %, and
the mutation gates on NodaTime and MediatR found no miss. The entry below says what it gives up.

A multi-targeted test project now runs each framework once instead of once per framework
([#67](https://github.com/SebHenn/dotnet-tia/issues/67)), which halves `NodaTime.Test`.

The graph cache format moves from 13 to 14, so the first run after upgrading rebuilds it.

### Changed

- **Narrows selection:** a value converter now reaches where its type is handed over, not
  everything that names the type ([#65](https://github.com/SebHenn/dotnet-tia/issues/65)). This
  covers `[TypeConverter(typeof(C))]`, and `[JsonConverter(typeof(C))]` from System.Text.Json or
  Newtonsoft, on a type `T`.
  - **What changed:** nothing calls `C`'s constructor, so the construction step from
    [#40](https://github.com/SebHenn/dotnet-tia/issues/40) continued from it into every member of
    `T` and everything that names `T`. On NodaTime, whose core types all carry a converter, a
    change to `PeriodPattern` selected all 3,742 tests instead of 682, and mean replay selection
    rose from 62.8 % to 71.0 %. Both `C` and `C`'s own dispatch hub now lead to `T`'s dispatch hub:
    the places that hand a `T` to code outside the solution. Reflection inside the solution,
    which is where `TypeDescriptor.GetConverter` and the serializers are, was already treated as
    always impacted.
  - **What it gives up:** a test that reached `C` only because it names `T` without handing a `T`
    over. Such a test cannot run `C` except through reflection, which stays covered.
  - **What it adds:** a change to `C.ConvertFrom` or `ConvertTo`, where a converter's behaviour
    lives, used to reach no test at all and now reaches the same places.
  - **Unchanged:** other attributes on a type, such as `[ServiceFilter]` or NUnit's
    `[TestFixtureSource]`, still reach every member of the type, because what they name is
    constructed to run those members.
  - **Cache:** the graph cache format moves from 13 to 14, so the first run rebuilds it.

### Fixed

- **A multi-targeted test project no longer runs every framework once per framework**
  ([#67](https://github.com/SebHenn/dotnet-tia/issues/67)). Such a project is loaded once per
  target framework, and each instance was invoked as a plain `dotnet test <project>`, which runs
  every framework the project declares. So a project with two frameworks ran each of them twice.
  - **`run` and `watch`:** instances that selected the same tests now run as one invocation, which
    is what `dotnet test` does unasked and runs the frameworks side by side. Instances that differ
    each pass their own `--framework`.
  - **The mutation gate and `shadow`:** these always pass `--framework`. Each framework now runs
    once, and its failures are read under the instance that produced them rather than under every
    instance at once.
  - **Measured on NodaTime:** `NodaTime.Test` (net48 and net10.0) took 49 s and now takes 24 s, and
    `run --full` went from 68 s to 30 s end to end.
  - A `--framework` passed after `--` is respected. `--json` reports each instance's
    `targetFramework`.

## [0.5.0] — 2026-10-24

This release closes the gaps the v0.4.0 review found, and the ones a security audit found after
it. Every change to selection **widens** it. A miss used to be possible in each of these cases:

- a top-level `Program.cs` or an assembly attribute ([#35](https://github.com/SebHenn/dotnet-tia/issues/35));
- a constructor only a DI container or test framework calls ([#40](https://github.com/SebHenn/dotnet-tia/issues/40));
- assembly- and session-wide setup hooks ([#41](https://github.com/SebHenn/dotnet-tia/issues/41));
- user-defined implicit conversions and test-configuration files ([#42](https://github.com/SebHenn/dotnet-tia/issues/42));
- a changed line that looks like a diff header ([#53](https://github.com/SebHenn/dotnet-tia/issues/53));
- source under a nested `bin/` or `obj/` folder ([#54](https://github.com/SebHenn/dotnet-tia/issues/54));
- a graph cache committed to the repository ([#52](https://github.com/SebHenn/dotnet-tia/issues/52)).

F# and VB test projects, which nothing ran before, not even a full run, now run whole whenever
they could be affected ([#44](https://github.com/SebHenn/dotnet-tia/issues/44)).

**Exit codes changed, and scripts may need to follow.** "Nothing could be concluded" is now 2
everywhere, and 1 means a defect was found:

- `verify` with no usable sample, or with a tree it cannot vouch for ([#36](https://github.com/SebHenn/dotnet-tia/issues/36), [#38](https://github.com/SebHenn/dotnet-tia/issues/38));
- several solutions and no `--solution` ([#37](https://github.com/SebHenn/dotnet-tia/issues/37));
- no solution at all, or a `--path` that does not exist or is not in a repository ([#50](https://github.com/SebHenn/dotnet-tia/issues/50)), which was exit 0 for `analyze` and 1 for `watch`.

`watch --run --once` now fails when its tests fail. `stats` rejects `--solution` and `--verbose`.
A run can now fail where it passed before, because an F# or VB test project ran, or because a
`*.runsettings`, `testconfig.json`, `xunit.runner.json` or `*.globalconfig` change forced a full run.

**Nothing here deliberately narrows selection.** The graph cache format moves from 10 to 13, so the
first run after upgrading rebuilds it. Every JSON document now opens with `"schemaVersion": 1`.

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
  count grows with hooks plus tests. This widens selection, and only for changes to those hooks or
  what they call.

- **A constructor only a DI container or a test framework calls reaches the tests that use it** ([#40](https://github.com/SebHenn/dotnet-tia/issues/40)).
  A service resolved by the container, or a fixture the framework creates through
  `IClassFixture<T>`, has no `new Foo()` anywhere. A change to its constructor, `InitializeAsync`,
  `DisposeAsync` or `Dispose` therefore reached nothing. When nothing outside the type calls such a
  member, the walk now continues from whatever names the type: the registration, or the test
  class declaring the fixture. If the registration sits in a member nothing calls either, such as
  a top-level `Program.cs` that `WebApplicationFactory<Program>` runs, the walk continues the same
  way from there. That applies only on this path, so an endpoint mapped in `Program.cs` still
  selects just the test that calls its route. Top-level statements now record their references
  against the synthesised entry point, so the registration is visible at all. A constructor that
  something calls is unchanged. This widens selection, and only for such members.

- **A change to a user-defined implicit conversion selects the code that converts** ([#42](https://github.com/SebHenn/dotnet-tia/issues/42)).
  An `implicit operator` is a call that nothing names. Its body could change and no test was
  selected, because only explicit casts recorded an edge to the operator. Every expression is now
  asked whether Roslyn applies a user-defined conversion to it, so an argument, a return, an
  initialiser, an assignment, a lambda body, a collection element and a `foreach` element all
  reach the operator. The deconstructing `foreach (var (a, b) in pairs)` recorded nothing at
  all, and now reaches the enumerator it uses and the `Deconstruct` it calls. On this
  repository, the graph walk costs about 2 % more CPU. This widens selection.

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

[Unreleased]: https://github.com/SebHenn/dotnet-tia/compare/v0.7.0...HEAD
[0.7.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.6.0...v0.7.0
[0.6.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/SebHenn/dotnet-tia/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/SebHenn/dotnet-tia/compare/v0.1.0...v0.2.0
[0.1.0]: https://github.com/SebHenn/dotnet-tia/releases/tag/v0.1.0
