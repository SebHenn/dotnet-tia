# dotnet tia

Test impact analysis for .NET. A `dotnet` global tool that takes a git diff and runs **only the tests that diff can actually affect**.

> **Status: planning.** No code yet — the design lives in [`docs/plan.md`](docs/plan.md). Implementation happens on a different machine.

```
$ dotnet tia run --base origin/main

  Analyzing diff...          14 files, 31 symbols changed
  Building symbol graph...   1,204 types / 8,930 members
  Impacted tests             87 of 3,412  (2.5%)

  ! 3 files fell back to full-project scope (reflection)

  Running 87 tests...        OK  12.4s
  Estimated saving           ~9m 40s vs full run
```

## Why

Per-test impact analysis for .NET currently exists only behind a paywall or a SaaS — Datadog Test Optimization, Tricentis SeaLights, NCrunch ($159/yr, IDE-only), or Azure DevOps' legacy VSTest-only collector. The free OSS options ([`dotnet-affected`](https://github.com/leonardochaia/dotnet-affected), Incrementalist) resolve impact at **project** granularity, so changing one file in a core library still runs every downstream test project in full.

Meanwhile .NET 10's Microsoft.Testing.Platform replaced VSTest's assembly-scanning plugins with first-class extension APIs, and xUnit v3, NUnit, MSTest and TUnit have all shipped MTP runners. The extension surface exists and nobody has built free test-level selection on it.

## How

Fully static — no instrumentation, no profiler, no prior coverage run:

1. Resolve the git diff to changed files and line ranges
2. Load the solution via `MSBuildWorkspace`
3. Map changed lines to changed symbols (including deletions, via the old tree)
4. Walk a reverse reference graph — callee→caller, interface↔implementation, base↔override
5. BFS to reach test methods
6. Emit per-project filters and invoke `dotnet test`

Interface edges mean dependency injection needs no special handling: a test calling `IFoo.Bar()` is already connected to `Foo.Bar()`.

Blind spots that static analysis genuinely has — reflection, source generators, `const` inlining, non-`.cs` test data — get explicit widening or full-run rules, and every one of them is reported rather than applied silently.

## Correctness

A tool that skips a test which would have failed is worse than no tool, so the merge gate is a mutation harness: inject a mutation, let `tia` select against it, then run the **full** suite. Any test that fails but wasn't selected is a miss. **Zero misses, or it doesn't merge.**

Real-commit replay across large OSS repos provides the selection-ratio and time-saved benchmarks.

## Planned scope

| Framework | Runner | Filter dialect |
|---|---|---|
| xUnit v2 | VSTest | `--filter "FullyQualifiedName~…"` |
| xUnit v3 | MTP native | `--filter-method` |
| xUnit v3 | VSTest bridge | VSTest syntax |
| NUnit | either | VSTest syntax |
| MSTest | either | VSTest syntax |
| TUnit | MTP | `--treenode-filter` |

## License

MIT (planned).
