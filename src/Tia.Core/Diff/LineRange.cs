using System.Globalization;

namespace Tia.Core.Diff;

/// <summary>A 1-based, inclusive range of source lines.</summary>
public readonly record struct LineRange(int Start, int End)
{
    public bool Intersects(int start, int end) => start <= End && end >= Start;

    public bool Intersects(LineRange other) => Intersects(other.Start, other.End);

    public bool Contains(int line) => line >= Start && line <= End;

    public override string ToString() =>
        Start == End
            ? Start.ToString(CultureInfo.InvariantCulture)
            : FormattableString.Invariant($"{Start}-{End}");
}
