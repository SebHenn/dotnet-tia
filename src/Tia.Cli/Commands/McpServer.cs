using System.Text.Json;
using System.Text.Json.Nodes;
using Tia.Core.Reporting;

namespace Tia.Cli.Commands;

/// <summary>What one invocation of this tool's own command line produced.</summary>
public sealed record TiaInvocation(int ExitCode, string Output, string Error);

public delegate Task<TiaInvocation> TiaRunner(IReadOnlyList<string> arguments, CancellationToken cancellationToken);

/// <summary>
/// A Model Context Protocol server over stdio that lets a coding agent ask this tool what a change
/// can affect (#173).
/// </summary>
/// <remarks>
/// <para>
/// Every tool runs this tool's own command line and reports what it said, rather than reaching
/// into the engine. That keeps one definition of every answer - the same widenings, the same
/// full-run reasons, the same exit codes - and means a running <c>tia server</c> makes the answers
/// warm without this class knowing about it.
/// </para>
/// <para>
/// Hand-written JSON-RPC rather than an SDK package: the surface is four tools, and a dependency
/// that runs inside a tool whose job is to be trusted is a cost to justify.
/// </para>
/// <para>
/// An agent that is told "these 12 tests" will read it as "nothing else can break". So a selection
/// is never returned bare: it always travels with the mode, the reasons for any full run, and every
/// widening, and the text says what a narrowed selection does and does not mean.
/// </para>
/// </remarks>
public sealed class McpServer(TiaRunner runner)
{
    public const string ProtocolVersion = "2025-06-18";

    internal const string Caution =
        "A selection is what the dependency graph says the diff can reach. It is not proof that nothing else breaks: " +
        "check `mode`, `fullRunReasons` and `widenings` before relying on it, and run the full suite before merging.";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static JsonObject Schema(params (string Name, string Type, string Description)[] properties)
    {
        var members = new JsonObject();
        foreach (var (name, type, description) in properties)
        {
            members[name] = new JsonObject { ["type"] = type, ["description"] = description };
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = members,
            ["additionalProperties"] = false,
        };
    }

    private static readonly (string Name, string Type, string Description) BaseRef =
        ("base", "string", "Git ref to diff the working tree against. Defaults to the repository's default branch.");

    public static JsonArray Tools() =>
    [
        Tool("tia_impacted", "Which tests the working tree's changes can affect, with the mode, full-run reasons and widenings that qualify the answer.", Schema(BaseRef)),
        Tool("tia_untested", "Changed symbols that no test reaches, with the same qualifying fields. Empty on a full run, where nothing was traversed.", Schema(BaseRef)),
        Tool("tia_explain", "Why a test was, or was not, selected for the working tree's changes; with symbol=true, which tests reach a symbol.",
            Schema(("name", "string", "Fully qualified test name or a suffix of one, or a symbol when symbol is true."), ("symbol", "boolean", "Explain a symbol instead of a test."), BaseRef)),
        Tool("tia_run", "Run only the impacted tests. The result includes the exit code and what it means; exit 0 on a selective run means the selected tests passed, nothing more.", Schema(BaseRef)),
    ];

    private static JsonObject Tool(string name, string description, JsonObject schema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = schema,
    };

