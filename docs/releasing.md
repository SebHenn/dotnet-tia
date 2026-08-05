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
| Environment | leave empty |

The policy is owned by a user or an organisation, and it covers every package that owner owns. If it
is owned by an organisation and you later leave that organisation, it goes inactive until you rejoin.

The **workflow filename is part of the trust**. Renaming `release.yml` breaks publishing, and it
breaks it at the point of pushing a tag rather than anywhere earlier.

### 2. Add your nuget.org username to the repository

Settings → Secrets and variables → Actions → new secret `NUGET_USER`, set to your nuget.org **profile
name**, not the email address you sign in with. The workflow fails with an explicit message if it is
missing, rather than failing later inside the login step.

### 3. Expect the policy to be pending at first

A new policy can start out *temporarily active for 7 days*, which is the usual state for a private
repository — and this repository is private, so expect it. nuget.org needs GitHub's numeric owner and
repository IDs to pin the policy to this exact
repository, and it only learns them from a real publish. Until one happens the policy behaves
normally but expires after a week; the first successful publish makes it permanent. You can restart
the window at any time, including after it lapses.

This exists to stop resurrection attacks: without those IDs, someone could delete a repository,
recreate it under the same name, and inherit the right to publish.

## Cutting a release

1. Update `VersionPrefix` in `Directory.Build.props`.
2. Commit it, and let CI go green on `main` — the matrix is what proves the commit, not the tag.
3. Tag and push:

   ```
   git tag v0.1.0
   git push origin v0.1.0
   ```

The tag must match `VersionPrefix`. A `v0.2.0` tag shipping a `0.1.0` package is not a mistake anyone
catches by looking, and it cannot be taken back, so the workflow compares the two and refuses the
push if they disagree.

## What runs before anything is published

In order, and all of it before the push:

- build with `TreatWarningsAsErrors`
- the full test suite
- `dotnet pack`
- the tag and the package version must agree
- the package must install as a tool from the artifact and successfully analyse this repository —
  proving the thing being published works, rather than the build output it came from

Only then does the job request a key and push, followed by a GitHub release with generated notes.

## Rehearsing it

`workflow_dispatch` runs everything above and stops short of publishing:

```
gh workflow run release.yml --ref main -f dry_run=true
```

The push and release steps are additionally guarded on `refs/tags/v*`, so a dispatch from a branch
cannot publish even with `dry_run` set to false. The built package is uploaded as an artifact so you
can inspect exactly what would have shipped.

## Package ID rules

Since 15 July 2026 nuget.org rejects pushes whose package ID is not ASCII letters, digits, dots and
dashes, with no consecutive separators. `dotnet-tia` satisfies this. It matters if the ID ever
changes.

[tp]: https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing
