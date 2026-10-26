using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// A record's, or a plain struct's, equality and formatting call those of every field (#121).
/// </summary>
/// <remarks>
/// <c>record Order(Money Total)</c> gets an <c>Equals</c> that calls
/// <c>EqualityComparer&lt;Money&gt;.Default.Equals</c> and a <c>PrintMembers</c> that formats
/// <c>Total</c>, and a struct that does not override <c>Equals</c> compares its fields through
/// <c>ValueType.Equals</c>. None of it is in the source, so handing an <c>Order</c> to
/// <c>Assert.Equal</c> reached what <c>Order</c> overrides and nothing of <c>Money</c>'s.
/// </remarks>
public sealed class ValueEqualityDispatchTests
{
    private static IReadOnlySet<string> Impacted(string source, string changedType, string changedMember, out Microsoft.CodeAnalysis.Compilation compilation)
    {
        compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);
        return new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, changedType, changedMember)]).Impacted;
    }

    private const string Money = """
        namespace App
        {
            public sealed class Money : System.IEquatable<Money>
            {
                public decimal Amount { get; init; }
                public bool Equals(Money? other) => other is not null && other.Amount == Amount;
                public override bool Equals(object? obj) => Equals(obj as Money);
                public override int GetHashCode() => Amount.GetHashCode();
                public override string ToString() => Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        """;

    [Fact]
    public void Comparing_two_records_reaches_the_Equals_of_a_field()
    {
        var impacted = Impacted(Money + """
            namespace App
            {
                public sealed record Order(Money Total);

                public static class Checks
                {
                    public static bool Same(Order a, Order b) => System.Collections.Generic.EqualityComparer<Order>.Default.Equals(a, b);
                }
            }
            """, "App.Money", "Equals", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Checks", "Same"), impacted);
    }

    [Fact]
    public void Formatting_a_record_reaches_the_ToString_of_a_field()
    {
        var impacted = Impacted(Money + """
            namespace App
            {
                public sealed record Order(Money Total);
                public static class Log { public static string Line(Order order) => $"saved {order}"; }
            }
            """, "App.Money", "ToString", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Log", "Line"), impacted);
    }

    [Fact]
    public void Comparing_two_plain_structs_reaches_the_Equals_of_a_field()
    {
        // ValueType.Equals compares the fields of a struct that does not override it.
        var impacted = Impacted(Money + """
            namespace App
            {
                public struct Line { public Money Price; }

                public static class Checks
                {
                    public static bool Same(Line a, Line b) => System.Collections.Generic.EqualityComparer<Line>.Default.Equals(a, b);
                }
            }
            """, "App.Money", "Equals", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Checks", "Same"), impacted);
    }

    [Fact]
    public void A_class_that_compares_by_reference_does_not_reach_its_fields()
    {
        // An ordinary class without an Equals override compares references; its fields' Equals
        // is never called by comparing two of them.
        var impacted = Impacted(Money + """
            namespace App
            {
                public sealed class Basket { public Money Total { get; } = new(); }

                public static class Checks
                {
                    public static bool Same(Basket a, Basket b) => System.Collections.Generic.EqualityComparer<Basket>.Default.Equals(a, b);
                }
            }
            """, "App.Money", "Equals", out var compilation);

        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Checks", "Same"), impacted);
    }

    [Fact]
    public void Printing_a_record_reaches_a_computed_property()
    {
        // PrintMembers reads every public property, so a computed getter is part of ToString.
        var impacted = Impacted("""
            namespace App
            {
                public sealed record Order(decimal Net)
                {
                    public decimal Gross => Net * 1.2m;
                }

                public static class Log { public static string Line(Order order) => $"saved {order}"; }
            }
            """, "App.Order", "Gross", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Log", "Line"), impacted);
    }
}
