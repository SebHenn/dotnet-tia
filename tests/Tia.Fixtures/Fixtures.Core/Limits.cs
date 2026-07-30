namespace Fixtures.Core;

/// <summary>
/// The constant-inlining case. Callers bake the value in at compile time and carry no reference
/// to the field, so a change here is invisible to any call-graph walk.
/// </summary>
public static class Limits
{
    public const int MaxRetries = 3;

    public static int DoubleTheLimit() => MaxRetries * 2;
}
