# Security Policy

## Reporting a vulnerability

Please report privately, through GitHub's
[private vulnerability reporting](https://github.com/SebHenn/dotnet-tia/security/advisories/new).
It opens a draft advisory visible only to the maintainers, and it is the only channel that keeps a
report confidential — do not open a public issue for something you believe is exploitable.

Include what you would want to receive: the version (`dotnet tia --version`), what you ran, what
happened, and a reproduction if you have one.

You should get an acknowledgement within **7 days** and an assessment within **30**. This is a
small project maintained in spare time, so those are honest targets rather than a guaranteed SLA; if
a week goes by with no reply, please chase it — the more likely cause is a missed notification than
a decision not to answer.

If a report is accepted, the fix ships in a new release and the advisory is published with credit
unless you would rather not be named. If it is declined, you get the reasoning, and you are free to
disclose it yourself.

## What is supported

Fixes go into a new release from `main`. There are no maintained release branches, so the supported
version is the latest one on nuget.org.

| Version | Supported |
|---|---|
| latest release | yes |
| anything older | no — upgrade |

## Threat model

`tia` is a local and CI developer tool. It reads a git repository, loads it with MSBuild, and
invokes `dotnet test`. That leads to two properties worth stating plainly, because they look like
vulnerabilities and are not:

**Analysing a repository executes that repository's build.** `MSBuildWorkspace` evaluates project
files, and loading a compilation runs the analysers and source generators the projects reference.
Running `dotnet tia` on untrusted code is exactly as dangerous as running `dotnet build` on it, and
no more. Do not point it at a repository you would not build.

**`dotnet tia run` executes the test suite it selected.** Running tests runs code. The selection
narrows *which* of your tests execute; it is not a sandbox.

Inside that model, these **are** in scope and worth reporting:

- Anything that makes `tia` execute a command, or pass an argument, that the analysed repository's
  content controls — a crafted test name, namespace, project name or file path that escapes into
  the `dotnet test` command line as something other than a filter value.
- Path traversal: a diff entry or project reference that causes a read or a write outside the
  repository root, including through `--cache-dir` or `--path`.
- Anything a crafted `.tia/graph-*.bin` can do beyond producing a wrong graph. The cache is a
  hand-written binary format read with `BinaryReader`, not a general-purpose object deserialiser, so
  the expected worst case is a malformed-input crash — anything stronger than that is a bug.
- Leaking repository content or credentials into somewhere it should not go. `tia` makes no network
  requests at all, so any outbound traffic from the tool itself is a finding.
- A dependency vulnerability that is actually reachable from `tia`'s code paths.

## Out of scope

- **A missed test is not a security issue.** It is the most important *correctness* issue this
  project has, and it has its own
  [report template](https://github.com/SebHenn/dotnet-tia/issues/new?template=missed-test.yml).
  Please file it there, in the open, where it can be discussed and gated.
- Vulnerabilities in the .NET SDK, MSBuild, Roslyn or a test framework, unless `tia` is what makes
  them reachable. Report those upstream.
- The two by-design properties above: that analysis builds the repository, and that `run` runs
  tests.
