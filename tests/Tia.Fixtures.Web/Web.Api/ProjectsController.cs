using Microsoft.AspNetCore.Mvc;

namespace Web.Api;

/// <summary>
/// What the controller returns. A class rather than a record, whose ToString would already name
/// every property: nothing in the source reads <see cref="Slug"/>, only the serializer (#125).
/// </summary>
public sealed class ProjectSummary(string name)
{
    public string Name { get; } = name;

    public string Slug => Name.Replace(' ', '-').ToLowerInvariant();
}

/// <summary>Attribute routing, where the template lives on the type and the method.</summary>
[ApiController]
[Route("projects")]
public sealed class ProjectsController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(new[] { new ProjectSummary("Test Impact") });
}
