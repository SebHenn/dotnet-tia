namespace Web.Api;

/// <summary>
/// Routes named once and referenced by constant. The literal reaching the Map call is a
/// <c>const</c>, so anything that only pattern-matches string literals at the call site sees
/// nothing here.
/// </summary>
public static class Routes
{
    public const string Count = "/contributors/count";
}
