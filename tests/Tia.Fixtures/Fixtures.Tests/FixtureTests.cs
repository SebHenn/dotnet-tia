using Fixtures.Core;

namespace Fixtures.Tests;

public class CalculatorTests
{
    [Fact]
    public void Adds() => Assert.Equal(3, new Calculator().Add(1, 2));

    [Fact]
    public void Subtracts() => Assert.Equal(1, new Calculator().Subtract(3, 2));
}

public class GreeterServiceTests
{
    [Fact]
    public void Welcomes_through_the_interface() =>
        Assert.Equal("Hello, Ada!", new GreeterService(new EnglishGreeter()).Welcome("Ada"));
}

public class LimitsTests
{
    [Fact]
    public void Doubles_the_limit() => Assert.Equal(6, Limits.DoubleTheLimit());
}

public class BoxTests
{
    [Fact]
    public void Round_trips()
    {
        var box = new Box<int>();
        box.Put(7);
        Assert.Equal(7, box.Take());
    }
}

public class SplitterTests
{
    [Fact]
    public void Takes_the_first() => Assert.Equal("a", new Splitter().First("a,b,c"));

    [Fact]
    public void Takes_the_last() => Assert.Equal("c", new Splitter().Last("a,b,c"));
}

public class ReflectiveFactoryTests
{
    [Fact]
    public void Creates_by_name() =>
        Assert.NotNull(ReflectiveFactory.Create(typeof(Widget).AssemblyQualifiedName!));
}
