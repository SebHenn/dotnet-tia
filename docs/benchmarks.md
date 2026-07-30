# Benchmarks

Measured runs, not projections. Reproduce them with the drivers in `tests/Tia.Validation`.

## Correctness: mutation harness

The merge gate. Inject a Stryker-style mutation, select against it, run the **full** suite, and
check that every failing test was in the selection.

| Repository | Samples | Usable | Misses | Typical selection |
|---|---:|---:|---:|---|
| `tests/Tia.Fixtures` (xUnit v3 + NUnit, 11 tests) | 30 | 24 | **0** | 1 / 11 |

Skipped samples are files that offer no mutation site, or mutations that do not compile. A sample
whose outcome cannot be read is reported as inconclusive, never as a pass.

```
dotnet run --project tests/Tia.Validation -- mutate --repo <path> --samples 200
```

## Selection ratio: commit replay

### FluentValidation

25 first-parent commits requested; 12 analysable with the SDK available here (older commits pin
SDKs that could not be restored). 2,460 tests across three target frameworks.

| Commit | Selected | Full | Mode | What it touched |
|---|---:|---:|---|---|
| `80b849552` | 45 | 2460 | selective | one test file |
| `d06e3f0d7` | 0 | 2448 | selective | docs only |
| `cc9917c36` | 0 | 2448 | selective | docs only |
| `37eb7c195` | 0 | 2448 | selective | docs only |
| `16b5334de` | 0 | 2448 | selective | docs only |
| `058865db4` | 2448 | 2448 | selective | `src/FluentValidation/` |
| `943979089` | 2457 | 2457 | selective | `src/FluentValidation/` |
| `0f02e0aaa` | 2460 | 2460 | selective | `src/FluentValidation/` |
| `bae891652` | 2460 | 2460 | selective | `src/FluentValidation/` |
| `4984ff538` | 2448 | 2448 | selective | `src/FluentValidation/` |
| `1e6eee271` | 2448 | 2448 | selective | `src/FluentValidation/` |
| `78421663e` | 2448 | 2448 | **full** | build inputs |

**Mean selection 58.5 % · full-run rate 8 % · commits with a widening 50 %**

Cold graph build: 421 types / 2,519 members / 16,075 edges in **18.6 s**.

### Reading that honestly

The result is bimodal, and the split is not noise:

- Changes **outside** the core library select 0–2 % of the suite. That is the win.
- Changes **inside** `src/FluentValidation/` select ~100 %, every time.

The cause is measurable rather than inferred. A one-line change to `CreditCardValidator.Name` - a
leaf class - selects all 2,460 tests, and the first widening reported is:

```
SourceGenerator | FluentValidation | 7 generated document(s) contributing 33 symbol(s) are treated as changed
```

FluentValidation runs `Zomp.SyncMethodGenerator`, which emits sync copies of the core async
validation API. Because a generator's output cannot be attributed to the input line that produced
it, every generated document is treated as changed whenever anything in the project changes - and
essentially every test depends on that generated API, so everything is selected.

This is a real limit of static analysis on a generator-bearing core library, not a tuning problem,
and no amount of graph precision removes it. The improvement that would is narrower: cache the
content hashes of the generated documents from the base revision, and treat only the ones that
actually differ as changed. In CI the base-branch graph cache is exactly the right baseline for
that, and it is the obvious next step. It is not implemented.

Two smaller effects were fixed along the way and are already reflected in the numbers above:

- **Reflection.** Widening the whole project for any reflecting file collapsed selection on a test
  suite that uses `typeof(x).GetProperty` routinely. Reflection now makes the *reflecting member*
  unconditionally impacted, which is the same safety statement scoped correctly: a reflecting test
  selects itself, a reflecting registry selects everything that reaches it.
- **Binding.** A purely syntactic scan flagged FluentValidation's own `expression.GetMember()`
  extension as reflection. Resolving the symbol first drops that class of false positive.

## What is not measured yet

- Polly pins an SDK feature band that is not installable here, and NodaTime was not run.
- No wall-clock saving is reported. Selection ratio is measured; the time saved depends on the
  suite's own shape, and quoting a figure from one repository would overstate it.
