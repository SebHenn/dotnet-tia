using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class OracleGateTests
{
    [Fact]
    public void A_project_that_does_not_compile_before_any_deletion_is_named_with_its_error_count()
    {
        var broken = new OracleProject("Broken", CompilationHarness.Compile("Broken", ("/repo/A.cs", "class A { Missing m; }")), []);
        var fine = new OracleProject("Fine", CompilationHarness.CompileValid("class B { }"), []);

        var found = OracleGate.BrokenBaselines([broken, fine]);

        Assert.Equal(["Broken has 1 errors before any deletion"], found);
    }

    [Fact]
    public void A_clean_solution_may_be_measured()
    {
        Assert.Null(OracleGate.Refusal([], [], allowPartial: false));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_partly_loaded_solution_is_refused_unless_partial_is_allowed(bool allowPartial)
    {
        var refusal = OracleGate.Refusal(["X did not load"], [], allowPartial);

        Assert.Equal(allowPartial, refusal is null);
        if (refusal is not null)
        {
            Assert.Contains("X did not load", refusal, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_project_that_does_not_compile_is_refused_like_one_that_did_not_load()
    {
        var refusal = OracleGate.Refusal([], ["Broken has 1 errors before any deletion"], allowPartial: false);

        Assert.Contains("Broken has 1 errors", refusal, StringComparison.Ordinal);
        Assert.Null(OracleGate.Refusal([], ["Broken has 1 errors before any deletion"], allowPartial: true));
    }
}
