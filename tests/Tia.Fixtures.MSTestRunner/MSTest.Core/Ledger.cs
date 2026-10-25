namespace MSTest.Fixtures;

/// <summary>The plain case: methods each test calls directly.</summary>
public class Ledger
{
    private int _balance;

    public int Deposit(int amount) => _balance += amount;

    public int Withdraw(int amount) => _balance -= amount;

    public int Balance => _balance;

    public bool IsOverdrawn => _balance < 0;

    /// <summary>Reaches its fee through the interface, so a fee's implementation is two hops away.</summary>
    public int Charge(IFee fee, int amount)
    {
        var cost = fee.For(amount);
        _balance -= cost;
        return cost;
    }
}

public interface IFee
{
    int For(int amount);
}

public sealed class FlatFee : IFee
{
    public int For(int amount) => amount > 0 ? 100 : 0;
}

public sealed class PercentFee(int percent) : IFee
{
    public int For(int amount) => amount * percent / 100;
}

/// <summary>Reached only through a class's [ClassInitialize], and a parameterised test.</summary>
public static class Formatter
{
    public static string Money(int cents) => cents < 0 ? "-" + Money(-cents) : $"{cents / 100}.{cents % 100:00}";

    public static string Percent(int value) => $"{value}%";

    public static string Prefix() => "EUR ";
}
