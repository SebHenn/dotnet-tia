# dotnet-deadcode

Finds code a .NET solution no longer uses: declarations, package and project references, and
orphaned `.cs` files. It marks everything reachable from the entry points, the tests and the
configured roots, and reports the rest, so code used only by other dead code is found too.

```
dotnet tool install -g dotnet-deadcode
dotnet-deadcode scan --path . --solution MySolution.slnx
```

The solution must be restored and buildable, because it is loaded with MSBuild. Exit codes: `0`
nothing found, `1` findings, `2` the solution did not load in full (a verdict about code the load
never saw would be a guess).

| Command | What it does |
|---|---|
| `scan` | Report unreached declarations: console, `--json`, `--sarif`, `--baseline` |
| `explain <symbol>` | Show the chain that keeps a symbol alive |
| `references`, `packages` | Unused project and package references |
| `files` | Orphaned `.cs` files and unnamed embedded resources |
| `oracle` | Delete every finding and compile the result against a baseline |
| `fix` | Delete findings, rebuild, and restore the files if the build fails |

## What a finding means

Nothing in the loaded solution needs the declaration. It does not mean deleting it is safe:
reflection, serialization and callers outside the solution can still use it. `oracle` and `fix`
check that the solution still **compiles** without it; neither runs your tests.

A GitHub Action wraps `scan` with a baseline and SARIF output: see
[`deadcode/`](https://github.com/SebHenn/dotnet-tia/tree/main/deadcode) in the repository.

Source and issues: https://github.com/SebHenn/dotnet-tia
