# Using `dotnet tia`

## Options

Every command takes these:

| Option | Default | Meaning |
|---|---|---|
| `--base`, `-b` | `origin/main` | Revision to diff against. The diff runs against the working tree, so uncommitted edits count. |
| `--path`, `-p` | current directory | Directory holding the solution. May be below the git root. |
| `--solution`, `-s` | discovered | Solution or project to analyse. |
| `--json` | off | Emit the full report as JSON on stdout. |
| `--verbose`, `-v` | off | List every selected test and log workspace diagnostics to stderr. |
| `--no-cache` | off | Ignore and do not write `.tia/graph-*.bin`. |
| `--full` | off | Skip selection and report a full run. |
| `--default-branch` | unset | Branch that always runs the whole suite. |
| `--no-fallback-full-on-error` | off | Fail instead of falling back to a full run when analysis throws. |

`--fallback-full-on-error` is **on** by default. The cost of an unnecessary full run is minutes; the cost of a missed test is a broken main branch.

### Command-specific

- `run` takes `--dry-run`, and forwards everything after `--` to `dotnet test`:
  `dotnet tia run --base origin/main -- --no-build --configuration Release`
- `explain <TestName>` matches any test whose fully qualified name ends with the argument, so `WidgetTests.Adds` is enough.
- `graph` takes `--output <file>` to write the graph summary and the discovered test list as JSON.
- `verify` takes `--mutate <n>` (default 25) and `--seed <n>` so a failing run can be replayed.

## Exit codes

| Command | 0 | non-zero |
|---|---|---|
| `analyze`, `graph`, `explain` | always | only on a usage error |
| `run` | every test project passed | the exit code of the first failing `dotnet test` |
| `verify` | no misses, at least one usable sample | a miss, or no usable sample |

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

The mutation harness needs to read test outcomes, which means a TRX-capable runner: `Microsoft.NET.Test.Sdk` for VSTest projects, `Microsoft.Testing.Extensions.TrxReport` for Microsoft.Testing.Platform projects. Match the extension's major version to the platform version your test framework pulls in — xUnit v3 3.2.x uses Microsoft.Testing.Platform 1.9.x, so pair it with `Microsoft.Testing.Extensions.TrxReport` 1.9.x. Without a usable reporter the harness reports **inconclusive**; it never reports a pass it could not observe.

## Troubleshooting

**"X does not compile"** and everything runs. Almost always an unrestored solution: a project with no resolved references still parses, so discovery would quietly find no tests in it. Run `dotnet restore` first.

**Everything is selected on every change.** Check `widenings`. The usual causes are a `const` change, a reflecting file in the impact set, or a project that emits source-generated code.

**A test you expected is missing.** Run `dotnet tia explain <TestName>`. It either prints the path or tells you nothing reaches it — and if nothing reaches it, that is a graph gap worth reporting.

**The base branch is not found.** `tia` needs the base revision in the local object store. In CI that means `fetch-depth: 0`; locally, `git fetch origin main`.

**A solution below the git root.** Point `--path` at the directory holding the solution and `--solution` at the solution file; diff paths are resolved against the repository root regardless, so a monorepo layout works. `.tia/` is written under `--path`.