    /// <summary>Answers one JSON-RPC message, or returns null for a notification.</summary>
    public async Task<JsonObject?> HandleAsync(JsonNode? message, CancellationToken cancellationToken)
    {
        if (message is not JsonObject request || request["method"]?.GetValue<string>() is not { } method)
        {
            return Error(null, -32600, "Not a JSON-RPC request.");
        }

        var id = request["id"]?.DeepClone();
        if (id is null)
        {
            return null;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, new JsonObject
                    {
                        ["protocolVersion"] = ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "dotnet-tia", ["version"] = typeof(McpServer).Assembly.GetName().Version?.ToString() ?? "0" },
                        ["instructions"] = Caution,
                    });
                case "ping":
                    return Result(id, new JsonObject());
                case "tools/list":
                    return Result(id, new JsonObject { ["tools"] = Tools() });
                case "tools/call":
                    return Result(id, await CallAsync(request["params"] as JsonObject, cancellationToken).ConfigureAwait(false));
                default:
                    return Error(id, -32601, $"Method not found: {method}");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A tool failure is reported as one, never as an empty selection.
            return Result(id, Text($"{ex.GetType().Name}: {ex.Message}", isError: true));
        }
    }

    private async Task<JsonObject> CallAsync(JsonObject? call, CancellationToken cancellationToken)
    {
        var name = call?["name"]?.GetValue<string>();
        var arguments = call?["arguments"] as JsonObject;
        var baseRef = arguments?["base"]?.GetValue<string>();
        List<string> baseArguments = baseRef is { Length: > 0 } ? ["--base", baseRef] : [];

        switch (name)
        {
            case "tia_impacted":
            case "tia_untested":
            {
                var run = await runner(["analyze", "--json", "--no-server", .. baseArguments], cancellationToken).ConfigureAwait(false);
                AnalysisReport? report;
                try
                {
                    report = AnalysisReport.FromJson(run.Output);
                }
                catch (JsonException)
                {
                    report = null;
                }

                if (report is null)
                {
                    return Text($"No analysis was produced (exit {run.ExitCode}). Nothing can be concluded about which tests are affected.\n{run.Error}".TrimEnd(), isError: true);
                }

                return name == "tia_impacted"
                    ? Text($"{Caution}\n\n{report.ToJson()}")
                    : Text($"{Caution}\n\n{Qualified(report)}");
            }

            case "tia_explain":
            {
                var target = arguments?["name"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(target))
                {
                    return Text("`name` is required.", isError: true);
                }

                List<string> symbol = arguments?["symbol"]?.GetValue<bool>() == true ? ["--symbol"] : [];
                var run = await runner(["explain", .. symbol, .. baseArguments, "--", target], cancellationToken).ConfigureAwait(false);
                return Text(Join(run), isError: run.ExitCode != 0);
            }

            case "tia_run":
            {
                var run = await runner(["run", .. baseArguments], cancellationToken).ConfigureAwait(false);
                return Text($"exit code {run.ExitCode}: {Meaning(run.ExitCode)}\n\n{Join(run)}", isError: run.ExitCode != 0);
            }

            default:
                return Text($"Unknown tool: {name}", isError: true);
        }
    }

    /// <summary>The untested changes together with everything that qualifies them.</summary>
    internal static string Qualified(AnalysisReport report) => new JsonObject
    {
        ["mode"] = report.Mode,
        ["fullRunReasons"] = JsonSerializer.SerializeToNode(report.FullRunReasons, AnalysisReport.JsonOptions),
        ["widenings"] = JsonSerializer.SerializeToNode(report.Widenings, AnalysisReport.JsonOptions),
        ["untestedChanges"] = JsonSerializer.SerializeToNode(report.UntestedChanges, AnalysisReport.JsonOptions),
    }.ToJsonString(Indented);

    internal static string Meaning(int exitCode) => exitCode switch
    {
        ExitCodes.Ok => "everything that ran passed. On a selective run that is the selected tests only.",
        ExitCodes.Inconclusive => "nothing could be concluded; no result about the tests exists.",
        _ => "not a pass: a test or the build failed, or the run did not finish. Read the output.",
    };

    private static string Join(TiaInvocation run) =>
        string.IsNullOrWhiteSpace(run.Error) ? run.Output : $"{run.Output}\n{run.Error}";

    private static JsonObject Text(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode id, JsonObject result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    /// <summary>Reads newline-delimited messages until the input ends.</summary>
    public async Task ServeAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        while (await input.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? response;
            try
            {
                response = await HandleAsync(JsonNode.Parse(line), cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                response = Error(null, -32700, "Parse error.");
            }

            if (response is not null)
            {
                await output.WriteLineAsync(response.ToJsonString()).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
