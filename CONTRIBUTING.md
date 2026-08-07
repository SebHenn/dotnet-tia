# Contributing

Thanks for looking. This is a small project with a large correctness obligation: a tool that skips a
test which would have failed is worse than no tool at all. Most of what follows exists to protect
that one property.

By participating you agree to the [Code of Conduct](CODE_OF_CONDUCT.md).

## The most valuable thing you can contribute

**A missed test.** If `tia` selected a set of tests, and a test outside that set would have failed,
that is the bug class this project cares about above all others — and it is the one class that
cannot be found from the inside. The engine has been gated against two repositories; yours is
shaped differently.

Two ways to find one without taking anything on trust:

```
# Run the whole suite anyway, and report which failures a selection would have skipped.
dotnet tia shadow --base origin/main

# Inject a mutation, select against it, run the full suite, and check nothing failed unselected.
dotnet tia verify --mutate 25
```

`shadow` costs one analysis on top of a suite that was going to run in full regardless, so it is
cheap enough to leave on for weeks. If either reports a miss, please open a
[missed test report](https://github.com/SebHenn/dotnet-tia/issues/new?template=missed-test.yml) —
it is the highest-priority issue type here, and the three real defects found so far all came from
pointing the harnesses at a repository the author did not write.

Include `dotnet tia explain <TheTestThatFailed>` output. It either prints the graph path or says
nothing reaches the test, and that distinction is most of the diagnosis.

Other things that help, in rough order of usefulness:

- **Selection ratios from your repository.** `dotnet tia analyze --base origin/main --json` on a
  handful of real commits. The benchmarks currently cover two libraries; every additional shape
  makes the published numbers less of a guess.
- **A framework or runner combination that misdetects.** See the table in the README for what is
  claimed.
- **Documentation that is wrong.** Including this file.

## Getting set up

You need the **.NET 10 SDK**. Nothing else — no global tools, no local services.

```
git clone https://github.com/SebHenn/dotnet-tia
cd dotnet-tia
dotnet restore
dotnet build
dotnet test
```

`dotnet test` runs the engine unit tests and the integration tests. The integration tests are the
slow ones: each drives a real `MSBuildWorkspace` over a real git repository, and the assembly is
deliberately serialised because two concurrent workspace loads in one process do not survive it.
Expect minutes, not seconds.

The fixture solutions under `tests/Tia.Fixtures*` are **analysed as source** and are not part of
`tia.slnx`. They have their own `Directory.Packages.props` and pin their own SDK, so they need their
own restore before a workspace can load them:

```
dotnet restore tests/Tia.Fixtures/Fixtures.slnx
dotnet restore tests/Tia.Fixtures.Tunit/Fixtures.Tunit.slnx
```

To try your build against a real repository, install it from a local pack:

```
dotnet pack src/Tia.Cli -c Release
dotnet tool install -g --add-source artifacts/nupkg dotnet-tia
```

## The layout

```
src/
  Tia.Core/         impact engine: diff, graph, selection, safety, cache. No MSBuild, no workspaces.
  Tia.Workspace/    MSBuildWorkspace loading, the analysis pipeline, the validation harnesses
  Tia.Frameworks/   test discovery and the filter dialects
  Tia.Cli/          System.CommandLine host, `dotnet tia`
tests/
  Tia.Core.Tests/        engine unit tests over in-memory compilations
  Tia.Integration.Tests/ end-to-end selection over the fixture solutions, real git and real MSBuild
  Tia.Fixtures/          xUnit v3 on MTP and NUnit on the VSTest bridge, plus the hard cases
  Tia.Fixtures.Tunit/    TUnit, on a repository opted into the platform-native `dotnet test`
  Tia.Validation/        nightly mutation and commit-replay drivers
```

**`Tia.Core` references only `Microsoft.CodeAnalysis.CSharp`** — no MSBuild, no Roslyn workspaces.
That is a deliberate boundary, not an accident of history: it is what lets the engine be tested
against `CSharpCompilation.Create` with no SDK resolution at all. Adding a workspace or MSBuild
dependency to `Tia.Core` will be asked about in review, because it costs the whole unit test
strategy.

[`docs/plan.md`](docs/plan.md) has the design rationale, [`docs/benchmarks.md`](docs/benchmarks.md)
the measurements and the assumptions they killed, and [`docs/coverage.md`](docs/coverage.md) the
dynamic-coverage route that was spiked and declined.

## What a change needs before it merges

**A test.** Engine behaviour goes in `Tia.Core.Tests` against an in-memory compilation where it can;
anything that only shows up through a real workspace, a real runner or a real git history goes in
`Tia.Integration.Tests`. A new graph edge that no test pins is a regression waiting for a refactor.

**A new graph edge or widening must say why it cannot cause a miss.** Not why it seemed reasonable —
why the case it does not cover is unreachable, or why it is a superset. Both directions are real
failure modes: an edge that is too narrow misses a test, and one that is too broad quietly selects
the whole suite and looks like it is working. The comments in `Safety/` and `Analysis/` are written
in that style; match it.

**Widenings are reported, never silent.** If your change expands scope, it goes through the widening
machinery so it shows up in the console output and in `--json`. Silent conservatism is
indistinguishable from a bug, and it is how these tools lose people's trust.

**Warnings are errors in CI.** Locally they stay warnings so iterating on a half-finished change is
not blocked; CI builds with `-p:TreatWarningsAsErrors=true` so the ratchet still holds on anything
merged. Style and analyzer rules live in [`.editorconfig`](.editorconfig) — please do not reformat
code your change does not touch.

**The mutation gate.** Zero misses, or it does not merge. It runs nightly and on demand rather than
per pull request, because every sample runs the whole suite once:

```
gh workflow run ci.yml
```

If your change touches selection, the graph, the safety model or a filter dialect, run it against
the fixtures before opening the pull request — that is seconds per sample rather than a minute:

```
dotnet run --project src/Tia.Cli -- verify --mutate 30 \
  --path tests/Tia.Fixtures --solution tests/Tia.Fixtures/Fixtures.slnx
```

`verify` **mutates your working tree in place** and restores each file afterwards, so it refuses to
start unless `git status` is clean — untracked files included, because the diff picks those up too.

## Pull requests

Branch from `main`, open a pull request against `main`. `main` is protected: it takes no direct
pushes and no force pushes.

Commit messages here are written as **what the commit does to the codebase, and why** — imperative
mood, and a body whenever the reason is not obvious from the diff. `git log` is part of the
documentation of why the safety model is shaped the way it is, and the existing history is the
style guide:

```
Stop resolving the temp directory to the filesystem root
Refuse to mutate a working tree that is not clean
Root the analysis where the caller looked, not where git resolved to
```

Small, focused pull requests get reviewed faster than large ones. If you are planning something
substantial — a new framework dialect, a change to how the graph is walked, anything touching the
cache key — please open an issue first so the design conversation happens before the work rather
than after it.

There is no CLA and no DCO sign-off. Contributions are accepted under the repository's
[MIT licence](LICENSE).

## Releasing

Maintainers only, and documented separately in [`docs/releasing.md`](docs/releasing.md). Publishing
is irreversible — nuget.org allows a version to be unlisted but never deleted — so everything that
could make a package wrong is checked before the push rather than after it.
