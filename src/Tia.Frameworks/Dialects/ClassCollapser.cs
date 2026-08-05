using Tia.Core.Model;

namespace Tia.Frameworks.Dialects;

/// <summary>A class whose every test was selected, so one clause can stand for all of them.</summary>
public sealed record CollapsedFilter(IReadOnlyList<TestMethod> WholeClasses, IReadOnlyList<TestMethod> IndividualTests);

/// <summary>
/// Shortens a filter by naming a whole class once instead of each of its tests.
/// </summary>
/// <remarks>
/// This is not cosmetic. A filter is abandoned entirely once it outgrows the command line, and on
/// a large suite that happens easily: 1,037 selected tests in NodaTime produced an 86,000
/// character filter, so the project ran whole - 1,734 tests instead of 1,037. Collapsing only
/// classes whose tests are *all* selected keeps the filter exact while cutting most of its length,
/// because selection tends to take classes wholesale.
/// </remarks>
public static class ClassCollapser
{
    public static CollapsedFilter Collapse(IReadOnlyList<TestMethod> selected, IReadOnlyList<TestMethod> allInProject)
    {
        if (allInProject.Count == 0)
        {
            return new CollapsedFilter([], selected);
        }

        var totalPerClass = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var test in allInProject)
        {
            var key = ClassKey(test);
            totalPerClass[key] = totalPerClass.GetValueOrDefault(key) + 1;
        }

        var selectedPerClass = new Dictionary<string, List<TestMethod>>(StringComparer.Ordinal);
        foreach (var test in selected)
        {
            var key = ClassKey(test);
            if (!selectedPerClass.TryGetValue(key, out var list))
            {
                list = [];
                selectedPerClass[key] = list;
            }

            list.Add(test);
        }

        var wholeClasses = new List<TestMethod>();
        var individual = new List<TestMethod>();

        foreach (var (key, tests) in selectedPerClass)
        {
            // Only when every test in the class is selected: anything less would silently run
            // tests the analysis did not choose.
            if (tests.Count > 1 && totalPerClass.GetValueOrDefault(key) == tests.Count)
            {
                wholeClasses.Add(tests[0]);
            }
            else
            {
                individual.AddRange(tests);
            }
        }

        return new CollapsedFilter(wholeClasses, individual);
    }

    private static string ClassKey(TestMethod test) =>
        test.Namespace.Length == 0 ? test.ClassName : test.Namespace + "." + test.ClassName;

    public static string FullClassName(TestMethod test) => ClassKey(test);
}
