namespace Tunit.Fixtures;

/// <summary>A second subject, so a selection can span two test classes - which is the case that
/// forces the tree-node dialect into a segment cross-product.</summary>
public class Calendar
{
    public int DaysIn(int month) => month == 2 ? 28 : 30;

    public bool IsLeapDay(int month, int day) => month == 2 && day == 29;
}
