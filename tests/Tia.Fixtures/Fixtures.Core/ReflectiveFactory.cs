namespace Fixtures.Core;

/// <summary>
/// The reflection case. Nothing statically references <see cref="Widget"/>'s constructor, so the
/// call graph has a hole here and the containing project has to be widened rather than trusted.
/// </summary>
public static class ReflectiveFactory
{
    public static object? Create(string typeName)
    {
        var type = Type.GetType(typeName);
        return type is null ? null : Activator.CreateInstance(type);
    }
}

public class Widget
{
    public string Describe() => "widget";
}
