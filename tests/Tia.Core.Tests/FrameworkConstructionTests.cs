using Tia.Core.Analysis;
using Tia.Core.Model;
using Tia.Frameworks;

namespace Tia.Core.Tests;

/// <summary>
/// A constructor that only a DI container or a test framework calls reaches what names its type
/// (#40).
/// </summary>
/// <remarks>
/// Such a type has no <c>new Foo()</c> anywhere, so its constructor had no dependents and a change
/// to it selected nothing. The registration, or the <c>IClassFixture&lt;Foo&gt;</c> on a test
/// class, is what runs it.
/// </remarks>
public sealed class FrameworkConstructionTests
{
    /// <summary>
    /// The application shape: a service registered by type, resolved through its interface, and
    /// a test that builds the container through the registering method.
    /// </summary>
    [Fact]
    public void A_service_only_a_container_constructs_reaches_the_test_that_registers_it()
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App
            {
                public interface IGreeter { string Greet(); }

                public sealed class Greeter : IGreeter
                {
                    private readonly string _greeting;
                    public Greeter() { _greeting = "hello"; }
                    public string Greet() => _greeting;
                }

                public sealed class Services
                {
                    public void Add<TService, TImplementation>() where TImplementation : TService { }
                }

                public static class Startup
                {
                    public static Services Configure()
                    {
                        var services = new Services();
                        services.Add<IGreeter, Greeter>();
                        return services;
                    }
                }
            }

            namespace App.Tests
            {
                public class StartupTests { [Xunit.Fact] public void Builds() => App.Startup.Configure(); }
                public class OtherTests { [Xunit.Fact] public void Unrelated() { } }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2, graph);

        var traversal = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Greeter", ".ctor")]);
        var builds = CompilationHarness.KeyOf(compilation, "App.Tests.StartupTests", "Builds");

        Assert.Contains(builds, traversal.Impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Tests.OtherTests", "Unrelated"), traversal.Impacted);
        Assert.Contains(traversal.PathTo(builds), step => step.IncomingEdge == EdgeKind.Construction);
    }

    /// <summary>
    /// A class fixture's constructor and its <c>InitializeAsync</c>: the framework calls both, and
    /// the fixture edges hang off the fixture's type, which the constructor never reached.
    /// </summary>
    [Theory]
    [InlineData(".ctor")]
    [InlineData("InitializeAsync")]
    public void A_fixture_lifecycle_member_reaches_the_tests_that_use_the_fixture(string member)
    {
        var compilation = CompilationHarness.CompileValid(CompilationHarness.XunitShim + """
            namespace App.Tests
            {
                public class DatabaseFixture
                {
                    public DatabaseFixture() { Rows = 3; }
                    public int Rows { get; private set; }
                    public System.Threading.Tasks.Task InitializeAsync() { Rows++; return System.Threading.Tasks.Task.CompletedTask; }
                }

                public class WidgetTests : Xunit.IClassFixture<DatabaseFixture>
                {
                    [Xunit.Fact] public void Works() { }
                }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        new TestDiscoverer().Discover(compilation, "App.Tests", TestFramework.XUnitV2, graph);

        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Tests.DatabaseFixture", member)]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Tests.WidgetTests", "Works"), impacted);
    }

    /// <summary>
    /// A type constructed the ordinary way keeps its constructor's ordinary reach: the code that
    /// constructs it, and not every member that merely names the type.
    /// </summary>
    [Fact]
    public void A_constructor_somebody_calls_does_not_reach_everything_that_names_the_type()
    {
        var compilation = CompilationHarness.CompileValid("""
            namespace App
            {
                public sealed class Money { public Money() { Amount = 1; } public int Amount { get; } }
                public static class Factory { public static Money Make() => new Money(); }
                public static class Printer { public static string Print(Money money) => money.Amount.ToString(); }
            }
            """);

        var graph = CompilationHarness.BuildGraph(compilation);
        var impacted = new ImpactSelector().Traverse(graph, [CompilationHarness.KeyOf(compilation, "App.Money", ".ctor")]).Impacted;

        Assert.Contains(CompilationHarness.KeyOf(compilation, "App.Factory", "Make"), impacted);
        Assert.DoesNotContain(CompilationHarness.KeyOf(compilation, "App.Printer", "Print"), impacted);
    }
}
