namespace Tia.Core.Safety;

/// <summary>
/// The second tier: changes that widen scope rather than bail out. Non-source content is
/// routinely test data or an embedded resource, and nothing in a symbol graph connects a
/// <c>.json</c> fixture to the test that loads it by path.
/// </summary>
/// <remarks>
/// <para>
/// This used to be an allow-list of extensions - <c>.json</c>, <c>.resx</c>, <c>.xml</c> and a
/// dozen more - which meant every data format nobody had thought of was silently ignored. The
/// NodaTime replay found what that costs: <c>Update TZDB to 2026c</c> changes
/// <c>src/NodaTime/TimeZones/Tzdb.nzd</c>, the embedded time zone database that the library reads
/// at runtime and that its tests assert the version of, and <c>tia</c> selected **zero tests**.
/// An allow-list of data formats is a list of the ones you have been bitten by.
/// </para>
/// <para>
/// So the rule is inverted: any non-source file that belongs to a project widens it. The scope is
/// what keeps that affordable - a file outside every project directory has no owner and widens
/// nothing, which is why a repository's <c>docs/</c> and <c>.github/</c> still select nothing at
/// all. What is left is files a project actually ships, and being wrong about one of those costs a
/// full run rather than a broken build.
/// </para>
/// <para>
/// This is also the one miss class the mutation gate can never find, because a mutation only ever
/// edits C#. It took replaying real commits, which is the argument for keeping both harnesses.
/// </para>
/// </remarks>
public static class ContentFileRules
{
    /// <summary>
    /// True when a file should widen its owning project to full scope. Everything that is not C#
    /// does, and ownership decides the rest.
    /// </summary>
    /// <remarks>
    /// There is deliberately no list of data extensions. An embedded resource can have any
    /// extension at all, and the tool cannot know which ones a given repository gives meaning to -
    /// NodaTime's is <c>.nzd</c>, and no list anyone would have written contains it. C# is excluded
    /// because it has a graph: its changes are resolved to symbols, which is strictly better than
    /// widening.
    /// </remarks>
    public static bool IsWideningContent(string path) =>
        path.Length > 0 && !Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);
}
