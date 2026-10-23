# Releasing

Publishing is irreversible. nuget.org does not allow a version to be deleted, only unlisted, and the
ID `dotnet-tia` is claimed by whoever pushes it first. So the release workflow checks everything that
could make a package wrong *before* the push, and the push itself only happens for a tag.

## One-time setup

Publishing uses [trusted publishing][tp] rather than a stored API key. GitHub signs a short-lived
token describing the workflow that is running; nuget.org checks that against a policy you registered
and hands back an API key valid for one hour. Nothing long-lived is kept in the repository, so there
is no key to leak, and none to rotate.

### 1. Register the policy on nuget.org

Sign in, click your username, choose **Trusted Publishing**, and add a policy:

| Field | Value |
|---|---|
| Repository Owner | `SebHenn` |
| Repository | `dotnet-tia` |
| Workflow File | `release.yml` — the filename only, not the `.github/workflows/` path |
| Environment | `release` |

**Environment** is what ties the key to an approval. With it set, nuget.org only accepts a token
issued to a job running in the [`release` environment](#4-the-release-environment), which is a job a
maintainer has approved. With it empty, a token from any job in `release.yml` would do. The policy
here was registered with it empty, before the workflow had an environment; the workflow publishes
either way, so if you find it still empty, set it.

The policy is owned by a user or an organisation, and it covers every package that owner owns. If it
is owned by an organisation and you later leave that organisation, it goes inactive until you rejoin.

The **workflow filename is part of the trust**. Renaming `release.yml` breaks publishing, and it
breaks it at the point of pushing a tag rather than anywhere earlier.

### 2. Add your nuget.org username to the repository

Settings → Secrets and variables → Actions → new secret `NUGET_USER`, set to your nuget.org **profile
name**, not the email address you sign in with. The workflow fails with an explicit message if it is
missing, rather than failing later inside the login step.

### 3. A new policy is temporary until the first publish

A policy can start out *temporarily active for 7 days*. nuget.org needs GitHub's numeric owner and
repository IDs to pin it to this exact repository, and it only learns them from a real publish. Until
one happens the policy behaves normally but expires after a week; the first successful publish makes
it permanent. You can restart the window at any time, including after it lapses.

This exists to stop resurrection attacks: without those IDs, someone could delete a repository,
recreate it under the same name, and inherit the right to publish.

**This is already done here.** `v0.1.0` published through this workflow on 5 August 2026, so the
policy is pinned and permanent. The step matters if the package ID, the owner or the workflow
filename ever changes — any of those needs a new policy, which starts the seven-day window again.

### 4. The `release` environment

Settings → Environments → `release`:

| Setting | Value |
|---|---|
| Required reviewers | `SebHenn` |
| Allow administrators to bypass configured protection rules | off |
| Deployment branches and tags | selected only: the tag pattern `v*`, no branches |

The publish job is the only job that runs in it, and the only job in the workflow that can ask for
an OIDC token. So nothing can trade a token for a nuget.org key until a reviewer has opened the run
and approved it, and an administrator cannot skip that. The tag rule means a run from a branch could
not deploy even if someone approved it, independently of the workflow's own check of the ref.

With one maintainer the reviewer is usually the person who pushed the tag, which is why self-review
is allowed — otherwise nothing could ever be approved. The approval still earns its place: it is a
pause between "a tag exists" and "nuget.org has it", in which the build can be read, and a tag pushed
by anything else with write access does not publish on its own.

The build job is deliberately *outside* the environment. A dry run from a branch never asks for it,
so the tag-only rule cannot get in its way.

### 5. Tags and releases cannot be rewritten

A tag ruleset named `release tags`, targeting `refs/tags/v*` with no bypass actors, blocks updating,
deleting and force-pushing a release tag. It does not restrict *creating* one — a tag can still be
pushed on any commit — which is why the workflow itself refuses a tag whose commit is not on `main`.

Immutable releases are on in the repository settings, so once a GitHub release is published neither
its assets nor its tag can change. The workflow creates the release with `gh release create`, which
saves it as a draft, uploads the package, and only then publishes it, so the release that becomes
immutable is never one with the package missing.

Between the two, and nuget.org's own rules, a published version is permanent everywhere. A mistake
is fixed by bumping `VersionPrefix` and tagging again, never by moving a tag.

## Cutting a release

1. Update `VersionPrefix` in `Directory.Build.props`.
2. Commit it, merge it, and let CI go green on `main` — the matrix is what proves the commit, not the
   tag.
3. Tag the merged commit on `main` and push the tag:

   ```
   git tag v0.1.0
   git push origin v0.1.0
   ```

4. Approve the deployment. The run stops at **publish to nuget.org** with *Waiting for review*. Read
   the summary of the build job first — it lists the SHA-256 of every file about to be pushed — then
   choose **Review deployments**, tick `release`, and **Approve and deploy**. Rejecting it ends the
   run with nothing published.

The tag must match `VersionPrefix`. A `v0.2.0` tag shipping a `0.1.0` package is not a mistake anyone
catches by looking, and it cannot be taken back, so the workflow compares the two and refuses the
push if they disagree. The tagged commit must also be on `main`: a tag on a side branch fails the
build job before it compiles anything.

Either failure spends the tag. The [tag ruleset](#5-tags-and-releases-cannot-be-rewritten) stops it
being moved or deleted, so the fix is a tag with a version that is not taken yet — bumping
`VersionPrefix` to match if need be — not a second push of the same one. Nothing was published, so
the skipped version costs nothing but a gap in the numbering.

## What runs before anything is published

The workflow is three jobs, and each holds only the permissions its own steps use:

| Job | Permissions | What it does |
|---|---|---|
| `build, test and pack` | `contents: read` | Everything listed below, then uploads the package as an artifact |
| `publish to nuget.org` | `id-token: write`, in the `release` environment | Downloads the package, checks it, trades the OIDC token for a key and pushes |
| `create the GitHub release` | `contents: write` | Downloads the package, checks it, and creates the release with generated notes and the `.nupkg` attached |

The build job runs the test suite, and with it every analyser, source generator and MSBuild target
the build pulls in. None of that ever runs beside the publishing token: the two later jobs do not
check out the repository or build anything.

In the build job, in order, and all of it before the push:

- on a tag, the tagged commit must be reachable from `origin/main`
- build with `TreatWarningsAsErrors`
- the full test suite
- `dotnet pack`
- the tag and the package version must agree
- the SHA-256 of every packed file is recorded, in the job summary and as a job output
- the package must install as a tool from the artifact and successfully analyse this repository —
  proving the thing being published works, rather than the build output it came from. The install
  uses the artifact directory as its *only* source and asks for the packed version exactly, so a
  version already on nuget.org can never be installed and tested in its place.

The publish and release jobs both check what they downloaded against the recorded checksums before
using it, so what reaches nuget.org and the GitHub release is byte for byte what the build job
tested. The GitHub release is created only after the push succeeds.

## Rehearsing it

`workflow_dispatch` runs the build job — everything above — and stops short of publishing:

```
gh workflow run release.yml --ref main -f dry_run=true
```

It works from any branch, and needs no approval, because only the publish job is in the `release`
environment. That job is guarded on `refs/tags/v*`, so a dispatch from a branch cannot publish even
with `dry_run` set to false, and the environment's tag-only rule would refuse it if it tried. The
built package is uploaded as an artifact so you can inspect exactly what would have shipped.

## Package ID rules

Since 15 July 2026 nuget.org rejects pushes whose package ID is not ASCII letters, digits, dots and
dashes, with no consecutive separators. `dotnet-tia` satisfies this. It matters if the ID ever
changes.

[tp]: https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing
