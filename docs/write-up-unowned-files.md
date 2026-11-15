# A zero-miss gate with a blind spot

*A draft for publishing. Every figure and quote is from issue #168, #178 and the changelog; check
the dates and add your own framing before it goes anywhere.*

`dotnet tia` selects the tests a git diff can affect. Its one rule is that it never skips a test
that would have failed, and it checks that rule with a mutation gate: `verify --mutate` changes a
random line of production code, asks the tool which tests it would run, runs the whole suite, and
counts a **miss** whenever a test fails that the selection left out. The release gate is zero
misses.

The gate was green. The tool still had a miss, and the gate could not have found it.

## The miss

Append a line to `docs/report.schema.json` in this repository and analyse:

```
  Diff                  1 file (0 C#), 0 symbols changed
  Impacted tests        0 of 741  (0.0 %)
```

No widening, no note. One of the tests reads that file at runtime (`ReportSchemaTests` validates
every report against it), so a bad edit to the schema fails a test the selection did not contain.
That is a miss, and a silent one: the output looked the same as a correct "nothing to run" for a
README edit.

The class is wider than one file. Anything a test reads by path from outside every project folder
has the same shape: a `docker-compose.yml`, a `db/*.sql`.

## Why the gate could not see it

`verify --mutate` mutated C# only. A tool that proves itself by breaking code cannot find a file
that no code change touches. The gate was exact about the question it asked, and the question left
out a whole kind of change.

It was found the other way round: by reading what the tests read, not by sampling more mutations.

## The fix, in three parts

1. **Report what was ignored.** Unowned files are listed in the console and in `--json`, so a
   selection of zero says what it did not look at.
2. **Select what reaches them.** When C# code names the file in a string literal, by file name or by
   the path segments passed to `Path.Combine`, the tests that reach that code are selected.
3. **Teach the gate.** `verify --mutate` now also mutates tracked text files no project owns, other
   than build files: each gets one site, a line appended that a JSON, YAML, XML or SQL parser
   refuses. A file nothing reads survives the mutation and counts as a clean sample.

Part 2 cannot see a path built at runtime or read from configuration, so it does not close the
class. `fullRunPaths` in `tia.json` is the manual answer for those, and the limitation is listed in
[`limitations.md`](limitations.md) rather than hidden.

## What to take from it

- A gate proves what it samples. State what it does not sample, and then widen the sample.
- "Selected nothing" has to be distinguishable from "could not tell". The first fix was to make the
  tool say which files it ignored.
- Selecting nothing for a README edit is correct and worth keeping, so the answer was never "run
  everything for every unowned file".
