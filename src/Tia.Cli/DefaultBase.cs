using Tia.Core.Infrastructure;

namespace Tia.Cli;

/// <param name="Hint">
/// Why none of the candidates resolved, for the full-run reason, or null when one did.
/// </param>
public sealed record ResolvedBase(string Ref, string? Hint);

/// <summary>
/// The base revision when <c>--base</c> was not given: the remote's default branch (#126).
/// </summary>
/// <remarks>
/// <para>
/// It used to be the literal <c>origin/main</c>. In a repository whose default branch is
/// <c>master</c>, <c>develop</c> or <c>trunk</c>, a bare <c>dotnet tia run</c> could not resolve it
/// and quietly became a full run, and a first-time user concluded the tool selects nothing.
/// </para>
/// <para>
/// The same order <c>tia hook install</c> writes into a pre-push hook, less the upstream: a hook
/// measures a push, and these commands measure a branch against what it will be merged into.
/// <c>origin/HEAD</c> is what <c>git clone</c> sets to the remote's default branch.
/// </para>
/// </remarks>
public static class DefaultBase
{
    private static readonly string[] Fallbacks = ["origin/main", "origin/master"];

    public static ResolvedBase Resolve(string directory)
    {
        if (!Directory.Exists(directory))
        {
            // Nothing to ask git in; the setup error about the directory comes later and says more.
            return new ResolvedBase(Fallbacks[0], null);
        }

        var head = ProcessRunner.Run("git", ["symbolic-ref", "--quiet", "--short", "refs/remotes/origin/HEAD"], directory);
        if (head.Succeeded && head.StandardOutput.Trim() is { Length: > 0 } remoteDefault)
        {
            return new ResolvedBase(remoteDefault, null);
        }

        foreach (var candidate in Fallbacks)
        {
            if (ProcessRunner.Run("git", ["rev-parse", "--verify", "--quiet", candidate + "^{commit}"], directory).Succeeded)
            {
                return new ResolvedBase(candidate, null);
            }
        }

        return new ResolvedBase(
            Fallbacks[0],
            "--base was not given, and none of origin/HEAD, origin/main or origin/master exists here. Pass --base, " +
            "or run `git remote set-head origin -a` so that origin/HEAD names the default branch");
    }
}
