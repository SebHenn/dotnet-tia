using System.Text.Json.Nodes;
using Tia.Cli.Commands;
using Tia.Core.Reporting;

namespace Tia.Integration.Tests;

/// <summary><c>tia mcp</c> (#173): an agent is never handed a selection without what qualifies it.</summary>
public sealed class McpServerTests
{
    private static Task<JsonObject?> Call(TiaRunner runner, string tool, JsonObject? arguments = null) =>
        new McpServer(runner).HandleAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments ?? new JsonObject() },
            },
            CancellationToken.None);

    private static string TextOf(JsonObject? response) => response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    private static bool IsError(JsonObject? response) => response!["result"]!["isError"]!.GetValue<bool>();

    private static AnalysisReport FullRun() => new()
    {
        Mode = "full",
        BaseRef = "origin/main",
        TotalTests = 10,
        SelectedTests = 10,
        FullRunReasons = ["a project file changed"],
        Widenings = [new WideningEvent("ContentFile", "Fixtures.Tests", "embeds it")],
    };

    [Fact]
    public async Task A_selection_carries_its_mode_reasons_and_widenings()
    {
        var response = await Call((_, _) => Task.FromResult(new TiaInvocation(0, FullRun().ToJson(), string.Empty)), "tia_impacted");

        var text = TextOf(response);
        Assert.False(IsError(response));
        Assert.Contains("not proof", text, StringComparison.Ordinal);
        Assert.Contains("a project file changed", text, StringComparison.Ordinal);
        Assert.Contains("ContentFile", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Untested_changes_are_returned_with_the_mode_and_widenings()
    {
        var response = await Call((_, _) => Task.FromResult(new TiaInvocation(0, FullRun().ToJson(), string.Empty)), "tia_untested");

        var text = TextOf(response);
        Assert.Contains("\"mode\": \"full\"", text, StringComparison.Ordinal);
        Assert.Contains("a project file changed", text, StringComparison.Ordinal);
        Assert.Contains("ContentFile", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_analysis_that_produced_nothing_is_an_error_not_an_empty_selection()
    {
        var response = await Call((_, _) => Task.FromResult(new TiaInvocation(2, string.Empty, "no solution")), "tia_impacted");

        Assert.True(IsError(response));
        Assert.Contains("Nothing can be concluded", TextOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_reports_its_exit_code_and_what_a_zero_means()
    {
        IReadOnlyList<string>? seen = null;
        var response = await Call((arguments, _) =>
        {
            seen = arguments;
            return Task.FromResult(new TiaInvocation(0, "passed", string.Empty));
        }, "tia_run", new JsonObject { ["base"] = "origin/main" });

        Assert.Equal(["run", "--base", "origin/main"], seen);
        Assert.Contains("exit code 0", TextOf(response), StringComparison.Ordinal);
        Assert.Contains("selected tests only", TextOf(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_run_is_an_error()
    {
        var response = await Call((_, _) => Task.FromResult(new TiaInvocation(1, "1 failed", string.Empty)), "tia_run");

        Assert.True(IsError(response));
    }

    [Fact]
    public async Task Explain_passes_the_name_after_a_separator_so_it_cannot_be_read_as_an_option()
    {
        IReadOnlyList<string>? seen = null;
        await Call((arguments, _) =>
        {
            seen = arguments;
            return Task.FromResult(new TiaInvocation(0, "why", string.Empty));
        }, "tia_explain", new JsonObject { ["name"] = "--full", ["symbol"] = true });

        Assert.Equal(["explain", "--symbol", "--", "--full"], seen);
    }

    [Fact]
    public async Task An_unknown_method_is_answered_and_a_notification_is_not()
    {
        var server = new McpServer((_, _) => throw new InvalidOperationException());

        var unknown = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 7, ["method"] = "nope" }, CancellationToken.None);
        var notification = await server.HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }, CancellationToken.None);

        Assert.Equal(-32601, unknown!["error"]!["code"]!.GetValue<int>());
        Assert.Null(notification);
    }

    [Fact]
    public async Task Four_tools_are_listed()
    {
        var response = await new McpServer((_, _) => throw new InvalidOperationException())
            .HandleAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" }, CancellationToken.None);

        Assert.Equal(4, response!["result"]!["tools"]!.AsArray().Count);
    }
}
