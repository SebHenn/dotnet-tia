using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Web.Tests;

/// <summary>
/// Functional tests that reach their endpoints the way a real one does: by route string. None of
/// them names an endpoint method, a controller or a handler - only the URL and the response - so
/// without an edge from route template to endpoint, a change to any endpoint body selects nothing.
/// </summary>
public sealed class EndpointTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private HttpClient Client => factory.CreateClient();

    [Fact]
    public async Task Lists_contributors()
    {
        var response = await Client.GetAsync("/contributors", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("ada", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gets_a_contributor_by_id()
    {
        var response = await Client.GetAsync("/contributors/7", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"id\":7", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counts_contributors_behind_the_group_prefix()
    {
        var response = await Client.GetAsync("/api/contributors/count", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Lists_projects_from_the_controller()
    {
        var response = await Client.GetAsync("/projects", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("tia", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }
}
