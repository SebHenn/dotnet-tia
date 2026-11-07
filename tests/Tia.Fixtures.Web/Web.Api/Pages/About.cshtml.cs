using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Web.Api.Pages;

/// <summary>
/// A page with no <c>@page</c> template, so its route is its file path alone (#169). The text is
/// decided in a constructor only the framework calls.
/// </summary>
public sealed class AboutModel : PageModel
{
    public AboutModel() => Text = "about contributors";

    public string Text { get; }
}
