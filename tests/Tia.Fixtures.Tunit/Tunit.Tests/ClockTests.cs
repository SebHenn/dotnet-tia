using Tunit.Fixtures;

namespace Tunit.Fixtures.Tests;

public class ClockTests
{
    [Test]
    public async Task Ticks()
    {
        var clock = new Clock();
        await Assert.That(clock.Tick()).IsEqualTo(1);
    }

    [Test]
    public async Task Starts_at_midnight()
    {
        var clock = new Clock();
        await Assert.That(clock.IsMidnight()).IsTrue();
    }
}

public class CalendarTests
{
    [Test]
    public async Task Counts_february()
    {
        await Assert.That(new Calendar().DaysIn(2)).IsEqualTo(28);
    }

    [Test]
    public async Task Recognises_a_leap_day()
    {
        await Assert.That(new Calendar().IsLeapDay(2, 29)).IsTrue();
    }
}
