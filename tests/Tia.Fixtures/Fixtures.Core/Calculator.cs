namespace Fixtures.Core;

/// <summary>The plain call-graph case: one caller, one callee, nothing clever.</summary>
public class Calculator
{
    public int Add(int a, int b) => a + b;

    public int Subtract(int a, int b) => a - b;
}
