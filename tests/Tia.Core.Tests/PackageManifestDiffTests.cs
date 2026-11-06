using Tia.Core.Safety;

namespace Tia.Core.Tests;

public sealed class PackageManifestDiffTests
{
    private const string Central = """
        <Project>
          <PropertyGroup>
            <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
          </PropertyGroup>
          <ItemGroup>
            <PackageVersion Include="Alpha" Version="1.0.0" />
            <PackageVersion Include="Beta" Version="2.0.0" />
          </ItemGroup>
        </Project>
        """;

    private static IReadOnlySet<string>? Changed(string after) => PackageManifestDiff.ChangedPackages(Central, after);

    [Fact]
    public void A_version_change_names_the_package()
    {
        var changed = Changed(Central.Replace("1.0.0", "1.0.1", StringComparison.Ordinal));

        Assert.Equal(["Alpha"], changed!);
    }

    [Fact]
    public void Several_version_changes_name_each_package()
    {
        var changed = Changed(Central
            .Replace("1.0.0", "1.0.1", StringComparison.Ordinal)
            .Replace("2.0.0", "2.0.1", StringComparison.Ordinal));

        Assert.Equal(["Alpha", "Beta"], changed!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_package_name_is_matched_whatever_its_case() =>
        Assert.Contains("ALPHA", Changed(Central.Replace("1.0.0", "1.0.1", StringComparison.Ordinal))!);

    [Fact]
    public void Formatting_and_comments_change_no_package() =>
        Assert.Empty(Changed(Central
            .Replace("<ItemGroup>", "<ItemGroup>\n    <!-- pinned -->\n", StringComparison.Ordinal)
            .Replace("\" />", "\"   />", StringComparison.Ordinal))!);

    [Fact]
    public void A_project_file_bump_is_read_the_same_way()
    {
        const string Before = """
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup>
                <PackageReference Include="Alpha" Version="1.0.0" PrivateAssets="all" />
              </ItemGroup>
            </Project>
            """;

        var changed = PackageManifestDiff.ChangedPackages(Before, Before.Replace("1.0.0", "1.1.0", StringComparison.Ordinal));

        Assert.Equal(["Alpha"], changed!);
    }

    [Fact]
    public void A_version_override_is_a_version() =>
        Assert.Equal(["Alpha"], PackageManifestDiff.ChangedPackages(
            """<Project><ItemGroup><PackageReference Include="Alpha" VersionOverride="1.0.0" /></ItemGroup></Project>""",
            """<Project><ItemGroup><PackageReference Include="Alpha" VersionOverride="1.0.1" /></ItemGroup></Project>""")!);

    [Fact]
    public void A_byte_order_mark_is_not_a_change() =>
        Assert.Empty(PackageManifestDiff.ChangedPackages("﻿" + Central, Central)!);

    /// <summary>Anything but a plain version change could mean anything, so it is not understood.</summary>
    [Theory]
    [InlineData("<ManagePackageVersionsCentrally>true", "<ManagePackageVersionsCentrally>false")]
    [InlineData("<PackageVersion Include=\"Beta\" Version=\"2.0.0\" />", "")]
    [InlineData("<PackageVersion Include=\"Beta\" Version=\"2.0.0\" />", "<PackageVersion Include=\"Beta\" Version=\"2.0.0\" /><PackageVersion Include=\"Gamma\" Version=\"3.0.0\" />")]
    [InlineData("Include=\"Beta\"", "Include=\"Gamma\"")]
    [InlineData("<ItemGroup>", "<ItemGroup Condition=\"'$(X)' == 'y'\">")]
    [InlineData("Include=\"Beta\" Version", "Include=\"Beta\" Condition=\"'$(X)' == 'y'\" Version")]
    [InlineData("</Project>", "<Import Project=\"more.props\" /></Project>")]
    [InlineData("<PackageVersion Include=\"Beta\" Version=\"2.0.0\" />", "<GlobalPackageReference Include=\"Beta\" Version=\"2.0.0\" />")]
    [InlineData("<PackageVersion Include=\"Beta\" Version=\"2.0.0\" />", "<PackageVersion Include=\"Beta\"><Version>2.0.0</Version></PackageVersion>")]
    public void Any_other_change_is_not_understood(string find, string replace) =>
        Assert.Null(Changed(Central.Replace(find, replace, StringComparison.Ordinal)));

    [Fact]
    public void A_global_package_reference_moving_is_not_understood()
    {
        const string Before = """<Project><ItemGroup><GlobalPackageReference Include="Alpha" Version="1.0.0" /></ItemGroup></Project>""";

        Assert.Null(PackageManifestDiff.ChangedPackages(Before, Before.Replace("1.0.0", "1.0.1", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_package_entry_naming_no_package_is_not_understood()
    {
        const string Before = """<Project><ItemGroup><PackageVersion Version="1.0.0" /></ItemGroup></Project>""";

        Assert.Null(PackageManifestDiff.ChangedPackages(Before, Before.Replace("1.0.0", "1.0.1", StringComparison.Ordinal)));
    }

    [Fact]
    public void Text_that_is_not_xml_is_not_understood() =>
        Assert.Null(Changed("<Project><ItemGroup>"));

    [Theory]
    [InlineData("Directory.Packages.props", true)]
    [InlineData("build/directory.packages.props", true)]
    [InlineData("src/App/App.csproj", true)]
    [InlineData("src/App/App.fsproj", false)]
    [InlineData("Directory.Build.props", false)]
    [InlineData("packages.lock.json", false)]
    public void Only_a_package_manifest_is_a_candidate(string path, bool expected) =>
        Assert.Equal(expected, PackageManifestDiff.IsManifest(path));
}
