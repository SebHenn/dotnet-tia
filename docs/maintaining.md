# Maintaining

Repository settings that are not visible from the source tree, and the reasoning behind them. If you
change one on github.com, change it here too — settings that nobody wrote down get reverted by
whoever next wonders why the merge button is greyed out.

For cutting a release, see [`releasing.md`](releasing.md).

## Branch protection

`main` is protected by a **repository ruleset** named `main`, targeting `~DEFAULT_BRANCH`:

| Rule | Setting |
|---|---|
| Restrict deletions | on |
| Block force pushes (non-fast-forward) | on |
| Require a pull request before merging | on |
| Required approvals | **0** |
| Dismiss stale approvals on push | on |
| Require conversation resolution | on |
| Allowed merge methods | merge, squash |
| Bypass actors | **none** |

Two of those are deliberate and look wrong at a glance.

**Zero required approvals, with no bypass actors.** GitHub does not let you approve your own pull
request. On a single-maintainer repository, requiring one approval would mean either nothing ever
merges, or the maintainer is added as a bypass actor — and a bypass actor is exempt from *every*
rule in the ruleset, including the requirement to use a pull request at all. Zero-with-no-bypass is
the stronger of the two: everyone, maintainer included, goes through a pull request, and nobody can
push to `main` directly. `current_user_can_bypass` reads `never`, which is the point.

Outside contributors are unaffected by the approval count either way — they have no write access, so
a maintainer merges their work regardless.

**Raise it to 1 the moment a second person has write access.** That is the change that makes the
rule mean something, and it is a one-line edit:

```
gh api -X PUT repos/SebHenn/dotnet-tia/rulesets/20553088 \
  --input ruleset.json   # with required_approving_review_count set to 1
```

Rebase merging is disabled at the repository level, and `delete_branch_on_merge` is on.

### Required status checks are not enabled yet

They should be, and they are the one thing left. GitHub Actions was down when this was set up, and a
required check that cannot report does not fail the pull request — it blocks it indefinitely, with
no way through except removing the rule. So the ruleset went up without them rather than protecting
`main` by making it unmergeable.

Once Actions is healthy and a pull request has produced a green run, add them:

```
gh api -X PUT repos/SebHenn/dotnet-tia/rulesets/20553088 --input - <<'JSON'
{
  "name": "main",
  "target": "branch",
  "enforcement": "active",
  "bypass_actors": [],
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    {
      "type": "pull_request",
      "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": true,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": true,
        "allowed_merge_methods": ["merge", "squash"]
      }
    },
    {
      "type": "required_status_checks",
      "parameters": {
        "strict_required_status_checks_policy": false,
        "required_status_checks": [
          { "context": "build and test (ubuntu-latest)" },
          { "context": "build and test (windows-latest)" },
          { "context": "build and test (macos-latest)" },
          { "context": "dogfood tia on its own diff" }
        ]
      }
    }
  ]
}
JSON
```

**Do not add the mutation harness to that list.** `mutation harness (zero misses is the gate)` is
gated on `workflow_dispatch` and `schedule`, so it never reports on a pull request. Requiring a check
that does not run is the same failure as requiring one that cannot run. The gate is enforced by
running it — nightly, and by hand before merging anything that touches selection — not by the merge
button.

Check names come from each job's `name:`, not its key. Rename a job in `ci.yml` and the required
check silently stops matching, which presents as a pull request that will not merge and a check that
looks green.

## nuget.org

Publishing uses trusted publishing; the policy and the `NUGET_USER` secret are covered in
[`releasing.md`](releasing.md). The one thing worth repeating here because it is invisible from the
code: **the trust is pinned to the workflow filename**. Renaming `.github/workflows/release.yml`
breaks publishing, and it breaks it at the point of pushing a tag.

## Other repository settings

| Setting | Value | Why |
|---|---|---|
| Issues | on | With four templates in `.github/ISSUE_TEMPLATE/`; blank issues stay enabled as an escape hatch |
| Discussions | on | Questions and "is this expected?" belong there rather than in the issue tracker |
| Private vulnerability reporting | on | The channel `SECURITY.md` points at. Without it, that link 404s |
| Dependabot | `.github/dependabot.yml` | Actions and root NuGet only — the fixture solutions are deliberately excluded, and the file says why |
| Rebase merging | off | The history is merge commits and squashes; a third shape is noise |
| Delete branch on merge | on | |

## Labels

`bug`, `enhancement`, `documentation` and friends are GitHub's defaults. These are the ones this
project added, and the issue templates apply them automatically:

| Label | Meaning |
|---|---|
| `miss` | A test that should have run was skipped. Triaged before anything else. |
| `safety` | Touches full-run triggers, widenings or selection bounds |
| `frameworks` | Test framework, runner or filter dialect support |
| `dependencies` | Dependabot |
