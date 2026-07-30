namespace Fixtures.Core;

/// <summary>
/// Exercised only by the NUnit fixture project, so selection over the VSTest dialect can be
/// asserted without disturbing the xUnit assertions.
/// </summary>
public class Counter
{
    private int _value;

    public int Increment() => ++_value;

    public int Decrement() => --_value;
}
