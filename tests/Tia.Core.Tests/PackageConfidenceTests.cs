using Tia.DeadCode;

namespace Tia.Core.Tests;

public sealed class PackageConfidenceTests
{
    [Theory]
    [InlineData("system.data.sqlclient")]
    [InlineData("serilog.sinks.console")]
    [InlineData("azure.identity")]
    [InlineData("microsoft.testplatform.testhost")]
    public void A_package_that_is_commonly_loaded_by_name_is_low(string package) =>
        Assert.Equal(Confidence.Low, PackageConfidence.Of(package));

    [Theory]
    [InlineData("autofac")]
    [InlineData("system.reflection.typeextensions")]
    public void An_ordinary_library_is_high(string package) =>
        Assert.Equal(Confidence.High, PackageConfidence.Of(package));
}

public sealed class PackageDeclarationTests
{
    [Fact]
    public void A_package_the_project_file_lists_is_declared_there()
    {
        var text = "<Project><ItemGroup><PackageReference Include=\"Autofac\" Version=\"1\" /></ItemGroup></Project>";

        Assert.True(UnusedPackages.Declares(text, "autofac"));
    }

    [Fact]
    public void A_package_the_project_file_does_not_list_is_not()
    {
        var text = "<Project><ItemGroup><PackageReference Include=\"Autofac.Extras\" /></ItemGroup></Project>";

        Assert.False(UnusedPackages.Declares(text, "autofac"));
    }
}

public sealed class RestoredProjectTests
{
    [Fact]
    public void The_project_an_assets_file_was_restored_for_is_read_from_the_file()
    {
        var json = "{\"project\":{\"restore\":{\"projectPath\":\"/repo/App/App.csproj\"}}}";

        Assert.EndsWith("/repo/App/App.csproj", PackageAssets.RestoredProject(json), StringComparison.Ordinal);
    }

    [Fact]
    public void An_assets_file_that_does_not_say_is_null() =>
        Assert.Null(PackageAssets.RestoredProject("{}"));
}
