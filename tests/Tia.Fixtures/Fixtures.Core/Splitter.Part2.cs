namespace Fixtures.Core;

/// <summary>The partial-class case, second half. The symbols of both halves are one type.</summary>
public partial class Splitter
{
    public string Last(string value)
    {
        var parts = value.Split(',');
        return parts[^1];
    }
}
