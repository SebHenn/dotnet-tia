namespace Fixtures.Core;

/// <summary>
/// Constructed only by <see cref="ReflectiveFactory"/>, which names it as a string. Deliberately in
/// its own file: while it sat next to the factory, a change to it was a change to the factory's
/// file, and the reflection rule fired for that reason rather than for the right one. Here, nothing
/// whatsoever connects this type to the test that asserts what it returns.
/// </summary>
public class Widget
{
    public override string ToString() => "widget";
}
