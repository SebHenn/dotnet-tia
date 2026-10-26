using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// Formatting a value calls a <c>ToString</c> or <c>TryFormat</c> that nothing in the source names
/// (#120).
/// </summary>
/// <remarks>
/// <c>$"{x}"</c> binds to the interpolation handler, and <c>"a" + x</c> to <c>string.operator +</c>,
/// both outside the solution; the call into the value's own formatting happens inside them. An edge
/// to the <c>ToString</c> declared on the static type covered the plainest case only.
/// </remarks>
public sealed class FormattingDispatchTests
{
    private static IReadOnlySet<string> Impacted(string source, string changedType, string changedMember, out Microsoft.CodeAnalysis.Compilation compilation)
    {
        compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);
        return new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, changedType, changedMember)]).Impacted;
    }

    [Fact]
    public void Concatenation_reaches_the_ToString_of_the_value()
    {
        var impacted = Impacted("""
            namespace App
            {
                public sealed class Currency { public override string ToString() => "EUR"; }
                public sealed class Price
                {
                    public Currency Currency { get; } = new();
                    public string Describe() => "Price in " + Currency;
                }
            }
            """, "App.Currency", "ToString", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Price", "Describe"), impacted);
    }

    [Fact]
    public void Interpolation_reaches_a_ToString_inherited_from_a_base_class()
    {
        var impacted = Impacted("""
            namespace App
            {
                public abstract class Entity { public override string ToString() => "entity"; }
                public sealed class Order : Entity { }
                public sealed class Log { public string Line(Order order) => $"saved {order}"; }
            }
            """, "App.Entity", "ToString", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Log", "Line"), impacted);
    }

    [Fact]
    public void Interpolation_of_an_interface_reaches_the_ToString_of_its_implementations()
    {
        var impacted = Impacted("""
            namespace App
            {
                public interface IShape { double Area(); }
                public sealed class Square : IShape
                {
                    public double Area() => 1;
                    public override string ToString() => "square";
                }

                public sealed class Report
                {
                    public IShape Shape { get; } = new Square();
                    public string Title() => $"shape {Shape}";
                }
            }
            """, "App.Square", "ToString", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Report", "Title"), impacted);
    }

    [Fact]
    public void Interpolation_reaches_TryFormat()
    {
        // The interpolation handler prefers ISpanFormattable.TryFormat to ToString.
        var impacted = Impacted("""
            namespace App
            {
                public readonly struct Money : System.ISpanFormattable
                {
                    public string ToString(string? format, System.IFormatProvider? provider) => "1";
                    public bool TryFormat(System.Span<char> destination, out int written, System.ReadOnlySpan<char> format, System.IFormatProvider? provider)
                    {
                        written = 0;
                        return true;
                    }
                }

                public sealed class Receipt { public string Total(Money money) => $"total {money}"; }
            }
            """, "App.Money", "TryFormat", out var compilation);

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Receipt", "Total"), impacted);
    }

    [Fact]
    public void Concatenating_strings_alone_adds_nothing()
    {
        // Only a non-string operand is formatted; a change elsewhere in the type is not a reason to
        // reach a method that merely joins two strings.
        var impacted = Impacted("""
            namespace App
            {
                public sealed class Currency
                {
                    public string Code { get; } = "EUR";
                    public override string ToString() => Code;
                }

                public sealed class Price { public string Describe(Currency currency) => "Price in " + currency.Code; }
            }
            """, "App.Currency", "ToString", out var compilation);

        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Price", "Describe"), impacted);
    }
}
