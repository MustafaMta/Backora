using Backora.Web.Api;
using Backora.Web.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Backora.Web.Tests;

/// <summary>Automated visual reviews run with ReadOnlyReview: no state-changing call may leave the frontend.</summary>
public class ReadOnlyReviewTests
{
    private static (ApiClient Client, FakeBackend Backend) Create()
    {
        var backend = new FakeBackend();
        backend.OnJson(HttpMethod.Get, "/api/v1/backup-jobs/3", new { });
        backend.OnJson(HttpMethod.Post, "/api/v1/", new { });
        var client = new ApiClient(new BackendHttp(backend, new BackendOptions { BaseUrl = "http://api.test" }), NullLogger<ApiClient>.Instance,
            Options.Create(new FrontendOptions { ReadOnlyReview = true }));
        return (client, backend);
    }

    [Theory]
    [InlineData("/backup-jobs/3/runs")]
    [InlineData("/runs/22/cancel")]
    [InlineData("/restores")]
    public async Task State_changing_posts_never_reach_the_backend(string path)
    {
        var (client, backend) = Create();
        var r = await client.PostAsync(path, new { });
        Assert.Equal(ApiOutcome.Forbidden, r.Outcome);
        Assert.Empty(backend.Requests);
    }

    [Fact]
    public async Task Deletes_and_puts_are_refused_reads_and_previews_pass()
    {
        var (client, backend) = Create();
        Assert.False((await client.DeleteAsync("/backup-jobs/3")).Ok);
        Assert.False((await client.PutAsync("/storage/s3/1", new { })).Ok);
        Assert.Empty(backend.Requests);
        Assert.True((await client.GetAsync<object>("/backup-jobs/3")).Ok);
        Assert.True((await client.PostAsync<object>("/backup-jobs/schedule-preview", new { })).Ok);
        Assert.Equal(2, backend.Requests.Count);
    }
}
