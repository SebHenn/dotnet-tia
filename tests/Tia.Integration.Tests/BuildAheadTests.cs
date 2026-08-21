using Tia.Cli.Commands;
using Tia.Core.Infrastructure;

namespace Tia.Integration.Tests;

/// <summary>
/// When the build is allowed to run alongside the analysis, and when <c>--no-build</c> may be passed.
/// </summary>
/// <remarks>
/// The concurrency is not the risk - a build that collides with the analysis produces a full run,
/// which is slow rather than wrong. <c>--no-build</c> is the risk: passing it when the build that
/// ran was not the build <c>dotnet test</c> would have run executes stale binaries and reports them
/// as current. These pin the gate in both directions, because a gate that is too permissive here
/// fails silently and green.
/// </remarks>
public sealed class BuildAheadTests
{
    [Fact]
    public void An_ordinary_run_builds_ahead()
    {
        Assert.True(BuildAhead.Applies(dryRun: false, []));
    }

    [Fact]
    public void A_dry_run_does_not()
    {
        // It prints the commands it would run and runs none of them, so building would be work
        // done for an invocation that was explicitly asked not to do any.
        Assert.False(BuildAhead.Applies(dryRun: true, []));
    }

    [Theory]
    [InlineData("--configuration")]
    [InlineData("--no-restore")]
    [InlineData("--framework")]
    [InlineData("-p:ContinuousIntegrationBuild=true")]
    public void Anything_the_caller_passed_after_the_separator_disqualifies_it(string argument)
    {
        // `--configuration Release` alone means the build this would have run is the wrong one, and
        // the tests would then run against a Debug output built minutes ago. Rather than maintain a
        // list of `dotnet test` arguments known to be harmless, anything at all disqualifies.
        Assert.False(BuildAhead.Applies(dryRun: false, [argument]));
    }

    [Fact]
    public void A_successful_build_reports_nothing_and_stops_nothing()
    {
        Assert.Null(BuildAhead.Report(new ProcessResult(0, "Build succeeded.", string.Empty)));
    }

    [Fact]
    public void A_failed_build_stops_the_run_with_its_own_exit_code()
    {
        // Without this the reader is told about test selection when the news is that the tree does
        // not compile - and a project that does not bind forces a full run, so the report would
        // look like a decision rather than a failure.
        Assert.Equal(2, BuildAhead.Report(new ProcessResult(2, "error CS1002: ; expected", string.Empty)));
    }
}
