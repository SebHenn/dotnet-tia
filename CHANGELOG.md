# Changelog

Notable changes to `dotnet-tia`. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While the major version is `0`, the CLI surface and the JSON schema may change between minor
versions. What will not change quietly is the safety model: a release that makes selection *narrower*
says so here, in its own entry, because that is the only kind of change that can turn a passing
suite into a missed test.

## [Unreleased]

Nothing yet beyond documentation.

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

[Unreleased]: https://github.com/SebHenn/dotnet-tia/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/SebHenn/dotnet-tia/releases/tag/v0.1.0
