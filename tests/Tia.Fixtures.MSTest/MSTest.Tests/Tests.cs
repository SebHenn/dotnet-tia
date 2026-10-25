using MSTest.Fixtures;

namespace MSTest.Fixtures.Tests;

[TestClass]
public sealed class LedgerTests
{
    private Ledger _ledger = null!;

    // Nothing calls it; only the fixture edge from [TestInitialize] connects it to the tests.
    [TestInitialize]
    public void CreateLedger() => _ledger = new Ledger();

    [TestMethod]
    public void Deposits() => Assert.AreEqual(5, _ledger.Deposit(5));

    [TestMethod]
    public void Withdraws() => Assert.AreEqual(-5, _ledger.Withdraw(5));

    [TestMethod]
    public void Charges_a_fee()
    {
        Assert.AreEqual(100, _ledger.Charge(new FlatFee(), 500));
        Assert.AreEqual(-100, _ledger.Balance);
    }

    [TestMethod]
    public void Is_overdrawn_after_a_charge()
    {
        _ledger.Charge(new PercentFee(10), 500);
        Assert.IsTrue(_ledger.IsOverdrawn);
    }
}

[TestClass]
public sealed class FeeTests
{
    [TestMethod]
    public void A_flat_fee_is_charged_on_any_amount() => Assert.AreEqual(100, new FlatFee().For(1));

    [TestMethod]
    public void A_flat_fee_is_not_charged_on_nothing() => Assert.AreEqual(0, new FlatFee().For(0));

    [TestMethod]
    public void A_percent_fee_scales() => Assert.AreEqual(25, new PercentFee(5).For(500));
}

[TestClass]
public sealed class FormatterTests
{
    private static string _prefix = string.Empty;

    [ClassInitialize]
    public static void Prepare(TestContext context) => _prefix = Formatter.Prefix();

    [TestMethod]
    public void Formats_money() => Assert.AreEqual("EUR 1.05", _prefix + Formatter.Money(105));

    [TestMethod]
    public void Formats_negative_money() => Assert.AreEqual("-2.50", Formatter.Money(-250));

    [TestMethod]
    [DataRow(5)]
    [DataRow(50)]
    public void Formats_percent(int value) => Assert.AreEqual($"{value}%", Formatter.Percent(value));
}
