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

    /// <summary>
    /// A Razor Page is routed by where its file sits under <c>Pages</c>, and its <c>@page</c>
    /// template is appended to that, or replaces it when it starts with a slash (#169).
    /// </summary>
    [Theory]
    [InlineData("/Pages/About.cshtml", null, "about")]
    [InlineData("/Pages/Contributors/Show.cshtml", null, "contributors/show")]
    [InlineData("/Pages/Contributors/Show.cshtml", "{id:int}", "contributors/show/*")]
    [InlineData("/Pages/Contributors/Show.cshtml", "/people/{id}", "people/*")]
    [InlineData("/Pages/Contributors/Show.cshtml", "~/people/{id}", "people/*")]
    [InlineData("/Areas/Admin/Pages/Users.cshtml", null, "admin/users")]
    public void A_razor_page_is_routed_by_its_path_and_template(string identifier, string? template, string expected)
    {
        Assert.Equal([expected], RouteTemplateIndex.RazorPageRoutes(identifier, template));
    }

    /// <summary>
    /// An <c>Index</c> page answers at its folder as well as by name. The root one answers at
    /// <c>/</c>, which matches nothing anyone could tell apart, so only its named route is kept.
    /// </summary>
    [Theory]
    [InlineData("/Pages/Contributors/Index.cshtml", null, "contributors/index", "contributors")]
    [InlineData("/Pages/Contributors/Index.cshtml", "{id}", "contributors/index/*", "contributors/*")]
    [InlineData("/Pages/Index.cshtml", null, "index", null)]
    public void An_index_page_answers_at_its_folder_too(string identifier, string? template, string named, string? folder)
    {
        string[] expected = folder is null ? [named] : [named, folder];

        Assert.Equal(expected, RouteTemplateIndex.RazorPageRoutes(identifier, template));
    }

    /// <summary>
    /// A trailing optional or catch-all parameter may be left out of the request, and a template
    /// with one segment too many meets nothing, so the route without it is kept as well.
    /// </summary>
    [Theory]
    [InlineData("{id?}")]
    [InlineData("{*rest}")]
    [InlineData("{**rest}")]
    public void A_trailing_optional_segment_may_be_left_out(string template)
    {
        Assert.Equal(["contributors/show/*", "contributors/show"],
            RouteTemplateIndex.RazorPageRoutes("/Pages/Contributors/Show.cshtml", template));
    }
}
