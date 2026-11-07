using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Api.Pages.Contributors;

/// <summary>
/// What a contributor's page shows. A class rather than a record, whose ToString would already
/// name every property: only the page reads <see cref="Display"/> (#169).
/// </summary>
public sealed class ContributorCard(int id)
{
    public int Id { get; } = id;

    public string Display => $"contributor #{Id}";
}

/// <summary>
/// A Razor Page's model (#169). The framework creates it and calls <see cref="OnGet"/>, and the
/// page is found by its file path and its <c>@page</c> template, so nothing in the solution names
/// either the page or this class's handler.
/// </summary>
public sealed class ShowModel : PageModel
{
    public string Heading { get; private set; } = string.Empty;

    public ContributorCard Card { get; private set; } = new(0);

    public void OnGet(int id)
    {
        Heading = "Contributor " + id;
        Card = new ContributorCard(id);
    }
}
