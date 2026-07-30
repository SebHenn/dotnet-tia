namespace Fixtures.Core;

/// <summary>
/// The reflection case, and a genuine blind spot: <see cref="Widget"/> is named only as a string,
/// so no static edge connects a change in Widget to the code that constructs and calls it.
/// </summary>
public static class ReflectiveFactory
{
    public static string Describe()
    {
        var type = Type.GetType("Fixtures.Core.Widget, Fixtures.Core");
        var instance = type is null ? null : Activator.CreateInstance(type);
        return instance?.ToString() ?? "none";
    }
}

public class Widget
{
    public override string ToString() => "widget";
}
