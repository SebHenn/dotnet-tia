# dotnet-deadcode action

Fails a job on declarations that nothing in the solution reaches from an entry point, a test or a
configured root, and can write the findings as SARIF for code scanning.

```yaml
jobs:
  dead-code:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      security-events: write
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet restore MySolution.slnx && dotnet build MySolution.slnx --no-restore
      - uses: SebHenn/dotnet-tia/deadcode@<tag>
        with:
          solution: MySolution.slnx
          baseline: deadcode-baseline.json   # optional: accept what exists today, fail on new findings
          sarif-file: deadcode.sarif         # optional: write SARIF instead of the plain report
      - uses: github/codeql-action/upload-sarif@v3
        if: always() && hashFiles('deadcode.sarif') != ''
        with:
          sarif_file: deadcode.sarif
```

| Input | Meaning |
|---|---|
| `path` | Directory to scan. Default `.`. |
| `solution` | Solution or project, relative to `path`. Discovered when empty. |
| `include-public` | Also report unused public members of libraries. |
| `baseline` | Findings this file holds are accepted; only others are reported. Write it with `dotnet-deadcode scan --write-baseline <file>`. |
| `sarif-file` | Write SARIF 2.1.0 here. Empty prints the plain report. |

The step exits 1 when there are findings and 2 when the solution did not load in full (a verdict
about code the load never saw would be a guess), and both fail the job. The finder is built from
this repository at the ref you pin, so the version you pin is the code that runs.
