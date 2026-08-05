using Fixtures.Core;
using NUnit.Framework;

namespace Fixtures.NUnitTests;

public class CounterTests
{
    private Counter _counter = null!;

    [SetUp]
    public void CreateCounter() => _counter = new Counter();

    [Test]
    public void Increments() => Assert.That(_counter.Increment(), Is.EqualTo(1));

    [Test]
    public void Decrements() => Assert.That(_counter.Decrement(), Is.EqualTo(-1));

    [TestCase(1)]
    [TestCase(2)]
    public void Increments_repeatedly(int times)
    {
        for (var i = 0; i < times; i++)
        {
            _counter.Increment();
        }

        Assert.That(_counter.Increment(), Is.EqualTo(times + 1));
    }
}
