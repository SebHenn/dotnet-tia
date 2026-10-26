using Tia.Core.Infrastructure;

namespace Tia.Integration.Tests;

/// <summary>git in a scratch repository, where a failing command fails the setup.</summary>
internal static class TestGit
{
    /// <summary>A new repository on <c>main</c>, with an identity to commit as.</summary>
    public static void Init(string root)
    {
        Run(root, "init", "--initial-branch=main");
        Run(root, "config", "user.email", "tia@example.invalid");
        Run(root, "config", "user.name", "tia tests");
    }

    public static void Run(string root, params string[] args)
    {
        var result = ProcessRunner.Run("git", args, root, cancellationToken: TestContext.Current.CancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {result.StandardError}");
        }
    }
}
