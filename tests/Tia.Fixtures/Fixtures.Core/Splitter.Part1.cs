namespace Fixtures.Core;

/// <summary>The partial-class case, first half.</summary>
public partial class Splitter
{
    public string First(string value) => value.Split(',')[0];
}
