using Microsoft.AspNetCore.Mvc;

namespace Web.Api;

/// <summary>Attribute routing, where the template lives on the type and the method.</summary>
[ApiController]
[Route("projects")]
public sealed class ProjectsController : ControllerBase
{
    [HttpGet]
    public IActionResult List() => Ok(new[] { "tia" });
}
