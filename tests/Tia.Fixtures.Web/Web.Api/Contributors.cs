namespace Web.Api;

public sealed record Contributor(int Id, string Name);

/// <summary>
/// The endpoint bodies. Nothing outside this assembly names these methods: the framework maps a
/// route string to them by registration, and a functional test names only the route and the
/// response shape. That is the whole gap this fixture exists to measure.
/// </summary>
public static class Contributors
{
    public static IResult List() => Results.Ok(new[] { new Contributor(1, "ada") });

    public static IResult ById(int id) => Results.Ok(new Contributor(id, "ada"));

    public static IResult Count() => Results.Ok(1);
}
