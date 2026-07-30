namespace Tunit.Fixtures;

public class Clock
{
    public int Hour { get; private set; }

    public int Tick() => Hour = (Hour + 1) % 24;

    public bool IsMidnight() => Hour == 0;
}
