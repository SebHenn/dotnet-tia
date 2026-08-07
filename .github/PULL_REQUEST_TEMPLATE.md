<!--
Thanks for the pull request. The checklist is short on purpose; the two items with prose next to
them are the ones that actually get discussed in review.

Delete anything that does not apply — a docs fix does not owe anyone a safety argument.
-->

## What this changes, and why

<!-- The "why" is the part that ages well. The diff already says what. -->

Closes #

## Safety

<!--
Only if this touches selection, the graph, a widening, the cache key or a filter dialect.

A tool that skips a test which would have failed is worse than no tool, so a change here needs to
say why it cannot cause a miss — not why it seemed reasonable, but why the case it does not cover is
unreachable, or why it is a superset. Both directions are real failure modes: an edge that is too
narrow misses a test, and one that is too broad quietly selects the whole suite and looks like it is
working.

If this change expands scope, confirm the expansion is reported through the widening machinery
rather than applied silently.
-->

## Checklist

- [ ] Tests cover the change — `Tia.Core.Tests` for engine behaviour, `Tia.Integration.Tests` for
      anything needing a real workspace, runner or git history
- [ ] `dotnet build -p:TreatWarningsAsErrors=true` is clean (CI builds this way)
- [ ] `dotnet test` passes
- [ ] Docs updated if behaviour, options or output changed (`README.md`, `docs/usage.md`)
- [ ] If this touches selection: the mutation gate still reports **zero misses**

      ```
      dotnet run --project src/Tia.Cli -- verify --mutate 30 \
        --path tests/Tia.Fixtures --solution tests/Tia.Fixtures/Fixtures.slnx
      ```
