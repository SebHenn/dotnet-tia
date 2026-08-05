using Tia.Core.Caching;
using Tia.Core.Model;
using Tia.Core.Validation;

namespace Tia.Core.Tests;

public sealed class CacheAndValidationTests
{
    [Fact]
    public void A_saved_graph_round_trips()
    {
        var graph = new ImpactGraph();
        graph.AddNode(new SymbolNode
        {
            Key = "Lib|T:App.Widget",
            DisplayName = "App.Widget",
            Kind = SymbolNodeKind.Type,
            ProjectName = "Lib",
            FilePath = "/repo/Widget.cs",
        });
        graph.AddNode(new SymbolNode
        {
            Key = "Lib|M:App.Widget.Value",
            DisplayName = "App.Widget.Value()",
            Kind = SymbolNodeKind.Method,
            ProjectName = "Lib",
            ContainingTypeKey = "Lib|T:App.Widget",
        });
        graph.AddEdge("Lib|M:App.Widget.Value", "Lib|M:App.Caller.Use", EdgeKind.Reference | EdgeKind.ImplementationToInterface);

        var cache = GraphCache.Empty("sdk-1");
        cache.Projects["Lib"] = new ProjectGraphFragment
        {
            ProjectName = "Lib",
            Fingerprint = "abc123",
            Graph = graph,
            Tests =
            [
                new TestMethod
                {
                    SymbolKey = "Lib|M:App.Tests.T.Works",
                    ClassKey = "Lib|T:App.Tests.T",
                    Namespace = "App.Tests",
                    ClassName = "T",
                    MethodName = "Works",
                    ProjectName = "Lib",
                    Framework = TestFramework.NUnit,
                    IsParameterized = true,
                },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"tia-cache-{Guid.NewGuid():n}.bin");
        try
        {
            cache.Save(path);
            var loaded = GraphCache.TryLoad(path, "sdk-1");

            Assert.NotNull(loaded);
            var fragment = loaded.Projects["Lib"];
            Assert.Equal("abc123", fragment.Fingerprint);
            Assert.Equal(2, fragment.Graph.NodeCount);
            Assert.Equal("/repo/Widget.cs", fragment.Graph.TryGetNode("Lib|T:App.Widget")!.FilePath);
            Assert.Equal(["Lib|M:App.Widget.Value"], fragment.Graph.MembersOfType("Lib|T:App.Widget"));
            Assert.Equal(
                EdgeKind.Reference | EdgeKind.ImplementationToInterface,
                fragment.Graph.DependentsOf("Lib|M:App.Widget.Value")["Lib|M:App.Caller.Use"]);

            var test = Assert.Single(fragment.Tests);
            Assert.Equal("App.Tests.T.Works", test.FullyQualifiedName);
            Assert.True(test.IsParameterized);
            Assert.Equal(TestFramework.NUnit, test.Framework);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_cache_written_in_an_older_format_is_rejected()
    {
        // The version check was unreachable by any test: the corrupt-cache test below writes plain
        // text, which fails the magic-number check one expression earlier. So a format bump
        // shipped with no proof that old caches are rejected rather than misread - and a misread
        // cache is a wrong answer, not a slow run.
        var path = Path.Combine(Path.GetTempPath(), $"tia-cache-{Guid.NewGuid():n}.bin");
        try
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(0x47414954u);  // "TIAG" - the right magic
                writer.Write(1);            // a format this build does not speak
                writer.Write(0);            // an empty string table, so the file is otherwise sane
            }

            Assert.Null(GraphCache.TryLoad(path, "sdk-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_truncated_cache_is_ignored_rather_than_fatal()
    {
        // The commonest real corruption: a CI job killed mid-write leaves a valid prefix, not
        // garbage. Garbage fails at the first byte; a prefix fails somewhere in the middle.
        var path = Path.Combine(Path.GetTempPath(), $"tia-cache-{Guid.NewGuid():n}.bin");
        try
        {
            GraphCache.Empty("sdk-1").Save(path);
            var complete = File.ReadAllBytes(path);
            File.WriteAllBytes(path, complete[..(complete.Length / 2)]);

            Assert.Null(GraphCache.TryLoad(path, "sdk-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Every_fragment_field_survives_a_round_trip()
    {
        // The existing round trip leaves ContentHash, SurfaceHash, CompileError and Reflections at
        // their defaults, so the reuse decision's inputs and the reflection records were written
        // and read by code no test had ever executed. CompileError also exercises the null
        // sentinel, and a reflection site outside any member is the case ReflectionRecord's own
        // documentation calls out as significant.
        var cache = GraphCache.Empty("sdk-1");
        cache.Projects["Lib"] = new ProjectGraphFragment
        {
            ProjectName = "Lib",
            Fingerprint = "fingerprint",
            ContentHash = "content-hash",
            SurfaceHash = "surface-hash",
            CompileError = "CS0246: missing type",
            Reflections =
            [
                new ReflectionRecord("Activator.CreateInstance", "Lib|M:App.Factory.Make", "/repo/Factory.cs"),
                new ReflectionRecord("dynamic", null, "/repo/Loose.cs"),
            ],
            Graph = new ImpactGraph(),
            Tests = [],
        };

        var path = Path.Combine(Path.GetTempPath(), $"tia-cache-{Guid.NewGuid():n}.bin");
        try
        {
            cache.Save(path);
            var fragment = GraphCache.TryLoad(path, "sdk-1")!.Projects["Lib"];

            Assert.Equal("content-hash", fragment.ContentHash);
            Assert.Equal("surface-hash", fragment.SurfaceHash);
            Assert.Equal("CS0246: missing type", fragment.CompileError);
            Assert.Equal(2, fragment.Reflections.Count);
            Assert.Equal("Lib|M:App.Factory.Make", fragment.Reflections[0].OwningMemberKey);
            Assert.Null(fragment.Reflections[1].OwningMemberKey);
            Assert.Equal("/repo/Loose.cs", fragment.Reflections[1].FilePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Saving_over_an_existing_cache_replaces_it()
    {
        // Save writes to a .tmp and moves it over, so an interrupted write cannot leave a torn
        // file in place. Every other test writes to a fresh path and never exercises the move.
        var path = Path.Combine(Path.GetTempPath(), $"tia-cache-{Guid.NewGuid():n}.bin");
        try
        {
            var first = GraphCache.Empty("sdk-1");
            first.Projects["Lib"] = Fragment("first");
            first.Save(path);

            var second = GraphCache.Empty("sdk-1");
            second.Projects["Lib"] = Fragment("second");
            second.Save(path);

            Assert.Equal("second", GraphCache.TryLoad(path, "sdk-1")!.Projects["Lib"].Fingerprint);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            File.Delete(path);
        }

        static ProjectGraphFragment Fragment(string fingerprint) => new()
        {
            ProjectName = "Lib",
            Fingerprint = fingerprint,
            Graph = new ImpactGraph(),
            Tests = [],
        };
    }

    [Fact]
    public void A_cache_written_by_another_sdk_is_ignored()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tia-cache-{Guid.NewGuid():n}.bin");
        try
        {
            GraphCache.Empty("sdk-1").Save(path);

            Assert.Null(GraphCache.TryLoad(path, "sdk-2"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_corrupt_cache_is_ignored_rather_than_fatal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tia-cache-{Guid.NewGuid():n}.bin");
        try
        {
            File.WriteAllText(path, "not a graph");

            Assert.Null(GraphCache.TryLoad(path, "sdk-1"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mutating_changes_the_source_and_says_what_it_did()
    {
        const string source = """
            namespace App
            {
                public class Widget
                {
                    public bool IsReady(int count) { return count > 3; }
                }
            }
            """;

        var mutation = new MutationEngine().TryMutate("/repo/Widget.cs", source, new Random(1));

        Assert.NotNull(mutation);
        Assert.NotEqual(source, mutation.MutatedText);
        Assert.Equal("Widget.IsReady", mutation.Member);
        Assert.NotEmpty(mutation.Description);
    }

    [Fact]
    public void Mutation_reports_nothing_when_there_is_nothing_to_mutate()
    {
        Assert.Null(new MutationEngine().TryMutate("/repo/Empty.cs", "namespace App { }", new Random(1)));
    }

    [Fact]
    public void Trx_results_are_read_and_data_cases_normalise_to_their_method()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tia-{Guid.NewGuid():n}.trx");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="App.Tests.WidgetTests.Works" outcome="Passed" />
                <UnitTestResult testName="App.Tests.WidgetTests.Cases(value: 3)" outcome="Failed" />
              </Results>
            </TestRun>
            """);

        try
        {
            var failed = Assert.Single(TrxParser.FailedTests(path));

            Assert.Equal("App.Tests.WidgetTests.Cases(value: 3)", failed);
            Assert.Equal("App.Tests.WidgetTests.Cases", TrxParser.NormalizeTestName(failed));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_NUnit_result_is_qualified_from_its_definition()
    {
        // NUnit's testName is the bare method name; the class is only in the definition. Reading
        // testName alone made every failing test in an NUnit repository look unselected, which is
        // how the gate came to report 362 misses of its own making on NodaTime.
        var path = WriteTrx("""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="a" testName="Increments" outcome="Passed" />
                <UnitTestResult testId="b" testName="Decrements" outcome="Failed" />
              </Results>
              <TestDefinitions>
                <UnitTest id="a"><TestMethod className="App.Tests.CounterTests" name="Increments" /></UnitTest>
                <UnitTest id="b"><TestMethod className="App.Tests.CounterTests" name="Decrements" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """);

        try
        {
            Assert.Equal("App.Tests.CounterTests.Decrements", Assert.Single(TrxParser.FailedTests(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void An_already_qualified_name_is_not_qualified_twice()
    {
        // xUnit puts the fully qualified name in both places.
        var path = WriteTrx("""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="a" testName="App.Tests.WidgetTests.Works" outcome="Failed" />
              </Results>
              <TestDefinitions>
                <UnitTest id="a"><TestMethod className="App.Tests.WidgetTests" name="App.Tests.WidgetTests.Works" /></UnitTest>
              </TestDefinitions>
            </TestRun>
            """);

        try
        {
            Assert.Equal("App.Tests.WidgetTests.Works", Assert.Single(TrxParser.FailedTests(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_result_with_no_definition_falls_back_to_its_reported_name()
    {
        var path = WriteTrx("""
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testId="missing" testName="App.Tests.WidgetTests.Works" outcome="Failed" />
              </Results>
            </TestRun>
            """);

        try
        {
            Assert.Equal("App.Tests.WidgetTests.Works", Assert.Single(TrxParser.FailedTests(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    // A parameterised NUnit *fixture* puts its arguments in the class name, so the arguments are
    // not always at the end - cutting at the first bracket threw the method name away.
    [InlineData("App.Tests.CounterTests(3).Increments(4)", "App.Tests.CounterTests.Increments")]
    [InlineData("App.Tests.WidgetTests.Cases(value: 3)", "App.Tests.WidgetTests.Cases")]
    [InlineData("App.Tests.WidgetTests.Works", "App.Tests.WidgetTests.Works")]
    [InlineData("Serialises(\"a,b\", 1)", "Serialises")]
    public void A_data_case_normalises_to_its_method(string reported, string expected)
    {
        Assert.Equal(expected, TrxParser.NormalizeTestName(reported));
    }

    private static string WriteTrx(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tia-{Guid.NewGuid():n}.trx");
        File.WriteAllText(path, content);
        return path;
    }
}
