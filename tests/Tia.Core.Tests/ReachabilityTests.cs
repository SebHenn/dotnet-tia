using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class ReachabilityTests
{
    private static IReadOnlyList<string> Unreached(string source, bool library = false)
    {
        var compilation = CompilationHarness.CompileValid(source);
        var graph = CompilationHarness.BuildGraph(compilation);
        var roots = Reachability.EntryPoints(graph).Concat(CompilationRoots.AssemblyAttributes(compilation));
        if (library)
        {
            roots = roots.Concat(CompilationRoots.PublicApi(compilation));
        }

        return [.. Reachability.Sweep(graph, roots).Unreached.Select(node => node.DisplayName)];
    }

    [Fact]
    public void A_member_the_runtime_calls_through_an_interface_outside_the_solution_lives_with_its_type()
    {
        // Nothing calls Dispose or ToString here, and nothing in source could: `using` and string
        // formatting go through System.IDisposable and System.Object.
        var unreached = Unreached("""
            public sealed class Handle : System.IDisposable
            {
                public void Dispose() { }
                public override string ToString() => "handle";
                public void Unused() { }
            }
            public sealed class Never : System.IDisposable
            {
                public void Dispose() { }
            }
            public static class Program
            {
                public static void Main() => System.GC.KeepAlive(new Handle());
            }
            """);

        Assert.Equal(["Handle.Unused()", "Never"], unreached);
    }

    [Fact]
    public void A_record_is_not_reported_for_the_members_the_compiler_wrote_into_it()
    {
        // Y is never read in source, and is still not a finding: the record's equality and its
        // printed form read it, and both are reachable from outside through System.Object.
        var unreached = Unreached("""
            public sealed record Point(int X, int Y)
            {
                public int Sum() => X + Y;
            }
            public static class Program
            {
                public static int Main() => new Point(1, 2).X;
            }
            """);

        Assert.Equal(["Point.Sum()"], unreached);
    }

    [Fact]
    public void A_type_handed_to_an_assembly_level_attribute_is_live()
    {
        var unreached = Unreached("""
            [assembly: Starts(typeof(App))]

            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class StartsAttribute : System.Attribute
            {
                public StartsAttribute(System.Type type) { }
            }
            public static class App
            {
                public static void Configure() { }
            }
            public static class Program
            {
                public static void Main() { }
            }
            """);

        Assert.Equal(["App.Configure()"], unreached);
    }

    [Fact]
    public void A_librarys_public_members_are_roots_and_what_only_its_internals_use_is_not()
    {
        var unreached = Unreached(
            """
            public class Api
            {
                public void Call() => Helper.Used();
                protected void ForSubclasses() { }
                internal void Forgotten() { }
            }
            internal static class Helper
            {
                public static void Used() { }
                public static void Unused() { }
            }
            """,
            library: true);

        Assert.Equal(["Api.Forgotten()", "Helper.Unused()"], unreached);
    }

    [Fact]
    public void Methods_that_only_call_each_other_are_unreached_although_each_has_a_reference()
    {
        var unreached = Unreached("""
            public static class Program
            {
                public static void Main() => Used();
                private static void Used() { }
                private static void Ping() => Pong();
                private static void Pong() => Ping();
            }
            """);

        Assert.Equal(["Program.Ping()", "Program.Pong()"], unreached);
    }

    [Fact]
    public void An_implementation_is_reached_through_a_call_on_its_interface()
    {
        var unreached = Unreached("""
            public interface IGreeter { string Greet(); string Shout(); }
            public sealed class Greeter : IGreeter
            {
                public string Greet() => "hi";
                public string Shout() => "HI";
                public string Whisper() => "hi";
            }
            public static class Program
            {
                public static void Main() => Run(new Greeter());
                private static void Run(IGreeter greeter) => greeter.Greet();
            }
            """);

        Assert.Equal(["IGreeter.Shout()", "Greeter.Shout()", "Greeter.Whisper()"], unreached);
    }

    [Fact]
    public void A_type_that_is_only_named_keeps_its_constructor_but_not_its_other_members()
    {
        // The shape of a container registration: nothing calls the constructor, and whatever was
        // handed the type is what does.
        var unreached = Unreached("""
            public sealed class Service
            {
                public Service() { }
                public void Work() { }
            }
            public static class Program
            {
                public static void Main() => Register(typeof(Service));
                private static void Register(System.Type type) { }
            }
            """);

        Assert.Equal(["Service.Work()"], unreached);
    }

    [Fact]
    public void An_unreached_type_is_one_finding_and_its_members_are_not_listed_under_it()
    {
        var unreached = Unreached("""
            public sealed class Orphan
            {
                public int Value;
                public void Work() { }
            }
            public static class Program
            {
                public static void Main() { }
            }
            """);

        Assert.Equal(["Orphan"], unreached);
    }
}
