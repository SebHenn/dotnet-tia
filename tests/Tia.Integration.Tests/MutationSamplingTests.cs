using Tia.Core.Model;
using Tia.Core.Validation;
using Tia.Workspace;
using Tia.Workspace.Harness;

namespace Tia.Integration.Tests;

/// <summary>
/// How the mutation gate chooses what to mutate (#39).
/// </summary>
/// <remarks>
/// It drew a file and then a site in it, both with replacement, from every production project. On
/// the fixture gate 47 mutated samples covered about 14 distinct mutations, one of them six times,
/// files with no site cost a skipped sample each, and on NodaTime 7 of 25 samples mutated benchmark
/// projects no test can observe.
/// </remarks>
public sealed class MutationSamplingTests : IDisposable
{
    private const string Source = """
        namespace App
        {
            public class Widget
            {
                public bool IsReady(int count) { return count > 3; }

                public void Touch(System.Collections.Generic.List<int> seen)
                {
                    seen.Add(1);
                    seen.Add(2);
                }
            }
        }
        """;

    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("tia-sampling-");

    public void Dispose() => _directory.Delete(recursive: true);

    /// <summary>
    /// Each site names exactly one mutation, so drawing sites without replacement draws distinct
    /// mutations. A dropped statement used to be one site per block with the statement picked at
    /// random inside it.
    /// </summary>
    [Fact]
    public void Every_site_is_its_own_distinct_mutation()
    {
        var engine = new MutationEngine();
        var count = engine.CountSites("/repo/Widget.cs", Source);

        var mutations = Enumerable.Range(0, count)
            .Select(site => engine.MutateAt("/repo/Widget.cs", Source, site))
            .OfType<Mutation>()
            .ToList();

        // `>`, `3`, `1`, `2`, and each of the two statements dropped.
        Assert.Equal(6, count);
        Assert.Equal(count, mutations.Count);
        Assert.Equal(count, mutations.Select(m => m.MutatedText).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, mutations.Count(m => m.Description.StartsWith("dropped statement", StringComparison.Ordinal)));
    }

    /// <summary>
    /// The whole space, each site once, files without sites contributing nothing - and the same
    /// order for the same seed, which is what makes `--seed` replay a run.
    /// </summary>
    [Fact]
    public void The_site_space_holds_every_site_once_in_an_order_the_seed_decides()
    {
        var widget = Write("Widget.cs", Source);
        var empty = Write("Empty.cs", "namespace App { }");
        var engine = new MutationEngine();

        var first = MutationHarness.SiteSpace([widget, empty], engine, new Random(7));
        var again = MutationHarness.SiteSpace([empty, widget], engine, new Random(7));
        var other = MutationHarness.SiteSpace([widget, empty], engine, new Random(8));

        Assert.Equal(6, first.Count);
        Assert.Equal(first.Count, first.Distinct().Count());
        Assert.All(first, site => Assert.Equal(widget, site.File));
        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
    }

    /// <summary>
    /// Only production code a test project can reach, directly or through another project. What
    /// is left out is named, because it is a decision about what the gate covers.
    /// </summary>
    [Fact]
    public void Only_projects_a_test_project_reaches_are_mutated()
    {
        var core = Project("Core", isTest: false);
        var app = Project("App", isTest: false, "Core");
        var benchmarks = Project("Benchmarks", isTest: false, "Core");
        var tests = Project("App.Tests", isTest: true, "App");

        var (files, unreferenced) = MutationHarness.CollectCandidates([core, app, benchmarks, tests]);

        Assert.Equal(["App", "Core"], files.Select(f => Path.GetFileName(Path.GetDirectoryName(f))).Order(StringComparer.Ordinal));
        Assert.Equal(["Benchmarks"], unreferenced);
    }

    private ProjectDescriptor Project(string name, bool isTest, params string[] references)
    {
        var directory = Path.Combine(_directory.FullName, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Code.cs"), "class C { int X => 1; }");

        return new ProjectDescriptor
        {
            Name = name,
            AssemblyName = name,
            FilePath = Path.Combine(directory, name + ".csproj"),
            IsTestProject = isTest,
            ProjectReferences = references,
        };
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_directory.FullName, name);
        File.WriteAllText(path, text);
        return path;
    }
}
