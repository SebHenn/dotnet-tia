using Tia.Core.Analysis;

namespace Tia.Core.Tests;

/// <summary>
/// The two rules the route edge rests on: how a route is folded to a comparable key, and what it
/// takes for a caller's path to meet an endpoint's template. Both are deliberately strict, because
/// a template that matches too much would connect every endpoint to every caller - which is the
/// "worse than nothing" outcome <c>docs/benchmarks.md</c> rejects.
/// </summary>
public sealed class RouteTemplateTests
{
    [Theory]
    [InlineData("/contributors", "contributors")]
    [InlineData("contributors/", "contributors")]
    [InlineData("/Contributors", "contributors")]
    [InlineData("/contributors/{id}", "contributors/*")]
    [InlineData("/contributors/{id:int}", "contributors/*")]
    [InlineData("/api/contributors/count", "api/contributors/count")]
    public void A_route_folds_to_a_comparable_key(string route, string expected)
    {
        Assert.Equal(expected, RouteTemplateIndex.Normalise(route));
    }

    /// <summary>
    /// A template with no literal segment matches almost any path of its length, so an edge from it
    /// would join everything to everything. Rejected rather than kept and hoped about.
    /// </summary>
    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("{id}")]
    [InlineData("/{controller}/{action}")]
    public void A_route_with_no_literal_segment_is_rejected(string route)
    {
        Assert.Null(RouteTemplateIndex.Normalise(route));
    }

    [Fact]
    public void A_concrete_path_meets_a_parameterised_template()
    {
        Assert.True(RouteTemplateIndex.Matches("contributors/*", "contributors/7"));
    }

    [Fact]
    public void A_template_does_not_meet_a_path_of_a_different_shape()
    {
        Assert.False(RouteTemplateIndex.Matches("contributors/*", "contributors"));
        Assert.False(RouteTemplateIndex.Matches("contributors", "contributors/7"));
    }

    [Fact]
    public void A_template_does_not_meet_an_unrelated_literal()
    {
        Assert.False(RouteTemplateIndex.Matches("contributors/*", "projects/7"));
    }

    /// <summary>
    /// Matching is ordinal, and the folding already lowercased both sides. Doing it with a
    /// culture-sensitive compare instead is how you get a rule that works in CI and not on the
    /// machine this was written on.
    /// </summary>
    [Fact]
    public void Matching_is_ordinal_after_folding()
    {
        var template = RouteTemplateIndex.Normalise("/Projects")!;
        var reference = RouteTemplateIndex.Normalise("/PROJECTS")!;

        Assert.True(RouteTemplateIndex.Matches(template, reference));
    }
}
