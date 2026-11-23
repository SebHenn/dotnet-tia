namespace Tia.DeadCode;

/// <summary>How far an unused-package report can be trusted.</summary>
public enum Confidence
{
    /// <summary>Nothing suggests the package is loaded by name; the compiler's view is likely the whole story.</summary>
    High,

    /// <summary>The package is of a kind that is commonly loaded at run time, so the compiler not using it proves little.</summary>
    Low,
}

/// <summary>
/// Packages that are used without the compiler seeing it: database providers an ORM picks by
/// connection string, logging sinks named in configuration, credential libraries an SDK client
/// resolves itself, test hosts and coverage collectors a runner loads. Matching only lowers a
/// report to <see cref="Confidence.Low"/>; it never adds one.
/// </summary>
public static class PackageConfidence
{
    private static readonly string[] Prefixes =
    [
        "system.data.", "microsoft.data.", "npgsql", "mysql", "oracle.", "sqlite", "microsoft.entityframeworkcore.",
        "serilog.sinks.", "serilog.enrichers.", "serilog.settings.", "nlog.", "log4net",
        "azure.identity", "microsoft.identity", "microsoft.testplatform.", "microsoft.net.test.sdk", "coverlet.",
        "microsoft.testing.", "xunit.runner.", "nunit3testadapter", "mstest.", "microsoft.aspnetcore.",
        "opentelemetry.exporter.", "opentelemetry.instrumentation.", "grpc.", "newtonsoft.json.", "system.text.encoding.codepages",
    ];

    /// <param name="package">The package id, lower case, as the assets file spells it.</param>
    public static Confidence Of(string package) =>
        Prefixes.Any(prefix => package.StartsWith(prefix, StringComparison.Ordinal)) ? Confidence.Low : Confidence.High;
}
