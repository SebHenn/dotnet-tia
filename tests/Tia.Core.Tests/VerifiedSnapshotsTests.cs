using Tia.Core.Safety;

namespace Tia.Core.Tests;

public sealed class VerifiedSnapshotsTests
{
    private const string Source = """
        namespace Tests;

        public class OrderTests
        {
            public void Total() { }
            public void Total_discounted() { }
            public void Totals() { }
            public void Refund() { }
        }

        public class Other { public void Total() { } }
        """;

    private static IReadOnlyList<string>? Resolve(string path) =>
        VerifiedSnapshots.TestsNamedBy(path, CompilationHarness.BuildGraph(CompilationHarness.CompileValid(Source), "App"));

    private static List<string> Names(IReadOnlyList<string>? keys) =>
        [.. keys!.Select(k => Tia.Core.Model.SymbolKeys.SimpleNameOf(k)!).Order(StringComparer.Ordinal)];

    [Fact]
    public void A_snapshot_selects_the_method_it_is_named_for_and_no_other_class()
    {
        var found = Resolve("tests/Snapshots/OrderTests.Total.verified.txt");

        Assert.Equal(["Total"], Names(found));
        Assert.Contains("OrderTests", found![0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_parameter_suffix_takes_the_method_it_follows_and_the_one_it_could_be_part_of()
    {
        // Total_discounted is a method; it is also Total with the parameter "discounted".
        Assert.Equal(["Total", "Total_discounted"], Names(Resolve("OrderTests.Total_discounted.verified.txt")));
    }

    [Fact]
    public void A_name_that_only_starts_like_a_method_is_not_that_method() =>
        Assert.Equal(["Totals"], Names(Resolve("OrderTests.Totals.verified.txt")));

    [Theory]
    [InlineData("OrderTests.Missing.verified.txt")]
    [InlineData("Missing.Total.verified.txt")]
    [InlineData("OrderTests.Total.received.txt")]
    [InlineData("Total.verified.txt")]
    [InlineData("OrderTests.verified.txt")]
    [InlineData("notes.md")]
    public void Anything_that_names_no_method_is_not_resolved(string path) =>
        Assert.Null(Resolve(path));

    [Fact]
    public void The_extension_and_directory_do_not_matter() =>
        Assert.Equal(["Refund"], Names(Resolve(@"a\b\OrderTests.Refund.verified.json")));

    [Fact]
    public void A_test_class_that_inherits_from_the_solution_is_not_resolved()
    {
        // The test that owns Derived.Inherited.verified.txt is declared on the base class.
        var graph = CompilationHarness.BuildGraph(CompilationHarness.CompileValid("""
            public class Base { public void Inherited() { } }
            public class Derived : Base { public void Own() { } }
            """), "App");

        Assert.Null(VerifiedSnapshots.TestsNamedBy("Derived.Own.verified.txt", graph));
    }

    [Fact]
    public void A_type_name_shared_by_two_types_is_not_resolved()
    {
        var graph = CompilationHarness.BuildGraph(CompilationHarness.CompileValid("""
            namespace A { public class Same { public void Run() { } } }
            namespace B { public class Same { public void Run() { } } }
            """), "App");

        Assert.Null(VerifiedSnapshots.TestsNamedBy("Same.Run.verified.txt", graph));
    }
}
