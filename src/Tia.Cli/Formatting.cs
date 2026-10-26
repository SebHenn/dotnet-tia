using System.Globalization;

namespace Tia.Cli;

/// <summary>How the console report and the Markdown summary write numbers and commits.</summary>
internal static class Formatting
{
    public static string Number(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Invariant, like every other number here. The counts were already pinned and the
    /// percentages were not, so on a runner whose culture uses a decimal comma one line read
    /// "1,204 of 3,730 (8,2 %)" - two conventions in the same sentence. Tool output is a format
    /// other programs and humans read across machines; it should not move with the locale.
    /// </summary>
    public static string Percent(double ratio) => FormattableString.Invariant($"{ratio:P1}");

    public static string Short(string commit) => commit.Length > 9 ? commit[..9] : commit;
}
